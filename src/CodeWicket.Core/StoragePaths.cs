using System;
using System.IO;

namespace CodeWicket.Core
{
    /// <summary>
    /// The roots of everything this product keeps on disk, derived from
    /// <see cref="Branding.StorageFolderName"/> so the folder is named in ONE place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It was named in twelve, across nine files and three assemblies — including three independent
    /// spellings of the log directory, one of which carried a comment explaining that it duplicated
    /// the others because <c>CodeWicket.Ide</c> may reference only Core. Putting the path IN Core
    /// retires that reason. A rename that caught two of the three would have split the logs across
    /// two roots with nothing failing.
    /// </para>
    /// <para>
    /// Lives beside <see cref="Branding"/> rather than inside it because that type is names and this
    /// one is IO: these members touch <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/>,
    /// and a caller on the
    /// startup-failure path needs the bare <c>const</c> instead (see the note on
    /// <see cref="LocalOrNull"/>'s siblings in <c>ChatToolWindow.WriteStartupError</c>).
    /// </para>
    /// </remarks>
    public static class StoragePaths
    {
        /// <summary>Roaming root: <c>%APPDATA%\&lt;product&gt;</c>. Config and saved sessions.</summary>
        public static string Roaming => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Branding.StorageFolderName);

        /// <summary>
        /// Local root: <c>%LOCALAPPDATA%\&lt;product&gt;</c>. Logs, attachments, workspaces, scratch —
        /// machine-local by design, so agent files never sync across machines.
        /// </summary>
        public static string Local => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Branding.StorageFolderName);

        /// <summary>
        /// The settings file's name under <see cref="Roaming"/>. Named here because two assemblies spell
        /// it: <c>ExtensionConfig</c> reads it, and <see cref="ProtectedPaths.MatchCommandText"/> looks
        /// for it on a command line — a rename caught by one of them would leave the guard matching a
        /// file nothing reads.
        /// </summary>
        public const string SettingsFileName = "config.json";

        /// <summary>Where every diagnostic log lives: <c>%LOCALAPPDATA%\&lt;product&gt;\logs</c>.</summary>
        public static string LogDirectory => Path.Combine(Local, "logs");

        /// <summary>
        /// The agent's workspace when no solution or folder is open: a stable per-user folder under
        /// <see cref="Local"/>. Deliberately NOT the system temp dir (OS scratch — predictable, shared,
        /// auto-cleaned) and NOT <see cref="Roaming"/> (agent files must not sync across machines).
        /// </summary>
        /// <remarks>
        /// Named HERE, beside <see cref="StubAgentWorkspace"/>, because the two are the only places
        /// under our state roots that an agent is handed as its own: <see cref="ProtectedPaths.Default"/>
        /// exempts exactly these from the guard that otherwise treats everything under
        /// <see cref="Roaming"/> and <see cref="Local"/> as the host's (pre-release security review,
        /// September 2026). A host composing the folder name itself would be a second spelling the exemption
        /// could silently disagree with.
        /// </remarks>
        public static string DefaultAgentWorkspace => Path.Combine(Local, "workspace");

        /// <summary>
        /// The stub IDE's workspace (<c>UseStubIde</c>) — kept apart from
        /// <see cref="DefaultAgentWorkspace"/> so stub experiments never mix with real default-workspace
        /// files. See that member for why the name lives here.
        /// </summary>
        public static string StubAgentWorkspace => Path.Combine(Local, "workspace-stub");

        /// <summary>
        /// <see cref="Local"/> (optionally plus <paramref name="subfolder"/>), or null when
        /// LocalAppData is unavailable — for the callers that point a SWEEP at their result.
        /// </summary>
        /// <remarks>
        /// Not a redundant overload. <see cref="Path.Combine(string, string)"/> of an empty first
        /// segment yields a RELATIVE path, so a sweep resolving one would run an age-and-budget delete
        /// pass against the process's current directory rather than a directory we own (issue #88).
        /// The two callers that need this — the attachment store and the diff scratch dir — already
        /// returned null for exactly that reason, and preserving their contract is not optional.
        /// <para>
        /// The subfolder is composed INSIDE the guard on purpose. Both callers wrapped the whole
        /// composition, not just the lookup, and a resolver that narrowed that to the lookup alone
        /// would let an exception escape into a caller whose entire contract is "null rather than a
        /// path I do not trust".
        /// </para>
        /// </remarks>
        public static string? LocalOrNull(string? subfolder = null)
        {
            try
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(local))
                    return null;

                var root = Path.Combine(local, Branding.StorageFolderName);
                return string.IsNullOrEmpty(subfolder) ? root : Path.Combine(root, subfolder);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
