using System;
using System.Collections.Generic;
using System.Linq;
using CodeWicket.Providers.Acp;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The non-interactive env defaults applied to every spawned ACP agent process (issue #22:
    /// "git rebase --continue" inside Kiro's headless shell spawned vim, which hung the turn
    /// forever). Defaults must land when absent and never clobber anything already set —
    /// system env, EngineEnvironment, and config Env all outrank them.
    /// </summary>
    public sealed class ProcessAcpConnectionEnvTests
    {
        // ProcessStartInfo.Environment is case-insensitive on Windows; mirror that.
        private static Dictionary<string, string?> WindowsLikeEnv() =>
            new(StringComparer.OrdinalIgnoreCase);

        [Fact]
        public void EmptyEnvironment_GetsAllDefaults()
        {
            var env = WindowsLikeEnv();

            ProcessAcpConnection.ApplyNonInteractiveDefaults(env);

            Assert.Equal("true", env["GIT_EDITOR"]);
            Assert.Equal("true", env["GIT_SEQUENCE_EDITOR"]);
            Assert.Equal("cat", env["GIT_PAGER"]);
            Assert.Equal("cat", env["PAGER"]);
            Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        }

        [Fact]
        public void ExistingValue_IsNeverOverwritten()
        {
            var env = WindowsLikeEnv();
            env["GIT_EDITOR"] = "code --wait"; // user deliberately configured an editor

            ProcessAcpConnection.ApplyNonInteractiveDefaults(env);

            Assert.Equal("code --wait", env["GIT_EDITOR"]);
            // The rest still get defaults.
            Assert.Equal("true", env["GIT_SEQUENCE_EDITOR"]);
        }

        [Fact]
        public void ExistingValue_DifferentCase_IsRespectedOnWindows()
        {
            var env = WindowsLikeEnv();
            env["git_editor"] = "vim";

            ProcessAcpConnection.ApplyNonInteractiveDefaults(env);

            // Case-insensitive dictionary: no second GIT_EDITOR key appears.
            Assert.Equal("vim", env["GIT_EDITOR"]);
            Assert.Single(env.Keys, k => k.Equals("GIT_EDITOR", StringComparison.OrdinalIgnoreCase));
        }
    }
}
