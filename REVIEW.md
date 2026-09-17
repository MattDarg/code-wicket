# Reviewing a change to Code Wicket

For anyone reviewing a pull request here, and for a coding agent asked to review one.

Most of what a review needs is ordinary: does it work, is it tested, is it clear. This file covers
the one thing that is not ordinary about this repository.

## The steering document is deliberately incomplete

[`AGENTS.md`](AGENTS.md) holds the rules a contributor can hit **cold** — before they had any reason
to open the document for the area they are changing — plus the security rules, which are there
because the cost of missing one is not a rework. **Everything else lives only in the area document**,
in full, under [`docs/engineering/`](docs/engineering/).

That split is on purpose: the steering document is loaded into every session, the area documents are
not, and rules that only matter inside one area were being paid for on every turn by everyone.

**The consequence for a review is a false negative, and it is the thing to guard against.** "I read
the steering document and it says nothing about this" is *not* evidence that no rule covers the
change. It usually means the rule is in the area document, which is where the rules are.

## What a review has to do here

1. **Work out which areas the diff touches.** The routing table at the top of `AGENTS.md` maps what
   you are changing onto the document to read. Use that table — **this file deliberately does not
   repeat it**, because two copies of a mapping drift, and the one in the steering document is the
   one an agent already has loaded.

2. **Read those documents before judging the change.** Not skim: the rules that get broken are the
   ones that read as arbitrary, and the reasoning that makes them non-arbitrary is in the document.
   Several of them were learned from a live capture or a wire trace, and cannot be re-derived from
   the code.

3. **Say which documents you read, and cite the rules you checked against.** A review that names
   none, over a diff that touches an area, has not been done — it has been skipped, and the two are
   indistinguishable from the outside. Citing is what makes the difference visible:

   > Checked against `backend-behaviours.md` (resume): the moved-root pre-check still asks before
   > anything is recorded, and `SameRoot` still resolves against the cold listing's own resolution.

   A citation that turns out to be wrong is a normal review disagreement. A review with no citation
   is the failure this file exists to stop.

4. **If the change establishes a new rule, check it was written down — in the area document.** A new
   invariant that lives only in the code is one nobody will know about in a month. It goes in the
   area document; it goes in the steering document *only* if it meets that file's own bar, which the
   file states.

   **The symptom to watch for is a steering-document entry that has grown into a paragraph with its
   own sub-points.** The file loses its shape by absorbing reasoning that belongs in an area document,
   each addition individually defensible, the drift only visible in aggregate.
   Nothing automated catches it — a length cap is a number someone picked and a list of banned
   phrasings only ever matches wording already removed, so both report green over the thing they
   would exist to find. It is a judgement, and this is where it gets made.

5. **Does the change put anything into the public repository that should not be there?** Chiefly a
   real path, a real machine name or a pasted log with a user profile in it — the things that arrive
   by copy-and-paste rather than by decision, in prose far more often than in code.

   `UserPathLeakTests` scans every published file for the two spellings it knows: `C:\Users\<name>`
   and the hyphen-flattened `C--Users-<name>-` that Claude Code writes in its own store. **Those are
   enumerated, not inferred**, so a third spelling passes it silently — which is the half a reviewer
   is for. The test also cannot judge a hostname, a ticket URL or an internal share. Look at the
   diff, not just the green tick.

## What this does not ask for

- **Not a second review of the area document itself.** If the change is consistent with the rules,
  that is the end of it.
- **Not a citation per file.** One per area the diff touches is what is wanted.
- **Not a blocker for a change that touches no area** — a typo fix, a comment, a version bump. The
  routing table resolving to nothing is a real answer, and saying so is a complete review.

---

Writing a change rather than reviewing one? [`CONTRIBUTING.md`](CONTRIBUTING.md) covers building,
the offline gates, and the habits this codebase is maintained with.
