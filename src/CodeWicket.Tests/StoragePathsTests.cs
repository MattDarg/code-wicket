using System;
using System.IO;
using CodeWicket.Core;
using CodeWicket.Shell;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The on-disk roots, which used to be twelve separate literals across nine files and three
    /// assemblies. These tests are a CANARY, not a description: they spell the expected paths out
    /// independently of <see cref="StoragePaths"/> so that renaming the storage folder has to be a
    /// deliberate act with a migration attached, exactly as
    /// <c>IdeMcpServerTests</c> pins the MCP server name.
    /// <para>
    /// What they cannot reach is the other half of the seam: the Tests project references neither
    /// <c>CodeWicket.Ide</c> nor <c>CodeWicket.VSExtension</c>, so the log directory the IDE
    /// tools write to, the diff scratch dir, the default workspace and the test-results root are all
    /// invisible here. That gap is what the source-scanning residue test exists for — a unit test
    /// cannot cover it, and assuming otherwise is how one of the three log spellings survived a
    /// rename in the first place.
    /// </para>
    /// </summary>
    public sealed class StoragePathsTests
    {
        private const string Folder = "code-wicket";

        private static string Roaming =>
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        private static string Local =>
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        [Fact]
        public void TheStorageFolderIsNamedOnce()
        {
            Assert.Equal(Folder, Branding.StorageFolderName);
        }

        [Fact]
        public void TheRootsResolveUnderTheUsersProfile()
        {
            Assert.Equal(Path.Combine(Roaming, Folder), StoragePaths.Roaming);
            Assert.Equal(Path.Combine(Local, Folder), StoragePaths.Local);
            Assert.Equal(Path.Combine(Local, Folder, "logs"), StoragePaths.LogDirectory);
        }

        [Fact]
        public void TheShellRootsSitUnderThem()
        {
            // DefaultPath honours RedirectTo, which the automated hosts set; assert the composed
            // shape rather than the live value so this test says the same thing either way.
            Assert.Equal(Path.Combine(Local, Folder, "logs"), ExtensionConfig.LogDirectory);
            Assert.Equal(Path.Combine(Roaming, Folder, "sessions"), FileSessionStore.DefaultRoot);
            Assert.Equal(
                Path.Combine(Local, Folder, "logs", "engine.log"),
                ExtensionConfig.EngineStderrLogFile);
        }

        /// <summary>
        /// The null contract the two SWEEP callers depend on. <see cref="Path.Combine(string, string)"/>
        /// of an empty first segment yields a RELATIVE path, so a sweep that resolved one would point
        /// an age-and-budget delete pass at the process's current directory instead of a directory we
        /// own (issue #88). Both callers returned null for that reason before the seam existed.
        /// </summary>
        [Fact]
        public void LocalOrNullIsRootedOrNull()
        {
            var local = StoragePaths.LocalOrNull();
            if (local is null)
                return; // no LocalAppData on this machine — the contract held

            Assert.True(Path.IsPathRooted(local));
            Assert.Equal(StoragePaths.Local, local);

            // The composed form is what the two sweep callers actually ask for.
            var scoped = StoragePaths.LocalOrNull("attachments");
            Assert.Equal(Path.Combine(StoragePaths.Local, "attachments"), scoped);
        }
    }
}
