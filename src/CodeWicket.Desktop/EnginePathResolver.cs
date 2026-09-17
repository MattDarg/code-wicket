using System;
using System.IO;
using CodeWicket.Core;

namespace CodeWicket.Desktop
{
    /// <summary>
    /// Locates the built engine executable for the Desktop host. Honors the
    /// <c>CWKT_ENGINE_EXE</c> environment variable; otherwise walks up to the solution root and
    /// probes the engine's build output (Debug then Release). Dev-only convenience — the VSIX
    /// ships the engine alongside itself and resolves it directly.
    /// </summary>
    internal static class EnginePathResolver
    {
        private const string EngineExeName = Branding.EngineExeName;

        public static string Resolve()
        {
            var fromEnv = Environment.GetEnvironmentVariable("CWKT_ENGINE_EXE");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
                return fromEnv!;

            var solutionDir = FindSolutionDir(AppContext.BaseDirectory);
            if (solutionDir is not null)
            {
                foreach (var config in new[] { "Debug", "Release" })
                {
                    var candidate = Path.Combine(
                        solutionDir, "src", "CodeWicket.Engine", "bin", config, "net10.0", EngineExeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            throw new FileNotFoundException(
                $"Could not locate {EngineExeName}. Build the solution, or set CWKT_ENGINE_EXE to its full path.");
        }

        private static string? FindSolutionDir(string start)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CodeWicket.slnx")))
                    return dir.FullName;
                dir = dir.Parent;
            }

            return null;
        }
    }
}
