using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Classification;
using DColor = System.Drawing.Color;
using WColor = System.Windows.Media.Color;
using VsResourceKeys = Microsoft.VisualStudio.Shell.VsResourceKeys;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// Re-themes the shared WPF chat to match Visual Studio: overrides CodeWicket.UI's
    /// "Chat.*" DynamicResource brush keys with colors resolved from the live VS theme
    /// (EnvironmentColors), so text/background are always readable in dark/light/blue themes
    /// instead of the UI library's hardcoded default palette. Re-applies on theme switches.
    /// </summary>
    internal static class VsTheme
    {
        public static void Apply(FrameworkElement view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Refresh(view);
            ApplyThemedControlStyles(view);

            // A tool window's content is unloaded/reloaded whenever its tab is switched away and back,
            // so we must re-subscribe on every Load (not subscribe once) — otherwise the first tab
            // switch tears down the handler and later theme changes stop applying. Re-applying the
            // brushes on (re)subscribe also catches any theme change that happened while unloaded.
            var subscribed = false;
            void OnThemeChanged(ThemeChangedEventArgs e) => Refresh(view);
            // Fonts & Colors edits (and the editor's own late reaction to a theme switch) raise
            // the format map's changed event, not ThemeChanged — track both so the code-snippet
            // colours stay in step with the editor.
            void OnFormatMapChanged(object s, EventArgs e) => Refresh(view);
            void Subscribe()
            {
                if (!subscribed)
                {
                    VSColorTheme.ThemeChanged += OnThemeChanged;
                    var map = TryGetClassificationFormatMap();
                    if (map != null)
                        map.ClassificationFormatMappingChanged += OnFormatMapChanged;
                    subscribed = true;
                }
                SetBrushes(view);
            }
            void Unsubscribe()
            {
                if (subscribed)
                {
                    VSColorTheme.ThemeChanged -= OnThemeChanged;
                    var map = TryGetClassificationFormatMap();
                    if (map != null)
                        map.ClassificationFormatMappingChanged -= OnFormatMapChanged;
                    subscribed = false;
                }
            }

            if (view.IsLoaded)
                Subscribe();
            view.Loaded += (s, e) => Subscribe();
            view.Unloaded += (s, e) => Unsubscribe();
        }

        /// <summary>
        /// Re-styles the standard WPF controls we use (the picker ComboBoxes) with Visual Studio's
        /// themed styles, so their dropdown chrome/popup/items match the IDE theme instead of the
        /// default system look. Our buttons keep their own keyed styles, so they're unaffected; the
        /// VS combo style tracks theme switches itself (DynamicResource to VS theme keys). Applied as
        /// implicit styles scoped to the view, once it's in the visual tree (so the keys resolve).
        /// </summary>
        private static void ApplyThemedControlStyles(FrameworkElement view)
        {
            if (view.IsLoaded)
                SetComboStyles(view);
            else
                view.Loaded += OnLoaded;

            void OnLoaded(object sender, RoutedEventArgs e)
            {
                view.Loaded -= OnLoaded;
                SetComboStyles(view);
            }
        }

        private static void SetComboStyles(FrameworkElement view)
        {
            TrySetImplicitStyle(view, typeof(ComboBox), VsResourceKeys.ComboBoxStyleKey);
            TrySetImplicitStyle(view, typeof(ComboBoxItem), VsResourceKeys.ComboBoxItemStyleKey);
        }

        private static void TrySetImplicitStyle(FrameworkElement view, Type controlType, object styleKey)
        {
            try
            {
                if (view.TryFindResource(styleKey) is Style style)
                    view.Resources[controlType] = style;
            }
            catch
            {
                // Theming is best-effort; a missing/incompatible VS style must not break the window.
            }
        }

        private static void SetBrushes(FrameworkElement view)
        {
            // Only ever called from the UI thread (Apply, Loaded, theme/format-map change events).
            ThreadHelper.ThrowIfNotOnUIThread();
            var bg = ToWpf(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey));
            var fg = ToWpf(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowTextColorKey));
            var border = ToWpf(VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBorderColorKey));

            // Core readability: window background + text straight from VS.
            view.Resources["Chat.Background"] = Brush(bg);
            view.Resources["Chat.Foreground"] = Brush(fg);
            view.Resources["Chat.Border"] = Brush(border);
            view.Resources["Chat.SubtleForeground"] = Brush(Blend(fg, bg, 0.45));

            // Subtle bubble/panel tints derived from the theme so they read in any variant.
            view.Resources["Chat.UserBackground"] = Brush(Blend(bg, fg, 0.10));
            view.Resources["Chat.AssistantBackground"] = Brush(Blend(bg, fg, 0.04));
            view.Resources["Chat.ToolBackground"] = Brush(Blend(bg, fg, 0.06));
            view.Resources["Chat.EditBackground"] = Brush(Blend(bg, fg, 0.06));
            view.Resources["Chat.InputBackground"] = Brush(Blend(bg, fg, 0.06));
            // Rendered-markdown code tint: a touch stronger so inline code / fenced blocks stand out.
            view.Resources["Chat.CodeBackground"] = Brush(Blend(bg, fg, 0.09));

            // Editable-field focus border: the theme's selection highlight, so the focus outline on the
            // input + command-rule fields tracks dark/light/blue instead of the UI library's fixed blue.
            var highlight = ToWpf(VSColorTheme.GetThemedColor(EnvironmentColors.SystemHighlightColorKey));
            view.Resources["Chat.InputBorderFocus"] = Brush(highlight);

            // Accent + status colors: fixed values that read on both dark and light.
            view.Resources["Chat.AccentBackground"] = Brush(WColor.FromRgb(0x0E, 0x63, 0x9C));
            view.Resources["Chat.AccentForeground"] = Brush(Colors.White);
            view.Resources["Chat.AccentBackgroundHover"] = Brush(WColor.FromRgb(0x11, 0x77, 0xBB));
            view.Resources["Chat.ErrorForeground"] = Brush(WColor.FromRgb(0xF4, 0x87, 0x71));
            view.Resources["Chat.SuccessForeground"] = Brush(WColor.FromRgb(0x4E, 0xC9, 0x4E));
            view.Resources["Chat.LaunchedForeground"] = Brush(WColor.FromRgb(0x4D, 0xAA, 0xFE));
            // Hover tint for buttons/chips: theme-derived (a clear step off the background) so it tracks
            // dark/light instead of the UI library's fixed grey (which read as ~background in the VSIX).
            view.Resources["Chat.HoverBackground"] = Brush(Blend(bg, fg, 0.16));

            SetCodeBrushes(view);
        }

        // Cached per devenv session: the "text" (editor) format map is a stable singleton, and it's
        // queryable via MEF without hosting an editor. Null when MEF/editor services are unavailable.
        private static IClassificationFormatMap _classificationFormatMap;
        private static IClassificationTypeRegistryService _classificationRegistry;

        private static IClassificationFormatMap TryGetClassificationFormatMap()
        {
            if (_classificationFormatMap != null)
                return _classificationFormatMap;
            try
            {
                var componentModel = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
                if (componentModel == null)
                    return null;
                _classificationRegistry = componentModel.GetService<IClassificationTypeRegistryService>();
                _classificationFormatMap = componentModel.GetService<IClassificationFormatMapService>()
                    ?.GetClassificationFormatMap("text");
            }
            catch
            {
                // Theming is best-effort; the UI library's default code palette stays in place.
            }
            return _classificationFormatMap;
        }

        /// <summary>
        /// Syntax colours + font for fenced code blocks: reads the LIVE editor values off the
        /// classification format map, so chat snippets match the user's actual Fonts &amp; Colors
        /// / theme exactly. Best-effort — any failure leaves the UI library's default palette
        /// (tuned for the default dark look) in place.
        /// </summary>
        private static void SetCodeBrushes(FrameworkElement view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var map = TryGetClassificationFormatMap();
            if (map == null || _classificationRegistry == null)
                return;

            var keyword = GetClassificationBrush(map, "keyword");
            var str = GetClassificationBrush(map, "string");
            var comment = GetClassificationBrush(map, "comment");
            var number = GetClassificationBrush(map, "number");
            // "class name" / "method name" are Roslyn's classifications (registered whenever the
            // language services are loaded) — the same teal/yellow the C# editor uses.
            var type = GetClassificationBrush(map, "class name");
            var function = GetClassificationBrush(map, "method name");
            SetIfResolved(view, "Chat.Code.Keyword", keyword);
            SetIfResolved(view, "Chat.Code.String", str);
            SetIfResolved(view, "Chat.Code.Comment", comment);
            SetIfResolved(view, "Chat.Code.Number", number);
            SetIfResolved(view, "Chat.Code.Type", type);
            SetIfResolved(view, "Chat.Code.Function", function);

            // SQL editor palette (red strings, magenta system functions). The SQL editor is a
            // LEGACY language service (IVsColorableItem — confirmed by scanning
            // SqlLanguageServices.dll: "SQL String"/"SQL System Function" are colorable items,
            // not MEF classification exports), so the classification registry typically doesn't
            // know these names; the authoritative read is the Fonts & Colors storage (Text Editor
            // category), which also works before any .sql has opened. Classification map is still
            // tried first in case a shim registered them; final fallback is the shared bucket so
            // e.g. a light VS theme never keeps the UI library's dark-tuned SQL defaults (a
            // yellow COUNT/SUM in a SQL snippet = both SQL lookups failed, function fallback).
            SetIfResolved(view, "Chat.Code.Sql.String",
                GetClassificationBrush(map, "SQL String") ?? GetFontAndColorItemBrush("SQL String") ?? str);
            SetIfResolved(view, "Chat.Code.Sql.Function",
                GetClassificationBrush(map, "SQL System Function") ?? GetFontAndColorItemBrush("SQL System Function") ?? function);

            // Mirror the user's editor font (Tools > Options > Fonts and Colors > Text Editor)
            // onto the chat's code surfaces; a font change raises ClassificationFormatMappingChanged,
            // so this tracks live edits too.
            try
            {
                var defaults = map.DefaultTextProperties;
                if (defaults != null && !defaults.TypefaceEmpty && defaults.Typeface?.FontFamily != null)
                    view.Resources["Chat.CodeFontFamily"] = defaults.Typeface.FontFamily;
            }
            catch
            {
                // Best-effort; the UI library's Cascadia Mono default stays.
            }
        }

        /// <summary>
        /// Re-reads everything the IDE owns: theme brushes and the environment font. Both handlers below
        /// call this, so a Fonts &amp; Colors edit updates the pane's text size as well as its colours.
        /// A change to the Environment Font specifically may raise neither event, in which case it lands
        /// on the next tool-window load (Extensions &gt; Code Wicket &gt; Restart forces one).
        /// </summary>
        private static void Refresh(FrameworkElement view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SetBrushes(view);
            SetEnvironmentFont(view);
            // Rendered markdown holds RESOLVED values rather than resource references (issue #86 — the
            // references were costing ~1.8ms each to resolve at attach, ~621 per message, which is what
            // made a scrollbar drag stutter). A value does not follow its key, so the documents already
            // built have to be rebuilt against the two lines above or the transcript alone keeps the old
            // theme. Only realised viewers exist to find; the rest build against the new values anyway.
            CodeWicket.UI.Markdown.MarkdownText.RefreshThemedValues(view);
        }

        /// <summary>
        /// Mirrors VS's environment font (Tools &gt; Options &gt; Environment &gt; Fonts and Colors &gt;
        /// "Environment Font") onto the chat's ambient font keys, so the pane grows with the rest of the
        /// IDE when a user sizes it up.
        /// <para>
        /// This is the ONLY size lever the chat's chrome has. The Ctrl+MouseWheel zoom deliberately
        /// scales content only — the header pickers and status strip sit outside the zoom transform,
        /// matching the VS editor, whose zoom likewise leaves VS's chrome alone. Before this, raising
        /// the environment font grew every VS surface EXCEPT our tool window.
        /// </para>
        /// <para>
        /// It REFERENCES VS's font resources rather than copying their values, which is the whole trick:
        /// a copy was always one change behind (measured in Visual Studio, 2026-07-26 — switching the Environment Font from large
        /// back to 9 rendered the pane large). Our handlers fire before VS has finished republishing its
        /// font resources, so whatever we read at that moment is the *previous* font, and nothing fires
        /// again afterwards to correct it. A resource reference sidesteps the ordering entirely: WPF
        /// re-evaluates it whenever the dictionary changes, with no event of ours involved.
        /// </para>
        /// <para>
        /// Resolved through the view rather than <c>Application.Current</c> because
        /// <see cref="FrameworkElement.TryFindResource"/> walks the element tree first and falls back to
        /// application scope, which is where VS merges its font resources. The probe is what makes the
        /// fallback safe: an unresolvable reference would leave WPF's own property default (12) rather
        /// than the UI library's <c>Chat.FontSize</c>, so when the keys are absent we set nothing and the
        /// XAML's DynamicResource to the theme default stands.
        /// </para>
        /// </summary>
        private static void SetEnvironmentFont(FrameworkElement view)
        {
            try
            {
                // TextElement's inherited properties rather than Control's aliases of them: same
                // underlying DPs, but these don't require the view to actually be a Control.
                if (view.TryFindResource(VsFonts.EnvironmentFontFamilyKey) is FontFamily)
                    view.SetResourceReference(
                        System.Windows.Documents.TextElement.FontFamilyProperty, VsFonts.EnvironmentFontFamilyKey);

                // Guarded against a zero/absent size, which would collapse every glyph in the pane.
                if (view.TryFindResource(VsFonts.EnvironmentFontSizeKey) is double size && size > 0)
                    view.SetResourceReference(
                        System.Windows.Documents.TextElement.FontSizeProperty, VsFonts.EnvironmentFontSizeKey);
            }
            catch
            {
                // Best-effort, like the code-font mirror: the UI library's Segoe UI / 12 stays.
            }
        }

        private static void SetIfResolved(FrameworkElement view, string key, SolidColorBrush brush)
        {
            if (brush != null)
                view.Resources[key] = brush;
        }

        // The Fonts & Colors "Text Editor" category — where legacy language services' colorable
        // items (the SQL editor's palette) live.
        private static readonly Guid TextEditorFontCategory = new Guid("A27B4E24-A735-4D1D-B8E7-9716E1E3D8E0");

        private static SolidColorBrush GetFontAndColorItemBrush(string itemName)
        {
            // Always called from the UI thread (Loaded / theme-changed / format-map-changed).
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                var storage = Package.GetGlobalService(typeof(SVsFontAndColorStorage)) as IVsFontAndColorStorage;
                if (storage == null)
                    return null;
                var category = TextEditorFontCategory;
                if (storage.OpenCategory(ref category,
                        (uint)(__FCSTORAGEFLAGS.FCSF_READONLY | __FCSTORAGEFLAGS.FCSF_LOADDEFAULTS)) != VSConstants.S_OK)
                    return null;
                try
                {
                    var info = new ColorableItemInfo[1];
                    if (storage.GetItem(itemName, info) != VSConstants.S_OK || info[0].bForegroundValid == 0)
                        return null;
                    var colorRef = info[0].crForeground;
                    // COLORREF is 0x00BBGGRR; a nonzero high byte encodes auto/system/indexed
                    // colours we can't map directly — treat those as unresolved.
                    if ((colorRef & 0xFF000000u) != 0)
                        return null;
                    return Brush(WColor.FromRgb(
                        (byte)(colorRef & 0xFF),
                        (byte)((colorRef >> 8) & 0xFF),
                        (byte)((colorRef >> 16) & 0xFF)));
                }
                finally
                {
                    storage.CloseCategory();
                }
            }
            catch
            {
                return null;
            }
        }

        private static SolidColorBrush GetClassificationBrush(IClassificationFormatMap map, string classificationType)
        {
            try
            {
                var type = _classificationRegistry.GetClassificationType(classificationType);
                if (type == null)
                    return null;
                var properties = map.GetTextProperties(type);
                if (properties == null || properties.ForegroundBrushEmpty)
                    return null;
                return properties.ForegroundBrush is SolidColorBrush solid ? Brush(solid.Color) : null;
            }
            catch
            {
                // Per-key best-effort: a missing classification must not break the others.
                return null;
            }
        }

        private static WColor ToWpf(DColor c) => WColor.FromArgb(c.A, c.R, c.G, c.B);

        private static WColor Blend(WColor a, WColor b, double t) => WColor.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));

        private static SolidColorBrush Brush(WColor c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }
    }
}
