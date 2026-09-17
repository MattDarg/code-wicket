using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// Turns a file reference written in agent prose into an absolute path under the workspace root —
    /// or into nothing, which is the answer that matters most. A dead link is worse than plain text
    /// (one link that does nothing teaches the user to distrust all of them), so resolution is
    /// existence-gated rather than pattern-gated: what the matcher accepts is only a candidate.
    /// <para>
    /// <b>No IO ever happens on the render path.</b> The assistant's <c>FlowDocument</c> is rebuilt on
    /// every streamed delta, so a message arriving in ~200 deltas would re-resolve each of its
    /// references ~200 times — with a <c>File.Exists</c>, and on a first miss a filename search, on the
    /// UI thread mid-stream. So the renderer only ever asks for an <i>already-known</i> answer
    /// (<see cref="TryGetResolved"/>); an unknown one renders as plain text and resolves off-thread via
    /// <see cref="RequestResolve"/>, upgrading to a link when it lands. Mid-stream that upgrade is free
    /// (the next delta re-renders anyway); after the last delta it costs one extra rebuild.
    /// </para>
    /// </summary>
    public sealed class FileReferenceResolver
    {
        /// <summary>
        /// Ceiling on the filename index. Large enough for any real repository; present so a root that
        /// accidentally points somewhere enormous degrades to "some references don't link" rather than
        /// to a background thread walking a whole drive.
        /// </summary>
        public const int MaxIndexedFiles = 60000;

        /// <summary>
        /// Paths kept per filename. Only ambiguity matters for a bare name, but a reference that
        /// carries directories is matched by suffix, so a handful of same-named files must survive.
        /// </summary>
        private const int MaxPathsPerName = 16;

        /// <summary>The cache is fed by model output, so it is bounded; it is a memo, so it just clears.</summary>
        private const int MaxCacheEntries = 4000;

        // The usual build/tooling noise. Excluding it is also what makes the majority resolution path
        // (a bare filename, 63% of real references) unambiguous: bin\Foo.dll copies and obj\ generated
        // sources are exactly the duplicates that would make a unique-match rule fail.
        private static readonly HashSet<string> SkipDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", ".git", ".hg", ".svn", ".vs", "node_modules", "packages", ".idea", "TestResults",
        };

        private readonly object _gate = new object();
        private readonly Dictionary<string, string?> _cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Action>> _waiters = new Dictionary<string, List<Action>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dispatcher? _dispatcher;

        private string? _root;
        private Task<Dictionary<string, List<string>>>? _indexTask;
        private CancellationTokenSource? _indexCts;
        private int _generation;

        /// <summary>
        /// Test seam: replaces the disk walk so a test can gate it (block it, or make it fail) without
        /// needing a directory tree big enough to be slow. Null = the real <see cref="BuildIndex"/>.
        /// </summary>
        internal Func<string, CancellationToken, Dictionary<string, List<string>>>? IndexBuilder;

        /// <summary>
        /// Optional sink for one line per completed index walk — its duration and how many files it
        /// indexed. Null (the default) writes nothing, which is what the Desktop host and the tests
        /// want; the VSIX points it at <c>engine.log</c>.
        ///
        /// It reports what we cannot otherwise see. The walk's cost belongs to the machine — repo
        /// size, LocalAppData or the repo itself redirected onto a share, on-access scanning of every
        /// enumeration — so "the blue links take a while to appear" is unattributable from here
        /// without a number, and a number needs the file count beside it to mean anything.
        /// </summary>
        public Action<string>? IndexLog;

        /// <param name="root">
        /// The directory agent-written relative paths resolve against — the chat's
        /// <c>AgentPathRoot</c>. Also the security boundary: nothing outside it is ever linkable.
        /// </param>
        public FileReferenceResolver(string? root)
        {
            _root = Normalize(root);
            // Captured where the view-model is built (the UI thread), so resolution callbacks land back
            // on it. Null in a plain unit test, where they run inline.
            _dispatcher = Dispatcher.FromThread(System.Threading.Thread.CurrentThread);
        }

        /// <summary>The current root; null/empty means nothing resolves.</summary>
        public string? Root
        {
            get { lock (_gate) return _root; }
        }

        /// <summary>
        /// Repoints at a new root (the agent reports its working directory after the session starts, and
        /// a restored transcript supplies the saved one). Everything learned about the old root is
        /// discarded — including the index, which is rebuilt lazily on the next reference.
        /// </summary>
        public void SetRoot(string? root)
        {
            var normalized = Normalize(root);
            CancellationTokenSource? abandoned;
            lock (_gate)
            {
                if (string.Equals(_root, normalized, StringComparison.OrdinalIgnoreCase))
                    return;
                _root = normalized;
                _generation++;
                _cache.Clear();
                _indexTask = null;
                abandoned = _indexCts;
                _indexCts = null;
            }

            // Dropping the task reference only FORGETS the walk; it does not stop it. That matters
            // because the root genuinely flaps during startup — opening a solution closes the previous
            // one first, so we transit the default workspace on the way — and each flap would otherwise
            // leave another full recursive walk (up to MaxIndexedFiles) running against a root nobody
            // is waiting on, competing for the disk with the one that replaced it. Cancelled outside the
            // lock: Cancel runs continuations inline, and none of them should be holding _gate.
            //
            // Deliberately not disposed. The source outlives this method only until the walk observes
            // the token, it holds no timer or wait handle, and disposing one whose token is still being
            // read by the walk is the unsafe half of the API. Letting it be collected is correct here.
            try { abandoned?.Cancel(); }
            catch { /* a faulted continuation is not our problem; the walk is what had to stop */ }
        }

        /// <summary>
        /// The already-known answer for <paramref name="pathText"/>: <c>true</c> with a non-null
        /// <paramref name="fullPath"/> for a resolved reference, <c>true</c> with null for one known
        /// not to resolve, <c>false</c> when it hasn't been looked at yet. Pure dictionary work — safe
        /// on the render path, which is the entire point of the split.
        /// </summary>
        public bool TryGetResolved(string pathText, out string? fullPath)
        {
            lock (_gate)
                return _cache.TryGetValue(pathText, out fullPath);
        }

        /// <summary>
        /// Resolves <paramref name="pathText"/> off the UI thread and invokes
        /// <paramref name="onResolved"/> (on the dispatcher) once the answer is cached — whether it
        /// resolved or not, so a caller waiting to re-render is never stranded. Concurrent requests for
        /// the same text share one resolution.
        /// </summary>
        public void RequestResolve(string pathText, Action onResolved)
        {
            if (string.IsNullOrEmpty(pathText) || onResolved is null)
                return;

            int generation;
            lock (_gate)
            {
                if (_cache.ContainsKey(pathText))
                {
                    // Raced with another viewer's resolution. Post rather than call: the caller is
                    // mid-render, and re-entering the render from inside it is a trap.
                    Post(onResolved);
                    return;
                }
                if (_waiters.TryGetValue(pathText, out var existing))
                {
                    existing.Add(onResolved);
                    return;
                }
                _waiters[pathText] = new List<Action> { onResolved };
                generation = _generation;
            }

            // Task.Run puts the START of the resolution on the pool (this is called from the render path,
            // i.e. the UI thread, and the direct-hit check below it touches the disk). Everything after
            // that is awaited, never blocked on — see ResolveCoreAsync.
            _ = Task.Run(() => ResolveAndCompleteAsync(pathText, generation));
        }

        private async Task ResolveAndCompleteAsync(string pathText, int generation)
        {
            string? resolved;
            try { resolved = await ResolveCoreAsync(pathText, generation).ConfigureAwait(false); }
            catch { resolved = null; }
            Complete(pathText, generation, resolved);
        }

        /// <summary>
        /// Resolves and caches, awaitable. The synchronous core of <see cref="RequestResolve"/>, exposed
        /// so the resolution rules can be asserted directly rather than through a rendered document.
        /// </summary>
        public Task<string?> ResolveAsync(string pathText)
        {
            if (string.IsNullOrEmpty(pathText))
                return Task.FromResult<string?>(null);

            int generation;
            lock (_gate)
            {
                if (_cache.TryGetValue(pathText, out var cached))
                    return Task.FromResult(cached);
                generation = _generation;
            }

            return Task.Run(async () =>
            {
                string? resolved;
                try { resolved = await ResolveCoreAsync(pathText, generation).ConfigureAwait(false); }
                catch { resolved = null; }
                Complete(pathText, generation, resolved);
                return resolved;
            });
        }

        /// <summary>
        /// Notes a file the agent just wrote. A file that is about to be <i>created</i> doesn't exist
        /// when it is first mentioned, and without this its negative answer would stand for the rest of
        /// the conversation — the reference stays plain text exactly where it is most useful. Cheaper
        /// and more precise than invalidating the whole negative cache: only the filename that changed
        /// is forgotten, and the index learns the new path instead of being rebuilt.
        /// </summary>
        public void NoteFileWritten(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            string full, leaf;
            try
            {
                full = Path.GetFullPath(path!);
                leaf = Path.GetFileName(full);
            }
            catch
            {
                return;
            }
            if (string.IsNullOrEmpty(leaf))
                return;

            lock (_gate)
            {
                if (_root is null || !IsUnderRoot(full, _root))
                    return;

                // Only negatives: a positive can go stale only on delete/rename, where the click
                // degrades to OpenFileAtLineAsync failing gracefully.
                List<string>? stale = null;
                foreach (var entry in _cache)
                {
                    if (entry.Value is null && string.Equals(LeafOf(entry.Key), leaf, StringComparison.OrdinalIgnoreCase))
                        (stale ??= new List<string>()).Add(entry.Key);
                }
                if (stale is not null)
                {
                    foreach (var key in stale)
                        _cache.Remove(key);
                }

                if (_indexTask is { IsCompleted: true, Status: TaskStatus.RanToCompletion } built)
                {
                    var index = built.Result;
                    if (!index.TryGetValue(leaf, out var paths))
                        index[leaf] = paths = new List<string>(1);
                    if (paths.Count < MaxPathsPerName && !paths.Contains(full, StringComparer.OrdinalIgnoreCase))
                        paths.Add(full);
                }
            }
        }

        // ---------------------------------------------------------------------------------------

        private void Complete(string pathText, int generation, string? resolved)
        {
            List<Action>? waiters;
            lock (_gate)
            {
                // A resolution that outlived its root answers a question nobody asked any more, so it
                // is not cached. RE-ASKED, though, rather than reported: the waiters stay registered and
                // the resolution runs again against the root that is current now.
                //
                // Releasing them was the original contract, and it did not survive contact with the only
                // waiter there is. MarkdownText re-renders ONLY on a cache hit — correct for a genuine
                // miss, where the rebuilt document would be identical, and wrong here, because a
                // mismatch caches nothing and looks exactly like one. So no re-render was queued, the
                // entry was gone from _waiters, and nothing else could ever trigger a retry: SetRoot
                // reuses the same resolver, so LinksProperty does not change either. Every `Foo.cs:42`
                // whose resolution was in flight across the change stayed unclickable plain text for the
                // life of the conversation — and the root legitimately flaps at startup, since a
                // solution opening transits the default workspace.
                //
                // Retrying inside the resolver keeps the fix where the knowledge is: it is the only
                // party that knows its own generation moved. It cannot spin, because a retry is only
                // ever started by a completion that has already done the work.
                if (generation != _generation)
                {
                    var current = _generation;
                    _ = Task.Run(() => ResolveAndCompleteAsync(pathText, current));
                    return;
                }

                _waiters.TryGetValue(pathText, out waiters);
                _waiters.Remove(pathText);

                if (_cache.Count >= MaxCacheEntries)
                    _cache.Clear();
                _cache[pathText] = resolved;
            }

            if (waiters is null || waiters.Count == 0)
                return;
            Post(() =>
            {
                foreach (var waiter in waiters)
                {
                    try { waiter(); } catch { /* a re-render must never take the resolver down */ }
                }
            });
        }

        private void Post(Action action)
        {
            if (_dispatcher is null || _dispatcher.CheckAccess())
            {
                try { action(); } catch { }
                return;
            }
            _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        }

        /// <summary>
        /// Resolution order, first hit wins: the path as written, the path combined with the root, then
        /// a unique filename match from the index (the majority of real references are a bare
        /// filename). Never throws — an unresolvable reference and a failed lookup are the same answer.
        /// </summary>
        private async Task<string?> ResolveCoreAsync(string pathText, int generation)
        {
            try
            {
                string? root;
                lock (_gate)
                {
                    if (generation != _generation)
                        return null;
                    root = _root;
                }
                if (string.IsNullOrEmpty(root))
                    return null;

                var normalized = pathText.Replace('/', Path.DirectorySeparatorChar);
                var direct = Path.IsPathRooted(normalized)
                    ? SafeFullPath(normalized)
                    : SafeFullPath(Path.Combine(root!, normalized));

                // Under-the-root is checked before existence, so a prompt-injected
                // "C:\Users\you\.aws\credentials:1" can never become a one-click open - and
                // neither can "docs\creds:1" through a junction the repository carried: the
                // spelling is under the root, the file is not, and the link walk runs before the
                // Exists that would follow it.
                if (direct is not null && IsUnderRoot(direct, root!) &&
                    !CodeWicket.Core.Ide.ReparsePoints.AnyBelow(root!, direct) && File.Exists(direct))
                    return direct;

                var index = await IndexAsync(root!, generation).ConfigureAwait(false);
                return index is null ? null : LookupInIndex(index, normalized, generation);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The filename index lookup. A bare name must be <b>unique</b> to link (ambiguity is rare and
        /// "don't link when ambiguous" is cheap and safe). A reference that carries directories is
        /// matched by path suffix instead, and does <i>not</i> fall back to the bare name: the agent
        /// said which <c>Foo.cs</c> it meant, so linking a different one would contradict it.
        /// </summary>
        private string? LookupInIndex(Dictionary<string, List<string>> index, string normalized, int generation)
        {
            var leaf = Path.GetFileName(normalized);
            if (string.IsNullOrEmpty(leaf))
                return null;

            lock (_gate)
            {
                if (generation != _generation || !index.TryGetValue(leaf, out var paths) || paths.Count == 0)
                    return null;

                var trimmed = normalized.TrimStart(Path.DirectorySeparatorChar);
                if (trimmed.IndexOf(Path.DirectorySeparatorChar) < 0)
                    return paths.Count == 1 ? paths[0] : null;

                var suffix = Path.DirectorySeparatorChar + trimmed;
                string? only = null;
                foreach (var candidate in paths)
                {
                    if (!candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (only is not null)
                        return null;
                    only = candidate;
                }
                return only;
            }
        }

        /// <summary>
        /// The filename index, built once per root on a background thread. Built lazily — a session
        /// where the agent never names a file pays nothing — and shared: concurrent resolutions all
        /// wait on the one walk rather than each starting their own.
        /// </summary>
        private async Task<Dictionary<string, List<string>>?> IndexAsync(string root, int generation)
        {
            Task<Dictionary<string, List<string>>> task;
            lock (_gate)
            {
                if (generation != _generation)
                    return null;
                if (_indexTask is null)
                {
                    _indexCts = new CancellationTokenSource();
                    var token = _indexCts.Token;
                    var builder = IndexBuilder ?? BuildIndex;
                    _indexTask = Task.Run(() => Timed(builder, root, token), token);
                }
                task = _indexTask;
            }

            // AWAITED, never blocked on. This used to be task.GetAwaiter().GetResult(), on the reasoning
            // that the caller was already off the UI thread so blocking "only parks this resolution".
            // It parked a THREADPOOL THREAD, once per distinct unresolved reference — and in devenv that
            // pool is shared with the whole IDE. A restored transcript asks for on the order of a hundred
            // references at once, nearly all of them bare filenames that miss the direct check and land
            // here, so ~100 work items would sit blocked on one directory walk while VS was trying to
            // load a solution on the same pool. The pool then injects replacement threads at a trickle,
            // so the stall lasted as long as the walk did — which on a network- or sync-backed repo with
            // endpoint security inspecting each enumeration is not a short time.
            //
            // Awaiting costs nothing extra (they were already waiting on this one shared task) and holds
            // no thread while it happens.
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch
            {
                return null; // cancelled by a root change, or the walk faulted: no index, no link
            }
        }

        /// <summary>
        /// Reports one walk to <see cref="IndexLog"/>, including the ones that are ABANDONED. A root
        /// that flaps during startup (opening a solution closes the previous one first, so we transit
        /// the default workspace on the way — see <see cref="SetRoot"/>) starts a walk per flap, and a
        /// log showing only the survivor would attribute the whole wait to it while hiding the two
        /// full-tree enumerations that ran alongside. Wraps rather than lives inside
        /// <see cref="BuildIndex"/> so the test seam is measured too.
        /// </summary>
        private Dictionary<string, List<string>> Timed(
            Func<string, CancellationToken, Dictionary<string, List<string>>> builder,
            string root,
            CancellationToken cancellationToken)
        {
            var log = IndexLog;
            if (log is null)
                return builder(root, cancellationToken);

            var watch = Stopwatch.StartNew();
            Dictionary<string, List<string>> map;
            try
            {
                map = builder(root, cancellationToken);
            }
            catch (Exception ex)
            {
                Report(log, ex is OperationCanceledException
                    ? Elapsed(watch) + ", abandoned (root changed)"
                    : Elapsed(watch) + ", failed (" + ex.GetType().Name + ")");
                throw;
            }

            var paths = 0;
            foreach (var entry in map)
                paths += entry.Value.Count;

            // Paths rather than files: a name past MaxPathsPerName is indexed but not kept, so this is
            // what the index actually holds — the number that explains a lookup, not just the walk.
            Report(log, Elapsed(watch) + ", " + paths.ToString(CultureInfo.InvariantCulture) + " paths, " +
                map.Count.ToString(CultureInfo.InvariantCulture) + " names, root " + root);
            return map;
        }

        private static string Elapsed(Stopwatch watch) =>
            watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms";

        private static void Report(Action<string> log, string line)
        {
            try { log("[file-index] " + line); }
            catch { /* a diagnostic must never take the walk down */ }
        }

        private static Dictionary<string, List<string>> BuildIndex(string root, CancellationToken cancellationToken)
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var indexed = 0;
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0 && indexed < MaxIndexedFiles)
            {
                // Checked per directory rather than per file: the walk is dominated by the enumeration
                // calls, and this is the granularity at which abandoning it actually saves anything.
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();

                try
                {
                    foreach (var file in Directory.GetFiles(directory))
                    {
                        var leaf = Path.GetFileName(file);
                        if (string.IsNullOrEmpty(leaf))
                            continue;
                        if (!map.TryGetValue(leaf, out var paths))
                            map[leaf] = paths = new List<string>(1);
                        if (paths.Count < MaxPathsPerName)
                            paths.Add(file);
                        if (++indexed >= MaxIndexedFiles)
                            break;
                    }
                }
                catch
                {
                    // Unreadable directory (permissions, a race with a delete): skip it, keep walking.
                }

                try
                {
                    foreach (var child in Directory.GetDirectories(directory))
                    {
                        // A linked directory is not walked: its files would index under the
                        // root's spelling and open from wherever the link points.
                        if (!SkipDirectories.Contains(Path.GetFileName(child)) &&
                            !CodeWicket.Core.Ide.ReparsePoints.IsLink(child))
                            pending.Push(child);
                    }
                }
                catch
                {
                }
            }

            return map;
        }

        private static string? Normalize(string? root)
        {
            if (string.IsNullOrEmpty(root))
                return null;
            try
            {
                return Path.GetFullPath(root!)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        private static string? SafeFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return null;
            }
        }

        // Delegated to the shared rule (Core.Ide.WorkspacePath) rather than kept as a third copy of the
        // same prefix test. It costs one extra GetFullPath per candidate — this input is already
        // full-pathed — which is allocation-only, no IO, and paid per file REFERENCE rather than per
        // glyph. If a --perf sweep ever moves on it, the answer is an already-normalized overload in
        // Core, never a re-fork of the rule.
        private static bool IsUnderRoot(string fullPath, string root)
            => CodeWicket.Core.Ide.WorkspacePath.IsUnderRoot(fullPath, root);

        private static string LeafOf(string pathText)
        {
            try
            {
                return Path.GetFileName(pathText.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
