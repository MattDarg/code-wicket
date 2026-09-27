"""Bug: the ending's removal is written too WIDE - every notice the send inserted, not only its own
account of the resume it was attempting.

The narrow rule registers what the send SAID ABOUT ITS ATTEMPT. The wide one also takes the backend's
own account of why the attempt failed - "Could not summarize" - off the screen, which is the one place
that reason is shown and what the user is left reading once the message is back in the composer.

This is the opposite direction to an-ending-leaves-its-resume-notices-standing.py, and a DIFFERENT
check holds it: registering the failure notice turns the banner-Cancel case red and leaves the
succeeded-attempt case green.
"""
import io, sys

P = "src/CodeWicket.UI/Sessions/PromptDelivery.cs"
s = io.open(P, encoding="utf-8").read()

old1 = """                const string Empty = "the backend returned an empty summary";
                Items.Add(new NoticeItemViewModel(
                    "Could not summarize the conversation: " + Empty + ".",
                    NoticeKind.Error));
                return (null, Empty);"""
new1 = """                const string Empty = "the backend returned an empty summary";
                var emptyNotice = new NoticeItemViewModel(
                    "Could not summarize the conversation: " + Empty + ".",
                    NoticeKind.Error);
                Items.Add(emptyNotice);
                _pending?.NoteResumeNotice(emptyNotice);
                return (null, Empty);"""

old2 = """                Items.Add(new NoticeItemViewModel($"Could not summarize the conversation: {ex.Message}", NoticeKind.Error));
                return (null, ex.Message);"""
new2 = """                var thrownNotice = new NoticeItemViewModel($"Could not summarize the conversation: {ex.Message}", NoticeKind.Error);
                Items.Add(thrownNotice);
                _pending?.NoteResumeNotice(thrownNotice);
                return (null, ex.Message);"""

if s.count(old1) != 1 or s.count(old2) != 1:
    sys.exit("inject target not found")
io.open(P, "w", encoding="utf-8", newline="").write(s.replace(old1, new1, 1).replace(old2, new2, 1))
