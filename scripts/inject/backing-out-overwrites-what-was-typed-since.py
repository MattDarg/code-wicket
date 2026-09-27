"""Bug: backing out of the resume-choice banner ASSIGNS the parked message over the composer.

The gesture that returns one message destroyed the other: anything typed while the banner was up was
replaced rather than followed. This is the shape that shipped, an assignment rather than the shared
rule for where a given-back message lands.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()
old = """            var parked = _parked;
            _parked = null;
            GiveBackParked(parked);
        }"""
new = """            var parked = _parked;
            _parked = null;
            if (parked is not null)
                InputText = parked.Text;
        }"""
if s.count(old) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old, new, 1))
