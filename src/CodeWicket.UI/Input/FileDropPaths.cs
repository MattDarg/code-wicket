using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;

namespace CodeWicket.UI.Input
{
    /// <summary>
    /// The file paths carried by a drag, from whichever format the source happened to publish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows Explorer publishes <c>CF_HDROP</c> (<see cref="DataFormats.FileDrop"/>), which is a
    /// <c>string[]</c> and needs no interpreting. Visual Studio's Solution Explorer may instead offer only
    /// <c>CF_VSSTGPROJECTITEMS</c> / <c>CF_VSREFPROJECTITEMS</c>, whose <c>|</c>-delimited layout is
    /// undocumented enough that reading it BY POSITION risks dropping project guids and display names into
    /// the user's prompt as though they were files.
    /// </para>
    /// <para>
    /// So nothing here is parsed by position. Every format is reduced to a bag of candidate tokens and
    /// each token is kept only if it names something that is actually on disk. There is no shape to keep
    /// in step with a future VS build, and a wrong guess costs nothing — it simply fails to exist and is
    /// dropped. The same reasoning lets a stream be decoded as UTF-16 AND as UTF-8 with both results
    /// offered: the wrong decoding produces mojibake, mojibake is not a file, and it disappears.
    /// </para>
    /// <para>
    /// <b><see cref="CouldCarryFiles"/> and <see cref="Decode"/> are split because the filesystem may not
    /// be touched during a drag.</b> <c>DragOver</c> fires on every mouse-move, so a <c>File.Exists</c> per
    /// token per move would put machine-priced IO on the UI thread — over a UNC share, with
    /// folder redirection, or behind EDR interception — during the one gesture where the pointer must stay
    /// smooth. The cheap half answers the cursor; the expensive half runs once, on the drop.
    /// </para>
    /// </remarks>
    public static class FileDropPaths
    {
        /// <summary>
        /// Installed by the host (the <c>MarkdownRenderFirewall.Log</c> pattern), so a drop can say what
        /// it saw. Always on rather than behind a setting: a drop is a rare, user-initiated gesture, not a
        /// per-render cost, and the question it answers — what does Solution Explorer actually publish? —
        /// is only answerable on a real VS.
        /// </summary>
        public static Action<string>? Log;

        // CF_HDROP aside, these are the formats a VS drag has been seen to offer. FileNameW/FileName are
        // the shell's single-file formats; the two VS ones are the project-item shapes Solution Explorer
        // publishes alongside CF_HDROP. Order is the order they are read in, and it only affects which duplicate
        // spelling wins - the dedupe keeps the first.
        private const string VsProjectItems = "CF_VSSTGPROJECTITEMS";
        private const string VsProjectRefs = "CF_VSREFPROJECTITEMS";
        private const string ShellFileNameW = "FileNameW";
        private const string ShellFileName = "FileName";

        // A drop is rare, but a 200-file multi-select would still make one unreadable line.
        private const int MaxLoggedPaths = 12;

        // AUTHORITATIVE: formats whose whole meaning is "these are the files being dragged" — in
        // PREFERENCE ORDER, one tier each, because they are not three sources. They are up to three
        // spellings of one fact, and reading them all put the same file in the composer twice.
        //
        // Explorer publishes all three for a single-file drag. CF_HDROP carries the long path;
        // FileNameW the same path again; FileName is ANSI and so carries the 8.3 SHORT name —
        // "CONTRO~1.PNG" for "control-100x200.png". Both spellings exist on disk and neither string
        // equals the other, so the existence test kept them and the dedupe could not see they were one
        // file. Measured on a live Explorer drop (2026-09-11):
        //
        //   read=[FileDrop,FileNameW,FileName] tokens=3 kept=2
        //   paths=["…\Pictures\control-100x200.png", "…\Pictures\CONTRO~1.PNG"]
        //
        // This is the tier rule the type already applies to the VS project blobs, applied one level
        // in — and it is the rule CouldCarryFiles' own remarks state: the single-file formats say
        // where a payload CAME FROM, not what was dragged. So CF_HDROP wins outright when present,
        // and the Unicode spelling wins over the ANSI one, which is what makes the 8.3 name
        // unreachable without canonicalizing paths (a filesystem round-trip this does not need).
        private static readonly string[][] FileFormatTiers =
        {
            new[] { DataFormats.FileDrop },
            new[] { ShellFileNameW },
            new[] { ShellFileName },
        };

        // AMBIGUOUS: VS's project-item shapes, which carry paths that are real but were not dragged —
        // measured on a live Solution Explorer drop, the containing .csproj comes along with the item.
        // Only read when nothing authoritative was offered. See FilterTiered.
        private static readonly string[] ProjectFormats = { VsProjectItems, VsProjectRefs };

        private static readonly string[] AllFormats =
        {
            DataFormats.FileDrop, ShellFileNameW, ShellFileName, VsProjectItems, VsProjectRefs,
        };

        // Formats that mean "a file was DRAGGED", whatever else rides along. CF_HDROP is the shell's
        // drag-a-file format, and the VS pair is Solution Explorer dragging an item.
        private static readonly string[] DraggedFileFormats =
        {
            DataFormats.FileDrop, VsProjectItems, VsProjectRefs,
        };

        // The shell's SINGLE-FILE formats, which say where a payload CAME FROM rather than what was
        // dragged - see CouldCarryFiles.
        private static readonly string[] SingleFileFormats = { ShellFileNameW, ShellFileName };

        // Text, for the one question the single-file formats cannot answer alone.
        private static readonly string[] TextFormats = { DataFormats.UnicodeText, DataFormats.Text };

        // Tokens arrive glued together by whichever convention the publisher used. All three separators
        // are tried at once because a format may use more than one, and splitting on a separator the
        // payload does not contain is harmless.
        private static readonly char[] Separators = { '|', '\0', '\r', '\n' };

        /// <summary>
        /// Whether this drag could be carrying files, judged on FORMAT PRESENCE alone — no decode, no
        /// filesystem. This is the answer <c>DragOver</c> uses, so it is deliberately optimistic: saying
        /// yes here and finding nothing on the drop is a quiet no-op, while saying no would hand the drag
        /// back to Visual Studio, which opens the file in an editor tab.
        /// </summary>
        /// <remarks>
        /// Optimistic, but not credulous: <c>FileNameW</c>/<c>FileName</c> alone are read as files ONLY
        /// where nothing was dragged as text.
        /// <para>
        /// The reason stands on the FORMATS, not on any one publisher. Those two are the shell's
        /// single-file formats and they answer a different question from CF_HDROP: they say where a
        /// payload CAME FROM, not what was dragged. The existing tier tells file formats apart from each
        /// other and had no way to tell "these are the dragged files" from "this is where the dragged
        /// text came from" - and only the presence of the text answers that. Same principle as the tier
        /// itself: THE GESTURE DECIDES, NOT THE FILE TYPE. So a drag offering a single-file format
        /// BESIDE text is ambiguous whoever published it, and the text wins.
        /// </para>
        /// <para>
        /// <b>The Office case that motivated this is UNCONFIRMED.</b> The claim is that Word/Excel
        /// publish the source document's path for Link Source beside <c>UnicodeText</c>, so a dragged
        /// paragraph would insert <c>C:\...\Doc.docx</c>. Measured against UNFIXED code, where the auto-converting check meant a bare
        /// <c>FileNameW</c> was enough to insert the path (2026-09-08, VS 18.9.1): dragging text out of
        /// Word gave the TEXT, and so did Outlook. Whatever Office publishes there, it is not these
        /// formats. Do not re-add the claim without a capture; if a report ever does show a path landing
        /// where text was dragged, this guard is already the fix.
        /// </para>
        /// A real file drag is untouched, because it publishes CF_HDROP (or the VS pair) and those are
        /// taken whatever else is present.
        /// </remarks>
        internal static bool CouldCarryFiles(IDataObject? data)
        {
            if (data is null)
                return false;

            foreach (var format in DraggedFileFormats)
            {
                if (PresentExact(data, format))
                    return true;
            }

            if (HasText(data))
                return false;

            foreach (var format in SingleFileFormats)
            {
                if (PresentExact(data, format))
                    return true;
            }

            return false;
        }

        /// <summary>Whether the drag offers a text payload at all — format presence, no decode.</summary>
        private static bool HasText(IDataObject data)
        {
            foreach (var format in TextFormats)
            {
                if (PresentExact(data, format))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Presence of a format the source ACTUALLY PUBLISHED, with no auto-conversion.
        /// </summary>
        /// <remarks>
        /// The distinction is the whole tier, not a refinement of it. <c>GetDataPresent</c> converts by
        /// default, and one of the conversions it offers is <c>FileNameW</c>/<c>FileName</c> →
        /// <c>FileDrop</c> — so a drag carrying only the shell's single-file formats answers TRUE for
        /// CF_HDROP, and "was a file dragged" and "does something name a file" collapse into the same
        /// question. Separating the two formats is pointless while the check that reads them cannot tell
        /// them apart, so a fix that separates only the formats passes review and fails its own test.
        /// </remarks>
        private static bool PresentExact(IDataObject data, string format)
        {
            try { return data.GetDataPresent(format, autoConvert: false); }
            catch (Exception e) when (IsDataObjectFailure(e)) { return false; }
        }

        /// <summary>
        /// Every real file or folder named by <paramref name="data"/>, in the order first seen. Runs on
        /// the drop only — see the remarks on the type.
        /// </summary>
        internal static IReadOnlyList<string> Decode(IDataObject? data, Func<string, bool>? exists = null)
        {
            var seenFormats = new List<string>();
            var totalTokens = 0;
            IReadOnlyList<string> kept = Array.Empty<string>();

            if (data is not null)
            {
                // First tier that names anything real wins, and the rest are never read — so the
                // `read=` half of the log line is the evidence that it stopped where it should.
                foreach (var tier in FileFormatTiers)
                {
                    var tokens = new List<string?>();
                    foreach (var format in tier)
                    {
                        if (!Present(data, format))
                            continue;
                        seenFormats.Add(format);
                        Gather(data, format, tokens);
                    }

                    totalTokens += tokens.Count;
                    if (tokens.Count == 0)
                        continue;

                    kept = Filter(tokens, exists);
                    if (kept.Count > 0)
                        break;
                }

                // The ambiguous tier, on the same rule and for the same reason: only when nothing
                // authoritative named a real file. See FilterTiered, whose contract this preserves.
                if (kept.Count == 0)
                {
                    var projectTokens = new List<string?>();
                    foreach (var format in ProjectFormats)
                    {
                        if (!Present(data, format))
                            continue;
                        seenFormats.Add(format);
                        Gather(data, format, projectTokens);
                    }

                    totalTokens += projectTokens.Count;
                    kept = Filter(projectTokens, exists);
                }
            }

            Report(data, seenFormats, totalTokens, kept);
            return kept;
        }

        /// <summary>
        /// The two-tier rule: what the <paramref name="authoritative"/> formats named, and only if they
        /// named nothing, what the ambiguous <paramref name="fallback"/> ones did.
        /// </summary>
        /// <remarks>
        /// <b>Measured on a live Solution Explorer drop, not reasoned about.</b> VS publishes
        /// <c>CF_HDROP</c> AND <c>CF_VSSTGPROJECTITEMS</c> for the same drag, and the project-item blob
        /// carries the containing <c>.csproj</c>'s path beside the item's. Both are real files on disk, so
        /// "keep whatever exists" kept both, and one dropped file put TWO paths in the composer.
        /// <para>
        /// The original rule was not so much wrong as incomplete: existence is the right test for "is this
        /// token a path at all", and no test whatever for "was this the thing the user dragged". Only the
        /// FORMAT can answer that — so one whose entire meaning is "these are the dragged files" wins
        /// outright, and the blob, which has no way to say which of its paths is the subject, is consulted
        /// only when nothing better was offered.
        /// </para>
        /// </remarks>
        internal static IReadOnlyList<string> FilterTiered(
            IEnumerable<string?> authoritative, IEnumerable<string?> fallback, Func<string, bool>? exists = null)
        {
            var kept = Filter(authoritative, exists);
            return kept.Count > 0 ? kept : Filter(fallback, exists);
        }

        /// <summary>
        /// The rule, with no data object and no filesystem of its own: split, unwrap, keep what
        /// <paramref name="exists"/> vouches for, dedupe, preserve order.
        /// </summary>
        /// <remarks>
        /// A token must be ROOTED to survive, and that is not a formality. A relative token would be
        /// checked — and later resolved — against the PROCESS working directory, which in the VS host is
        /// devenv's install folder: a spelling nothing else in the product measures from. Refusing it here
        /// is the other end of <c>WorkspacePath.ForPrompt</c>'s contract, and this is the layer where the
        /// fact is knowable.
        /// </remarks>
        /// <param name="exists">
        /// How to decide a token names something real. Defaults to the filesystem; a caller passes its own
        /// only to keep a test off the disk.
        /// </param>
        internal static IReadOnlyList<string> Filter(IEnumerable<string?> tokens, Func<string, bool>? exists = null)
        {
            exists ??= OnDisk;
            var kept = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var raw in tokens)
            {
                if (string.IsNullOrEmpty(raw))
                    continue;

                foreach (var piece in raw!.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = piece.Trim().Trim('"');
                    if (candidate.Length == 0 || !Rooted(candidate))
                        continue;
                    if (!seen.Add(candidate))
                        continue;
                    if (exists(candidate))
                        kept.Add(candidate);
                }
            }

            return kept;
        }

        private static bool OnDisk(string path)
        {
            // Folders count: a Solution Explorer folder is a legitimate thing to point an agent at, and
            // filtering on File.Exists alone would make that drop silently do nothing.
            try { return File.Exists(path) || Directory.Exists(path); }
            catch (Exception e) when (IsIoFailure(e)) { return false; }
        }

        private static bool Rooted(string path)
        {
            try { return Path.IsPathRooted(path); }
            catch (ArgumentException) { return false; }
        }

        private static bool Present(IDataObject data, string format)
        {
            // The source is a DIFFERENT PROCESS and can die mid-drag, so neither presence nor retrieval
            // may throw into a drag handler. Same reason SafeGetClipboardData exists, one degree worse.
            try { return data.GetDataPresent(format); }
            catch (Exception e) when (IsDataObjectFailure(e)) { return false; }
        }

        private static void Gather(IDataObject data, string format, List<string?> into)
        {
            object? value;
            try { value = data.GetData(format); }
            catch (Exception e) when (IsDataObjectFailure(e)) { return; }

            switch (value)
            {
                case string[] many:
                    into.AddRange(many);
                    break;
                case string one:
                    into.Add(one);
                    break;
                case MemoryStream stream:
                    AddDecodings(stream, into);
                    break;
            }
        }

        // Both decodings are offered rather than one being chosen: the encoding of the VS formats is not
        // documented, and a wrong guess yields mojibake, which is not a path and is filtered out. Picking
        // wrong would instead lose the drop with nothing to show for it.
        private static void AddDecodings(MemoryStream stream, List<string?> into)
        {
            byte[] bytes;
            try { bytes = stream.ToArray(); }
            catch (Exception e) when (IsIoFailure(e)) { return; }

            if (bytes.Length == 0)
                return;

            into.Add(Decode(Encoding.Unicode, bytes));
            into.Add(Decode(Encoding.UTF8, bytes));
        }

        private static string? Decode(Encoding encoding, byte[] bytes)
        {
            try { return encoding.GetString(bytes); }
            catch (ArgumentException) { return null; }
        }

        // Written even when nothing was recognised and nothing was kept. An empty line and no line at all
        // are the same absence otherwise, and they mean opposite things: "Solution Explorer published a
        // format we do not read" versus "the drag never reached us at all".
        private static void Report(IDataObject? data, List<string> read, int tokens, IReadOnlyList<string> kept)
        {
            var log = Log;
            if (log is null)
                return;

            string[] available;
            try { available = data?.GetFormats() ?? Array.Empty<string>(); }
            catch (Exception e) when (IsDataObjectFailure(e)) { available = new[] { "(unreadable)" }; }

            var line = new StringBuilder("[drop] formats=[")
                .Append(string.Join(",", available))
                .Append("] read=[")
                .Append(string.Join(",", read))
                .Append("] tokens=").Append(tokens)
                .Append(" kept=").Append(kept.Count);

            // Every kept path, not just the first. "kept=2" was the entire evidence for the Solution
            // Explorer defect, and WHICH second path it was had to be inferred from the count.
            if (kept.Count > 0)
                line.Append(" paths=[\"").Append(string.Join("\", \"", kept.Take(MaxLoggedPaths))).Append("\"]");
            if (kept.Count > MaxLoggedPaths)
                line.Append(" (+").Append(kept.Count - MaxLoggedPaths).Append(" more)");

            try { log(line.ToString()); }
            catch (Exception) { /* a diagnostic may never break the gesture it is describing */ }
        }

        private static bool IsDataObjectFailure(Exception e)
            => e is System.Runtime.InteropServices.COMException
               || e is System.Runtime.InteropServices.ExternalException
               || e is OutOfMemoryException
               || e is InvalidOperationException
               || e is NotSupportedException;

        private static bool IsIoFailure(Exception e)
            => e is IOException
               || e is UnauthorizedAccessException
               || e is ArgumentException
               || e is NotSupportedException
               || e is PathTooLongException
               || e is System.Security.SecurityException
               || e is ObjectDisposedException;
    }
}
