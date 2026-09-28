"""Bug: the write tee logs a frame only after it has been sent.

The original order: FrameTeeWriteStream forwarded the bytes and only then queued the frame to the sink.
A far end that acts on the frame at once - answering it, or ending the connection and disposing the
sink - can finish before the enqueue, and the sink's dispose drains only what is already queued, so
the last frame before a teardown was written to a closed file and lost silently. Moving the inner
write back ahead of the logging restores exactly that order and nothing else.
"""
import io, sys
P = "src/CodeWicket.Core/FrameTee.cs"
s = io.open(P, encoding="utf-8-sig").read()
old = """        public override void Write(byte[] buffer, int offset, int count)
        {
            // Queued to the sink BEFORE"""
new = """        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            // Queued to the sink BEFORE"""
old2 = """                catch { /* logging is best-effort */ }
            }
            _inner.Write(buffer, offset, count);
        }"""
new2 = """                catch { /* logging is best-effort */ }
            }
        }"""
if s.count(old) != 1 or s.count(old2) != 1:
    sys.exit("inject target not found")
s = s.replace(old, new, 1).replace(old2, new2, 1)
io.open(P, "w", encoding="utf-8-sig", newline="").write(s)
