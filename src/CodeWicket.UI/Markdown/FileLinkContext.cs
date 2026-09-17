using System;
using System.Threading.Tasks;

namespace CodeWicket.UI.Markdown
{
    /// <summary>
    /// What the markdown renderer needs to turn a file reference in agent prose into a working link:
    /// where paths resolve (<see cref="Resolver"/>) and how to open one (<see cref="Open"/>). Carried
    /// to the viewer as an attached property alongside <c>md:MarkdownText.Text</c> rather than being a
    /// static hook on the renderer, because the root differs per session and the Desktop self-check
    /// builds two view-models in one process.
    /// <para>
    /// A host with no way to open a file (Desktop's stub, a unit test) simply has no context, and every
    /// reference renders as plain text — the same degradation as the permission banner's "View diff"
    /// button hiding where there is no diff viewer.
    /// </para>
    /// </summary>
    public sealed class FileLinkContext
    {
        private readonly Func<string, int, Task> _openFile;

        public FileLinkContext(FileReferenceResolver resolver, Func<string, int, Task> openFile)
        {
            Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _openFile = openFile ?? throw new ArgumentNullException(nameof(openFile));
        }

        public FileReferenceResolver Resolver { get; }

        /// <summary>
        /// Opens a resolved reference in the host's editor. Navigation only: this is a read the user
        /// asked for, which is why it needs no permission round-trip — anything that would <i>act</i> on
        /// model-authored prose has to go through the permission handler like any tool call.
        /// </summary>
        public void Open(string fullPath, int line)
        {
            if (string.IsNullOrEmpty(fullPath))
                return;
            try
            {
                _ = _openFile(fullPath, line < 1 ? 1 : line);
            }
            catch
            {
                // Opening a file is best-effort; never crash the chat over a click.
            }
        }
    }
}
