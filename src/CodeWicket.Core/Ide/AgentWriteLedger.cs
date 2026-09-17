using System;
using System.Collections.Generic;
using System.Linq;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// The files the agent has written in this process, by canonical path — and when each loaded
    /// project last reloaded, so a write can be told from a reload that came after it (issue #257).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a ledger and not a file watcher.</b> Every write an agent makes through a file tool
    /// already reaches the host: as the mirrored diff of the tool call on the backends that write for
    /// themselves, and as our own client-fs write on the ones that route through us. Measured on the
    /// wire for both (issue #257, 2026-09-12). That stream has one property a watcher on the project
    /// file cannot have: it NEVER contains Visual Studio's own saves of the project file, which a bare
    /// <c>IVsFileChangeEx</c> advise would report on every class added through Solution Explorer.
    /// What the stream does not carry is a file created by a shell command — the path is inside a
    /// command line nobody parses — and that gap is accepted here, not papered over.
    /// </para>
    /// <para>
    /// <b>What it is for.</b> A loaded project that does not contain a file the agent wrote is a
    /// silent-green build: the file is never compiled, every instrument agrees the solution is clean,
    /// and the agent then invents a cause. The ledger supplies the candidates the IDE side checks
    /// against the loaded projects, and — for a project file or an import — the fact that the agent
    /// changed it, since a non-SDK project takes neither in place. See <see cref="ProjectStaleness"/>
    /// for what is then said.
    /// </para>
    /// <para>
    /// <b>No expiry.</b> Measured in Visual Studio (#257, 2026-09-12): a stale legacy project shows no decay across
    /// four builds, a full rebuild and a 30 s idle. Nothing clears an entry but the events that mean
    /// the condition has ended — the project reloading, the solution closing — and the caller's own
    /// check that a source file has become a member. A clock here would turn a true fact into a false
    /// absence on a schedule.
    /// </para>
    /// <para>
    /// <b>Reloads are recorded, not only applied.</b> A project's reload closes the record of ITS
    /// project file outright. An import is different: one <c>Directory.Build.props</c> sits above many
    /// projects, and a reload of one of them takes the import for that one and no other — so the
    /// Exercising the fix in Visual Studio found the import note still naming a project that had demonstrably reloaded.
    /// Hence <see cref="NoteReloaded"/> and <see cref="WasReloadedSince"/>: the write and the reload
    /// are ordered against each other, per project, by a monotonic sequence rather than a clock, so
    /// two events in the same tick cannot come out unordered.
    /// </para>
    /// <para>
    /// Paths are stored canonical (<see cref="AgentPath.Canonical(string, string?)"/>) so the two
    /// spellings one file arrives in — Kiro v3's <c>file:///c%3A/…</c> URI on the completed frame,
    /// the Windows path on our own write — are one entry. <b>A relative path is refused</b>, never
    /// rooted here: the shell does not know the agent's cwd, and rooting against anything else names
    /// a different file (issue #54).
    /// </para>
    /// </remarks>
    public sealed class AgentWriteLedger
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, long> _writes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _reloads = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private long _sequence;

        /// <summary>
        /// Records a write to <paramref name="path"/>. A null, empty or unrooted path is ignored.
        /// Returns whether anything was recorded.
        /// </summary>
        public bool Record(string? path)
        {
            var canonical = Canonicalize(path);
            if (canonical is null)
                return false;
            lock (_gate)
                _writes[canonical] = ++_sequence;
            return true;
        }

        /// <summary>Drops the entry for <paramref name="path"/>, if any. Returns whether one existed.</summary>
        public bool Forget(string? path)
        {
            var canonical = Canonicalize(path);
            if (canonical is null)
                return false;
            lock (_gate)
                return _writes.Remove(canonical);
        }

        /// <summary>
        /// Records that the project at <paramref name="projectFile"/> (re)loaded now — after every
        /// write recorded so far, before every write recorded later.
        /// </summary>
        public bool NoteReloaded(string? projectFile)
        {
            var canonical = Canonicalize(projectFile);
            if (canonical is null)
                return false;
            lock (_gate)
                _reloads[canonical] = ++_sequence;
            return true;
        }

        /// <summary>
        /// Whether the project at <paramref name="projectFile"/> has reloaded since the write recorded
        /// for <paramref name="writtenPath"/>. False when either is unknown: a reload nobody saw is not
        /// a reload, and a write nobody recorded has nothing to be after.
        /// </summary>
        public bool WasReloadedSince(string? projectFile, string? writtenPath)
        {
            var project = Canonicalize(projectFile);
            var written = Canonicalize(writtenPath);
            if (project is null || written is null)
                return false;
            lock (_gate)
            {
                return _writes.TryGetValue(written, out var wrote)
                    && _reloads.TryGetValue(project, out var reloaded)
                    && reloaded > wrote;
            }
        }

        /// <summary>Drops every entry — the solution closed, so no loaded project can be stale against them.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _writes.Clear();
                _reloads.Clear();
            }
        }

        /// <summary>Whether <paramref name="path"/> has been recorded and not since forgotten.</summary>
        public bool Contains(string? path)
        {
            var canonical = Canonicalize(path);
            if (canonical is null)
                return false;
            lock (_gate)
                return _writes.ContainsKey(canonical);
        }

        /// <summary>The recorded paths, oldest write first. A copy: safe to walk while writes continue.</summary>
        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
                return _writes.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        }

        /// <summary>The number of recorded paths.</summary>
        public int Count
        {
            get { lock (_gate) return _writes.Count; }
        }

        private static string? Canonicalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var canonical = AgentPath.Canonical(path!);
            return AgentPath.IsRooted(canonical) ? canonical : null;
        }
    }
}
