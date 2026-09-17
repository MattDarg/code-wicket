using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using WpfTable = System.Windows.Documents.Table;
using WpfTableRow = System.Windows.Documents.TableRow;
using WpfTableCell = System.Windows.Documents.TableCell;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// Renders a markdown string into a WPF <see cref="FlowDocument"/> by walking Markdig's AST.
    /// We use the actively-maintained Markdig <em>parser</em> (its WPF renderer, Markdig.Wpf, has
    /// been dead since 2021) and own the WPF rendering here so the output follows the chat theme:
    /// colours/fonts are attached via <see cref="FrameworkContentElement.SetResourceReference"/> to
    /// the <c>Chat.*</c> DynamicResource keys, so a host re-theme (the VSIX mapping VS colours) flows
    /// through live. Covers the subset agents stream: headings, paragraphs, emphasis/strikethrough,
    /// inline + fenced code, ordered/bulleted lists, block quotes, links, thematic breaks, and
    /// simple pipe tables. Anything unrecognised falls back to its plain text.
    /// </summary>
    internal sealed class MarkdownFlowRenderer
    {
        // Deliberately modest: default CommonMark + strikethrough + pipe tables + bare autolinks
        // + task lists. (We only enable extras we actually render, to keep the mapping predictable.)
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UsePipeTables()
            .UseAutoLinks()
            .UseTaskLists()
            .Build();

        // Matches the FlowDocument FontSize below; inline renderers thread the effective size so
        // emoji drawings scale with their surrounding text (headings pass their larger size).
        private const double DefaultFontSize = 13;

        private readonly FileLinkContext? _links;
        private readonly ICollection<string>? _pendingReferences;

        // One tokenize allowance per document render (a renderer lives for exactly one Render call).
        // What it bounds is the RENDER, which neither the per-block deadline nor the cache does -
        // see CodeHighlighter.DocumentTokenizeBudget (pre-release security review, September 2026).
        private readonly CodeHighlighter.HighlightBudget _highlightBudget =
            new CodeHighlighter.HighlightBudget(CodeHighlighter.DocumentTokenizeBudget);

        /// <summary>
        /// Themed values resolved once by the caller, or null to fall back to a resource reference per
        /// element. See <see cref="MarkdownPalette"/> — this field is the whole of issue #86's fix, and
        /// its null case is a live one (a viewer rendering out of the tree), not a defensive check.
        /// </summary>
        private readonly MarkdownPalette? _palette;

        private MarkdownFlowRenderer(
            FileLinkContext? links, ICollection<string>? pendingReferences, MarkdownPalette? palette)
        {
            _links = links;
            _pendingReferences = pendingReferences;
            _palette = palette;
        }

        /// <summary>
        /// Assigns a resolved value, or registers a resource reference when the palette could not answer.
        /// One place, so no call site can quietly keep the expensive path when the cheap one is available.
        /// </summary>
        private void Themed(
            DependencyObject element, DependencyProperty property, object? resolved, string key)
        {
            if (resolved is not null)
            {
                element.SetValue(property, resolved);
                return;
            }

            switch (element)
            {
                case FrameworkContentElement content:
                    content.SetResourceReference(property, key);
                    break;
                case FrameworkElement framework:
                    framework.SetResourceReference(property, key);
                    break;
            }
        }

        /// <param name="links">
        /// Where file references in the prose resolve and open; null renders them as plain text.
        /// </param>
        /// <param name="pendingReferences">
        /// Collects the references this pass could not answer from cache. The caller resolves them off
        /// the UI thread and re-renders — the renderer itself never does IO (see
        /// <see cref="FileReferenceResolver"/>).
        /// </param>
        /// <param name="palette">
        /// Themed values already resolved against a viewer that is in the tree. Null renders exactly as
        /// this always did — a resource reference per element — which is both the fallback for a detached
        /// viewer and what every caller with no viewer at all gets. See <see cref="MarkdownPalette"/>.
        /// </param>
        public static FlowDocument ToFlowDocument(
            string? markdown, FileLinkContext? links = null, ICollection<string>? pendingReferences = null,
            MarkdownPalette? palette = null)
            => new MarkdownFlowRenderer(links, pendingReferences, palette).Render(markdown);

        private FlowDocument Render(string? markdown)
        {
            // WPF's FlowDocument defaults TextAlignment to Justify, which stretches assistant
            // chat text edge-to-edge with uneven word spacing. Left-align so it reads naturally.
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = 13,
                TextAlignment = TextAlignment.Left,
            };
            // FontFamily is deliberately NOT set: the document inherits it from the ChatView root, which
            // is where the host's typeface lands (the VSIX points that at VS's environment font, the
            // standalone hosts at Chat.FontFamily). Pinning it here meant assistant text stayed Segoe UI
            // while the user's own bubbles and the input box followed the IDE — two typefaces in one
            // conversation. FontSize stays pinned above: size is the property with two levers (content
            // rides Ctrl+MouseWheel zoom, chrome rides the environment font), and typeface has one.
            Themed(doc, TextElement.ForegroundProperty, _palette?.Foreground, "Chat.Foreground");

            // Explicit non-null pattern: net472's string.IsNullOrEmpty lacks [NotNullWhen], so it
            // wouldn't narrow 'markdown' to non-null for the Parse call below.
            if (markdown is { Length: > 0 })
            {
                var parsed = Markdig.Markdown.Parse(markdown, Pipeline);
                foreach (var block in parsed)
                {
                    var rendered = RenderBlock(block);
                    if (rendered != null)
                        doc.Blocks.Add(rendered);
                }
            }

            return doc;
        }

        private WpfBlock? RenderBlock(MdBlock block)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    return RenderHeading(heading);

                case ParagraphBlock paragraph:
                {
                    var para = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                    AddInlines(para.Inlines, paragraph.Inline);
                    return para;
                }

                case ListBlock list:
                    return RenderList(list);

                case QuoteBlock quote:
                    return RenderQuote(quote);

                // FencedCodeBlock derives from CodeBlock, so this covers both.
                case CodeBlock code:
                    return RenderCodeBlock(code);

                case ThematicBreakBlock:
                    return RenderThematicBreak();

                case MdTable table:
                    return RenderTable(table);

                default:
                    // HTML blocks and anything else: surface any inline text rather than dropping it.
                    if (block is LeafBlock leaf && leaf.Inline != null)
                    {
                        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
                        AddInlines(para.Inlines, leaf.Inline);
                        return para;
                    }
                    return null;
            }
        }

        private WpfBlock RenderHeading(HeadingBlock heading)
        {
            var size = heading.Level switch { 1 => 19.0, 2 => 17.0, 3 => 15.0, _ => 13.0 };
            var para = new Paragraph
            {
                FontSize = size,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, heading.Level <= 2 ? 8 : 6, 0, 4),
            };
            AddInlines(para.Inlines, heading.Inline, size);
            return para;
        }

        private WpfBlock RenderCodeBlock(CodeBlock code)
        {
            var para = new Paragraph
            {
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 2, 0, 6),
                FontSize = 12,
            };
            // The fence's language tag (```csharp → Info="csharp"; Markdig splits any trailing
            // words into Arguments). Indented/bare fences highlight nothing and render plain.
            //
            // A fence with NO closing marker is not highlighted at all (issue #177). While a reply
            // streams, the block currently arriving is unterminated on every one of MarkdownText's
            // ~10-a-second rebuilds — so it is precisely the syntactically incomplete input the
            // tokenizer is worst at, re-fed to it on every tick, and it can never be answered from
            // the tokenize cache because the text is different each time. Waiting for the fence to
            // close removes that whole case; what is left is content that is PERMANENTLY truncated
            // (the reported payload), which then simply never highlights instead of spending the
            // tokenize budget on every rebuild forever. Highlighting arrives when the block settles.
            var fence = code as FencedCodeBlock;
            var closed = fence is { ClosingFencedCharCount: > 0 };
            CodeHighlighter.AppendTo(para.Inlines, ExtractCode(code), closed ? fence!.Info : null, _highlightBudget);
            Themed(para, TextElement.FontFamilyProperty, _palette?.CodeFontFamily, "Chat.CodeFontFamily");
            Themed(para, TextElement.BackgroundProperty, _palette?.CodeBackground, "Chat.CodeBackground");
            return para;
        }

        private static string ExtractCode(CodeBlock code)
        {
            var sb = new StringBuilder();
            var lines = code.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                sb.Append(lines.Lines[i].Slice.ToString());
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        private WpfBlock RenderThematicBreak()
        {
            var rule = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
            Themed(rule, Border.BackgroundProperty, _palette?.Border, "Chat.Border");
            return new BlockUIContainer(rule) { Margin = new Thickness(0) };
        }

        private WpfBlock RenderQuote(QuoteBlock quote)
        {
            var section = new Section
            {
                Padding = new Thickness(10, 0, 0, 0),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Margin = new Thickness(0, 2, 0, 6),
            };
            Themed(section, TextElement.ForegroundProperty, _palette?.SubtleForeground, "Chat.SubtleForeground");
            Themed(section, WpfBlock.BorderBrushProperty, _palette?.Border, "Chat.Border");
            foreach (var child in quote)
            {
                var rendered = RenderBlock(child);
                if (rendered != null)
                    section.Blocks.Add(rendered);
            }
            return section;
        }

        private WpfBlock RenderList(ListBlock list)
        {
            var wpfList = new List
            {
                MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 2, 0, 6),
                // Left padding gives the bullet/number markers room; without it WPF clips them.
                Padding = new Thickness(22, 0, 0, 0),
            };
            // >= 1, not >= 0: WPF REFUSES StartIndex 0 ("'0' is not a valid value for property
            // 'StartIndex'"), and CommonMark accepts `0.` as an ordered list, so an agent replying with
            // a list numbered from zero threw out of the render. MarkdownRenderFirewall catches it, so
            // the failure is not a crash - it is the ENTIRE message dropping to plain text under
            // "Markdown formatting is unavailable for this message", for one character of input.
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start >= 1)
                wpfList.StartIndex = start;

            // GitHub-style task lists: the checkbox replaces the bullet, so suppress the marker.
            if (list.OfType<ListItemBlock>().Any(IsTaskItem))
                wpfList.MarkerStyle = TextMarkerStyle.None;

            foreach (var item in list.OfType<ListItemBlock>())
            {
                var listItem = new ListItem();
                foreach (var child in item)
                {
                    var rendered = RenderBlock(child);
                    if (rendered is Paragraph p)
                        p.Margin = new Thickness(0, 0, 0, 2); // compact rows within a list
                    if (rendered != null)
                        listItem.Blocks.Add(rendered);
                }
                if (listItem.Blocks.Count == 0)
                    listItem.Blocks.Add(new Paragraph());
                wpfList.ListItems.Add(listItem);
            }
            return wpfList;
        }

        private static bool IsTaskItem(ListItemBlock item) =>
            item.Count > 0 && item[0] is ParagraphBlock p
            && p.Inline?.FirstChild is Markdig.Extensions.TaskLists.TaskList;

        private WpfBlock RenderTable(MdTable table)
        {
            var wpfTable = new WpfTable { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 6) };
            var columnCount = table.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
            for (var i = 0; i < columnCount; i++)
                wpfTable.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            wpfTable.RowGroups.Add(group);

            foreach (var row in table.OfType<MdTableRow>())
            {
                var wpfRow = new WpfTableRow();
                foreach (var cell in row.OfType<MdTableCell>())
                {
                    var wpfCell = new WpfTableCell
                    {
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(5, 2, 5, 2),
                    };
                    Themed(wpfCell, WpfBlock.BorderBrushProperty, _palette?.Border, "Chat.Border");
                    if (row.IsHeader)
                        wpfCell.FontWeight = FontWeights.Bold;

                    foreach (var child in cell)
                    {
                        var rendered = RenderBlock(child);
                        if (rendered is Paragraph p)
                            p.Margin = new Thickness(0); // no inter-block gap inside a cell
                        if (rendered != null)
                            wpfCell.Blocks.Add(rendered);
                    }
                    wpfRow.Cells.Add(wpfCell);
                }
                group.Rows.Add(wpfRow);
            }
            return wpfTable;
        }

        private void AddInlines(InlineCollection target, ContainerInline? container, double fontSize = DefaultFontSize)
        {
            if (container == null)
                return;
            foreach (var inline in container)
                AppendInline(target, inline, fontSize);
        }

        private void AppendInline(InlineCollection target, MdInline inline, double fontSize)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AppendText(target, literal.Content.ToString(), fontSize, asCode: false);
                    break;

                case EmphasisInline emphasis:
                {
                    Span span;
                    if (emphasis.DelimiterChar == '~')
                        span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    else if (emphasis.DelimiterCount >= 2)
                        span = new Bold();
                    else
                        span = new Italic();
                    foreach (var child in emphasis)
                        AppendInline(span.Inlines, child, fontSize);
                    target.Add(span);
                    break;
                }

                case CodeInline code:
                    // Literal code stays literal: no emoji substitution inside inline code. File
                    // references DO apply — 85% of the ones agents actually write sit inside an inline
                    // code span, so this is the primary link path, not a secondary one.
                    AppendText(target, code.Content, fontSize, asCode: true);
                    break;

                case Markdig.Extensions.TaskLists.TaskList task:
                    target.Add(BuildTaskCheckBox(task.Checked));
                    break;

                case LinkInline link:
                    AppendLink(target, link, fontSize);
                    break;

                case AutolinkInline autolink:
                {
                    var url = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url;
                    target.Add(BuildHyperlink(new Run(autolink.Url), url, null));
                    break;
                }

                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : (WpfInline)new Run(" "));
                    break;

                case HtmlEntityInline entity:
                    // Entities can decode to emoji (e.g. &#x2705;), so they ride the emoji path too.
                    EmojiText.AppendTo(target, entity.Transcoded.ToString(), fontSize);
                    break;

                // Raw HTML/XML in prose (e.g. an un-fenced <PropertyGroup> in assistant text) parses as
                // HtmlInline; render its source text. Without this it fell to the default case, whose
                // ToString() on a Markdig node returns the CLR type name — so every swallowed tag showed
                // as a literal "Markdig.Syntax.Inlines.HtmlInline" in the transcript.
                case HtmlInline html:
                    target.Add(new Run(html.Tag));
                    break;

                case ContainerInline containerInline:
                    // Unknown container (e.g. HTML spans): render its children inline.
                    foreach (var child in containerInline)
                        AppendInline(target, child, fontSize);
                    break;

                default:
                {
                    var text = inline.ToString();
                    if (!string.IsNullOrEmpty(text))
                        target.Add(new Run(text));
                    break;
                }
            }
        }

        /// <summary>
        /// The one owner of literal-text decomposition. File references are split out first and the
        /// remaining segments go to the emoji seam — a path can't contain an emoji, so the order is
        /// safe, and keeping one owner is what stops two seams each assuming they see raw text.
        /// <paramref name="asCode"/> renders the segments as inline code (same styling either way, so a
        /// linked reference inside a code span still reads as code).
        /// </summary>
        private void AppendText(InlineCollection target, string text, double fontSize, bool asCode)
        {
            if (text.Length == 0)
                return;

            var matches = _links is null ? null : FileReferenceMatcher.Scan(text);
            if (matches is null || matches.Count == 0)
            {
                AppendPlain(target, text, fontSize, asCode);
                return;
            }

            var consumed = 0;
            foreach (var match in matches)
            {
                if (match.Index > consumed)
                    AppendPlain(target, text.Substring(consumed, match.Index - consumed), fontSize, asCode);
                AppendReference(target, text.Substring(match.Index, match.Length), match, fontSize, asCode);
                consumed = match.Index + match.Length;
            }
            if (consumed < text.Length)
                AppendPlain(target, text.Substring(consumed), fontSize, asCode);
        }

        private void AppendPlain(InlineCollection target, string text, double fontSize, bool asCode)
        {
            if (text.Length == 0)
                return;
            if (asCode)
            {
                target.Add(StyleAsCode(new Run(text)));
                return;
            }
            EmojiText.AppendTo(target, text, fontSize);
        }

        /// <summary>
        /// Renders one matched reference: a link when the resolver already knows where it points, plain
        /// text otherwise. An unknown one is recorded for the caller to resolve off-thread — nothing
        /// here touches the disk (see <see cref="FileReferenceResolver"/>).
        /// </summary>
        private void AppendReference(
            InlineCollection target, string display, FileReferenceMatch match, double fontSize, bool asCode)
        {
            if (_links is null || !_links.Resolver.TryGetResolved(match.PathText, out var fullPath))
            {
                _pendingReferences?.Add(match.PathText);
                AppendPlain(target, display, fontSize, asCode);
                return;
            }
            if (fullPath is null)
            {
                // Known not to resolve — generated code, an SDK file outside the workspace, or a token
                // that was never a file at all. Plain text is the honest render.
                AppendPlain(target, display, fontSize, asCode);
                return;
            }

            var run = new Run(display);
            if (asCode)
                StyleAsCode(run);
            var hyperlink = new Hyperlink(run);
            Themed(hyperlink, TextElement.ForegroundProperty, _palette?.LinkForeground, "Chat.LinkForeground");
            // Same rule as the web links: the real destination is always the tooltip. Here it also
            // answers "which Program.cs?" without a click.
            hyperlink.ToolTip = match.Line is { } line ? fullPath + ":" + line : fullPath;
            var target1 = fullPath;
            var openLine = match.OpenLine;
            hyperlink.Click += (_, e) =>
            {
                e.Handled = true;
                _links.Open(target1, openLine);
            };
            target.Add(hyperlink);
        }

        /// <summary>
        /// Inline code styling — <b>the highest-volume themed element in a document by a wide margin</b>,
        /// two per span and 258 spans in the message that made issue #86 measurable. Instance rather than
        /// static purely so it can see <see cref="_palette"/>.
        /// </summary>
        private Run StyleAsCode(Run run)
        {
            Themed(run, TextElement.FontFamilyProperty, _palette?.CodeFontFamily, "Chat.CodeFontFamily");
            Themed(run, TextElement.BackgroundProperty, _palette?.CodeBackground, "Chat.CodeBackground");
            return run;
        }

        private WpfInline BuildTaskCheckBox(bool isChecked)
        {
            var checkBox = new CheckBox
            {
                IsChecked = isChecked,
                // Display-only, GitHub-style: the agent's checklist state isn't editable here.
                IsHitTestVisible = false,
                IsTabStop = false,
            };
            Themed(checkBox, FrameworkElement.StyleProperty, _palette?.TaskCheckBox, "Chat.TaskCheckBox");
            var container = new InlineUIContainer(checkBox) { BaselineAlignment = BaselineAlignment.Center };
            // No trailing space: the literal that follows the checkbox keeps its own leading space.
            ChatClipboard.SetCopyText(container, isChecked ? "[x]" : "[ ]");
            return container;
        }

        private void AppendLink(InlineCollection target, LinkInline link, double fontSize)
        {
            if (link.IsImage)
            {
                AppendImage(target, link, fontSize);
                return;
            }

            var hyperlink = new Hyperlink();
            AddInlines(hyperlink.Inlines, link, fontSize);
            if (hyperlink.Inlines.Count == 0 && !string.IsNullOrEmpty(link.Url))
                hyperlink.Inlines.Add(new Run(link.Url));
            ConfigureHyperlink(hyperlink, link.Url, link.Title);
            target.Add(hyperlink);
        }

        private void AppendImage(InlineCollection target, LinkInline link, double fontSize)
        {
            // Policy (see MarkdownImages): data: URIs and files under the workspace root render
            // inline; remote images are never auto-fetched (exfiltration channel) — they fall
            // through to the alt text, upgraded to a hyperlink when the target is a safely navigable
            // web URL so the user can still opt to open the image. The root is the link context's:
            // the same one every file reference in the prose is contained by, and a renderer with no
            // link context has no root and so renders no file.
            var image = MarkdownImages.TryCreateImage(link.Url, _links?.Resolver.Root);
            if (image != null)
            {
                var container = new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Bottom };
                ChatClipboard.SetCopyText(container, ExtractPlainText(link));
                target.Add(container);
                return;
            }

            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && IsWebOrMail(uri))
            {
                var hyperlink = new Hyperlink();
                AddInlines(hyperlink.Inlines, link, fontSize);
                if (hyperlink.Inlines.Count == 0)
                    hyperlink.Inlines.Add(new Run(link.Url));
                ConfigureHyperlink(hyperlink, link.Url, link.Title);
                target.Add(hyperlink);
                return;
            }

            // Undecodable/disallowed and not web-openable: surface the alt text so nothing is lost.
            AddInlines(target, link, fontSize);
        }

        private static string ExtractPlainText(ContainerInline container)
        {
            var sb = new StringBuilder();
            foreach (var inline in container)
            {
                if (inline is LiteralInline literal)
                    sb.Append(literal.Content.ToString());
                else if (inline is ContainerInline nested)
                    sb.Append(ExtractPlainText(nested));
            }
            return sb.ToString();
        }

        private Hyperlink BuildHyperlink(WpfInline content, string? url, string? title)
        {
            var hyperlink = new Hyperlink(content);
            ConfigureHyperlink(hyperlink, url, title);
            return hyperlink;
        }

        private void ConfigureHyperlink(Hyperlink hyperlink, string? url, string? title)
        {
            Themed(hyperlink, TextElement.ForegroundProperty, _palette?.LinkForeground, "Chat.LinkForeground");

            // Link text (and the target) come from agent/model output, which can be adversarial
            // (prompt injection, a poisoned repo/page relayed back). Two guards:
            //  1) Only http/https/mailto are navigable. Anything else (file:, javascript:, custom
            //     protocol handlers, ...) stays inert styled text so a link can't shell-execute an
            //     arbitrary scheme via Process.Start(UseShellExecute).
            //  2) The real destination is always the tooltip, so it's visible on hover before a
            //     click — the visible link text can claim to point anywhere.
            var navigable = Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsWebOrMail(uri);
            if (navigable)
            {
                hyperlink.NavigateUri = uri;
                hyperlink.RequestNavigate += OnRequestNavigate;
            }

            var destination = navigable ? uri!.AbsoluteUri : url;
            hyperlink.ToolTip = (string.IsNullOrEmpty(title), string.IsNullOrEmpty(destination)) switch
            {
                (false, false) => title + "\n" + destination,
                (false, true) => title,
                (true, false) => destination,
                _ => null,
            };
        }

        private static bool IsWebOrMail(Uri uri) =>
            uri.Scheme == Uri.UriSchemeHttp
            || uri.Scheme == Uri.UriSchemeHttps
            || uri.Scheme == Uri.UriSchemeMailto;

        private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            e.Handled = true;
            try
            {
                // Defense in depth: re-check the scheme even though only safe links get here.
                if (e.Uri != null && e.Uri.IsAbsoluteUri && IsWebOrMail(e.Uri))
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Opening a link is best-effort; never crash the chat over it.
            }
        }
    }
}
