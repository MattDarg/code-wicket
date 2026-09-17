using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeWicket.Core.Ide
{
    /// <summary>Which of the three stale-project shapes a note describes (issue #257).</summary>
    public enum ProjectStalenessKind
    {
        /// <summary>A source file the agent wrote is in no loaded project, and the project file on disk does not name it either.</summary>
        NotInProject,

        /// <summary>The project file on disk names the file (or was itself changed), but the copy Visual Studio has loaded predates that.</summary>
        ProjectNotReloaded,

        /// <summary>An import (<c>Directory.Build.props</c>, a <c>.props</c>/<c>.targets</c>) was changed and non-SDK projects under it will not take it until reloaded.</summary>
        ImportNotReloaded,
    }

    /// <summary>One thing the last build did not do that the agent would otherwise assume it did.</summary>
    public sealed class ProjectStalenessNote
    {
        public ProjectStalenessNote(ProjectStalenessKind kind, string file, string? project, string message)
        {
            Kind = kind;
            File = file;
            Project = project;
            Message = message;
        }

        public ProjectStalenessKind Kind { get; }

        /// <summary>The file the note is about: the orphaned source, the project file, or the import.</summary>
        public string File { get; }

        /// <summary>The loaded project the note is about, where there is one.</summary>
        public string? Project { get; }

        /// <summary>The sentence the agent reads. The wording is the behaviour; tests assert it.</summary>
        public string Message { get; }
    }

    /// <summary>A source file the agent wrote that the loaded projects do not contain.</summary>
    public sealed class OrphanedSourceFile
    {
        public OrphanedSourceFile(
            string path, string? nearestProjectFile, ProjectStyle? nearestProjectStyle,
            bool listedInProjectFileOnDisk, bool projectFileWrittenAndNotReloaded,
            bool compiledThisBuild = false)
        {
            Path = path;
            NearestProjectFile = nearestProjectFile;
            NearestProjectStyle = nearestProjectStyle;
            ListedInProjectFileOnDisk = listedInProjectFileOnDisk;
            ProjectFileWrittenAndNotReloaded = projectFileWrittenAndNotReloaded;
            CompiledThisBuild = compiledThisBuild;
        }

        /// <summary>
        /// Whether THIS build reported a diagnostic in the file — proof it was compiled this once, even
        /// though the loaded project does not contain it. The red phase of the oscillation: a rebuild
        /// evaluates the project from disk and compiles the file, while the loaded copy still excludes
        /// it, so the next incremental build is green again. Without this the note said "was not
        /// compiled" beside an <c>errors[]</c> entry for the very file (exercising the fix in Visual Studio, 2026-09-12).
        /// </summary>
        public bool CompiledThisBuild { get; }

        /// <summary>The file, canonical.</summary>
        public string Path { get; }

        /// <summary>The loaded project whose directory contains the file (the deepest one), or null when none does.</summary>
        public string? NearestProjectFile { get; }

        /// <summary>That project's dialect, or null when it could not be read.</summary>
        public ProjectStyle? NearestProjectStyle { get; }

        /// <summary>Whether that project's file ON DISK names this file (<see cref="ProjectFileText.NamesFile"/>).</summary>
        public bool ListedInProjectFileOnDisk { get; }

        /// <summary>Whether the agent wrote that project's file in this conversation and it has not reloaded since.</summary>
        public bool ProjectFileWrittenAndNotReloaded { get; }
    }

    /// <summary>A non-SDK project whose project file the agent changed and which has not reloaded since.</summary>
    public sealed class StaleProjectFile
    {
        public StaleProjectFile(string path)
        {
            Path = path;
        }

        public string Path { get; }
    }

    /// <summary>An import the agent changed, and the non-SDK projects under it that will not take the change until reloaded.</summary>
    public sealed class StaleImportFile
    {
        public StaleImportFile(string path, IReadOnlyList<string> legacyProjectFiles)
        {
            Path = path;
            LegacyProjectFiles = legacyProjectFiles;
        }

        public string Path { get; }

        public IReadOnlyList<string> LegacyProjectFiles { get; }
    }

    /// <summary>
    /// What the agent is told when a build compiled something other than the files it wrote (issue #257).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure is a silent green.</b> Both backends write files themselves, and a non-SDK
    /// project enumerates its sources explicitly, so a new file lands on disk in no project — the build
    /// is green and the code does not exist. Where the agent also edits the project file, the loaded
    /// copy predates the edit and Visual Studio defers the reload prompt until the IDE is next
    /// activated; every build until then compiles the OLD file list. And a changed import is taken by
    /// a loaded non-SDK project only on reload, with no prompt at all. All three measured live
    /// (#257 rounds 1–2 in Visual Studio and the Kiro leg, 2026-09-12), on both backends.
    /// </para>
    /// <para>
    /// <b>The wording carries three facts the agent cannot get anywhere else</b>, and each was chosen
    /// against a wrong move it was observed to make or would plausibly make. (1) The file was NOT
    /// compiled, so a green build says nothing about it and its errors cannot be reported. (2) What a
    /// REBUILD does: on a stale project it surfaces the file as a compile error and repairs nothing —
    /// the next incremental build is green again with the file still missing (measured: green ×4,
    /// rebuild red, build green). Left unsaid, that oscillation reads as "transient, fixed itself",
    /// the #250 shape. (3) What actually ends it — a reload the USER performs — so the agent neither
    /// rewrites the file nor edits the project file a second time.
    /// </para>
    /// <para>
    /// <b>Every sentence states what was CHECKED and hedges nothing</b> (the <see cref="FileWriteRefusal"/>
    /// rule): the loaded project was asked and said no; the project file on disk was read and does or
    /// does not name the file; the project's dialect was read. Where a fact could not be established the
    /// sentence for it is not printed rather than guessed at.
    /// </para>
    /// <para>
    /// Pure and in Core so the wording is testable — the facts are gathered by the VS-bound catalog,
    /// which no test project can reference. The IDE side owns three things this deliberately does not:
    /// WHICH files are candidates (the <see cref="AgentWriteLedger"/>, filtered to
    /// <see cref="IsCompiledSource"/>), the membership answer (the loaded hierarchy), and the
    /// dialect read. Restricting candidates to compiled-source extensions is what keeps a written
    /// README or JSON file — in no project, correctly — from producing a note.
    /// </para>
    /// </remarks>
    public static class ProjectStaleness
    {
        /// <summary>
        /// Extensions a project compiles and therefore must CONTAIN for the file to exist to the build.
        /// Chosen for the project systems where membership is explicit: C#/VB (legacy), F# (SDK-style
        /// but always explicit), C++ (always explicit); XAML and RESX are items in a legacy project too.
        /// A file with any other extension is never reported, whatever project it is in.
        /// </summary>
        private static readonly HashSet<string> CompiledSourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs", ".vb", ".fs", ".fsi", ".cpp", ".cc", ".cxx", ".c", ".h", ".hpp", ".hxx", ".inl", ".xaml", ".resx",
        };

        /// <summary>Whether <paramref name="path"/> is a source file a project would have to list to compile.</summary>
        public static bool IsCompiledSource(string? path) =>
            !string.IsNullOrEmpty(path) && CompiledSourceExtensions.Contains(SafeExtension(path!));

        /// <summary>
        /// Whether <paramref name="path"/> is an MSBuild project file: any extension ending in
        /// <c>proj</c>, so <c>.csproj</c>, <c>.vbproj</c>, <c>.fsproj</c>, <c>.vcxproj</c>, <c>.sqlproj</c>
        /// and the rest are one rule rather than a list that a new project type falls outside of.
        /// </summary>
        public static bool IsProjectFile(string? path) =>
            !string.IsNullOrEmpty(path) && SafeExtension(path!).EndsWith("proj", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether <paramref name="path"/> is an MSBuild import (<c>.props</c> or <c>.targets</c>).</summary>
        public static bool IsImportFile(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            var ext = SafeExtension(path!);
            return ext.Equals(".props", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".targets", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The notes for one build, in the order the agent should read them: orphaned sources first
        /// (the file it just wrote), then project files, then imports. Empty when there is nothing to say.
        /// </summary>
        public static IReadOnlyList<ProjectStalenessNote> Describe(
            IReadOnlyList<OrphanedSourceFile>? orphans,
            IReadOnlyList<StaleProjectFile>? staleProjectFiles,
            IReadOnlyList<StaleImportFile>? staleImports)
        {
            var notes = new List<ProjectStalenessNote>();

            foreach (var orphan in orphans ?? Array.Empty<OrphanedSourceFile>())
                notes.Add(DescribeOrphan(orphan));

            // A project file the agent changed whose loaded copy is stale is worth saying even when no
            // orphan hangs off it (a reference or property change) — but not twice: an orphan above has
            // already said it for its own project.
            var projectsAlreadyNamed = new HashSet<string>(
                (orphans ?? Array.Empty<OrphanedSourceFile>())
                    .Where(o => o.ListedInProjectFileOnDisk && o.NearestProjectFile is not null)
                    .Select(o => o.NearestProjectFile!),
                StringComparer.OrdinalIgnoreCase);
            foreach (var stale in staleProjectFiles ?? Array.Empty<StaleProjectFile>())
            {
                if (projectsAlreadyNamed.Contains(stale.Path))
                    continue;
                notes.Add(new ProjectStalenessNote(
                    ProjectStalenessKind.ProjectNotReloaded, stale.Path, stale.Path,
                    $"{Name(stale.Path)} was changed in this conversation and the loaded project has not been reloaded "
                    + "since, so this build used the previous copy of the project file. A rebuild will not take the "
                    + "change either; only reloading the project does, which Visual Studio asks the user to do when "
                    + "they next activate the IDE. Do not edit the project file again to compensate."));
            }

            foreach (var import in staleImports ?? Array.Empty<StaleImportFile>())
            {
                var projects = string.Join(", ", import.LegacyProjectFiles.Select(Name));
                notes.Add(new ProjectStalenessNote(
                    ProjectStalenessKind.ImportNotReloaded, import.Path, null,
                    $"{Name(import.Path)} was changed in this conversation. {projects} "
                    + (import.LegacyProjectFiles.Count == 1 ? "is not an SDK-style project and does " : "are not SDK-style projects and do ")
                    + "not take a changed import until reloaded — Visual Studio does not prompt for this — so this build "
                    + "used the previous values. A rebuild will not change this; the user must unload and reload the "
                    + (import.LegacyProjectFiles.Count == 1 ? "project" : "projects") + ", or reopen the solution."));
            }

            return notes;
        }

        /// <summary>The notes' messages as one string for a payload's <c>note</c>, or null when there are none.</summary>
        public static string? Summary(IReadOnlyList<ProjectStalenessNote>? notes) =>
            notes is null || notes.Count == 0 ? null : string.Join(" ", notes.Select(n => n.Message));

        private static ProjectStalenessNote DescribeOrphan(OrphanedSourceFile orphan)
        {
            var file = Name(orphan.Path);

            // The red phase: this build's own diagnostics name the file, so "was not compiled" would
            // contradict the payload it rides on — the one defect this whole type exists to prevent.
            // The fact worth stating is the other half: the loaded project STILL excludes it, so the
            // next incremental build drops it again, and the reload is still the only thing that ends it.
            if (orphan.CompiledThisBuild)
            {
                var project = orphan.NearestProjectFile is null ? null : Name(orphan.NearestProjectFile);
                var where = project is null
                    ? "no loaded project contains it"
                    : $"the copy of {project} Visual Studio has loaded still does not contain it";
                return new ProjectStalenessNote(
                    orphan.ListedInProjectFileOnDisk ? ProjectStalenessKind.ProjectNotReloaded : ProjectStalenessKind.NotInProject,
                    orphan.Path, orphan.NearestProjectFile,
                    $"{file} WAS compiled by this build — its diagnostics are in this result — but {where}, so "
                    + "the next incremental build will exclude it again and report green with the file missing. "
                    + "This is the loaded project being re-read from disk for one build, not a fix. Only reloading "
                    + "the project ends it, which Visual Studio asks the user to do when they next activate the IDE. "
                    + "Fix the errors this result reports; do not rewrite the file or edit the project file again.");
            }

            if (orphan.NearestProjectFile is not null && orphan.ListedInProjectFileOnDisk)
            {
                var project = Name(orphan.NearestProjectFile);
                var when = orphan.ProjectFileWrittenAndNotReloaded
                    ? $"{project} on disk lists it — that edit was made in this conversation — but the copy of the project "
                      + "Visual Studio has loaded predates the edit"
                    : $"{project} on disk lists it, but the copy of the project Visual Studio has loaded does not";
                return new ProjectStalenessNote(
                    ProjectStalenessKind.ProjectNotReloaded, orphan.Path, orphan.NearestProjectFile,
                    $"{file} was not compiled: {when}, so this build used the previous file list. A rebuild would "
                    + "surface the file as a compile error and repair nothing — the next build would be green again "
                    + "with the file still missing. Only reloading the project ends this, which Visual Studio asks the "
                    + "user to do when they next activate the IDE. Do not rewrite the file or edit the project file again.");
            }

            if (orphan.NearestProjectFile is null)
                return new ProjectStalenessNote(
                    ProjectStalenessKind.NotInProject, orphan.Path, null,
                    $"{file} is under no loaded project's directory, so it was not compiled and its errors cannot be "
                    + "reported; the build's result says nothing about it. A rebuild will not change this. Check the "
                    + "path is where the intended project's sources live.");

            var nearest = Name(orphan.NearestProjectFile);
            var how = orphan.NearestProjectStyle switch
            {
                ProjectStyle.Legacy =>
                    $"{nearest} lists its source files explicitly (it is not an SDK-style project), so a new file "
                    + $"must be added to {nearest} as an item before it exists to the build.",
                ProjectStyle.Sdk =>
                    $"{nearest} includes source files by wildcard, so check that the file is under the project's "
                    + "directory and is not excluded by the project file.",
                _ =>
                    $"{nearest} is the nearest loaded project; its project file could not be read to say how it "
                    + "includes sources.",
            };
            return new ProjectStalenessNote(
                ProjectStalenessKind.NotInProject, orphan.Path, orphan.NearestProjectFile,
                $"{file} is in no loaded project, so it was not compiled and its errors cannot be reported; the "
                + $"build's result says nothing about it. {how} A rebuild will not change this.");
        }

        private static string Name(string path)
        {
            try { return Path.GetFileName(path); }
            catch (ArgumentException) { return path; }
        }

        private static string SafeExtension(string path)
        {
            try { return Path.GetExtension(path) ?? string.Empty; }
            catch (ArgumentException) { return string.Empty; }
        }
    }
}
