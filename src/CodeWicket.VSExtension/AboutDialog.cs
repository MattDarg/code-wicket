using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using CodeWicket.Core;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The "Extensions &gt; Code Wicket &gt; About" dialog: which build is installed, and the
    /// handful of facts a bug report needs, in one copyable block.
    /// </summary>
    /// <remarks>
    /// Built in code rather than XAML to match how the tool window composes its own status content,
    /// and themed from <see cref="EnvironmentColors"/> so it reads correctly in every VS theme.
    /// <para>
    /// Everything shown is resolved from the running process (assembly metadata, the bundled engine's
    /// version resource, the shell's own version properties) - nothing is asked of the engine or a
    /// backend CLI. That keeps the dialog answerable when the product is broken, which is when someone
    /// opens About: a chat window that never started still has a version, and the engine's build stamp
    /// is exactly what diagnoses the stale-bundled-engine failure (see AGENTS.md).
    /// </para>
    /// <para>
    /// Both build rows come from <see cref="BuildStamp"/>, i.e. from a stamp put in at COMPILE time.
    /// They used to be <c>File.GetLastWriteTime</c>, which is when the file was installed - a deploy
    /// gives every file in the folder one identical timestamp, so the two rows could not differ from
    /// each other and neither was a build time (issue #136).
    /// </para>
    /// <para>
    /// It also holds no runtime dependency on our other assemblies: <see cref="Branding.ProductName"/>
    /// is a const and is inlined at compile time, so this dialog still opens on a machine where the
    /// missing-dependency startup failure is the thing being reported. Calling anything else in
    /// Core/Shell from here would quietly give that up.
    /// </para>
    /// </remarks>
    internal sealed class AboutDialog : DialogWindow
    {
        private const string RepositoryUrl = "https://github.com/MattDarg/code-wicket";

        private const string EngineRelativePath = EngineFolderName + @"\" + Branding.EngineExeName;

        /// <summary>The folder the VSIX bundles the engine into, beside the extension assembly.</summary>
        private const string EngineFolderName = "engine";

        /// <summary>Drawn size of the mark. Roughly the height of the name plus the tagline beside it.</summary>
        private const double MarkSize = 48;

        private AboutDialog(string version, IReadOnlyList<KeyValuePair<string, string>> rows, string details)
        {
            Title = "About " + Branding.ProductName;
            HasMaximizeButton = false;
            HasMinimizeButton = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var text = Themed(EnvironmentColors.ToolWindowTextColorKey);
            Background = Themed(EnvironmentColors.ToolWindowBackgroundColorKey);
            Foreground = text;

            var body = new StackPanel { Margin = new Thickness(16) };

            // Name and tagline left, mark right. A Grid rather than a DockPanel because the star
            // column is what keeps the mark hard against the right edge whatever the name's width -
            // and the name is about to get longer in other languages, not shorter.
            var header = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var lockup = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            lockup.Children.Add(new TextBlock
            {
                Text = Branding.ProductName,
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = text,
            });

            // Grey, unlike the detail rows below. Those are full-colour, so a full-colour tagline
            // would join that block and read as another fact about the build; grey keeps the top of
            // the dialog identity and the bottom of it data.
            lockup.Children.Add(new TextBlock
            {
                Text = Branding.Tagline,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = Themed(EnvironmentColors.SystemGrayTextColorKey),
            });
            header.Children.Add(lockup);

            var mark = BuildMark();
            if (mark != null)
            {
                Grid.SetColumn(mark, 1);
                header.Children.Add(mark);
            }

            body.Children.Add(header);

            body.Children.Add(BuildDetailGrid(rows, text));
            //TODO: body.Children.Add(BuildRepositoryLink());
            body.Children.Add(BuildButtons(details));

            Content = body;
        }

        /// <summary>
        /// Shows the dialog modally. <paramref name="shell"/> supplies the VS version; a null shell (or
        /// one that won't answer) just omits that row rather than failing the whole dialog.
        /// </summary>
        public static void Show(IVsShell shell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // The version half of the stamp is the number the VSIX installs under: it comes from the
            // ONE place it is authored, source.extension.vsixmanifest, via StampVersionFromVsixManifest
            // in the .csproj. Reading it back off assembly metadata rather than parsing a deployed
            // manifest keeps this free of IO and of assumptions about how the extension was deployed.
            var stamp = BuildStamp.ForAssembly(typeof(AboutDialog).Assembly);
            var version = stamp.Version;
            var enginePath = BundledEnginePath;
            var rows = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("Version", version),
                new KeyValuePair<string, string>("Build", DescribeBuild(stamp, ExtensionAssemblyPath)),
                new KeyValuePair<string, string>("Visual Studio", DescribeVisualStudio(shell)),
                new KeyValuePair<string, string>("Bundled engine", DescribeBuild(BuildStamp.ForFile(enginePath), enginePath)),
                new KeyValuePair<string, string>("Location", ExtensionDirectory),
            };

            // The version is a row now, so the header line drops it rather than stating it twice.
            var details = new StringBuilder()
                .AppendLine(Branding.ProductName);
            foreach (var row in rows)
                details.AppendLine(row.Key + ": " + row.Value);

            new AboutDialog(version, rows, details.ToString()).ShowModal();
        }

        /// <summary>
        /// The product mark, or null if it cannot be loaded — in which case the dialog simply draws
        /// without it.
        /// </summary>
        /// <remarks>
        /// Null rather than an exception because of what this dialog is FOR: it is what someone opens
        /// when the product is broken, so a decorative element must not be able to take it down. That
        /// is also why the image is an embedded Resource rather than a file beside the assembly — a
        /// file can be absent, a Resource cannot be if this assembly loaded at all.
        /// <para>
        /// The assembly name is read at runtime rather than written into the pack URI, so the .NET
        /// identity rename does not silently break this: a hardcoded name would still compile, still
        /// pass every offline check, and produce a dialog with no mark on it.
        /// </para>
        /// </remarks>
        private static Image BuildMark()
        {
            try
            {
                var assembly = typeof(AboutDialog).Assembly.GetName().Name;
                var source = new BitmapImage();
                source.BeginInit();
                source.UriSource = new Uri(
                    "pack://application:,,,/" + assembly + ";component/Resources/code-wicket-128.png",
                    UriKind.Absolute);
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.EndInit();
                source.Freeze();

                var image = new Image
                {
                    Source = source,
                    Width = MarkSize,
                    Height = MarkSize,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(12, 0, 0, 0),
                };

                // 128 down to 48: the default scaling mode is visibly soft at that ratio, and the
                // mark's whole construction is 8-unit strokes on a 128 grid.
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // --- The facts ---

        private static string ExtensionAssemblyPath => typeof(AboutDialog).Assembly.Location;

        private static string ExtensionDirectory
        {
            get
            {
                try
                {
                    return Path.GetDirectoryName(ExtensionAssemblyPath) ?? "unknown";
                }
                catch
                {
                    return "unknown";
                }
            }
        }

        /// <summary>
        /// The engine the VSIX ships. A <c>CWKT_ENGINE_EXE</c> override would point elsewhere, which
        /// is why the row is labelled "Bundled" rather than claiming to be the engine in use.
        /// </summary>
        private static string BundledEnginePath
        {
            get
            {
                var directory = ExtensionDirectory;
                return directory == "unknown" ? null : Path.Combine(directory, EngineRelativePath);
            }
        }

        /// <summary>
        /// A binary's build identity - the "which build am I actually running?" answer the tool window
        /// also shows while starting, where stale installs (Exp hive or double-click) have burned us
        /// before.
        /// </summary>
        /// <remarks>
        /// The fallback is the point of the method. A binary built before the stamp existed, or copied
        /// in by hand, has no build time to report - so rather than showing the file's timestamp as
        /// though it were one (issue #136, exactly the bug), it shows it under its true name. Being
        /// told the install time is useful; being told the install time and calling it a build is not.
        /// </remarks>
        private static string DescribeBuild(BuildStamp stamp, string path)
        {
            var described = stamp?.Describe();
            if (!string.IsNullOrEmpty(described))
                return described;

            try
            {
                // A missing bundled engine is a distinct answer from an unreadable one, and it is the
                // one that explains a chat window which never starts.
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return "not found";

                return "installed " + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm")
                    + " (build time unknown)";
            }
            catch
            {
                return "unknown";
            }
        }

        private static string DescribeVisualStudio(IVsShell shell)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // ReleaseVersion is the exact build ("18.7.36030.5 D18.7"); the brand name distinguishes a
            // Preview channel from the release one. Either may be absent on a shell that doesn't answer.
            var version = GetShellProperty(shell, (int)__VSSPROPID5.VSSPROPID_ReleaseVersion);
            var brand = GetShellProperty(shell, (int)__VSSPROPID5.VSSPROPID_AppBrandName);
            var edition = GetEdition();

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(brand))
                parts.Add(brand);
            if (!string.IsNullOrWhiteSpace(edition) && (brand is null || brand.IndexOf(edition, StringComparison.OrdinalIgnoreCase) < 0))
                parts.Add(edition);
            if (!string.IsNullOrWhiteSpace(version))
                parts.Add(version);

            return parts.Count == 0 ? "unknown" : string.Join(" ", parts);
        }

        private static string GetShellProperty(IVsShell shell, int propertyId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                if (shell is null)
                    return null;

                return shell.GetProperty(propertyId, out var value) == Microsoft.VisualStudio.VSConstants.S_OK
                    ? value as string
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Community / Professional / Enterprise, which the shell properties above don't carry.</summary>
        private static string GetEdition()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                return (Package.GetGlobalService(typeof(EnvDTE._DTE)) as EnvDTE._DTE)?.Edition;
            }
            catch
            {
                return null;
            }
        }

        // --- The dialog ---

        private UIElement BuildDetailGrid(IReadOnlyList<KeyValuePair<string, string>> rows, Brush text)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (var i = 0; i < rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = rows[i].Key,
                    Margin = new Thickness(0, 0, 12, 4),
                    Foreground = text,
                    // The label column is supporting detail; dimming it by opacity rather than by a
                    // second theme colour keeps this readable in every theme without a second key.
                    Opacity = 0.7,
                };
                Grid.SetRow(label, i);
                Grid.SetColumn(label, 0);
                grid.Children.Add(label);

                var value = new TextBlock
                {
                    Text = rows[i].Value,
                    Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = text,
                };
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);
            }

            return grid;
        }

        private UIElement BuildRepositoryLink()
        {
            var link = new Hyperlink(new Run(RepositoryUrl))
            {
                // NavigateUri is what makes this behave (and render) as a link; navigation is then
                // handled here rather than by WPF, which has no default navigator outside a frame.
                // Only RequestNavigate is handled - adding a Click handler as well would open the
                // browser twice, since both fire for one click.
                NavigateUri = new Uri(RepositoryUrl),
                Foreground = Themed(EnvironmentColors.ControlLinkTextColorKey),
            };
            link.RequestNavigate += (s, e) =>
            {
                e.Handled = true;
                OpenExternal(RepositoryUrl);
            };

            return new TextBlock(link) { Margin = new Thickness(0, 12, 0, 0) };
        }

        private UIElement BuildButtons(string details)
        {
            var copy = new Button
            {
                Content = "_Copy details",
                MinWidth = 92,
                Padding = new Thickness(12, 4, 12, 4),
            };
            copy.Click += (s, e) =>
            {
                // Copy is why the details are formatted as text at all: an About box's job here is to
                // make a bug report a paste rather than a transcription. A clipboard the shell is
                // holding can refuse the write, so a failure just leaves the button alone.
                try
                {
                    Clipboard.SetDataObject(details, copy: true);
                    copy.Content = "Copied";
                }
                catch
                {
                }
            };

            var close = new Button
            {
                Content = "Close",
                MinWidth = 92,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(12, 4, 12, 4),
                IsDefault = true,
                IsCancel = true,
            };
            close.Click += (s, e) => Close();

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0),
            };
            panel.Children.Add(copy);
            panel.Children.Add(close);
            return panel;
        }

        private static void OpenExternal(string url)
        {
            try
            {
                // UseShellExecute so the user's default browser handles it; Start returns null when an
                // already-running browser takes the request, which `using` tolerates.
                using (Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }))
                {
                }
            }
            catch
            {
            }
        }

        private static SolidColorBrush Themed(ThemeResourceKey key)
        {
            var color = VSColorTheme.GetThemedColor(key);
            var brush = new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }
}
