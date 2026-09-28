using CodeWicket.UI.ViewModels;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The user's route to a steer from the keyboard: Alt+Q to put the tray on Steer (when it is not
    /// already there), then Enter. Driven through the view-model's commands, since the key handler reads
    /// the live <c>Keyboard.Modifiers</c> and a test cannot synthesise Ctrl.
    /// </summary>
    internal static class TrayGestures
    {
        public static void EnterUnderSteer(this ChatViewModel vm)
        {
            if (vm.PendingReleaseMode != PendingRelease.NextStep)
                vm.TogglePendingReleaseCommand.Execute(null);

            vm.SendCommand.Execute(null);
        }
    }
}
