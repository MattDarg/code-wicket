"""Bug: a C# comment names an injection script by its bare name, which the merge deletes.

The path check reads C# for the injection directory's path, so a bare name in a comment passes it; only
the published-file name check sees one. This puts a bare name into an ordinary code comment, which is
the form the docs-only version of that check let through. The check does not read the scripts
themselves, so the name is spelled.
"""
import io, sys
P = "src/CodeWicket.Tests/SourceTree.cs"
s = io.open(P, encoding="utf-8").read()
old = """
        public static IEnumerable<FileInfo> PublishedFiles()
"""
new = """
        // Proved by a-stopped-tray-is-stranded-by-the-next-send.
        public static IEnumerable<FileInfo> PublishedFiles()
"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
