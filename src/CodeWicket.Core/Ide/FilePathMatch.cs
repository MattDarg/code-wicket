using System;
using System.Collections.Generic;
using System.IO;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// The one rule for turning an agent-written file path into a file the IDE actually knows about, and
    /// for deciding when that path is too vague to act on.
    /// <para>
    /// It lives in Core, away from the VS-bound project, for the reason <see cref="LineEndings"/> and
    /// <see cref="DiagnosticDeduper"/> do: the decision is pure string work and is unit-testable without
    /// devenv, while the collection that drives it — enumerating a Roslyn solution's documents — is the
    /// VS-coupled part and stays there.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Why one rule and not two.</b> The tools used to disagree. <c>get_diagnostics</c> accepted a
    /// relative file filter by path suffix while <c>apply_code_fix</c> compared for equality against
    /// Roslyn's <c>Document.FilePath</c>, which is always absolute — so an agent could FIND a diagnostic
    /// at a relative path and then be refused when it tried to FIX one at that same path (measured live
    /// 2026-08-04, twice, before the agent worked out to pass an absolute path). They were reconciled by
    /// writing "deliberately the same rule" in a comment on each. This type is that comment made
    /// structural: both call in here, so they cannot drift apart again without the tests noticing.
    /// </remarks>
    public static class FilePathMatch
    {
        /// <summary>
        /// Whether <paramref name="candidatePath"/> satisfies <paramref name="requestedPath"/>: exactly,
        /// or — when the request is relative — as a path SUFFIX.
        /// </summary>
        /// <remarks>
        /// The suffix must begin at a separator, which is the whole reason it is prepended rather than a
        /// bare <c>EndsWith</c>: <c>MyProgram.cs</c> must not satisfy a request for <c>Program.cs</c>.
        /// A request that is itself absolute is matched by equality only — an absolute path that names
        /// nothing is wrong, not under-specified.
        /// </remarks>
        public static bool Matches(string? candidatePath, string? requestedPath)
        {
            if (string.IsNullOrEmpty(candidatePath) || string.IsNullOrEmpty(requestedPath))
                return false;

            var candidate = Normalize(candidatePath!);
            var requested = Normalize(requestedPath!).TrimStart('\\');

            if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase))
                return true;

            return !IsRooted(requested)
                && candidate.EndsWith("\\" + requested, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves <paramref name="requestedPath"/> against the paths the IDE knows about, in descending
        /// order of precision: exactly as written, then combined with <paramref name="rootDirectory"/>,
        /// then as a path suffix — which must identify exactly ONE file to count.
        /// </summary>
        /// <param name="requestedPath">The path as the agent wrote it: absolute, or relative to <paramref name="rootDirectory"/>.</param>
        /// <param name="candidatePaths">
        /// Every path the caller is willing to resolve to. Duplicates are expected and are NOT ambiguity:
        /// a multi-targeted project surfaces one file once per target framework, and those are the same
        /// file. Ambiguity is counted over DISTINCT paths for exactly that reason.
        /// </param>
        /// <param name="rootDirectory">
        /// What a relative path is combined against (the solution folder). Null/empty simply skips that
        /// step — an "Open Folder" workspace has no solution file, and the suffix pass answers there.
        /// </param>
        /// <remarks>
        /// Ambiguity is reported rather than resolved, and reported rather than merely refused. A caller
        /// that mutates (<c>apply_code_fix</c>) must refuse it, because a fix applied to the wrong file is
        /// not recoverable; a caller that reads (<c>get_diagnostics</c>) may proceed and say so. Both need
        /// to know WHICH files, and the earlier version of this code computed that and then discarded it,
        /// reporting "no document open in the solution for 'Program.cs'" when two had been found — the
        /// opposite problem, and it sends the caller looking for a file that is right there.
        /// </remarks>
        public static PathResolution Resolve(
            IEnumerable<string?>? candidatePaths, string? requestedPath, string? rootDirectory)
        {
            if (candidatePaths is null || string.IsNullOrEmpty(requestedPath))
                return PathResolution.None;

            var requested = Normalize(requestedPath!);

            // Materialized once: the passes below each need the full set, and the caller's sequence may be
            // an expensive enumeration over a Roslyn solution.
            var candidates = new List<string>();
            foreach (var path in candidatePaths)
            {
                if (!string.IsNullOrEmpty(path))
                    candidates.Add(path!);
            }

            if (FirstExact(candidates, requested) is { } exact)
                return PathResolution.Found(exact);

            if (!IsRooted(requested) && !string.IsNullOrEmpty(rootDirectory))
            {
                string? combined = null;
                try { combined = Normalize(Path.GetFullPath(Path.Combine(rootDirectory!, requested))); }
                catch { /* an unusable path is simply not a match */ }

                if (combined is not null && FirstExact(candidates, combined) is { } rooted)
                    return PathResolution.Found(rooted);
            }

            if (IsRooted(requested))
                return PathResolution.None;

            var suffix = "\\" + requested.TrimStart('\\');
            var matched = new List<string>();
            foreach (var candidate in candidates)
            {
                if (!Normalize(candidate).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ContainsPath(matched, candidate))
                    matched.Add(candidate);
            }

            return matched.Count switch
            {
                0 => PathResolution.None,
                1 => PathResolution.Found(matched[0]),
                _ => PathResolution.Ambiguous(matched),
            };
        }

        private static string? FirstExact(List<string> candidates, string normalizedRequest)
        {
            foreach (var candidate in candidates)
            {
                if (string.Equals(Normalize(candidate), normalizedRequest, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        private static bool ContainsPath(List<string> paths, string path)
        {
            foreach (var existing in paths)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Separators are unified to <c>\</c> rather than to <see cref="Path.DirectorySeparatorChar"/>:
        /// these paths come off a Windows IDE and the comparison must be stable regardless of which
        /// spelling the agent used, which is the shape the tools were already normalizing to.
        /// </summary>
        private static string Normalize(string path) => path.Replace('/', '\\');

        /// <summary>
        /// Rooted-ness is decided on the normalized text, not by <c>Path.IsPathRooted</c> alone,
        /// so a path written with forward slashes reads the same as its backslash twin.
        /// </summary>
        private static bool IsRooted(string normalizedPath)
        {
            try { return Path.IsPathRooted(normalizedPath); }
            catch { return false; } // an unrepresentable path is not a rooted one
        }
    }

    /// <summary>The outcome of <see cref="FilePathMatch.Resolve"/>: one file, several, or none.</summary>
    public sealed class PathResolution
    {
        private static readonly string[] NoPaths = new string[0];

        internal static readonly PathResolution None = new PathResolution(null, NoPaths);

        private PathResolution(string? path, IReadOnlyList<string> ambiguousMatches)
        {
            Path = path;
            AmbiguousMatches = ambiguousMatches;
        }

        internal static PathResolution Found(string path) => new PathResolution(path, NoPaths);

        internal static PathResolution Ambiguous(IReadOnlyList<string> matches) => new PathResolution(null, matches);

        /// <summary>The single matching path, or null when there was none or several.</summary>
        public string? Path { get; }

        /// <summary>
        /// The distinct paths a relative request matched when it matched more than one; empty otherwise.
        /// Non-empty means the request was too vague to act on, NOT that the file is missing — the two
        /// call for opposite messages.
        /// </summary>
        public IReadOnlyList<string> AmbiguousMatches { get; }

        /// <summary>True when exactly one file matched.</summary>
        public bool IsMatch => Path is not null;

        /// <summary>True when several distinct files matched and the caller must disambiguate.</summary>
        public bool IsAmbiguous => AmbiguousMatches.Count > 0;
    }
}
