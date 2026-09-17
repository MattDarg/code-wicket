using System;
using System.IO;

namespace CodeWicket.Shell
{
    /// <summary>
    /// Resolves a scratch directory for dev-host working files (the Desktop host's smoke result and
    /// screenshot, the Console proofs' output). Defaults to a <c>scratch/</c> folder next to the
    /// running binary — which lives under the build output (<c>bin/</c>), already gitignored — so
    /// artifacts are easy to find next to the app and never tracked. Set the
    /// <c>CWKT_SCRATCH_DIR</c> environment variable to put them somewhere else.
    /// </summary>
    public static class HostScratch
    {
        /// <summary>Environment variable that overrides the scratch base directory.</summary>
        public const string OverrideEnvVar = "CWKT_SCRATCH_DIR";

        /// <summary>Returns (creating if needed) a scratch directory named <paramref name="name"/>.</summary>
        public static string ResolveDir(string name)
        {
            // Allow "console/acp"-style names while keeping separators native in the result.
            name = name.Replace('/', Path.DirectorySeparatorChar);

            var baseDir = Environment.GetEnvironmentVariable(OverrideEnvVar);
            if (string.IsNullOrWhiteSpace(baseDir))
                baseDir = Path.Combine(AppContext.BaseDirectory, "scratch");

            var dir = Path.Combine(baseDir, name);
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
