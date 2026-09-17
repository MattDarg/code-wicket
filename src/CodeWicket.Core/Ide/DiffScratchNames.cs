using System;
using System.IO;
using System.Text;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// What the two sides of a diff are called on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host opens a diff by writing a "before" and an "after" file into one shared scratch
    /// directory and pointing VS's diff service at them. That makes the NAME load-bearing in a way it
    /// does not look: it is the only thing keeping one card's diff away from another's.
    /// </para>
    /// <para>
    /// Named from the leaf alone (<c>Program.before.cs</c>), two cards in one turn collided. A turn
    /// editing <c>ProjectA\Program.cs</c> and <c>ProjectB\Program.cs</c> writes both to the same two
    /// paths — and the host deliberately keeps up to sixteen diff windows live, matched on path AND
    /// content, so the second card does not reactivate the first. Clicking A, promoting it to a
    /// permanent tab, then clicking B rewrote those files under the open window: VS's file-change
    /// tracking reloads it, and window A now shows B's text under A's caption. Where the open window
    /// holds its backing files instead, the write throws a sharing violation into the outer catch and
    /// the click silently does nothing at all.
    /// </para>
    /// <para>
    /// In Core, and pure, because that is what makes it checkable: the properties that decide whether
    /// this works — different directories differ, the same file agrees with itself, the extension
    /// survives, the result stays a legal bounded filename — are all statements about a string, and
    /// none of them needs Visual Studio to ask.
    /// </para>
    /// </remarks>
    public static class DiffScratchNames
    {
        /// <summary>
        /// How much of the leaf name survives into the scratch name. Generous enough to stay readable
        /// in the diff window's caption, short enough that the whole name plus the scratch directory
        /// cannot approach a path limit.
        /// </summary>
        public const int MaxLeafLength = 60;

        /// <summary>
        /// The scratch file names for the two sides of a diff of <paramref name="fullPath"/>.
        /// </summary>
        /// <remarks>
        /// Deterministic on purpose: the same file must produce the same two names every time, because
        /// re-opening a diff for a file rewrites those files rather than accumulating new ones, and the
        /// host's own reactivation path assumes it. So this is a hash of the DIRECTORY, not a GUID.
        /// <para>
        /// The extension is preserved because it is what gives each side syntax highlighting in the
        /// diff window — the reason the original code built the name from the stem in the first place.
        /// </para>
        /// </remarks>
        public static (string Before, string After) For(string fullPath)
        {
            var leaf = Leaf(fullPath);
            var ext = ExtensionOf(fullPath);
            var stem = StemOf(leaf, ext);
            var scope = DirectoryHash(fullPath);

            return (stem + "." + scope + ".before" + ext, stem + "." + scope + ".after" + ext);
        }

        /// <summary>
        /// A short, stable, filename-safe hash of everything about the path except its leaf — what
        /// makes two same-named files in different projects differ.
        /// </summary>
        /// <remarks>
        /// Not a cryptographic hash and it does not need to be: a collision costs one diff window
        /// showing the wrong text, exactly as today, and there is nothing to attack here. FNV-1a is
        /// chosen for being short to write down and stable across processes and runtimes — a
        /// <c>string.GetHashCode</c> is randomised per process, so the same file would get different
        /// names on the next launch and the scratch directory would fill with orphans.
        /// </remarks>
        private static string DirectoryHash(string fullPath)
        {
            var directory = string.Empty;
            try { directory = Path.GetDirectoryName(fullPath) ?? string.Empty; }
            catch (ArgumentException) { directory = fullPath ?? string.Empty; }

            unchecked
            {
                const uint offset = 2166136261;
                const uint prime = 16777619;
                var hash = offset;
                foreach (var c in directory)
                {
                    // Case-insensitive, because Windows is: the same file spelled two ways must not get
                    // two windows. Same reasoning as Breakpoints.Key and AgentPath.
                    hash = (hash ^ char.ToUpperInvariant(c)) * prime;
                }

                return hash.ToString("x8");
            }
        }

        private static string Leaf(string fullPath)
        {
            try { return Path.GetFileName(fullPath) ?? string.Empty; }
            catch (ArgumentException) { return string.Empty; }
        }

        private static string ExtensionOf(string fullPath)
        {
            try { return Path.GetExtension(fullPath) ?? string.Empty; }
            catch (ArgumentException) { return string.Empty; }
        }

        /// <summary>
        /// The leaf without its extension, sanitized and clamped — never empty, so the name can never
        /// degrade to a bare dot.
        /// </summary>
        private static string StemOf(string leaf, string ext)
        {
            var stem = ext.Length > 0 && leaf.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                ? leaf.Substring(0, leaf.Length - ext.Length)
                : leaf;

            // Hoisted: Path.GetInvalidFileNameChars() clones its internal array on every call, so
            // asking inside the loop allocated one array per CHARACTER — sixty of them to name one
            // sixty-character stem, on every open-diff gesture.
            var invalid = Path.GetInvalidFileNameChars();

            var clean = new StringBuilder(stem.Length);
            foreach (var c in stem)
                clean.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

            var result = clean.ToString();
            if (result.Length > MaxLeafLength)
                result = result.Substring(0, MaxLeafLength);

            return result.Length == 0 ? "file" : result;
        }
    }
}
