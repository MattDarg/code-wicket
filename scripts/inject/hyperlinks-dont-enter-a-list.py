"""Bug: the hyperlink walker stops descending into a bullet list.

`Hyperlinks()` answers "every hyperlink in this document, at any nesting depth", and every caller
reads a zero from it as "the link was never made". Take the descent out of the List case and a
reference written in a bullet - the commonest shape in agent prose - becomes invisible to it, so
the file-reference check reports the plain-text-to-link upgrade as never having happened while the
product performed it correctly.
"""
import io, sys
P = "src/CodeWicket.Desktop/App.xaml.cs"
s = io.open(P, encoding="utf-8-sig").read()
old = "Collect(list.ListItems);"
new = "_ = list;"
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8-sig", newline="").write(s.replace(old, new, 1))
