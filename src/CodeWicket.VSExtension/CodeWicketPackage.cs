using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using CodeWicket.Core;
using Task = System.Threading.Tasks.Task;

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// The VSIX package: registers the chat tool window and the command that opens it, and loads
    /// in the background. Holds no engine state itself — each <see cref="ChatToolWindow"/> owns its
    /// own engine connection.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ChatToolWindow), Style = VsDockStyle.Tabbed, Orientation = ToolWindowOrientation.Right)]
    // Settings have TWO renderers over ONE model (ExtensionConfig over config.json): the unified-settings
    // region below on VS 2026, and the classic pages here, which are the only settings UI on VS 2022 -
    // its Options dialog renders a classic page or nothing, never an external region (see SettingsPages).
    // The placeholder Unified Settings would otherwise draw for each of these on 18.x is suppressed in
    // CodeWicket.SettingsManifest.pkgdef; that pairing is what NuGet ships on VS 2022.
    [ProvideOptionPage(typeof(GeneralOptionPage), SettingsPageIds.Category, "General", 0, 0, false)]
    [ProvideOptionPage(typeof(BackendsOptionPage), SettingsPageIds.Category, "Backends", 0, 0, false)]
    [ProvideOptionPage(typeof(PermissionsOptionPage), SettingsPageIds.Category, "Permissions", 0, 0, false)]
    [ProvideOptionPage(typeof(AdvancedOptionPage), SettingsPageIds.Category, "Advanced", 0, 0, false)]
    // Proffer the unified-settings provider (its GUID matches registration.json's callback.serviceId).
    [ProvideService(typeof(CodeWicketSettingsProvider), IsAsyncQueryable = true)]
    public sealed class CodeWicketPackage : AsyncPackage
    {
        public const string PackageGuidString = "1b202172-f8f1-4fa8-a765-d5fc4cc053be";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Register the external settings provider for the unified-settings region.
            AddService(typeof(CodeWicketSettingsProvider),
                (container, ct, type) => Task.FromResult<object>(new CodeWicketSettingsProvider(JoinableTaskFactory)),
                promote: true);

            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            // View, and the Extensions > Code Wicket menu (see the .vsct).
            await ShowChatWindowCommand.InitializeAsync(this);
            await OpenSettingsCommand.InitializeAsync(this);
            await OpenLogsCommand.InitializeAsync(this);
            await RestartCommand.InitializeAsync(this);
            await AboutCommand.InitializeAsync(this);
            await AddDebugContextCommand.InitializeAsync(this);
            // Advised at PACKAGE LOAD, not on window open: there is no accessor for a thread or a
            // frame anywhere on IVsDebugger..IVsDebugger10 (enumerated unfiltered), so the only
            // route to one is being told, and a user already stopped when the pane opens would
            // otherwise leave us with nothing until they stopped again.
            Ide.Ad7DebugEvents.Advise();
        }

        // --- Async tool window plumbing ---

        public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType) =>
            toolWindowType == typeof(ChatToolWindow).GUID ? this : null;

        protected override string GetToolWindowTitle(Type toolWindowType, int id) =>
            toolWindowType == typeof(ChatToolWindow) ? Branding.ChatWindowName : base.GetToolWindowTitle(toolWindowType, id);

        protected override Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken) =>
            Task.FromResult<object>(null);
    }
}
