using System;
using System.IO;
using System.Runtime.InteropServices;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Whether a path under a root reaches its file through a symbolic link or junction — the
    /// question a lexical containment test (<see cref="WorkspacePath.IsUnderRoot"/>) cannot answer,
    /// because <c>Path.GetFullPath</c> never touches the filesystem and a link inside the root can
    /// name anywhere at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it exists</b> (pre-release security review, September 2026). The inline-image containment
    /// accepted any rooted path whose full spelling started with the
    /// workspace root, then opened it. A repository cloned with symlinks on, or an archive extracted
    /// with them preserved, can carry <c>docs/logo.png</c> as a link to <c>\\attacker\share\logo.png</c>;
    /// the spelling passes, and <c>FileInfo.Exists</c> follows the link to the named host - the NTLM
    /// handshake and the SMB timeout on the dispatcher that the image containment excludes.
    /// </para>
    /// <para>
    /// <b>Refused, never followed, and only the steps BELOW the root are examined.</b> Following a
    /// link to see where it goes is the operation that authenticates to the host; the walk here asks
    /// each component on its own, with an API that reads the entry and not the target. A link ABOVE
    /// the root - a developer's <c>C:\src</c> to <c>D:\src</c> junction - carries the whole project
    /// and moves nothing out of reach, so it is not walked. Same distinction as
    /// <c>ProjectWorkspaceRootOverride.LinkBelow</c>, which faces the same shape for a different value.
    /// </para>
    /// <para>
    /// <b>The reparse TAG is read, not the attribute.</b> A OneDrive placeholder carries the reparse
    /// attribute and is not a link; refusing those would refuse every image in a synced repository,
    /// which on the net472 slice - the one devenv loads - is where the coarse attribute check the
    /// override code falls back to would have bitten. <c>FindFirstFileW</c> returns the tag for a
    /// reparse point without opening it, on both frameworks, so there is one implementation and one
    /// answer. Only the two tags that redirect a path count: symbolic link and mount point (junction).
    /// </para>
    /// <para>
    /// A component that cannot be read - missing, or refused - is reported as NOT a link, so the
    /// caller's own existence check gets to say "not there". The failure this must never have is
    /// saying "no link" over one that exists, and the API does not have it: an entry that IS a reparse
    /// point is found with its tag or not found at all.
    /// </para>
    /// </remarks>
    public static class ReparsePoints
    {
        private const uint FileAttributeReparsePoint = 0x400;
        private const uint TagSymbolicLink = 0xA000000C;
        private const uint TagMountPoint = 0xA0000003;
        private const int MaxSteps = 64;

        /// <summary>
        /// True when any path component of <paramref name="fullPath"/> below <paramref name="root"/>,
        /// the leaf included, is a symbolic link or junction. <paramref name="fullPath"/> must already
        /// lie under <paramref name="root"/> lexically; a walk that never reaches the root refuses.
        /// </summary>
        public static bool AnyBelow(string root, string fullPath)
        {
            var current = fullPath;
            for (var step = 0; step < MaxSteps; step++)
            {
                if (WorkspaceRootLocator.SameRoot(current, root))
                    return false;
                if (IsLink(current))
                    return true;

                string? parent;
                try { parent = Path.GetDirectoryName(current); }
                catch (ArgumentException) { return true; }
                if (string.IsNullOrEmpty(parent))
                    return true; // climbed off the top without meeting the root: not under it after all
                current = parent!;
            }

            return true;
        }

        /// <summary>
        /// Whether <paramref name="path"/> itself is a symbolic link or junction, read from the entry
        /// and never by following it. False for a plain file or directory, a cloud placeholder, and a
        /// path that cannot be found.
        /// </summary>
        public static bool IsLink(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;

            var handle = FindFirstFileW(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), out var data);
            if (handle == InvalidHandle)
                return false;
            FindClose(handle);

            if ((data.dwFileAttributes & FileAttributeReparsePoint) == 0)
                return false;
            return data.dwReserved0 == TagSymbolicLink || data.dwReserved0 == TagMountPoint;
        }

        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileW(string lpFileName, out WIN32_FIND_DATAW lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr hFindFile);
    }
}
