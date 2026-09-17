"""Bug: the policy never looks at where a write lands.

The original shape (pre-release security review, September 2026): the mode ladder classified every edit as the Edit tier
without reading its path, so under AcceptEdits a write to the extension's own config.json
was approved with no banner. Removing the containment check here restores exactly that -
every other tier is left intact, so the injected build is the original one and nothing else.
"""
import io, sys
P = "src/CodeWicket.Shell/PolicyPermissionHandler.cs"
s = io.open(P, encoding="utf-8-sig").read()
old = """        private bool IsProtectedWrite(PermissionRequest request) =>
            request.Path is not null && !IsReadKind(request.Kind) && _protectedPaths.Contains(request.Path);"""
new = """        private bool IsProtectedWrite(PermissionRequest request) => false;"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8-sig", newline="").write(s.replace(old, new, 1))
