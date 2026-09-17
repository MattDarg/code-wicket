using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

// Compiled into the net472 VSIX (Nullable=disable) AND linked into the net10 test project
// (Nullable=enable), so the file pins its own setting rather than inheriting two different ones.
#nullable disable

namespace CodeWicket.VSExtension
{
    /// <summary>
    /// What a built binary says about itself: its version, the commit it was built from, and
    /// <b>when it was built</b> - read back from the build metadata that
    /// <c>src\BuildStamp.targets</c> stamps onto <c>AssemblyInformationalVersion</c>.
    /// </summary>
    /// <remarks>
    /// Issue #136. The About dialog used to answer "which build is this?" with
    /// <c>File.GetLastWriteTime</c>, which is when the file was <i>installed</i>: measured, a
    /// deploy stamps every file in the extension folder - shell and bundled engine alike - with
    /// one identical time, so the two rows it showed could not differ from each other and neither
    /// was a build time. A compile-time stamp is the only thing that survives being copied.
    /// <para>
    /// The stamp rides <c>InformationalVersion</c> because it has to be readable off the
    /// <b>engine</b>, which is net10 while this assembly is net472: an attribute would need the
    /// assembly loaded, whereas <c>InformationalVersion</c> lands in the Win32 version resource as
    /// <c>ProductVersion</c>, which <see cref="FileVersionInfo"/> reads from the file without
    /// loading it. One mechanism, both rows.
    /// </para>
    /// <para>
    /// Parsing is by SHAPE, not by position: build metadata is an unordered dot-separated set in
    /// semver, the SDK appends the commit itself, and a future identifier must not shift the
    /// meaning of the ones already there. An unrecognised identifier is ignored, and an
    /// unstamped binary parses to a stamp with no time rather than failing - the callers'
    /// fallback is to report the install time and <i>say</i> that is what it is.
    /// </para>
    /// </remarks>
    internal sealed class BuildStamp
    {
        /// <summary>What the timestamp identifier looks like: <c>yyyyMMddTHHmmZ</c>.</summary>
        private const string TimestampFormat = "yyyyMMdd'T'HHmm'Z'";

        /// <summary>Marks a build produced by the release workflow (see BuildStamp.targets).</summary>
        private const string CiIdentifier = "ci";

        private BuildStamp(string version, string commit, DateTime? builtUtc, bool continuousIntegration)
        {
            Version = version;
            Commit = commit;
            BuiltUtc = builtUtc;
            ContinuousIntegration = continuousIntegration;
        }

        /// <summary>The release version, with the build metadata stripped ("0.2.0"). Never null.</summary>
        public string Version { get; }

        /// <summary>The full commit sha, or null when the build had no source-control information.</summary>
        public string Commit { get; }

        /// <summary>When the binary was compiled, in UTC, or null if it carries no stamp.</summary>
        public DateTime? BuiltUtc { get; }

        /// <summary>True for a build produced by CI, which is what separates an official build
        /// from a local one of the same commit.</summary>
        public bool ContinuousIntegration { get; }

        /// <summary>The commit in git's short form, or null.</summary>
        public string ShortCommit =>
            Commit is null ? null : (Commit.Length > 7 ? Commit.Substring(0, 7) : Commit);

        /// <summary>
        /// The build's identity as one line - "2026-08-13 22:01 UTC (972afa5)" - or null when the
        /// binary carries no build time, which is the caller's cue to fall back and relabel.
        /// </summary>
        /// <remarks>
        /// UTC, spelled out: the whole point of this dialog is a copyable bug report, and a bare
        /// wall-clock time from another timezone is not comparable to a release's.
        /// </remarks>
        public string Describe()
        {
            if (BuiltUtc is null)
                return null;

            var text = BuiltUtc.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

            var detail = ShortCommit;
            if (ContinuousIntegration)
                detail = detail is null ? "CI" : detail + ", CI";

            return detail is null ? text : text + " (" + detail + ")";
        }

        /// <summary>Reads the stamp off a loaded assembly (this one).</summary>
        public static BuildStamp ForAssembly(Assembly assembly)
        {
            try
            {
                return Parse(assembly?
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
            }
            catch
            {
                return Parse(null);
            }
        }

        /// <summary>
        /// Reads the stamp off a file's version resource - no assembly load, so this works on the
        /// net10 engine from inside net472 devenv, and on a file that cannot be loaded at all.
        /// </summary>
        public static BuildStamp ForFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                    return null;

                return Parse(FileVersionInfo.GetVersionInfo(path).ProductVersion);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Splits an informational version - "0.2.0+&lt;sha&gt;.20260813T2201Z.ci" - into its parts.
        /// Never throws and never returns null; an unrecognised or absent value yields a stamp
        /// whose <see cref="Describe"/> is null.
        /// </summary>
        public static BuildStamp Parse(string informationalVersion)
        {
            if (string.IsNullOrWhiteSpace(informationalVersion))
                return new BuildStamp("unknown", null, null, false);

            var text = informationalVersion.Trim();
            var plus = text.IndexOf('+');
            if (plus < 0)
                return new BuildStamp(text, null, null, false);

            var version = plus > 0 ? text.Substring(0, plus) : "unknown";
            var metadata = text.Substring(plus + 1).Split('.');

            string commit = null;
            DateTime? builtUtc = null;
            var ci = false;

            foreach (var identifier in metadata)
            {
                if (identifier.Length == 0)
                    continue;

                if (builtUtc is null && TryParseTimestamp(identifier, out var stamped))
                    builtUtc = stamped;
                else if (commit is null && IsCommit(identifier))
                    commit = identifier;
                else if (string.Equals(identifier, CiIdentifier, StringComparison.OrdinalIgnoreCase))
                    ci = true;
            }

            return new BuildStamp(version, commit, builtUtc, ci);
        }

        private static bool TryParseTimestamp(string identifier, out DateTime utc)
        {
            // AssumeUniversal|AdjustToUniversal, or a stamp read on a machine in another timezone
            // would be shifted by its offset and then labelled "UTC" - wrong in a way nobody local
            // to the build could ever see.
            return DateTime.TryParseExact(
                identifier,
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc);
        }

        /// <summary>
        /// A git sha: hex, and long enough not to be mistaken for something else. Shape rather than
        /// position, so an identifier added beside it later cannot be read as the commit.
        /// </summary>
        private static bool IsCommit(string identifier)
        {
            if (identifier.Length < 7 || identifier.Length > 40)
                return false;

            foreach (var c in identifier)
            {
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex)
                    return false;
            }

            return true;
        }
    }
}
