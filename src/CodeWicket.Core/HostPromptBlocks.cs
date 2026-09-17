using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace CodeWicket.Core
{
    /// <summary>
    /// The tags the HOST wraps around text it injects into a prompt — the workspace snapshot, the
    /// one-shot IDE-tools steer, the mid-turn framing, a summary resume's recap, the two captures the
    /// user can hand over from the IDE, and the fence a transcript is handed to the summarizer inside.
    /// Names only: what goes inside each block belongs to whoever writes it.
    /// <para>They live together because two different jobs need the same list and neither can afford
    /// to hold its own copy. WRITING them is spread across three projects (the ACP layer writes two,
    /// the chat view-model the other two), and READING them back is <see cref="Strip"/> — which is
    /// only correct if it knows every tag any of us writes. A tag added in one place and missed in the
    /// other is silent: the block simply arrives in an imported transcript as the user's own words.</para>
    /// </summary>
    public static class HostPromptBlocks
    {
        /// <summary>
        /// The per-prompt workspace snapshot (see <c>WorkspaceContextFormatter</c>).
        /// <para><b>Fenced on the wire since the pre-release security review</b>, and so the second block whose
        /// fenced spelling the reader accepts. Its body is repository text the host merely quotes -
        /// a diagnostic's message is whatever a <c>#warning</c> in a cloned file says, the selection
        /// is whatever the file holds - so a bare <c>&lt;/workspace-context&gt;</c> inside it closed
        /// our block early and left the rest of that message standing at host level, ahead of the
        /// user's words, on every prompt in that solution. Escaping the tag text was tried twice
        /// and defeated twice by the same evasion moved one character (<c>&lt;/workspace-context\u200B&gt;</c>,
        /// <c>&lt;/ workspace-context&gt;</c>, a homoglyph): a model reads a tag more loosely than an
        /// escaper can enumerate. The nonce removes the enumeration instead - the close that ends
        /// the block is a string the content was written before.</para>
        /// <para>The BARE spelling stays recognised on the way back in, as it must: every conversation
        /// this extension created before the fence carries it, and so does Kiro's own store, which
        /// names each of them after this block (see <see cref="IsOurFraming"/>).</para>
        /// </summary>
        public static readonly HostPromptBlock WorkspaceContext =
            new HostPromptBlock("workspace-context", acceptsFencedSpelling: true);

        /// <summary>The session's one-shot list of the IDE-integrated tools exposed over MCP.</summary>
        public static readonly HostPromptBlock IdeTools = new HostPromptBlock("ide-tools");

        /// <summary>The framing that tells the agent which mid-turn gesture delivered a held message.</summary>
        public static readonly HostPromptBlock MidTurnMessage = new HostPromptBlock("mid-turn-message");

        /// <summary>
        /// The recap sent in place of a full history on a summary resume.
        /// <para><b>Fenced on the wire</b> (see <see cref="HostPromptBlock.Fenced"/>) - the first block
        /// to be, and so the first whose fenced spelling the reader below accepts. Its body is a model's answer
        /// about a conversation whose text we do not control, so a bare
        /// <c>&lt;/conversation-summary&gt;</c> occurring inside it would close our own block early and
        /// let whatever followed read as the host speaking.</para>
        /// </summary>
        public static readonly HostPromptBlock ConversationSummary =
            new HostPromptBlock("conversation-summary", acceptsFencedSpelling: true);

        /// <summary>
        /// A break-mode capture the user attached to a message — the call stack and locals they were
        /// looking at (issue #73, rung 2).
        /// <para><b>Deliberately NOT in <see cref="All"/>, and that omission is load-bearing rather
        /// than an oversight.</b> <see cref="All"/> is <see cref="Strip"/>'s removal set, and the four
        /// blocks in it are ours in the sense that matters there: they say something the user did not
        /// say, so replaying them as the user's own words is the defect Strip exists to prevent. This
        /// block is the opposite — it carries what the user deliberately handed over, and on the
        /// import path (issue #108) it is the ONLY surviving copy of that capture, since a foreign
        /// conversation has no log of ours behind it. Stripping it would delete the evidence the
        /// message was written about and leave "why is Items empty?" standing alone.</para>
        /// <para>Pinned by a test, because an omission from a list reads as a mistake to the next
        /// person to look at it, and "fixing" it is one line.</para>
        /// <para><b>Fenced on the wire</b> since the pre-release security review, the same finding as the
        /// workspace block's and the same fix: a local's value or a stop reason is whatever the
        /// debugged program held, so a bare <c>&lt;/debug-state&gt;</c> inside it closed the block
        /// early. The bare spelling stays readable for every conversation written before.</para>
        /// </summary>
        public static readonly HostPromptBlock DebugState =
            new HostPromptBlock("debug-state", acceptsFencedSpelling: true);

        /// <summary>
        /// A Visual Studio Output-window pane the user attached — what a build, a test run or an
        /// extension printed where the agent cannot see it.
        /// <para><b>Out of <see cref="All"/> for the same reason as <see cref="DebugState"/>:</b> it
        /// carries what the user handed over, not what we said about it, so stripping it would delete
        /// the evidence the message was written about. The two are the same kind of block and belong
        /// to the same list.</para>
        /// <para><b>Fenced on the wire</b> (pre-release security review, September 2026): the pane is what a build, a test run
        /// or an extension printed, which is repository text once removed - a <c>#warning</c> that
        /// spells the close tag ends up in the Output window verbatim. Same fence, same reader rule,
        /// bare spelling kept readable.</para>
        /// </summary>
        public static readonly HostPromptBlock OutputWindow =
            new HostPromptBlock("output-window", acceptsFencedSpelling: true);

        /// <summary>
        /// The conversation text handed to the one-shot summarizer, fenced so that transcript cannot be
        /// read as an instruction to it (see <c>EngineService.BuildSummarizationPrompt</c>).
        /// <para>Always written <see cref="HostPromptBlock.Fenced"/>, and its nonce is minted in the
        /// ENGINE process while the summary's is minted in the shell - two values on two seams, so the
        /// model being asked to summarize is never shown the nonce that will wrap its own answer.</para>
        /// <para><b>Deliberately in none of the lists below, and that is not the omission the class
        /// remarks warn about.</b> Those are about a tag that is written and then has to be read back.
        /// This one is written and never read: it goes to a THROWAWAY session whose turn the engine
        /// consumes rather than records, so it reaches no transcript of ours, and it does not stand at
        /// the start of that prompt - the instruction does - so the leading-run walk could not reach it
        /// in any case. Listing it would only widen what an ordinary user message is allowed to lose,
        /// for a tag no reader ever meets.</para>
        /// </summary>
        public static readonly HostPromptBlock ConversationTranscript = new HostPromptBlock("conversation-transcript");

        /// <summary>
        /// Every block whose contents are OURS rather than the user's — the removal set
        /// <see cref="Strip"/> works from. <see cref="DebugState"/> is excluded on purpose; see its
        /// own remarks.
        /// </summary>
        public static readonly IReadOnlyList<HostPromptBlock> All =
            new[] { WorkspaceContext, IdeTools, MidTurnMessage, ConversationSummary };

        /// <summary>
        /// Blocks that carry what the USER handed over rather than what we said about it. They are
        /// recognised by <see cref="SplitLeading"/> and kept; <see cref="Strip"/> does not touch them.
        /// </summary>
        public static readonly IReadOnlyList<HostPromptBlock> UserContributed =
            new[] { DebugState, OutputWindow };

        /// <summary>Every tag either half writes, for a walk that has to step over all of them.</summary>
        private static readonly IReadOnlyList<HostPromptBlock> Recognised =
            new[]
            {
                WorkspaceContext, IdeTools, MidTurnMessage, ConversationSummary,
                DebugState, OutputWindow,
            };

        /// <summary>
        /// Removes a leading run of host blocks from a message the USER is recorded as having sent —
        /// which is what a <c>session/load</c> replay hands back when the conversation being imported
        /// is one we ourselves created (issue #108).
        /// <para>Without this the import renders our own framing as the user's words, which is the
        /// prohibition the mid-turn block is already written under: that framing goes on the wire only,
        /// "or a resume would replay ours as theirs". Two of the four are prepended to the user's text
        /// inside the SAME content block (the mid-turn framing and a summary recap), so they come back
        /// fused to it; the other two are content blocks of their own and strip to nothing, which is
        /// also the right answer — they were never anything the user said.</para>
        /// <para>The three ways it is conservative are <see cref="WalkLeading"/>'s, which is the one
        /// walk both readers share.</para>
        /// </summary>
        /// <summary>
        /// True when <paramref name="text"/> begins with one of our own blocks - used to recognise a
        /// backend-generated name that is really our prompt framing wearing a title.
        /// <para><b>Measured, on 37 kiro-cli conversations:</b> the only two that were ever prompted are
        /// both ours, and both are named <c>&lt;workspace-context&gt;</c>. Kiro derives a session's name
        /// from its first prompt, and our first content block is the workspace snapshot - so every
        /// conversation this extension starts is named after our framing in Kiro's own store, in its
        /// CLI's history, and in the picker that offers it back.</para>
        /// <para>How much of the prompt it takes differs by engine, and only one half is established:
        /// v3 reads as the first 80 characters of the whole trimmed prompt (from the agent's source),
        /// while v2's exact rule is NOT known - its binary is stripped, and what was measured is only
        /// that the title came back as exactly <c>&lt;workspace-context&gt;</c> rather than 80
        /// characters of it. This test is on the PREFIX for that reason, so it holds either way.</para>
        /// <para>A DISPLAY guard, deliberately. The obvious fixes are worse or unavailable: reordering
        /// the blocks changes what the model reads first, which is the ordering's whole purpose; Kiro's
        /// v3 engine has a rename method and a displayText field but its v2 engine has NEITHER, and v2
        /// is what kiro-cli runs by default. So the only lever that covers both engines is refusing to
        /// repeat the string back at the user.</para>
        /// <para>It does NOT strip and show the remainder: the truncated tail
        /// (<c>"Working directory: C:\..."</c>) looks like a real title, which is a better-dressed lie
        /// than the tag. A conversation we cannot name is unnamed.</para>
        /// </summary>
        public static bool IsOurFraming(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // Through the same matcher the walk uses, so a fenced spelling of a block is recognised as
            // that block here too. It has to be: a summary resume's first prompt now opens with
            // <conversation-summary-NONCE>, and a guard that only knew the bare tag would answer "not
            // ours" and put the nonce on screen as a conversation's title.
            return MatchOpenTag(text!.TrimStart(), 0, All) is not null;
        }

        /// <summary>
        /// <b>What Strip deliberately does not do.</b> It stops at the first block it does not own,
        /// which since <see cref="DebugState"/> exists means it can stop with more of our own framing
        /// still ahead of it: the wire order is summary, then the user's capture, then the mid-turn
        /// framing, so a message carrying a capture leaves Strip with a <c>&lt;mid-turn-message&gt;</c>
        /// block untouched behind it. That is safe only because it is not the last word - the host
        /// runs <see cref="SplitLeading"/> over the remainder, which walks the WHOLE run and knows
        /// which half of it is ours. Widening Strip to step past a kept block would delete that block,
        /// which is the one thing it must never do.
        /// </summary>
        public static string Strip(string? text) => WalkLeading(text, All).Text;

        /// <summary>
        /// Separates a leading run of host blocks into the ones the USER contributed - which are kept
        /// and handed back - and the ones we wrote, which are discarded, leaving the user's own words.
        /// <para>This is the reading half of the import path (issue #108) once a message can carry a
        /// capture (issue #73). <see cref="Strip"/> alone is not enough there, because a kept block
        /// sits in the MIDDLE of the run: it stops at the capture and leaves our mid-turn framing
        /// standing behind it, which would then render as something the user wrote.</para>
        /// <para><b>Reordering the blocks so the capture came last would ALSO fix that, and it is the
        /// worse fix.</b> It works only while the capture is the last kept block: the next one to
        /// arrive reintroduces the bug silently, long after anyone remembers the ordering was
        /// load-bearing. Walking the whole run is order-independent, so it cannot rot that way.</para>
        /// </summary>
        public static LeadingBlocks SplitLeading(string? text) => WalkLeading(text, Recognised);

        /// <summary>
        /// The one walk over a leading run of host blocks. <see cref="Strip"/> and
        /// <see cref="SplitLeading"/> differ ONLY in which blocks they recognise, and saying that in
        /// the code is the point: two walkers would be two answers to "what does a host block look
        /// like", and they would disagree about a tag one day.
        /// </summary>
        /// <remarks>
        /// Conservative in three ways, each of them a way this could damage a real message. Only a run
        /// at the very START is considered, so a message that merely MENTIONS a tag keeps it - which is
        /// also what stops a user quoting a capture back mid-message having it lifted out from under
        /// them. An unterminated open tag stops the walk rather than running to the end of the text, or
        /// a user asking about a tag they typed would have their whole message deleted. And a message
        /// that matched nothing comes back unchanged rather than trimmed, so this can never quietly
        /// reshape ordinary text.
        /// </remarks>
        private static LeadingBlocks WalkLeading(string? text, IReadOnlyList<HostPromptBlock> among)
        {
            if (string.IsNullOrEmpty(text))
                return new LeadingBlocks(Array.Empty<HostPromptBlockValue>(), text ?? string.Empty);

            var pos = 0;
            var matched = false;
            List<HostPromptBlockValue>? kept = null;

            while (true)
            {
                var scan = pos;
                while (scan < text!.Length && char.IsWhiteSpace(text[scan]))
                    scan++;

                if (MatchOpenTag(text, scan, among) is not { } block)
                    break;

                var bodyAt = scan + block.Open.Length;
                var closeAt = text.IndexOf(block.Close, bodyAt, StringComparison.Ordinal);
                if (closeAt < 0)
                    break;

                if (!IsOurs(block))
                {
                    kept ??= new List<HostPromptBlockValue>();
                    kept.Add(new HostPromptBlockValue(block, text.Substring(bodyAt, closeAt - bodyAt).Trim()));
                }

                pos = closeAt + block.Close.Length;
                matched = true;
            }

            if (!matched)
                return new LeadingBlocks(Array.Empty<HostPromptBlockValue>(), text!);

            // Only the separator we put between our block and the user's words goes with it.
            return new LeadingBlocks(
                (IReadOnlyList<HostPromptBlockValue>?)kept ?? Array.Empty<HostPromptBlockValue>(),
                text!.Substring(pos).TrimStart());
        }

        // Through Canonical, so a fenced spelling answers for the block it is a spelling of.
        private static bool IsOurs(HostPromptBlock block)
        {
            foreach (var ours in All)
                if (ReferenceEquals(ours, block.Canonical))
                    return true;
            return false;
        }

        /// <summary>
        /// The block whose open tag starts at <paramref name="index"/>, or null for none - matched
        /// against <paramref name="among"/>, which is the only thing separating the two readers.
        /// </summary>
        /// <remarks>
        /// <para>Two spellings, and the second is deliberately narrow. The bare <c>&lt;name&gt;</c> is
        /// what most writers emit and stays readable forever - an older build's framing has to be
        /// recognised when its conversation is imported by a newer one. The fenced
        /// <c>&lt;name-NONCE&gt;</c> is accepted ONLY for a block we actually fence on the wire, and
        /// ONLY in the exact shape <see cref="HostPromptBlock.NewNonce"/> writes.</para>
        /// <para><b>Anything looser is a widening of the reader, not a fence.</b> With a suffix rule of
        /// "some letters and digits", a user message opening <c>&lt;workspace-context-2&gt;</c> - the
        /// framing they just saw, pasted back with a number on it - would be eaten as ours, silently,
        /// on the import path. The narrow rule turns nobody's message into ours that was not already.
        /// </para>
        /// <para>A fenced match names ITSELF: the block returned carries the tag exactly as the text
        /// spelled it, so the close it is ended by is the one this open declared, and a bare close
        /// inside the body ends nothing. <see cref="HostPromptBlock.Canonical"/> is how the caller
        /// still knows which named block it has.</para>
        /// </remarks>
        private static HostPromptBlock? MatchOpenTag(
            string text, int index, IReadOnlyList<HostPromptBlock> among)
        {
            foreach (var block in among)
            {
                if (index + block.Open.Length <= text.Length &&
                    string.CompareOrdinal(text, index, block.Open, 0, block.Open.Length) == 0)
                {
                    return block;
                }

                if (block.AcceptsFencedSpelling && MatchFencedOpenTag(text, index, block) is { } fenced)
                    return fenced;
            }

            return null;
        }

        /// <summary>
        /// <c>&lt;name-NONCE&gt;</c> at <paramref name="index"/>, as a block naming that exact
        /// spelling - or null, which is every near miss: a suffix of the wrong length, an uppercase
        /// digit, a further hyphen, anything that is not one of the characters a nonce is made of.
        /// </summary>
        private static HostPromptBlock? MatchFencedOpenTag(string text, int index, HostPromptBlock block)
        {
            //     <     name                -     nonce                        >
            var length = 1 + block.Name.Length + 1 + HostPromptBlock.NonceLength + 1;
            if (index < 0 || index + length > text.Length)
                return null;

            if (text[index] != '<' ||
                string.CompareOrdinal(text, index + 1, block.Name, 0, block.Name.Length) != 0 ||
                text[index + 1 + block.Name.Length] != '-' ||
                text[index + length - 1] != '>')
            {
                return null;
            }

            var nonceAt = index + 1 + block.Name.Length + 1;
            for (var i = 0; i < HostPromptBlock.NonceLength; i++)
                if (!HostPromptBlock.IsNonceChar(text[nonceAt + i]))
                    return null;

            return block.FencedAs(text.Substring(nonceAt, HostPromptBlock.NonceLength));
        }
    }

    /// <summary>What <see cref="HostPromptBlocks.SplitLeading"/> found.</summary>
    public sealed class LeadingBlocks
    {
        internal LeadingBlocks(IReadOnlyList<HostPromptBlockValue> kept, string text)
        {
            Kept = kept;
            Text = text;
        }

        /// <summary>The user-contributed blocks, in the order they appeared. Empty for most messages.</summary>
        public IReadOnlyList<HostPromptBlockValue> Kept { get; }

        /// <summary>What is left once our own framing is removed - the user's own words.</summary>
        public string Text { get; }
    }

    /// <summary>One recognised block and the text inside it.</summary>
    public sealed class HostPromptBlockValue
    {
        internal HostPromptBlockValue(HostPromptBlock block, string body)
        {
            // The CANONICAL block, never the spelling it happened to arrive in: callers identify a kept
            // block by reference (ReferenceEquals against DebugState) and name it by Block.Name, and
            // neither may start depending on whether the tag carried a nonce.
            Block = block.Canonical;
            Body = body;
        }

        public HostPromptBlock Block { get; }

        public string Body { get; }
    }

    /// <summary>
    /// One host-injected prompt block, named once so every writer and the reader agree.
    /// <para>Two spellings of the same block. The BARE one, <c>&lt;name&gt;</c>, is the default and
    /// what nearly everything writes. The FENCED one, minted by <see cref="Fenced"/>, hangs a freshly
    /// generated nonce off the tag NAME, so the string that ends the block is one the text inside it
    /// cannot have guessed. Both answer <see cref="Canonical"/> with the same instance, which is how a
    /// reader knows which block it has without caring how it was spelled.</para>
    /// </summary>
    public sealed class HostPromptBlock
    {
        /// <summary>Hex digits in a fence nonce - 8 bytes of CSPRNG output, written out.</summary>
        internal const int NonceLength = 16;

        internal HostPromptBlock(string name, bool acceptsFencedSpelling = false)
        {
            Name = name;
            Open = "<" + name + ">";
            Close = "</" + name + ">";
            Canonical = this;
            AcceptsFencedSpelling = acceptsFencedSpelling;
        }

        private HostPromptBlock(HostPromptBlock canonical, string nonce)
        {
            Name = canonical.Name + "-" + nonce;
            Open = "<" + Name + ">";
            Close = "</" + Name + ">";
            Canonical = canonical;
            // A fenced spelling is not itself fenceable: nothing writes <name-a-b>, so nothing reads it.
            AcceptsFencedSpelling = false;
        }

        /// <summary>The tag name, without angle brackets.</summary>
        public string Name { get; }

        /// <summary>The opening tag, e.g. <c>&lt;workspace-context&gt;</c>.</summary>
        public string Open { get; }

        /// <summary>The closing tag, e.g. <c>&lt;/workspace-context&gt;</c>.</summary>
        public string Close { get; }

        /// <summary>
        /// The named block this is a spelling OF: itself for the bare spelling, the block it was minted
        /// from for a fenced one. Every identity comparison a reader makes goes through here.
        /// </summary>
        public HostPromptBlock Canonical { get; }

        /// <summary>True for a spelling carrying a nonce rather than the plain tag name.</summary>
        public bool IsFenced => !ReferenceEquals(Canonical, this);

        /// <summary>
        /// Whether the reader accepts a nonce-suffixed spelling of this block. True only for a block
        /// actually written that way on the wire, because accepting a spelling is accepting an input we
        /// did not accept before. See <c>HostPromptBlocks.MatchOpenTag</c>.
        /// </summary>
        internal bool AcceptsFencedSpelling { get; }

        /// <summary>
        /// A spelling of this block whose tag name carries a fresh nonce, so the close that ends it is
        /// a string nothing inside the body could have produced.
        /// <para><b>Mint one per send and never cache it.</b> A nonce reused across two sends is a
        /// nonce the first send's content may already have been shown - and the text being wrapped is
        /// precisely the text that would like to end the block early.</para>
        /// </summary>
        public HostPromptBlock Fenced() => new HostPromptBlock(Canonical, NewNonce());

        /// <summary>This block under a nonce already written down - how the reader names what it read.</summary>
        internal HostPromptBlock FencedAs(string nonce) => new HostPromptBlock(Canonical, nonce);

        /// <summary>
        /// <see cref="NonceLength"/> lowercase hex digits over 8 cryptographically random bytes. A
        /// guessable nonce is no nonce: the content it fences is the content that would like to guess
        /// it.
        /// </summary>
        internal static string NewNonce()
        {
            var bytes = new byte[NonceLength / 2];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(bytes);

            var chars = new char[NonceLength];
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = HexDigit(bytes[i] >> 4);
                chars[(i * 2) + 1] = HexDigit(bytes[i] & 0xF);
            }

            return new string(chars);
        }

        /// <summary>The characters a nonce is made of, and so the only ones a fenced tag may carry.</summary>
        internal static bool IsNonceChar(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');

        private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'a' + (value - 10));
    }
}
