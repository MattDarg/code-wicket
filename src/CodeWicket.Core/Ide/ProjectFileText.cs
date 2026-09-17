using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// What a project file on DISK says, read as text — the half of a membership question the loaded
    /// project cannot answer (issue #257).
    /// </summary>
    /// <remarks>
    /// A loaded non-SDK project that predates an edit to its own project file answers "not a member"
    /// for a file the project file on disk names. Telling that case (reload the project) from a file
    /// in no project at all (add it) needs the disk-side reading. <b>Bounded on purpose</b>: this is a
    /// file-name match against the project XML, enough to choose which sentence to print, and not a
    /// membership oracle — it does not evaluate conditions, globs or imports.
    /// </remarks>
    public static class ProjectFileText
    {
        /// <summary>
        /// Whether the project XML in <paramref name="projectXml"/> has any item whose <c>Include</c>
        /// names a file called <paramref name="fileName"/> (the leaf name, compared case-insensitively).
        /// Malformed XML is <c>false</c>: an unreadable project file is not evidence of anything.
        /// </summary>
        public static bool NamesFile(string? projectXml, string? fileName)
        {
            if (string.IsNullOrEmpty(projectXml) || string.IsNullOrEmpty(fileName))
                return false;
            try
            {
                var doc = XDocument.Parse(projectXml!);
                return doc.Descendants()
                    .Select(e => e.Attribute("Include")?.Value)
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Any(v => string.Equals(LeafName(v!), fileName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// <see cref="NamesFile(string?, string?)"/> over the file at <paramref name="projectPath"/>.
        /// A file that cannot be read is <c>false</c>, for the reason above.
        /// </summary>
        public static bool NamesFileOnDisk(string? projectPath, string? fileName)
        {
            if (string.IsNullOrEmpty(projectPath))
                return false;
            try
            {
                return File.Exists(projectPath) && NamesFile(File.ReadAllText(projectPath), fileName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The MSBuild dialect of the project at <paramref name="projectPath"/>, or null when the file
        /// cannot be parsed — never assumed legacy, for the reason <see cref="TestProjects.DetermineProjectStyle"/>
        /// gives.
        /// </summary>
        public static ProjectStyle? Style(string? projectPath) =>
            string.IsNullOrEmpty(projectPath) ? null : TestProjects.DetermineProjectStyle(projectPath!);

        // An Include is an MSBuild path, which may use either separator and may carry metadata like
        // "..\Shared\X.cs" — only the leaf is compared.
        private static string LeafName(string include)
        {
            var i = include.LastIndexOfAny(new[] { '\\', '/' });
            return i < 0 ? include : include.Substring(i + 1);
        }
    }
}
