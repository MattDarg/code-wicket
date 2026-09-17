using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace CodeWicket.Ide
{
    /// <summary>
    /// Asks the LOADED projects whether they contain a file (issue #257).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loaded hierarchy, not the project file on disk and not Roslyn: it is the copy that builds.
    /// A non-SDK project whose file was edited externally keeps its pre-edit item list until reloaded
    /// (Visual Studio defers the prompt to the IDE's next activation), so this answers <c>false</c>
    /// for a file the project file on disk names — which is the consequence the build acts on, and
    /// the thing worth reporting. Roslyn's document list would agree for C#/VB but is blind to C++ and
    /// to a workspace that has not warmed; the hierarchy needs neither. Proved in Visual Studio by proxy:
    /// <c>open_file</c> put the orphaned file under <i>Miscellaneous Files</i>, which is this same
    /// lookup made by <c>OpenDocumentViaProject</c>.
    /// </para>
    /// <para>
    /// <b>A membership answer from a hierarchy that does not BUILD is not membership.</b> The
    /// Miscellaneous Files project holds whatever is open outside a project, and a solution folder's
    /// items are not compiled by anything; a file found only there is in no project for the build's
    /// purposes, which is the question being asked. Both are excluded by type.
    /// </para>
    /// <para>
    /// Null means the question could not be asked — no shell service, or a call that failed — and the
    /// caller says nothing rather than reporting an absence it did not establish.
    /// </para>
    /// </remarks>
    internal static class ProjectMembershipProbe
    {
        // EnvDTE80.ProjectKinds.vsProjectKindSolutionFolder, as a Guid.
        private static readonly Guid SolutionFolderType = new Guid("66A26720-8FB5-11D2-AA7E-00C04F688DDE");

        /// <summary>
        /// Whether a loaded, buildable project contains <paramref name="path"/>; null when the shell
        /// could not be asked. Call on the UI thread.
        /// </summary>
        public static bool? IsInLoadedProject(IServiceProvider serviceProvider, string path)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(path))
                return null;
            if (!(serviceProvider?.GetService(typeof(SVsUIShellOpenDocument)) is IVsUIShellOpenDocument openDocument))
                return null;

            try
            {
                var hr = openDocument.IsDocumentInAProject(path, out var hierarchy, out _, out _, out var docInProject);
                if (ErrorHandler.Failed(hr))
                    return null;
                if (docInProject != (int)__VSDOCINPROJECT.DOCINPROJ_DocInProject)
                    return false;
                return hierarchy is not null && IsBuildableProject(hierarchy);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsBuildableProject(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (ErrorHandler.Failed(hierarchy.GetGuidProperty(
                        VSConstants.VSITEMID_ROOT, (int)__VSHPROPID.VSHPROPID_TypeGuid, out var type)))
                    return true; // a project that will not say what it is: not evidence of a non-project
                if (type == VSConstants.CLSID.MiscellaneousFilesProject_guid)
                    return false;
                if (type == SolutionFolderType)
                    return false;
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
