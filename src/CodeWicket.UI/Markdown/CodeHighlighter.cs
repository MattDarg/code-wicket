using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Documents;
using ColorCode;
using ColorCode.Common;
using ColorCode.Compilation;
using ColorCode.Parsing;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// The syntax-highlighting seam. Fenced code blocks funnel through <see cref="AppendTo"/>,
    /// which renders the code as coloured <see cref="Run"/>s whose Foreground is attached via
    /// DynamicResource to the <c>Chat.Code.*</c> keys — so the default palette lives in
    /// DefaultTheme.xaml and the VSIX can override it with the user's live editor colours.
    /// The engine behind the seam is ColorCode.Core (pure-managed lexical tokenizer, zero
    /// package dependencies); like <see cref="EmojiText"/>, swapping the engine (e.g.
    /// AvalonEdit's HighlightingManager for more languages) is a one-file change.
    /// </summary>
    internal static class CodeHighlighter
    {
        /// <summary>
        /// Appends <paramref name="code"/> to <paramref name="target"/> as syntax-coloured Runs.
        /// An empty/unknown <paramref name="languageTag"/> (or any tokenizer failure) falls back
        /// to a single plain Run — identical to the un-highlighted rendering, so a bare fence is
        /// a zero-regression no-op. The caller's InlineCollection must be dedicated to this code
        /// block (it is only ever appended to, never rolled back).
        /// </summary>
        /// <param name="budget">
        /// The document's remaining tokenize allowance (see <see cref="HighlightBudget"/>), or null
        /// for a caller rendering one block on its own. Once spent, every further block in that
        /// document renders plain and is NOT cached - it was never tried.
        /// </param>
        public static void AppendTo(InlineCollection target, string code, string? languageTag, HighlightBudget? budget = null)
        {
            if (code.Length == 0)
                return;

            var language = ResolveLanguage(languageTag);
            var segments = language == null ? null : TryTokenize(code, language, budget);
            if (segments == null)
            {
                target.Add(new Run(code));
                return;
            }

            // SQL gets language-scoped keys where VS's SQL editor palette visibly diverges from
            // the core editor classifications (red strings, magenta system functions).
            var isSql = language!.Id == LanguageId.Sql;
            foreach (var segment in segments)
            {
                var run = new Run(segment.Text);
                var brushKey = MapScopeToBrushKey(segment.ScopeName, isSql);
                if (brushKey != null)
                    run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
                target.Add(run);
            }
        }

        private static ILanguage? ResolveLanguage(string? languageTag)
        {
            if (string.IsNullOrWhiteSpace(languageTag))
                return null;
            try
            {
                // FindById matches ids AND aliases (cs/c#/js/ts/py/xaml→xml, ...); unknown → null.
                return Languages.FindById(languageTag!.Trim());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Null means "render plain" — the engine failed on this input, or ran out of time.
        /// Answers from <see cref="Cache"/> where it can; see that field for why that matters.
        /// </summary>
        private static List<Segment>? TryTokenize(string code, ILanguage language, HighlightBudget? budget)
        {
            var key = language.Id + "\u0000" + code;
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var cached))
                    return cached;
                if (Failures.Contains(key))
                    return null;
            }

            // Not cached and the document has spent its allowance: plain, and NOT remembered as a
            // failure - it was never tried, and a later render with allowance to spare will try it.
            if (budget is { Exhausted: true })
                return null;

            var clock = Stopwatch.StartNew();
            var segments = Tokenize(code, language);
            budget?.Charge(clock.Elapsed);

            lock (CacheLock)
            {
                if (segments is null)
                {
                    // Failures live APART from successes and are never evicted by them (pre-release
                    // security review, September 2026). With one dictionary cleared at capacity, a pane
                    // holding more than the capacity in ordinary blocks emptied the cache under the
                    // one entry that must not be retried, and the deadline was paid again on the
                    // next rebuild - the tax the cache exists to remove, back for exactly the input
                    // it was built for. A failure is a key and nothing else, so this store can be
                    // much larger than the success cache for the same memory.
                    if (Failures.Count >= FailureCapacity)
                        Failures.Clear();
                    Failures.Add(key);
                }
                else
                {
                    // Crude eviction: every success costs about the same to rebuild and a pane's
                    // working set of code blocks is small, so a clear is cheaper to reason about
                    // than an LRU — and since unclosed fences never reach here
                    // (MarkdownFlowRenderer.RenderCodeBlock), the cache only ever sees settled text
                    // and so does not churn on a streaming reply.
                    if (Cache.Count >= CacheCapacity)
                        Cache.Clear();
                    Cache[key] = segments;
                }
            }

            return segments;
        }

        /// <summary>
        /// How much tokenize time ONE document render may spend before the rest of its fenced blocks
        /// render plain (pre-release security review, September 2026). <see cref="TokenizeTimeout"/> bounds a block and the
        /// cache bounds a block's lifetime, but neither bounds a RENDER: a message with N distinct
        /// blocks that cannot tokenize cost N deadlines on its first render, and with the cache
        /// thrashing (see <see cref="Failures"/>) on every render after. This is the per-render
        /// bound. Two deadlines' worth: enough for hundreds of ordinary blocks (well under a
        /// millisecond each) and at most two pathological ones per render, after which the rest
        /// wait their turn - each render caches what it tried, so a document converges to free in
        /// N/2 renders rather than staying at N deadlines forever. Cache hits cost nothing against it.
        /// </summary>
        internal static readonly TimeSpan DocumentTokenizeBudget = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// One document render's tokenize allowance. Created by the flow renderer per document and
        /// threaded to every <see cref="AppendTo"/> in it; the highlighter charges each UNCACHED
        /// tokenize against it and stops trying once it is spent.
        /// </summary>
        internal sealed class HighlightBudget
        {
            private TimeSpan _remaining;

            public HighlightBudget(TimeSpan allowance) => _remaining = allowance;

            public bool Exhausted => _remaining <= TimeSpan.Zero;

            public void Charge(TimeSpan spent) => _remaining -= spent;
        }

        private static List<Segment>? Tokenize(string code, ILanguage language)
        {
            try
            {
                return new SegmentCollector().Tokenize(code, language);
            }
            catch
            {
                // The tokenizer runs over code it cannot assume is complete — it must never take
                // down rendering. RegexMatchTimeoutException derives from TimeoutException and so
                // lands here too, which is what turns issue #177's runaway match into the ordinary
                // plain-Run fallback rather than a frozen IDE.
                return null;
            }
        }

        /// <summary>
        /// Ceiling on a single ColorCode match (issue #177). ColorCode's JSON master pattern nests
        /// quantifiers over the same character class — <c>[^"\\]*</c> appears both before and inside
        /// the repeating group — so a string literal with no closing quote gives the engine
        /// exponentially many ways to split the run and no way to match any of them. A user's
        /// 570-character truncated JSON block froze Visual Studio outright.
        ///
        /// <para><b>A timeout is the only thing that terminates such a match.</b> A backtracking
        /// <see cref="Regex"/> is not cancellable, so moving the tokenize to a worker thread would
        /// trade a frozen UI for a core spinning forever on a thread nothing can reclaim; and the
        /// <c>catch</c> in <see cref="Tokenize"/> — written for exactly this class of input — is
        /// structurally unable to help, because a hang raises nothing.</para>
        ///
        /// <para>Budget: a legitimate block tokenizes in well under a millisecond, and this runs on
        /// MarkdownText's ~100ms render tick, so 50ms is generous for real work and small against
        /// the tick. .NET only tests the deadline periodically, so the true ceiling is somewhat
        /// higher (~66ms measured on the reported payload) — which is why it is paid at most once
        /// per distinct block rather than once per tick.</para>
        /// </summary>
        internal static readonly TimeSpan TokenizeTimeout = TimeSpan.FromMilliseconds(50);

        private const int CacheCapacity = 64;

        /// <summary>
        /// The failure store's cap. A failure entry is its key alone, so this can be far above
        /// <see cref="CacheCapacity"/> for the same memory - and it has to be: it is the number of
        /// distinct pathological blocks a pane can hold before the deadline is paid twice for one.
        /// </summary>
        private const int FailureCapacity = 1024;

        private static readonly object CacheLock = new object();

        /// <summary>
        /// Tokenize results, keyed by language id + the exact code text — <b>failures (a null entry)
        /// included, which is the point of it</b>.
        ///
        /// <para><b>What it is for: a block that can never tokenize pays
        /// <see cref="TokenizeTimeout"/> once, not once per rebuild.</b> <see cref="MarkdownText"/>
        /// re-renders the whole message on every throttled tick, so without the null entry a
        /// permanently truncated block would spend 50ms against a 100ms interval — half the UI
        /// thread, for as long as the message is on screen. The timeout stops the hang; this stops
        /// the tax, and that alone is what earns the cache its place.</para>
        ///
        /// <para><b>What it is NOT: a material rendering speedup — do not reach for this shape
        /// expecting one.</b> It does make repeat tokenizes of already-settled blocks free, but
        /// those were cheap to begin with. Measured: ten repeat <see cref="AppendTo"/> calls over a
        /// well-formed ~5,600-char JSON block cost 158.5ms against 16.3ms for the first — i.e. no
        /// cheaper at all, because the cost is dominated by building the <see cref="Run"/>s and
        /// their resource references, which is not cached and <i>cannot</i> be: every render needs
        /// its own. That agrees with issue #86, which put <c>attach</c> at 95–98% of a render.</para>
        /// </summary>
        private static readonly Dictionary<string, List<Segment>> Cache =
            new Dictionary<string, List<Segment>>(StringComparer.Ordinal);

        /// <summary>
        /// The blocks that could not tokenize - the entries the cache exists for - kept apart from
        /// <see cref="Cache"/> so that ordinary blocks can never evict them. See TryTokenize.
        /// </summary>
        private static readonly HashSet<string> Failures = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Test seam: the caches are process-wide, so a timing assertion must start cold.</summary>
        internal static void ClearCacheForTests()
        {
            lock (CacheLock)
            {
                Cache.Clear();
                Failures.Clear();
            }
        }

        // ColorCode reports scope names per token; we bucket them onto a deliberately small brush
        // palette (keyword/string/comment/number/type/function) that the VSIX can source from VS's
        // live classification format map, plus Sql.* variants where VS's SQL editor palette visibly
        // differs from the core classifications. Unmapped scopes inherit Chat.Foreground.
        private static string? MapScopeToBrushKey(string? scopeName, bool isSql)
        {
            switch (scopeName)
            {
                case ScopeName.Keyword:
                case ScopeName.ControlKeyword:
                case ScopeName.PseudoKeyword:
                case ScopeName.PreprocessorKeyword:
                case ScopeName.JsonConst:
                case ScopeName.XmlName:
                case ScopeName.HtmlElementName:
                case ScopeName.HtmlTagDelimiter:
                    return "Chat.Code.Keyword";

                case ScopeName.String:
                case ScopeName.StringCSharpVerbatim:
                case ScopeName.StringEscape:
                case ScopeName.JsonString:
                case ScopeName.XmlAttributeValue:
                case ScopeName.XmlAttributeQuotes:
                case ScopeName.HtmlAttributeValue:
                case ScopeName.CssPropertyValue:
                    return isSql ? "Chat.Code.Sql.String" : "Chat.Code.String";

                case ScopeName.Comment:
                case ScopeName.XmlComment:
                case ScopeName.HtmlComment:
                case ScopeName.XmlDocComment:
                case ScopeName.XmlDocTag:
                    return "Chat.Code.Comment";

                case ScopeName.Number:
                case ScopeName.JsonNumber:
                    return "Chat.Code.Number";

                case ScopeName.ClassName:
                case ScopeName.Type:
                case ScopeName.TypeVariable:
                case ScopeName.NameSpace:
                case ScopeName.Constructor:
                case ScopeName.Predefined:
                case ScopeName.Intrinsic:
                case ScopeName.BuiltinValue:
                case ScopeName.PowerShellType:
                case ScopeName.JsonKey:
                case ScopeName.XmlAttribute:
                case ScopeName.HtmlAttributeName:
                case ScopeName.CssSelector:
                case ScopeName.CssPropertyName:
                    return "Chat.Code.Type";

                // SqlSystemFunction only ever occurs in SQL, so it goes straight to the SQL key.
                case ScopeName.SqlSystemFunction:
                    return "Chat.Code.Sql.Function";

                case ScopeName.BuiltinFunction:
                case ScopeName.PowerShellCommand:
                    return "Chat.Code.Function";

                default:
                    return null;
            }
        }

        /// <summary>
        /// The parser <see cref="SegmentCollector"/> runs on — identical to the one ColorCode would
        /// build for itself, except that every language it compiles carries
        /// <see cref="TokenizeTimeout"/>.
        /// </summary>
        private static readonly ILanguageParser TimeoutParser =
            new LanguageParser(new TimeoutLanguageCompiler(), new GlobalLanguageRepository());

        /// <summary>
        /// ColorCode builds each language's master pattern with <c>new Regex(pattern)</c> — no
        /// options argument and, decisively, <b>no match timeout</b> — and caches it in a static
        /// dictionary. That constructor is not ours to change, but every part needed to substitute
        /// our own is public: this delegates to a real <see cref="LanguageCompiler"/> over
        /// <b>our own</b> cache and lock, then swaps in an equivalent Regex that carries a deadline.
        ///
        /// <para>Because the dictionary is ours, ColorCode's own static cache is never mutated —
        /// nothing else in the process (this is an IDE; other extensions share the assembly) can
        /// observe a different Regex.</para>
        /// </summary>
        private sealed class TimeoutLanguageCompiler : ILanguageCompiler
        {
            private readonly LanguageCompiler _inner = new LanguageCompiler(
                new Dictionary<string, CompiledLanguage>(),
                new ReaderWriterLockSlim());

            public CompiledLanguage Compile(ILanguage language)
            {
                var compiled = _inner.Compile(language);

                // Regex.ToString() returns the pattern and Options the flags, so the rebuild is
                // faithful — ColorCode's flags are inline in the pattern itself ((?x), (?-xis)(?m)),
                // which is why Options comes back None and why the constructor took no options.
                // The inner compiler hands back the same cached instance every time, so this
                // replaces the Regex once per language and is a reference test thereafter. A race
                // between two threads on that first call costs an extra identical Regex, nothing
                // more: the assignment is a reference store and either instance is correct.
                if (compiled.Regex.MatchTimeout != TokenizeTimeout)
                {
                    compiled.Regex = new Regex(
                        compiled.Regex.ToString(), compiled.Regex.Options, TokenizeTimeout);
                }

                return compiled;
            }
        }

        /// <summary>
        /// How <see cref="LanguageParser"/> resolves an <i>embedded</i> language — the JavaScript
        /// inside an HTML <c>&lt;script&gt;</c>, the C# inside an .aspx. CodeColorizerBase's default
        /// parser uses ColorCode's own static registry for this; supplying our own parser means
        /// supplying this too, and delegating straight back to those statics is what keeps
        /// embedded-language highlighting byte-identical to before the timeout was added.
        /// </summary>
        private sealed class GlobalLanguageRepository : ILanguageRepository
        {
            public IEnumerable<ILanguage> All => Languages.All;

            public ILanguage FindById(string languageId) => Languages.FindById(languageId);

            public void Load(ILanguage language) => Languages.Load(language);
        }

        // No 'record' types in this project (no IsExternalInit polyfill) — plain struct.
        private readonly struct Segment
        {
            public Segment(string text, string? scopeName)
            {
                Text = text;
                ScopeName = scopeName;
            }

            public string Text { get; }
            public string? ScopeName { get; }
        }

        /// <summary>
        /// Adapts ColorCode's push-parser into a flat segment list. The parser hands the source
        /// back in sequential chunks, each with a scope tree whose indices are relative to that
        /// chunk; flattening is depth-first so a child scope's colour wins over its parent's for
        /// the child's span. Indices are clamped defensively (the whole tokenize is also wrapped
        /// in try/catch by the caller).
        /// </summary>
        private sealed class SegmentCollector : CodeColorizerBase
        {
            private readonly List<Segment> _segments = new List<Segment>();

            // Null styles = ColorCode's defaults. The PARSER is ours, and that is issue #177's fix:
            // the default one would compile each language's master pattern with no match timeout,
            // which is unrecoverable rather than merely slow. TimeoutParser holds the compiled-language
            // cache, so per-call construction of this collector stays cheap.
            public SegmentCollector() : base(null!, TimeoutParser)
            {
            }

            public List<Segment> Tokenize(string code, ILanguage language)
            {
                languageParser.Parse(code, language, (parsed, scopes) => Write(parsed, scopes));
                return _segments;
            }

            protected override void Write(string parsedSourceCode, IList<Scope> scopes)
            {
                EmitSpan(parsedSourceCode, 0, parsedSourceCode.Length, null, scopes);
            }

            private void EmitSpan(string text, int start, int end, string? scopeName, IList<Scope>? children)
            {
                var cursor = start;
                if (children != null)
                {
                    foreach (var child in children.OrderBy(s => s.Index))
                    {
                        var childStart = Math.Max(child.Index, cursor);
                        var childEnd = Math.Min(child.Index + child.Length, end);
                        if (childEnd <= childStart)
                            continue;
                        if (childStart > cursor)
                            Add(text, cursor, childStart, scopeName);
                        EmitSpan(text, childStart, childEnd, child.Name, child.Children);
                        cursor = childEnd;
                    }
                }
                if (cursor < end)
                    Add(text, cursor, end, scopeName);
            }

            private void Add(string text, int start, int end, string? scopeName) =>
                _segments.Add(new Segment(text.Substring(start, end - start), scopeName));
        }
    }
}
