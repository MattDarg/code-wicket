using System;
using System.IO;
using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// The disk-side half of the membership question (issue #257): does the project file ON DISK name
    /// a file the LOADED project says it does not contain? A bounded file-name match, enough to choose
    /// between "reload the project" and "add the file", and nothing more.
    /// </summary>
    public sealed class ProjectFileTextTests : IDisposable
    {
        private const string LegacyXml = @"<?xml version=""1.0"" encoding=""utf-8""?>
<Project ToolsVersion=""15.0"" xmlns=""http://schemas.microsoft.com/developer/msbuild/2003"">
  <ItemGroup>
    <Compile Include=""Properties\AssemblyInfo.cs"" />
    <Compile Include=""Canary.cs"" />
    <None Include=""packages.config"" />
  </ItemGroup>
</Project>";

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cwkt-projectfiletext-" + Guid.NewGuid().ToString("N"));

        public ProjectFileTextTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        [Theory]
        [InlineData("Canary.cs", true)]
        [InlineData("canary.CS", true)]
        [InlineData("AssemblyInfo.cs", true)]   // the leaf of a nested Include
        [InlineData("packages.config", true)]   // any item type, not only Compile
        [InlineData("Other.cs", false)]
        [InlineData("Properties", false)]       // a directory segment is not a file name
        public void NamesFileMatchesTheLeafOfAnyInclude(string fileName, bool expected)
        {
            Assert.Equal(expected, ProjectFileText.NamesFile(LegacyXml, fileName));
        }

        /// <summary>Unreadable is not evidence — of membership or of its absence.</summary>
        [Theory]
        [InlineData("<Project><ItemGroup><Compile Include=\"Canary.cs\"")]
        [InlineData("not xml at all")]
        [InlineData("")]
        [InlineData(null)]
        public void MalformedOrEmptyXmlNamesNothing(string? xml)
        {
            Assert.False(ProjectFileText.NamesFile(xml, "Canary.cs"));
        }

        [Fact]
        public void NamesFileOnDiskReadsTheFileAndAMissingOneNamesNothing()
        {
            var project = Path.Combine(_dir, "Legacy.csproj");
            File.WriteAllText(project, LegacyXml);

            Assert.True(ProjectFileText.NamesFileOnDisk(project, "Canary.cs"));
            Assert.False(ProjectFileText.NamesFileOnDisk(project, "Other.cs"));
            Assert.False(ProjectFileText.NamesFileOnDisk(Path.Combine(_dir, "Missing.csproj"), "Canary.cs"));
            Assert.False(ProjectFileText.NamesFileOnDisk(null, "Canary.cs"));
        }

        /// <summary>
        /// The dialect decides which sentence is printed; it comes from the same reader run_tests uses,
        /// with its rule that an unparseable file is null and never assumed legacy.
        /// </summary>
        [Fact]
        public void StyleReadsTheDialectAndAnUnreadableFileIsNull()
        {
            var legacy = Path.Combine(_dir, "Legacy.csproj");
            File.WriteAllText(legacy, LegacyXml);
            var sdk = Path.Combine(_dir, "Modern.csproj");
            File.WriteAllText(sdk, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup /></Project>");
            var broken = Path.Combine(_dir, "Broken.csproj");
            File.WriteAllText(broken, "<Project><ItemGroup>");

            Assert.Equal(ProjectStyle.Legacy, ProjectFileText.Style(legacy));
            Assert.Equal(ProjectStyle.Sdk, ProjectFileText.Style(sdk));
            Assert.Null(ProjectFileText.Style(broken));
            Assert.Null(ProjectFileText.Style(Path.Combine(_dir, "Missing.csproj")));
            Assert.Null(ProjectFileText.Style(null));
        }
    }
}
