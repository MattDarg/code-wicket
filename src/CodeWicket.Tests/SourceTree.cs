using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The shipping source tree, as one definition. Every scan that guards a convention across the
    /// solution reads it from here.
    /// </summary>
    /// <remarks>
    /// This is shared rather than copied because the enumeration IS the scope of every guard built on
    /// it: two copies drift, one gains a file extension the other does not, and a scan quietly stops
    /// covering the files it was written for while still passing. Nothing announces that.
    /// </remarks>
    internal static class SourceTree
    {
        /// <summary>
        /// Locates <c>src/</c> by walking up from the test binary until a known-live source file is
        /// found, so the scans work from a build output at any depth.
        /// </summary>
        public static DirectoryInfo Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "src", "CodeWicket.Core", "Branding.cs");
                if (File.Exists(candidate))
                    return new DirectoryInfo(Path.Combine(dir.FullName, "src"));
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Could not locate src/ above " + AppContext.BaseDirectory);
        }

        /// <summary>
        /// The repository root — the directory holding <c>src/</c>. Separate from <see cref="Root"/>
        /// because a guard over PROSE (the steering doc, the area docs) needs the level above the one
        /// every code guard reads, and deriving it here keeps the upward walk in one place.
        /// </summary>
        public static DirectoryInfo RepoRoot()
        {
            return Root().Parent
                ?? throw new DirectoryNotFoundException("src/ has no parent directory");
        }

        /// <summary>
        /// Every text file that goes into the public repository: the files git TRACKS, less
        /// <c>.claude/</c>, which is the maintainer's own working material and is excluded from the
        /// import by pathspec.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="Files"/>, and deliberately wider. That enumeration is scoped to
        /// <c>src/</c> because the guards built on it enforce conventions about CODE, and prose has no
        /// stake in them. A LEAK scan has the opposite scope: it is not about code at all, it is about
        /// what a stranger can read once the repository is public, so the only files it may skip are
        /// the ones that never ship.
        /// <para>
        /// The distinction is not academic — a real user path reached <c>docs/engineering/</c> and was
        /// caught by hand, because every automated scan was pointed at <c>src/</c>.
        /// </para>
        /// <para>
        /// <b>TRACKED, not "on disk", because the import is <c>git archive</c>.</b> This used to walk
        /// the filesystem behind a skip list of <c>.claude</c>, <c>.git</c>, <c>bin</c> and
        /// <c>obj</c>, which is not what ships: a working copy also holds ignored local material —
        /// <c>.vs/</c>, scratch output, a security scan's own report folder — and every one of those
        /// carries the developer's real paths, by construction. Scanning them turns the leak guard
        /// red over files that will never be published, i.e. a failure the reader cannot fix by
        /// fixing the repository, which is how a guard gets switched off rather than heeded. Asking
        /// git also ends the skip list as a category: a new ignored folder needs no entry here, and
        /// the enumeration cannot silently drift from the thing it claims to describe.
        /// </para>
        /// <para>
        /// <b>Git also decides what is TEXT, not a list of extensions.</b> This used to keep sixteen
        /// extensions, and every guard built on it silently never opened the rest of the published
        /// text: the XAML views and theme, the site's stylesheet, script and config, the favicon SVG,
        /// <c>LICENSE</c>, <c>NOTICE</c> and the extensionless dotfiles. That was not hypothetical — a
        /// security-review citation in <c>ChatView.xaml</c>, a <c>.claude/</c> path in the site
        /// stylesheet and a pull-request citation in <c>.gitattributes</c> all sat there, caught only by
        /// hand, and any of them could have come back unseen. <c>git ls-files --eol</c> reports git's own
        /// content-based verdict from the index, <c>i/-text</c> for a file it judges binary, so a new
        /// file type is covered the day it is added and nothing has to be declared. It is not
        /// <c>git check-attr binary</c>, which only knows what <c>.gitattributes</c> says: an undeclared
        /// binary type reads as "unspecified" there and would be opened as text.
        /// </para>
        /// <para>
        /// <b>It THROWS rather than falling back to the walk.</b> A fallback would be invisible —
        /// the scan would keep passing while measuring something else entirely — and an instrument
        /// that cannot say whether it looked is worse than one that stops.
        /// </para>
        /// <para>
        /// <b>What asking git costs: a file nobody has staged yet is invisible to every scan built on
        /// this.</b> So the run in the session that WRITES a leak is green, and the run after the
        /// commit is red. The guard still fires before anything is published, which is what it is for; what it cannot
        /// do is tell an author about a file they have only just created. A green run over a new file
        /// is not evidence about that file until it is tracked.
        /// </para>
        /// <para>
        /// <b>A guard built on this enumeration makes its own INJECTION SCRIPT a member of the set it
        /// guards</b>. A script that quotes the leak it injects, or spells out the citation it bans, turns
        /// the suite red the moment it is staged — green in the session that wrote it, per the paragraph
        /// above. An injection script must ASSEMBLE the offending
        /// string from parts rather than spell it, or describe it without quoting it. The same applies to
        /// a guard's own negative cases, which is why each of those files exempts them line by line
        /// rather than exempting itself: a file-level allowlist would be a rubber stamp on exactly the
        /// lines a future change is about.
        /// </para>
        /// <para>
        /// <b>Paths are matched BELOW the repository root, never as full paths.</b> A worktree
        /// checked out under <c>.claude/worktrees/</c> has <c>\.claude\</c> in every full path, so a
        /// full-path match skipped the whole tree — and the leak scan passed over an empty
        /// enumeration.
        /// </para>
        /// </remarks>
        public static IEnumerable<FileInfo> PublishedFiles()
        {
            var root = RepoRoot();
            var sep = Path.DirectorySeparatorChar;

            return TrackedTextPaths(root)
                .Where(rel => !(sep + rel).Contains(sep + ".claude" + sep))
                .Select(rel => new FileInfo(Path.Combine(root.FullName, rel)))
                // A path git tracks but the working tree no longer has (a deletion not yet
                // committed) is nothing a scan can read.
                .Where(f => f.Exists);
        }

        /// <summary>
        /// Every path git tracks under <paramref name="root"/> whose index content git judges to be
        /// text, repo-relative, with separators in this platform's spelling. Throws when git cannot
        /// answer, for <see cref="PublishedFiles"/>' reason: a scan over nothing passes.
        /// </summary>
        private static IEnumerable<string> TrackedTextPaths(DirectoryInfo root)
        {
            string output;
            try
            {
                using var git = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "git", "ls-files --eol -z")
                {
                    WorkingDirectory = root.FullName,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })
                    ?? throw new InvalidOperationException("git ls-files could not be started.");

                // Both pipes drained before the wait: a chatty git filling stderr while nothing
                // reads it blocks the child forever.
                var stdout = git.StandardOutput.ReadToEndAsync();
                var stderr = git.StandardError.ReadToEndAsync();
                git.WaitForExit(60_000);
                output = stdout.GetAwaiter().GetResult();
                var errors = stderr.GetAwaiter().GetResult();

                if (git.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"git ls-files exited {git.ExitCode} in {root.FullName}: {errors}");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "The published-file enumeration asks git which files are tracked, and git could not be "
                    + "run in " + root.FullName + ". Every scan built on it would otherwise measure "
                    + "something other than what the import publishes.", ex);
            }

            // Each record is "i/<index> w/<worktree> attr/<attributes>", a TAB, then the path.
            var paths = new List<string>();
            foreach (var record in output.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var tab = record.IndexOf('\t');
                if (tab < 0)
                    throw new InvalidOperationException(
                        "git ls-files --eol returned a record with no tab before its path, so which files "
                        + "are text cannot be read from it: " + record);

                var index = record.Substring(0, tab).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)[0];
                if (index == "i/-text")
                    continue;

                paths.Add(record.Substring(tab + 1).Replace('/', Path.DirectorySeparatorChar));
            }

            if (paths.Count == 0)
                throw new InvalidOperationException(
                    "git ls-files reported no tracked files in " + root.FullName
                    + "; every scan over this enumeration would pass vacuously.");

            return paths;
        }

        /// <summary>
        /// Every source file a guard may read: the hand-written file types under <c>src/</c>, with
        /// build output excluded. <c>.claude/</c> and <c>docs/</c> are deliberately out of scope —
        /// they are prose, and the conventions these scans enforce are conventions about code.
        /// </summary>
        /// <remarks>
        /// Matched below <c>src/</c> rather than against the full path, for the reason
        /// <see cref="PublishedFiles"/> records: a checkout under a directory that happens to be named
        /// <c>bin</c> or <c>obj</c> would otherwise skip the whole tree, and every scan built on this
        /// would pass over nothing. Both enumerations are written the same way on purpose — two
        /// idioms for one question in one file is how the next one gets it wrong.
        /// </remarks>
        public static IEnumerable<FileInfo> Files()
        {
            var root = Root();
            var sep = Path.DirectorySeparatorChar;
            var skip = new[] { sep + "obj" + sep, sep + "bin" + sep };

            return new[] { "*.cs", "*.csproj", "*.json", "*.vsct", "*.pkgdef", "*.vsixmanifest" }
                .SelectMany(pattern => root.EnumerateFiles(pattern, SearchOption.AllDirectories))
                .Where(f => !skip.Any(s => (sep + Path.GetRelativePath(root.FullName, f.FullName)).Contains(s)));
        }
    }
}
