using System;
using System.Collections.Generic;

namespace CodeWicket.Core.Ide
{
    /// <summary>
    /// Two-tier de-dup for diagnostic collection (get_diagnostics). Within a single source (the Roslyn
    /// compiler + analyzer passes, or the Error List) two diagnostics that share a <c>(file,line,code)</c>
    /// but cover different SPANS are distinct and both count — a coarse <c>(file,line,code)</c> key merges
    /// them (e.g. two CS8602 "possibly null" on one line), under-counting. Across sources it stays COARSE: an
    /// Error List row whose <c>(file,line,code)</c> a Roslyn pass already reported is dropped even if the two
    /// disagree on the column by one — the Error List copy is VS's live analysis over a possibly-different
    /// snapshot, so column-matching it against Roslyn isn't reliable, and a coarse cross-source collapse can't
    /// double-report a genuine duplicate. Compiler precedence &gt; analyzer &gt; errorList is preserved by add
    /// order (a Roslyn pass added first claims the coarse key).
    ///
    /// The within-source key differs by source because the two carry different fidelity, and the START column
    /// alone isn't enough to separate distinct diagnostics: the real XmlRequestReader.cs pair from issue #47
    /// was two CS8602 at <c>(68,28,68,40)</c> and <c>(68,28,68,59)</c> — same start, different end. Roslyn
    /// hands us the full span (<c>Location.GetLineSpan()</c>), so its key is the whole span. The Error List
    /// table exposes no end-span column at all (which is why build_solution counts its rows 1:1 rather than
    /// keying them — see bc72b7f), so its key stays start-only and an end-span-only difference between two
    /// Error List rows still merges. Narrow residual gap: Full scope reads the Error List with
    /// <c>excludeCompilerCodes:true</c>, so the CS/BC codes this pattern shows up in never travel that path.
    ///
    /// Pure logic over <c>(file, line, column, endLine, endColumn, code)</c> — no VS dependency — so it lives
    /// in Core and is unit-testable without a VS workspace (the collection that drives it is the VS-coupled part).
    /// </summary>
    public sealed class DiagnosticDeduper
    {
        private readonly HashSet<string> _fine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);          // full span (Roslyn) / (file,line,column,code) (Error List) — within a source
        private readonly HashSet<string> _roslynCoarse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // (file,line,code) claimed by a Roslyn pass

        /// <summary>
        /// Adds a Roslyn (compiler/analyzer) diagnostic, keyed on its full span so distinct same-line
        /// diagnostics survive even when they share a start column. Records the coarse key so a later Error
        /// List copy of the same code+line collapses onto it.
        /// </summary>
        public bool TryAddRoslyn(string file, int line, int column, int endLine, int endColumn, string code)
        {
            if (!_fine.Add(RoslynFine(file, line, column, endLine, endColumn, code)))
                return false;
            _roslynCoarse.Add(Coarse(file, line, code));
            return true;
        }

        /// <summary>
        /// Adds an Error List row. Dropped (returns false) if a Roslyn pass already reported this code at this
        /// line (coarse cross-source collapse, column-drift-proof); otherwise deduped on start position among
        /// Error List rows — the table surfaces no end span to key on.
        /// </summary>
        public bool TryAddErrorList(string file, int line, int column, string code)
            => !_roslynCoarse.Contains(Coarse(file, line, code)) && _fine.Add(ErrorListFine(file, line, column, code));

        private static string Coarse(string file, int line, string code)
            => (file ?? string.Empty).ToLowerInvariant() + "|" + line + "|" + (code ?? string.Empty);

        private static string ErrorListFine(string file, int line, int column, string code)
            => Coarse(file, line, code) + "|" + column;

        // Distinct shape from ErrorListFine (extra segments), so the two sources' fine keys can never collide —
        // cross-source collapse is the coarse set's job, not this one's.
        private static string RoslynFine(string file, int line, int column, int endLine, int endColumn, string code)
            => ErrorListFine(file, line, column, code) + "|" + endLine + "|" + endColumn;
    }
}
