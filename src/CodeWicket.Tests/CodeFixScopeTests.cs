using CodeWicket.Core.Ide;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// <c>apply_code_fix</c>'s <c>scope</c> argument (issue #91) — the decision half of it, extracted from
    /// <c>VsToolCatalog</c> for the same reason <see cref="FilePathMatchTests"/>' subject was: the tool
    /// catalog is net472 + VS SDK + Roslyn and this project cannot reference it, so a rule left in there is
    /// a rule no test can reach. The Roslyn half — building the <c>FixAllContext</c> and running it — stays
    /// there and needs a live devenv.
    /// </summary>
    public sealed class CodeFixScopeTests
    {
        /// <summary>
        /// Absent means Line. This is the compatibility guarantee: every caller written before the argument
        /// existed sends no scope at all, and must keep fixing exactly one occurrence.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TryParse_defaultsToLine(string? argument)
        {
            Assert.True(CodeFixScopes.TryParse(argument, out var scope, out var error));
            Assert.Equal(CodeFixScope.Line, scope);
            Assert.Null(error);
        }

        [Theory]
        [InlineData("line", CodeFixScope.Line)]
        [InlineData("document", CodeFixScope.Document)]
        [InlineData("Document", CodeFixScope.Document)]
        [InlineData("  DOCUMENT  ", CodeFixScope.Document)]
        // "file" is the word a model reaches for about a file; refusing a synonym costs a round trip to
        // learn nothing.
        [InlineData("file", CodeFixScope.Document)]
        public void TryParse_acceptsTheSpellingsAgentsActuallyWrite(string argument, CodeFixScope expected)
        {
            Assert.True(CodeFixScopes.TryParse(argument, out var scope, out var error));
            Assert.Equal(expected, scope);
            Assert.Null(error);
        }

        /// <summary>
        /// Project and Solution are real <c>FixAllScope</c> values, so a model may well try them. They are
        /// refused — the permission tier resolves by tool NAME, so a solution-wide fix-all would auto-approve
        /// under AcceptEdits identically to a one-line fix — and the refusal has to say what to do INSTEAD,
        /// or the agent's only move is to guess again.
        /// </summary>
        [Theory]
        [InlineData("project")]
        [InlineData("solution")]
        public void TryParse_refusesWiderScopesAndNamesTheAlternative(string argument)
        {
            Assert.False(CodeFixScopes.TryParse(argument, out var scope, out var error));
            Assert.Equal(CodeFixScope.Line, scope);   // never a silent widening
            Assert.NotNull(error);
            Assert.Contains(argument, error);
            Assert.Contains("once per file", error);
            Assert.Contains("document", error);
        }

        /// <summary>An unrecognised value is refused rather than defaulted: silently fixing one occurrence when the caller asked for something else is the failure this argument exists to avoid.</summary>
        [Fact]
        public void TryParse_refusesAnUnknownScopeAndListsTheValidOnes()
        {
            Assert.False(CodeFixScopes.TryParse("everything", out _, out var error));
            Assert.NotNull(error);
            Assert.Contains("everything", error);
            Assert.Contains("'line'", error);
            Assert.Contains("'document'", error);
        }

        // ---- Which argument each scope requires ---------------------------------------------------

        /// <summary>A line scope is a POSITION, so it needs the line — unchanged from before the argument existed.</summary>
        [Fact]
        public void Validate_lineScopeRequiresALine()
        {
            Assert.Contains("'line'", CodeFixScopes.Validate(CodeFixScope.Line, line: -1, diagnosticId: "IDE0055"));
            Assert.Contains("'line'", CodeFixScopes.Validate(CodeFixScope.Line, line: 0, diagnosticId: null));
            Assert.Null(CodeFixScopes.Validate(CodeFixScope.Line, line: 1, diagnosticId: null));
        }

        /// <summary>
        /// A document scope is a RULE, so it needs the id and NOT the line. Both halves matter: dropping the
        /// line requirement is what makes "fix every IDE0055 in this file" one call, and requiring the id is
        /// what stops it meaning "fix everything", which is a much larger edit than the caller asked for.
        /// </summary>
        [Fact]
        public void Validate_documentScopeRequiresAnIdAndNotALine()
        {
            Assert.Null(CodeFixScopes.Validate(CodeFixScope.Document, line: -1, diagnosticId: "IDE0055"));

            var error = CodeFixScopes.Validate(CodeFixScope.Document, line: 12, diagnosticId: null);
            Assert.NotNull(error);
            Assert.Contains("diagnosticId", error);
            Assert.Contains("get_diagnostics", error);   // the message has to say where to get one
            Assert.NotNull(CodeFixScopes.Validate(CodeFixScope.Document, line: 12, diagnosticId: "   "));
        }
    }
}
