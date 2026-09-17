using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using CodeWicket.Core;
using CodeWicket.Core.Ide;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Routes the agent's reads/writes through the VS editor. A write opens the file and replaces
    /// only the changed span (common-prefix/suffix diff) in the editor's text buffer, so the change
    /// dirties the buffer, joins the undo stack, and flows through source control — like a user edit,
    /// but with a tight, precise undo rather than a whole-document churn.
    /// </summary>
    /// <remarks>
    /// Falls back to a DTE whole-document replace, then a direct disk write, if the buffer path is
    /// unavailable. Relative paths resolve against the workspace root. All work is on the UI thread.
    /// </remarks>
    public sealed class VsEditApplier : IEditApplier
    {
        private readonly DTE2 _dte;
        private string _rootPath;
        private string _agentRootPath;
        private readonly IServiceProvider _serviceProvider;
        private readonly IVsEditorAdaptersFactoryService _adapters;
        private readonly IVsDifferenceService _diffService;

        public VsEditApplier(
            DTE2 dte, string rootPath, IServiceProvider serviceProvider,
            IVsEditorAdaptersFactoryService adapters, IVsDifferenceService diffService)
        {
            _dte = dte;
            _rootPath = rootPath;
            _serviceProvider = serviceProvider;
            _adapters = adapters;
            _diffService = diffService;

            // Reclaim the previous sessions' diff scratch files (issue #88). Unawaited and OFF the UI
            // thread — VsIdeServices.Create runs on it, and bulk file enumeration + deletion must
            // never run there. That is a rule, not a measurement result: the cost of touching this
            // directory is not ours to predict. On-access AV/EDR scanning hooks enumerate and delete
            // (this project already treats corporate EDR as a live constraint — it is why these files
            // aren't in temp), LocalAppData can be caught by folder redirection onto a network share,
            // and our users are not all on local SSDs (issue #86 is a VDI sluggishness report). A
            // local timing establishes a floor and tells us nothing about the ceiling.
            //
            // For what the floor is worth: 1 ms over the 24 files issue #88 was filed on, but O(files)
            // with a fat tail — 488 ms over 10k, 2.8 s over 40k. The FIRST sweep after this ships is
            // the bad one, landing on however much a machine accumulated while nothing reclaimed at
            // all, which is exactly the run that would have stalled the window opening.
            //
            // Racing a diff the user opens meanwhile is benign: the file list is snapshotted at
            // enumeration, a just-written file is never past the age cutoff, and budget eviction is
            // oldest-first so it sorts last. There is nothing to await — no later work depends on
            // the result.
            //
            // The catch is deliberate belt-and-braces, NOT redundancy: RetentionSweep.Run never
            // throws today (blanket catch, pinned by RetentionSweepTests), and this covers the day
            // that contract changes. It matters because Forget() is documented as "consumes a task
            // and doesn't do anything with it" — a genuine no-op that does NOT observe the fault. An
            // escaped exception would fault the task, go unobserved, and surface only at finalization
            // via TaskScheduler.UnobservedTaskException, which since .NET 4.5 doesn't tear the
            // process down. So the failure mode wouldn't be a crash — it would be SILENCE: retention
            // simply stops, with nothing logged and nothing to notice but the folder growing again.
            Task.Run(() =>
            {
                try { SweepDiffScratch(); }
                catch (Exception ex) { ToolErrorLog.Write("diff scratch sweep", ex); }
            }).Forget();
        }

        /// <summary>The solution root — the fallback a relative path resolves against (e.g. when the solution changes).</summary>
        internal void SetRootPath(string rootPath) => _rootPath = rootPath;

        /// <summary>
        /// The directory the AGENT is actually running in, which is what its relative paths are measured
        /// from — and is not necessarily the solution root: <c>WorkspaceRootLocator</c> walks up for the
        /// backend's workspace marker (issue #54), so a `.kiro` above the solution folder makes the two
        /// differ. Pushed down per session, since it is a property of the session rather than of the IDE;
        /// null/empty (no session, or a backend that reports none) falls back to the solution root, which
        /// is the previous behaviour and correct whenever they coincide.
        /// </summary>
        /// <remarks>
        /// Resolving against the wrong one of these does not fail — it succeeds against a DIFFERENT file.
        /// A write CREATES, so <c>fs/write_text_file "proj/File.cs"</c> rooted a level too deep reports a
        /// clean edit while the file the agent meant is untouched, and the read that would have shown the
        /// mistake returns the content of the file it just created. Nothing surfaces.
        /// </remarks>
        internal void SetAgentRootPath(string agentRootPath) => _agentRootPath = agentRootPath;

        public async Task<string> ReadTextFileAsync(string path, int? line = null, int? limit = null, CancellationToken cancellationToken = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var full = Resolve(path);
            // An open document wins even when nothing is on disk yet (a new unsaved file is real
            // content the agent should see); only a path that is neither open nor on disk is missing.
            // Throwing rather than returning "" is the contract — see IEditApplier.ReadTextFileAsync.
            var open = TryGetOpenDocumentText(full);
            if (open is null && !File.Exists(full))
                throw new FileNotFoundException($"File not found: {full}", full);

            var content = open ?? File.ReadAllText(full);

            // Shared with the stub/Console appliers, which used to ignore the range entirely.
            return TextRange.Slice(content, line, limit);
        }

        // Returned non-nullable: this project is nullable-oblivious (the `?` would warn CS8632) and the
        // VS applier always has a real before/after to report. The interface's nullable annotation is for
        // hosts that can't.
        public async Task<FileWriteResult> WriteTextFileAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var full = Resolve(path);

            // Capture the "before" content for the diff preview, then ensure the file exists.
            var oldText = TryGetOpenDocumentText(full) ?? (File.Exists(full) ? File.ReadAllText(full) : string.Empty);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            if (!File.Exists(full))
                File.WriteAllText(full, string.Empty);

            // Agents routinely send "\n" for a file that is CRLF on disk. Applied verbatim that leaves the
            // document with MIXED endings, which VS then raises its modal "inconsistent line endings" dialog
            // over the next time the file is opened — on the UI thread, mid-turn, in front of the agent's own
            // edit. It also quietly defeats ComputeMinimalEdit: the common prefix stops at the first line
            // break, so a one-line change rewrites the WHOLE document, churning the undo stack, the diff
            // preview and the git diff. ACP's write carries the full file, so matching the endings here also
            // repairs a file that is already mixed.
            content = LineEndings.Match(content, oldText);

            // Buffer first, then DTE, then disk — but ONLY while each says "not me". A route that was
            // asked and REFUSED ends the write here: routing around the editor's refusal is what turns a
            // user's "Cancel" on the make-writable prompt into a silent disk write behind their back,
            // and File.WriteAllText can't clear a read-only attribute anyway, so the fallback would fail
            // with a worse message than the one we can give.
            var route = TryWriteThroughBuffer(full, content);
            if (route == WriteRoute.Unavailable)
                route = TryWriteThroughDte(full, content);

            if (route == WriteRoute.Refused)
                // Thrown, not returned: this is a MUTATION that changed nothing, so there is no result to
                // report — only a false impression to correct. Reported success here and the agent
                // carried on believing the file said something it does not (found by the user, 2026-09-03,
                // by marking the target read-only and cancelling VS's prompt).
                //
                // The reason is CHECKED rather than guessed at — see FileWriteRefusal for what a hedged
                // one cost on the wire. The editor tells us it refused and not why, so the one fact we
                // can establish ourselves is the file's own read-only attribute.
                throw new IOException(FileWriteRefusal.Describe(full, IsMarkedReadOnly(full)));

            if (route == WriteRoute.Applied)
                TrySaveDocument(full);
            else
                File.WriteAllText(full, content);

            // Deliberately NO diff window here. The diff is opened on demand, by clicking the edit's
            // transcript row (ChatToolWindow's openDiff -> ShowDiffPreviewAsync) — the same gesture as
            // an edit the agent applied itself. Auto-opening made this route the odd one out: a Kiro v3
            // turn touching five files threw five windows across the IDE unasked, on top of the five
            // clickable cards AcpMapper still raises from the completed tool_call's diff content.
            //
            // What the auto-opened window DID have over the click path was fidelity: it showed the true
            // pre-write content against the real file, where the click path re-derives both sides from
            // the agent's reported diff (and, for a hunk, reconstructs the surrounding file). That's
            // what the return value is for — the caller feeds this exact pair back to the diff so the
            // reconstruction is never needed for a write we performed. See FileWriteResult.
            return new FileWriteResult(full, oldText, content);
        }

        /// <summary>
        /// The 1-based line an edit card's change sits at in the file as it stands NOW, for "Open file"
        /// on that card; 0 (the top) when it can't be located and the backend reported nothing.
        /// <para>
        /// Reads the same source <see cref="ShowDiffPreviewAsync"/> reconstructs against — the live
        /// buffer if the document is open, else disk — so the line this navigates to and the line in
        /// the diff window's caption are computed from identical inputs by the identical rule
        /// (<see cref="DiffSideBuilder.LocateLine"/> shares <c>LocateHunk</c> with
        /// <c>TryBuild</c>). Same thread discipline, and for the same reasons: only the buffer read is
        /// UI-thread-affine, and the disk read plus a whole-file scan are hopped off it.
        /// </para>
        /// </summary>
        public async Task<int> ResolveDiffLineAsync(
            string path, string oldText, string newText, int? reportedLine = null, CancellationToken cancellationToken = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            try
            {
                // Inside the try with everything else it feeds: locating is best-effort, and an
                // unrootable path is one more thing that leaves the reported line as the answer.
                var full = Resolve(path);
                var openText = TryGetOpenDocumentText(full);

                await TaskScheduler.Default;

                var current = openText ?? (File.Exists(full) ? File.ReadAllText(full) : null);
                return DiffSideBuilder.LocateLine(current, oldText, newText, reportedLine);
            }
            catch (Exception)
            {
                // Locating is best-effort — the reported line, else the top, is always a usable answer.
                return reportedLine is { } r && r > 0 ? r : 0;
            }
        }

        /// <summary>
        /// Opens VS's native diff viewer for an edit the agent applied itself (e.g. Kiro's built-in
        /// file tool). Both sides are written to temp files, so the preview is independent of what's
        /// currently on disk. Read-only — the file is not modified.
        /// </summary>
        /// <remarks>
        /// A surgical edit (Claude's <c>Edit</c>, Kiro's <c>fsReplace</c>) reports only the changed
        /// hunk as <paramref name="oldText"/>/<paramref name="newText"/>, so a raw before/after of
        /// those shows the change with no surrounding file. We reconstruct a full-file before/after by
        /// splicing the hunk into the file's *current* content (the live buffer, else disk) — which,
        /// at click time, already reflects every edit the agent made — so the change is shown in
        /// context. See <see cref="DiffSideBuilder"/> for the safety rails / fallback — it lives in
        /// Core so those are testable without devenv, and it runs off the UI thread here.
        /// </remarks>
        public async Task ShowDiffPreviewAsync(string path, string oldText, string newText, int? reportedLine = null, CancellationToken cancellationToken = default)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (_diffService is null)
                return;

            try
            {
                // Inside the try, like the locate above: the preview is best-effort, so a path with
                // no root to measure it from shows no diff rather than throwing into the click.
                var full = Resolve(path);
                var name = Path.GetFileName(full);

                // Only the LIVE BUFFER read needs the UI thread — DTE's document collection is
                // UI-thread-affine. Everything downstream of it is ordinary I/O and CPU, so it is
                // hopped off deliberately: a whole-file scan and splice plus two writes of a 100 KB+
                // source file has no business on the thread painting the IDE, even on a gesture the
                // user initiated. The I/O half is also not something a local timing can bound — same
                // AV/EDR, redirected-profile and VDI reasoning as the sweep in the constructor.
                var openText = TryGetOpenDocumentText(full);

                await TaskScheduler.Default;

                var current = openText ?? (File.Exists(full) ? File.ReadAllText(full) : null);

                // Widen the hunk to a full-file before/after when we can do so unambiguously; otherwise
                // fall back to the reported (possibly hunk-only) text.
                var widened = DiffSideBuilder.TryBuild(current, oldText, newText, reportedLine);
                var (beforeText, afterText) = widened is { } w
                    ? (w.Before, w.After)
                    : (oldText ?? string.Empty, newText ?? string.Empty);

                // When we located the edit in the full file, surface its line in the tab caption — a
                // sanity check that the reconstruction landed where the user expects (the native diff
                // view shows no line-number gutter unless it's globally enabled). If the backend
                // reported a different line (the file shifted since the edit), show both.
                var caption = Branding.ProductName + ": " + name;
                if (widened is { } l)
                    caption += reportedLine is { } r && r != l.Line
                        ? $" (line {l.Line}, edit reported {r})"
                        : $" (line {l.Line})";

                // OpenComparisonWindow2 does NOT dedupe — an identical file pair builds another
                // comparison window on every call. That stayed invisible while every tab was
                // provisional (each new preview takes over the one slot), but once the user promotes a
                // diff to a permanent tab, clicking the same card again stacks a second tab beside it.
                // So re-activate the window we already opened for this exact diff instead.
                //
                // This MUST stay ahead of the writes below, which is also why the thread hops land
                // where they do: on a repeat click the window we're about to reactivate may hold its
                // own backing files open, and a write that failed on the sharing violation would fall
                // into the catch and make the click do nothing at all.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                if (TryReactivateDiff(full, beforeText, afterText))
                    return;

                await TaskScheduler.Default;

                // Named by the WHOLE path, not the leaf. Both sides land in one shared scratch
                // directory and up to sixteen diff windows stay live, matched on path AND content, so
                // a leaf-only name let a turn editing ProjectA\Program.cs and ProjectB\Program.cs
                // rewrite one card's files under the other's open window. The rule and its evidence
                // are on Core.Ide.DiffScratchNames, where they are checkable; matching extensions
                // still give both sides the right syntax highlighting.
                var (beforeName, afterName) = DiffScratchNames.For(full);
                var beforeFile = Path.Combine(DiffScratchDir(), beforeName);
                var afterFile = Path.Combine(DiffScratchDir(), afterName);
                File.WriteAllText(beforeFile, beforeText);
                File.WriteAllText(afterFile, afterText);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

                // Open as a PREVIEW (provisional) tab, like VS's own Git diffs. A diff is a glance
                // artifact — read it, move on — so a turn touching five files shouldn't leave five
                // pinned tabs behind once the user has clicked through the cards. Each new diff takes
                // over the single preview slot instead of accumulating; promoting one to a permanent
                // tab is the standard gesture (double-click the tab, or start editing).
                //
                // __VSDIFFSERVICEOPTIONS carries no preview flag, so this is set as AMBIENT document
                // state around the call rather than passed to it. It degrades to today's behaviour on
                // its own: if the user has preview tabs off (Tools > Options > Environment > Tabs and
                // Windows) or the diff editor doesn't support provisional viewing, the window still
                // opens, just permanently.
                IVsWindowFrame frame;
                using (new NewDocumentStateScope(
                    __VSNEWDOCUMENTSTATE.NDS_Provisional, VSConstants.NewDocumentStateReason.Navigation))
                {
                    frame = _diffService.OpenComparisonWindow2(
                        beforeFile, afterFile,
                        caption, full,
                        "Before", "After (agent)", name, null,
                        (uint)(__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary
                               | __VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary));
                }

                if (frame != null)
                {
                    _openDiffs.Add(new OpenDiff(full, beforeText, afterText, frame));
                    if (_openDiffs.Count > MaxRememberedDiffs)
                        _openDiffs.RemoveAt(0);
                }
            }
            catch (Exception)
            {
                // Preview is best-effort.
            }
        }

        /// <summary>A comparison window we opened, remembered so a repeat click re-activates it.</summary>
        /// <remarks>
        /// Keyed by the diff's *content*, not just the path: a second edit to the same file is a
        /// different diff and legitimately deserves its own window, which also means one file can hold
        /// several live entries — one per promoted tab — each re-activated by its own transcript card.
        /// </remarks>
        private sealed class OpenDiff
        {
            public OpenDiff(string path, string before, string after, IVsWindowFrame frame)
            {
                Path = path;
                Before = before;
                After = after;
                Frame = frame;
            }

            public IVsWindowFrame Frame { get; }

            private string Path { get; }
            private string Before { get; }
            private string After { get; }

            public bool Matches(string path, string before, string after) =>
                string.Equals(Path, path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Before, before, StringComparison.Ordinal)
                && string.Equals(After, after, StringComparison.Ordinal);
        }

        // Entries hold whole-file before/after text, so the list is capped rather than left to grow.
        private const int MaxRememberedDiffs = 16;

        private readonly List<OpenDiff> _openDiffs = new List<OpenDiff>();

        /// <summary>Re-shows the window already open for this exact diff, if there is one and it's alive.</summary>
        private bool TryReactivateDiff(string path, string before, string after)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            for (var i = 0; i < _openDiffs.Count; i++)
            {
                if (!_openDiffs[i].Matches(path, before, after))
                    continue;

                if (IsFrameAlive(_openDiffs[i].Frame) && TryActivate(_openDiffs[i].Frame))
                    return true;

                // The user closed it (or the next preview replaced it) — forget it and open afresh.
                _openDiffs.RemoveAt(i);
                return false;
            }

            return false;
        }

        private static bool IsFrameAlive(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // A closed frame is a dead RCW, so any property read fails — exactly the signal we want.
            // Probe before Show() so a click can never silently do nothing: a failed probe falls
            // through to opening a fresh window.
            try { return ErrorHandler.Succeeded(frame.GetProperty((int)__VSFPROPID.VSFPROPID_Caption, out _)); }
            catch (Exception) { return false; }
        }

        private static bool TryActivate(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Show() activates without promoting, so re-clicking a still-provisional diff keeps it
            // provisional — the tab only becomes permanent when the user says so.
            try { return ErrorHandler.Succeeded(frame.Show()); }
            catch (Exception) { return false; }
        }

        // Scratch dir for the diff viewer's before/after copies — a stable per-user dir, deliberately
        // NOT temp (corporate EDR flags temp activity, and these hold agent-generated source content).
        // Same-named files just overwrite, so the file COUNT is bounded by the set of distinct file
        // stems ever diffed rather than by the number of diffs — which is why this stayed small enough
        // to go unnoticed. It is not bounded over time: that set is the union across every solution,
        // forever, and each file holds a WHOLE side of the diff. Hence SweepDiffScratch.
        //
        // We pass VSDIFFOPT_*FileIsTemporary, which asks VS to delete these when the comparison window
        // closes, and the sweep is deliberately a backstop to it rather than a replacement: whether the
        // reap fires depends on how the window died (issue #88 was filed on a folder holding pairs
        // three weeks old, including ones evicted from the preview slot mid-session). Nothing here
        // depends on which way that goes.
        //
        // Falls back to temp only if LocalAppData is unavailable — see DiffScratchRoot.
        private static string DiffScratchDir()
        {
            var dir = DiffScratchRoot();
            if (dir == null)
                return Path.GetTempPath();

            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch
            {
                return Path.GetTempPath();
            }
        }

        /// <summary>
        /// The scratch dir's path, or null when LocalAppData is unavailable. Never creates it, and
        /// never falls back to temp: this is what the sweep resolves against, and a sweep must only
        /// ever run over a directory we exclusively own. Pointing an age-and-budget delete pass at
        /// <c>%TEMP%</c> would reach the whole machine's temp files.
        /// </summary>
        private static string DiffScratchRoot()
        {
            return StoragePaths.LocalOrNull("diff");
        }

        /// <summary>Files older than this are dropped from the diff scratch dir.</summary>
        /// <remarks>
        /// Matches the log policy's 7 days. It also has to clear the longest a diff window can
        /// plausibly stay open: a promoted (permanent) tab survives until the user closes it, and
        /// deleting a live window's backing file out from under it is the one way this pass could be
        /// user-visible. A week is comfortably past that, and a still-open window's file fails to
        /// delete anyway (TryDelete swallows it and the next pass retries).
        /// </remarks>
        internal const int DiffScratchMaxAgeDays = 7;

        /// <summary>
        /// Total size the diff scratch dir is kept under. Tighter than the log budget: a log is read
        /// by a human debugging a field report, whereas a before/after pair is dead the moment its
        /// window closes — the only reason to keep any is the window that might still be showing one.
        /// </summary>
        internal const long DiffScratchBudgetBytes = 20L * 1024 * 1024;

        /// <summary>
        /// One retention pass over the diff scratch dir, kicked off at applier construction — i.e.
        /// once per chat window, the same point the log directory is swept. Nothing WE opened is
        /// alive yet at that point (<see cref="_openDiffs"/> is empty), and the age cutoff is what
        /// keeps a second devenv's live windows out of range.
        /// </summary>
        /// <remarks>
        /// Deliberately owned by the applier that WRITES these files, rather than moved to the engine
        /// process: the engine has no other knowledge of this directory, and it also runs in hosts
        /// that never write a VS diff at all (Desktop/<c>--fake</c>, the Console host, stub-IDE mode),
        /// so it would be reaping a directory it doesn't own. Keeping the writer and the reaper in one
        /// place is what stops the path drifting between them. The reason to go off-thread is the
        /// UI-thread cost, not ownership — see the constructor.
        /// </remarks>
        internal static void SweepDiffScratch() =>
            // Reported on the same line shape as the log sweeps, so one grep of engine.log covers
            // every scratch directory we reap and a slow one can be told from a slow one elsewhere.
            DiagnosticLog.ReportRetention("diff", RetentionSweep.Run(
                DiffScratchRoot(),
                TimeSpan.FromDays(DiffScratchMaxAgeDays),
                DiffScratchBudgetBytes));

        /// <summary>
        /// Opens the file's editor buffer — in the BACKGROUND, never taking focus — and replaces only
        /// the changed span.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This deliberately does NOT use <c>VsShellUtilities.OpenDocument</c>, which is the obvious
        /// helper and the one this used to call (issue #124). Its last act is an unconditional
        /// <c>windowFrame.Show()</c> — read off the shipped IL, not inferred — so an agent write
        /// yanked the caret out of the chat box and into the editor mid-sentence. There is no argument
        /// to it that suppresses that, and <see cref="NewDocumentStateScope"/> does not either: the
        /// scope is honoured by the OPEN, and the <c>Show()</c> comes after it. So we drive
        /// <see cref="IVsUIShellOpenDocument.OpenDocumentViaProject"/> ourselves — the same call the
        /// helper makes — and reveal the frame with <c>ShowNoActivate</c> instead.
        /// </para>
        /// <para>
        /// The write still goes through the editor rather than to disk, and that is the point of the
        /// buffer route: the change dirties the real buffer, so it joins the undo stack and Ctrl+Z
        /// takes it back. The tab appearing is the price of that, and the user asked only that it not
        /// steal the keyboard — hence a background tab rather than no tab.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Whether the file carries the read-only attribute; null when the question couldn't be
        /// answered. Null rather than false deliberately — see <see cref="FileWriteRefusal.Describe"/>:
        /// "we couldn't tell" must not be reported to the agent as "it is writable".
        /// </summary>
        private static bool? IsMarkedReadOnly(string fullPath)
        {
            try
            {
                return File.Exists(fullPath) && File.GetAttributes(fullPath).HasFlag(FileAttributes.ReadOnly);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// What a write route did. Three outcomes and not two, because <see cref="Unavailable"/> and
        /// <see cref="Refused"/> must lead to opposite places: the first means "this route isn't here,
        /// try the next one", the second means the editor was asked and said no, and routing around a
        /// refusal is precisely what must not happen.
        /// </summary>
        private enum WriteRoute
        {
            /// <summary>This route isn't available (no buffer, no adapters) — try the next.</summary>
            Unavailable,

            /// <summary>The file now holds the requested content.</summary>
            Applied,

            /// <summary>The editor rejected the change. Nothing was written and nothing else should be.</summary>
            Refused,
        }

        /// <summary>
        /// Writes through the live editor buffer, so the change dirties the buffer, joins the undo stack
        /// and flows through source control like a user edit.
        /// </summary>
        /// <remarks>
        /// <b><c>edit.Apply()</c> does not throw when the change is rejected — it returns the UNCHANGED
        /// snapshot.</b> A read-only file (or one needing a source-control checkout) makes the editor
        /// raise its "make writable?" prompt from inside <c>Apply</c>; dismissing that prompt cancels the
        /// edit, <c>Apply</c> returns quietly, and this method used to answer <c>true</c> — so the write
        /// reported SUCCESS to the agent with the file untouched, which is the one outcome AGENTS.md's
        /// tool-result rule forbids outright: a mutation that changed nothing has no answer to return,
        /// only a false impression to correct. Reproduced by the user, 2026-09-03, by marking the target
        /// read-only and clicking Cancel.
        /// <para>
        /// So the result is CONFIRMED rather than assumed, and confirmed against the buffer already in
        /// hand — never by re-reading the file. Re-reading would have to guess its source: the disk copy
        /// is legitimately stale here (the buffer route deliberately leaves the change unsaved when
        /// <see cref="TrySaveDocument"/> can't save it), so a disk comparison would report a successful
        /// edit as a failure and send the agent into a retry loop. <c>Canceled</c> is read as well as the
        /// text compared, because it names the cause for the message where the comparison only knows
        /// that the content differs.
        /// </para>
        /// </remarks>
        private WriteRoute TryWriteThroughBuffer(string fullPath, string content)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_adapters is null || _serviceProvider is null)
                return WriteRoute.Unavailable;

            try
            {
                var vsTextView = OpenInBackground(fullPath);
                if (vsTextView is null)
                    return WriteRoute.Unavailable;

                var buffer = _adapters.GetWpfTextView(vsTextView)?.TextBuffer;
                if (buffer is null)
                    return WriteRoute.Unavailable;

                var oldText = buffer.CurrentSnapshot.GetText();
                if (string.Equals(oldText, content, StringComparison.Ordinal))
                    return WriteRoute.Applied; // no change — don't dirty the buffer

                var (start, length, replacement) = ComputeMinimalEdit(oldText, content);
                bool canceled;
                using (var edit = buffer.CreateEdit())
                {
                    edit.Replace(new Span(start, length), replacement);
                    edit.Apply();
                    canceled = edit.Canceled;
                }

                if (canceled)
                    return WriteRoute.Refused;

                // The backstop for a rejection that doesn't set Canceled: whatever the editor did, the
                // buffer either holds what was asked for or it does not.
                return string.Equals(buffer.CurrentSnapshot.GetText(), content, StringComparison.Ordinal)
                    ? WriteRoute.Applied
                    : WriteRoute.Refused;
            }
            catch (Exception)
            {
                return WriteRoute.Unavailable;
            }
        }

        /// <summary>
        /// Opens the document without activating it and returns its text view (null if the shell gave
        /// us no frame, or no text view — a binary or designer-only editor).
        /// </summary>
        /// <remarks>
        /// The <see cref="NewDocumentStateScope"/> is not redundant with skipping <c>Show()</c>: it
        /// covers a project type whose <c>OpenItem</c> activates the window itself on the way out of
        /// <c>OpenDocumentViaProject</c>, which our choice of reveal cannot reach.
        /// <c>Guid.Empty</c> is <c>LOGVIEWID_Primary</c> — the same logical view the helper was asked
        /// for before, so which editor opens is unchanged.
        /// </remarks>
        private IVsTextView OpenInBackground(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!(_serviceProvider.GetService(typeof(SVsUIShellOpenDocument)) is IVsUIShellOpenDocument openDocument))
                return null;

            var logicalView = Guid.Empty;
            IVsWindowFrame frame;
            // The reason guid is a label the shell attributes the open to, not a behaviour, and it has
            // no neutral member (FindSymbolResults / FindResults / Navigation / SolutionExplorer /
            // TeamExplorer) — Navigation is the nearest true one and is what the diff path already uses.
            using (new NewDocumentStateScope(
                __VSNEWDOCUMENTSTATE.NDS_NoActivate, VSConstants.NewDocumentStateReason.Navigation))
            {
                ErrorHandler.ThrowOnFailure(openDocument.OpenDocumentViaProject(
                    fullPath, ref logicalView, out _, out _, out _, out frame));
            }

            if (frame is null)
                return null;

            // The frame exists but has never been made visible, so the tab appears here — behind
            // whatever the user is looking at. Best-effort: a frame that refuses to show is still a
            // frame we can edit through, and refusing the write over a cosmetic failure would be worse.
            try { frame.ShowNoActivate(); }
            catch (Exception) { /* the edit is what matters, not the tab */ }

            return VsShellUtilities.GetTextView(frame);
        }

        /// <summary>Fallback: open via DTE and replace the whole document through an EditPoint.</summary>
        /// <remarks>
        /// No <c>Activate()</c> here (issue #124) — it was a focus steal, and nothing below needs the
        /// window to be active to reach its <c>TextDocument</c>. DTE's own open may still bring the
        /// window forward; this path only runs when the buffer route above has already failed.
        /// </remarks>
        /// <summary>
        /// The DTE fallback for when the buffer route isn't available. Confirms the result the same way
        /// and for the same reason as <see cref="TryWriteThroughBuffer"/>: <c>ReplaceText</c> can leave
        /// the document untouched without raising anything, and a write that changed nothing must not be
        /// reported as a write.
        /// </summary>
        private WriteRoute TryWriteThroughDte(string fullPath, string content)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var window = _dte.ItemOperations.OpenFile(fullPath, EnvDTE.Constants.vsViewKindTextView);

                if (window?.Document?.Object("TextDocument") is EnvDTE.TextDocument textDocument)
                {
                    var start = textDocument.StartPoint.CreateEditPoint();
                    start.ReplaceText(textDocument.EndPoint, content, (int)EnvDTE.vsEPReplaceTextOptions.vsEPReplaceTextKeepMarkers);

                    var landed = textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
                    return string.Equals(landed, content, StringComparison.Ordinal)
                        ? WriteRoute.Applied
                        : WriteRoute.Refused;
                }
            }
            catch (Exception)
            {
                // No usable text document here — fall through to the disk write, which reports its own
                // failure by throwing. Deliberately Unavailable and not Refused: nothing was asked and
                // nothing said no, so the remaining route is still the right thing to try.
            }

            return WriteRoute.Unavailable;
        }

        /// <summary>
        /// Saves the document just edited so disk matches the editor, scoped to that one file (never a
        /// global Save-All, which would sweep up the user's unrelated edits).
        /// </summary>
        /// <remarks>
        /// The buffer/DTE write paths above land the edit as an UNSAVED buffer. Without this, the change
        /// reached disk only incidentally — at the next build, via VS's "Before building: Save all changes"
        /// option — so until then the agent's own shell (git status/diff, a dotnet run) and every other
        /// on-disk reader saw pre-edit content, and a user who has that option off would silently build and
        /// test stale output. It also makes this path agree with the other two edit routes: Kiro v2 and
        /// Claude Code write disk themselves, and rename_symbol/apply_code_fix save via
        /// VsToolCatalog.SaveChangedDocuments. The editor undo stack survives the save, so the edit stays
        /// undoable with Ctrl+Z (which re-dirties the buffer) for as long as the document is open. The file
        /// always exists on disk by this point (the caller creates it), so the save can never prompt for a
        /// filename. Best-effort per document: a buffer that refuses to save (read-only, transient VS state)
        /// leaves the old behaviour, which the next build still recovers — it must not fail the write.
        /// </remarks>
        private void TrySaveDocument(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (EnvDTE.Document document in _dte.Documents)
            {
                try
                {
                    if (!document.Saved
                        && string.Equals(document.FullName, fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        document.Save();
                        return;
                    }
                }
                catch (Exception)
                {
                    // A document that throws on inspection or save shouldn't stop the scan.
                }
            }
        }

        /// <summary>Returns the (start, length, replacement) of the minimal edit between old and new text.</summary>
        private static (int start, int length, string replacement) ComputeMinimalEdit(string oldText, string newText)
        {
            var max = Math.Min(oldText.Length, newText.Length);

            var prefix = 0;
            while (prefix < max && oldText[prefix] == newText[prefix])
                prefix++;

            var suffix = 0;
            while (suffix < (oldText.Length - prefix)
                   && suffix < (newText.Length - prefix)
                   && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix])
                suffix++;

            var start = prefix;
            var length = oldText.Length - prefix - suffix;
            var replacement = newText.Substring(prefix, newText.Length - prefix - suffix);
            return (start, length, replacement);
        }

        // The agent's cwd first, the solution root second — see SetAgentRootPath for why they differ and
        // what picking wrong costs. AgentPath.Canonical is shared with AcpMapper deliberately: the mapper's
        // answer is the transcript's key and the write capture's, this one decides where the bytes land,
        // and the two disagreeing is how one write became two cards. It also collapses the separators
        // Path.Combine used to leave mixed ("C:\ws\proj/File.cs").
        //
        // A RELATIVE PATH WITH NO ROOT REFUSES RATHER THAN GUESSING (issue #54). There used to be a third
        // fallback here, Directory.GetCurrentDirectory(), which in devenv is VS's own install directory —
        // so a relative path arriving with neither root known would have resolved under Program Files and
        // a write would have CREATED it there, reported clean. It was unreachable in the VS host and is
        // not kept for that: VsIdeServices always has a root (the solution, the open folder, or the
        // default workspace, which it creates), so the fallback could only ever have fired for a host
        // that has neither — and for that host the honest answer is the one #54 settled, that resolving
        // against the wrong root finds a DIFFERENT REAL FILE rather than failing.
        private string Resolve(string path)
        {
            var root = AgentPath.PreferredRoot(_agentRootPath, _rootPath);
            if (root is null && !AgentPath.IsRooted(path))
                throw new InvalidOperationException(
                    $"Cannot resolve the relative path '{path}': no agent working directory and no solution "
                    + "root are known, so there is nothing to measure it from. The file is unchanged.");

            return AgentPath.Canonical(path, root);
        }

        /// <summary>
        /// The live editor text for <paramref name="fullPath"/>, or null when there is no readable open
        /// document for it — which is the contract all four callers already rely on, every one of them
        /// treating null as "nothing open here, use the disk".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every failure here answers null and never throws, and that is the whole point (issue
        /// #189).</b> <c>TextDocument.StartPoint</c> is automation's text-point machinery, bound to a
        /// realised text view; a document sitting open in the background under Miscellaneous Files on a
        /// fallback editor — what a path outside the solution with no registered editor factory gets —
        /// answers a bare <c>E_FAIL</c> from it. That <c>COMException</c> used to escape into
        /// <see cref="ReadTextFileAsync"/> and <see cref="WriteTextFileAsync"/> and fail the whole
        /// operation, though this is only the PREFERRED source and both callers carry a complete disk
        /// fallback behind it. The write in particular had three working routes to land the bytes
        /// (buffer, DTE, <c>File.WriteAllText</c> — all three of which catch and fall through) and
        /// reached none of them, because an OPTIONAL capture threw.
        /// </para>
        /// <para>
        /// <b>The asymmetry was the tell.</b> The two DISPLAY callers (<c>ResolveDiffLineAsync</c>,
        /// <c>ShowDiffPreviewAsync</c>) each wrap this in a catch and degrade; the two AGENT-FACING ones
        /// — the pair that decides whether the user's file changes — were the only two without one.
        /// Guarding inside the method rather than at each site is what stops the next caller getting it
        /// wrong, and it is what the <c>Try</c> in the name already promised.
        /// </para>
        /// <para>
        /// <b>The fallback is logged, because it is the one case where our answer is not the best one
        /// available.</b> Reading disk for a document that is open and DIRTY reads stale. That is
        /// strictly better than the total failure it replaces, and far better than what the throw could
        /// produce on the half where only the READ fails and the write then succeeds: an agent that
        /// cannot read a file concludes it is empty and writes a whole reconstruction over it — the
        /// exact client-fs shape AGENTS.md warns about, here landing on an unsaved buffer.
        /// </para>
        /// <para>
        /// The per-document catch is the second half, matching <see cref="TrySaveDocument"/>'s loop: one
        /// document that throws on <c>FullName</c> or <c>Object("TextDocument")</c> must not blind the
        /// scan before it reaches the right one.
        /// </para>
        /// </remarks>
        private string TryGetOpenDocumentText(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (EnvDTE.Document document in _dte.Documents)
            {
                try
                {
                    if (string.Equals(document.FullName, fullPath, StringComparison.OrdinalIgnoreCase)
                        && document.Object("TextDocument") is EnvDTE.TextDocument textDocument)
                    {
                        return textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
                    }
                }
                catch (Exception ex)
                {
                    // Keep scanning rather than returning: the document that threw may not even be the
                    // one asked for (FullName itself can fail), so a later one can still be the match.
                    ToolErrorLog.Write("open document text, falling back to disk: " + fullPath, ex);
                }
            }

            return null;
        }
    }
}
