using System.Collections.Generic;
using System.Text;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>
    /// Serializes the transcript items to one Markdown document (the "Copy all" / "Export as
    /// Markdown" actions). Assistant text is already markdown and passes through verbatim; user
    /// text is emitted literally under its own heading; structured items (tools, edits, plans,
    /// test results) render as compact markdown mirroring what their cards show.
    /// </summary>
    public static class TranscriptMarkdown
    {
        /// <summary>
        /// How a message's images are carried into the markdown (issue #118). The two consumers of this
        /// document want genuinely different things, which is why this is a parameter rather than a
        /// house style.
        /// </summary>
        public enum ImageExport
        {
            /// <summary>
            /// A link to the saved file. Compact, which is what the CLIPBOARD needs — "Copy all" exists
            /// to be pasted into an issue or a chat, and megabytes of base64 there is hostile.
            /// <para>It rots: attachments live under a retention sweep, so the link outlives the file by
            /// design. Acceptable for a clipboard, whose lifetime is the next paste.</para>
            /// </summary>
            Link,

            /// <summary>
            /// The image inline as a <c>data:</c> URI. For the EXPORTED FILE, which the user picked a
            /// location for and means to keep or send: a link into their own LocalAppData is portable to
            /// nobody and is deleted by retention within days, so an export built from links is an
            /// artifact that silently rots — the same class of failure as an image that pastes blank.
            /// </summary>
            Embed,
        }

        /// <summary>
        /// Total base64 an <see cref="ImageExport.Embed"/> document will carry before the rest degrade
        /// to links. Each image is already capped by the paste path, so this only bites on a
        /// conversation with a great many of them — where an unbounded export would be worse than a
        /// partial one. What it drops, it says.
        /// </summary>
        private const int EmbedBudgetBytes = 24 * 1024 * 1024;

        /// <summary>Builds the document; empty when there's nothing worth exporting.</summary>
        public static string Build(
            string? title, IEnumerable<ChatItemViewModel> items, ImageExport images = ImageExport.Link)
        {
            var budget = EmbedBudgetBytes;
            var body = new StringBuilder();
            foreach (var item in items)
            {
                var block = Render(item, images, ref budget);
                if (string.IsNullOrWhiteSpace(block))
                    continue;
                body.AppendLine(block!.TrimEnd());
                body.AppendLine();
            }

            if (body.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title))
            {
                sb.Append("# ").AppendLine(title!.Trim());
                sb.AppendLine();
            }
            sb.Append(body);
            return sb.ToString().TrimEnd();
        }

        private static string? Render(ChatItemViewModel item, ImageExport images, ref int budget) => item switch
        {
            MessageItemViewModel m => RenderMessage(m, images, ref budget),
            ToolItemViewModel t => RenderTool(t),
            EditItemViewModel e => RenderEdit(e),
            PlanItemViewModel p => RenderPlan(p),
            CrewItemViewModel c => RenderCrew(c),
            TestRunResultItemViewModel r => RenderTestRun(r),
            NoticeItemViewModel n => n.IsError ? "**Error:** " + n.Text : "*" + n.Text + "*",
            _ => item.CopyText,
        };

        private static string? RenderMessage(MessageItemViewModel m, ImageExport images, ref int budget)
        {
            // An IMAGE-ONLY message is a whole message — paste a screenshot, press Enter — so emptiness
            // is not enough to drop it. It used to be, and the export silently lost the entire turn
            // rather than merely its picture.
            // An IMAGE-ONLY or CONTEXT-ONLY message is a whole message, so emptiness alone cannot
            // drop one. The attachment half of this was a shipped bug (the export lost the entire
            // turn), and a captured call stack with no covering sentence is the same shape.
            if (string.IsNullOrWhiteSpace(m.Text) && !m.HasAttachments && !m.HasContexts)
                return null;
            if (m.IsThinking)
                return Quote("*Thinking*\n\n" + m.Text.Trim());

            var sb = new StringBuilder();
            sb.Append(m.IsUser ? "## User" : "## Assistant").Append("\n\n");

            foreach (var attachment in m.Attachments)
            {
                sb.Append(RenderAttachment(attachment, images, ref budget)).Append('\n');
            }

            if (m.HasAttachments && !string.IsNullOrWhiteSpace(m.Text))
                sb.Append('\n');

            sb.Append(m.Text.Trim());

            // AFTER the text, unlike the images. A capture is a wall of frames and values: leading
            // with it buries the sentence the user actually wrote, and the export is read top-down.
            foreach (var context in m.Contexts)
                RenderContext(sb, context);

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// One attached piece of IDE context (issue #73), fenced so frame names, paths and values
        /// survive verbatim - the same reason the error Details panel is plain text rather than
        /// markdown. Labelled with what the chip said, so the export and the pane agree about what
        /// was handed over.
        /// </summary>
        private static void RenderContext(StringBuilder sb, ContextItemViewModel context)
        {
            sb.AppendLine().AppendLine().Append("**Attached from the IDE - ")
              .Append(Escape(context.Label)).Append("**");
            AppendFenced(sb, context.Text);
        }

        /// <summary>
        /// One attached image. Always a markdown image with the file's name as ALT TEXT, so that even
        /// where it cannot render — GitHub does not resolve <c>data:</c> URIs, and a link into
        /// LocalAppData resolves nowhere but this machine — the reader is told a picture was there and
        /// what it was called. The rule the whole feature is built on holds here too: a message must
        /// never read as though it carried no image.
        /// </summary>
        private static string RenderAttachment(AttachmentViewModel attachment, ImageExport images, ref int budget)
        {
            var alt = Escape(attachment.Name);
            var hasPath = attachment.FilePath is { Length: > 0 };

            if (images == ImageExport.Embed && attachment.TryReadBytes() is { Length: > 0 } bytes)
            {
                if (bytes.Length <= budget)
                {
                    budget -= bytes.Length;
                    return "![" + alt + "](data:" + attachment.MimeType + ";base64,"
                        + System.Convert.ToBase64String(bytes) + ")";
                }

                // Over budget but present: fall back to the link and SAY SO. A reader who finds a link
                // where the rest of the document has pictures is owed the reason — this one has a fix
                // (export fewer, or find the file), and "the image is gone" does not.
                if (hasPath)
                    return "![" + alt + "](" + FileUri(attachment) + ") *(not embedded: export image budget reached)*";
            }

            // A link is only honest if it resolves. Checking costs a stat rather than a read, which is
            // cheap enough even for the clipboard form — and a broken link is worse than a sentence,
            // because it looks like the picture should be there.
            if (hasPath && Exists(attachment.FilePath!))
                return "![" + alt + "](" + FileUri(attachment) + ")";

            // Two different absences, and they are not the same fact. A path that no longer resolves
            // was reclaimed by retention; no path at all means the save failed when the message was
            // sent. Only the first is a loss worth reporting as one.
            return hasPath
                ? "*[image: " + alt + " — no longer available]*"
                : "*[image: " + alt + "]*";
        }

        private static bool Exists(string path)
        {
            try { return System.IO.File.Exists(path); }
            catch { return false; }
        }

        private static string FileUri(AttachmentViewModel attachment) =>
            attachment.FilePath is { Length: > 0 } path
                ? new System.Uri(path).AbsoluteUri
                : string.Empty;

        /// <summary>Markdown alt text sits inside <c>[]</c>, so a name containing one would end it early.</summary>
        private static string Escape(string? text) =>
            (text ?? string.Empty).Replace("[", "\\[").Replace("]", "\\]");

        private static string RenderTool(ToolItemViewModel t)
        {
            var sb = new StringBuilder();
            sb.Append("**Tool:** ").Append(t.Title);
            // The file the call acts on, when the backend's title didn't name it (Kiro's "Read File").
            if (t.HasTargetPath)
                sb.Append(" `").Append(t.TargetDisplayPath).Append('`');
            if (!string.IsNullOrEmpty(t.KindLabel))
                sb.Append(" *(").Append(t.KindLabel).Append(")*");
            sb.Append(t.Status switch
            {
                ToolStatus.Success => " ✓",
                ToolStatus.Failed => " ✗",
                // Launched, not finished. Spelt out rather than given a mark of its own: a reader of
                // the export has no legend, and an unfamiliar glyph beside a sub-agent row would be
                // read as whichever of ✓/✗ it most resembles.
                ToolStatus.Launched => " *(launched, still running)*",
                _ => " …",
            });

            if (t.HasDescription)
                sb.AppendLine().AppendLine().Append('*').Append(t.Description).Append('*');
            // How the call came to be permitted, in the same place the row shows it. An exported
            // transcript is a record of what an agent did in someone's IDE, and "who approved this" is
            // one of the first things asked of such a record — an export that drops it answers the
            // question with silence, which reads as nobody having wondered.
            if (t.HasPermissionSummary)
                sb.AppendLine().AppendLine().Append(t.PermissionSummary);
            if (!string.IsNullOrEmpty(t.Detail))
                AppendFenced(sb, t.Detail!);
            if (t.HasErrorDetail)
            {
                sb.AppendLine().AppendLine().Append("stderr:");
                AppendFenced(sb, t.ErrorDetail!);
            }

            // A sub-agent's own work, indented under the row that launched it (issue #125). Routed back
            // through the top-level dispatcher rather than through RenderTool, because a child is not
            // always a tool row — an edit that built no row of its own nests here as a card — and a
            // deeper nesting then needs nothing added either.
            foreach (var child in t.Children)
            {
                var rendered = child switch
                {
                    ToolItemViewModel childTool => RenderTool(childTool),
                    EditItemViewModel childEdit => RenderEdit(childEdit),
                    _ => child.CopyText,
                };
                if (string.IsNullOrEmpty(rendered))
                    continue;
                sb.AppendLine().AppendLine();
                sb.Append(Indent(rendered!, "  "));
            }

            return sb.ToString();
        }

        /// <summary>Prefixes every line, so nested content stays inside its parent's list item.</summary>
        private static string Indent(string text, string prefix) =>
            prefix + string.Join("\n" + prefix, text.Split('\n'));

        private static string RenderEdit(EditItemViewModel e)
        {
            var sb = new StringBuilder();
            sb.Append("**Edit:** `").Append(e.DisplayPath).Append('`');
            if (e.HasOperation)
                sb.Append(" *(").Append(e.Operation).Append(")*");
            // The agent's stated reason, mirroring the tool rows' description subtitle.
            if (e.HasIntent)
                sb.AppendLine().AppendLine().Append('*').Append(e.Intent).Append('*');
            // ...and how it was permitted, as the tool rows carry it. This is the half of the export that
            // matters most: a card is a file the agent CHANGED, so "was anyone asked?" is a sharper
            // question here than on a call that only read something.
            if (e.HasPermissionSummary)
                sb.AppendLine().AppendLine().Append(e.PermissionSummary);
            return sb.ToString();
        }

        private static string RenderPlan(PlanItemViewModel p)
        {
            var sb = new StringBuilder("**Plan:**");
            if (p.HasTitle)
                sb.Append(' ').Append(p.Title);
            sb.AppendLine();
            foreach (var task in p.Tasks)
                sb.AppendLine().Append("- [").Append(task.Completed ? 'x' : ' ').Append("] ").Append(task.Description);
            return sb.ToString();
        }

        private static string RenderCrew(CrewItemViewModel c)
        {
            var sb = new StringBuilder("**Crew:**");
            if (c.HasTitle)
                sb.Append(' ').Append(c.Title);
            sb.AppendLine();
            foreach (var agent in c.Agents)
            {
                sb.AppendLine().Append("- [").Append(agent.IsRunning ? ' ' : 'x').Append("] ").Append(agent.Name);
                // The result is markdown itself; blockquoted and indented so it stays inside the
                // list item without its own headings/fences breaking the surrounding document.
                if (agent.HasResult)
                    sb.AppendLine().Append(Quote(agent.Result!.Trim(), indent: "  "));
            }
            return sb.ToString();
        }

        private static string RenderTestRun(TestRunResultItemViewModel r)
        {
            var sb = new StringBuilder();
            sb.Append("**Tests:** ").Append(r.Summary);
            if (r.HasFailures)
            {
                sb.AppendLine();
                foreach (var f in r.Failures)
                {
                    sb.AppendLine().Append("- ✗ ").Append(f.Name);
                    if (f.HasLocation)
                        sb.Append(" (`").Append(f.LocationLabel).Append("`)");
                    // Where the test itself is, when it threw elsewhere (a shared helper, a BDD step) —
                    // the export is the durable record, so it carries both halves the card offers.
                    if (f.HasTestLocation)
                        sb.Append(" — test: `").Append(f.TestLocationLabel).Append('`');
                }
                if (r.Truncated)
                    sb.AppendLine().Append("- …more failures not shown");
            }
            return sb.ToString();
        }

        /// <summary>Appends <paramref name="body"/> as a fenced code block (fence longer than any
        /// backtick run inside, so embedded ``` sequences can't terminate it early).</summary>
        private static void AppendFenced(StringBuilder sb, string body)
        {
            var fence = Fence(body);
            sb.AppendLine().AppendLine().AppendLine(fence).AppendLine(body.TrimEnd()).Append(fence);
        }

        private static string Fence(string body)
        {
            int run = 0, max = 2;
            foreach (var c in body)
            {
                run = c == '`' ? run + 1 : 0;
                if (run > max)
                    max = run;
            }
            return new string('`', max + 1);
        }

        private static string Quote(string text, string indent = "")
        {
            var sb = new StringBuilder();
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                sb.Append(indent).Append(line.Length == 0 ? ">" : "> " + line).AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }
}
