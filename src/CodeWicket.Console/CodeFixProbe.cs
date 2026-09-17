using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CodeWicket.Probes
{
    /// <summary>
    /// A real Roslyn compilation with the real C# code-fix providers, standing in for the live
    /// <c>VisualStudioWorkspace</c> that <see cref="CodeWicket.Ide.CodeFixActions"/> normally runs
    /// against.
    /// </summary>
    /// <remarks>
    /// <b>Homed here and LINKED into the test project</b>, alongside the linked
    /// <c>CodeFixActions.cs</c> it drives: running the real fixers is a proof activity, and the two
    /// consumers want different things out of it — the tests want a verdict on every run, while
    /// <c>Console codefix-actions</c> prints the table you read when a test says the premise has
    /// changed. Neither is redundant: a verdict cannot show you WHAT the fixers now register.
    /// </para>
    /// <para>
    /// Issue #142 existed because <b>nothing outside devenv ever asked a registered code action to
    /// produce its operations</b> — the Ide project holds Roslyn compile-only, so a whole class of fix
    /// could be permanently unreachable with every build green and every test passing. This harness is
    /// the smallest thing that can decide the question offline.
    /// </para>
    /// <para>
    /// Deliberately close to <c>VsToolCatalog.ApplyCodeFixAsync</c>'s own gather: providers are
    /// reflected out of the Features assembly rather than resolved through MEF (there is no MEF host
    /// here), a provider that cannot run headlessly is skipped rather than sinking the pass, and the
    /// applicability question is asked exactly as the tool asks it — <c>GetOperationsAsync</c>, then
    /// look for an <see cref="ApplyChangesOperation"/>. It is NOT a replica of devenv: VS composes a
    /// larger provider set and supplies services this workspace lacks, which is what
    /// <c>IsInteractive</c> exists to handle and why a live Visual Studio instance remains the final word.
    /// </para>
    /// </remarks>
    internal static class CodeFixProbe
    {
        private static readonly Lazy<ImmutableArray<CodeFixProvider>> LazyProviders =
            new Lazy<ImmutableArray<CodeFixProvider>>(LoadProviders, LazyThreadSafetyMode.ExecutionAndPublication);

        private static readonly Lazy<ImmutableArray<MetadataReference>> LazyReferences =
            new Lazy<ImmutableArray<MetadataReference>>(LoadReferences, LazyThreadSafetyMode.ExecutionAndPublication);

        internal static ImmutableArray<CodeFixProvider> Providers => LazyProviders.Value;

        /// <summary>Every C# <see cref="CodeFixProvider"/> the Features assembly can construct.</summary>
        private static ImmutableArray<CodeFixProvider> LoadProviders()
        {
            // Touch a known type so the assembly is loaded before we go looking for it by name.
            _ = typeof(CodeFixProvider);
            var assembly = Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features");

            var builder = ImmutableArray.CreateBuilder<CodeFixProvider>();
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || !typeof(CodeFixProvider).IsAssignableFrom(type))
                    continue;
                try
                {
                    var ctor = type
                        .GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                        .FirstOrDefault(c => c.GetParameters().Length == 0);
                    if (ctor is null)
                        continue;
                    builder.Add((CodeFixProvider)ctor.Invoke(null));
                }
                catch
                {
                    // A provider we cannot construct is one devenv composes differently, not a failure here.
                }
            }
            return builder.ToImmutable();
        }

        /// <summary>The running runtime's reference set, so ordinary BCL types resolve.</summary>
        private static ImmutableArray<MetadataReference> LoadReferences()
        {
            var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
            var builder = ImmutableArray.CreateBuilder<MetadataReference>();
            foreach (var path in trusted.Split(Path.PathSeparator))
            {
                if (path.Length == 0 || !File.Exists(path))
                    continue;
                var name = Path.GetFileNameWithoutExtension(path);
                // The BCL only: pulling Roslyn's own assemblies into the compilation under test would
                // let a fix "resolve" against types the scenario never meant to have in scope.
                if (!name.StartsWith("System.", StringComparison.Ordinal) &&
                    !string.Equals(name, "mscorlib", StringComparison.Ordinal) &&
                    !string.Equals(name, "netstandard", StringComparison.Ordinal))
                    continue;
                try { builder.Add(MetadataReference.CreateFromFile(path)); }
                catch { }
            }
            return builder.ToImmutable();
        }

        /// <summary>A one-document project holding <paramref name="source"/>.</summary>
        internal static Document DocumentFor(string source)
        {
            var workspace = new AdhocWorkspace();
            var project = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Default,
                "FixProbe",
                "FixProbe",
                LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: LazyReferences.Value));
            return workspace.AddDocument(project.Id, "Probe.cs", SourceText.From(source));
        }

        /// <summary>The first compiler diagnostic with <paramref name="diagnosticId"/>, or null.</summary>
        internal static async Task<Diagnostic?> FirstDiagnosticAsync(Document document, string diagnosticId)
        {
            var model = await document.GetSemanticModelAsync().ConfigureAwait(false);
            return model?.GetDiagnostics()
                .Where(d => string.Equals(d.Id, diagnosticId, StringComparison.Ordinal))
                .OrderBy(d => d.Location.SourceSpan.Start)
                .FirstOrDefault();
        }

        /// <summary>
        /// The actions providers REGISTER for a diagnostic — unflattened, i.e. what
        /// <c>ApplyCodeFixAsync</c> collected before issue #142 was fixed.
        /// </summary>
        internal static async Task<List<CodeAction>> RegisteredActionsAsync(Document document, Diagnostic diagnostic)
        {
            var actions = new List<CodeAction>();
            foreach (var provider in Providers)
            {
                bool fixable;
                try { fixable = provider.FixableDiagnosticIds.Contains(diagnostic.Id); }
                catch { continue; }
                if (!fixable)
                    continue;

                try
                {
                    await provider.RegisterCodeFixesAsync(new CodeFixContext(
                        document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Matches the tool: a provider that can't run headlessly is skipped, not fatal.
                }
            }
            return actions;
        }

        /// <summary>
        /// What the tool actually offers, out of actions <b>already registered</b>. Takes the list
        /// rather than re-gathering it: a second <see cref="RegisteredActionsAsync"/> pass builds fresh
        /// <see cref="CodeAction"/> instances, so a caller holding both would find that no leaf is
        /// reference-equal to its own offered counterpart — which silently turns "this leaf was not
        /// offered" into a claim that is true of every leaf.
        /// </summary>
        internal static List<CodeAction> Offered(IEnumerable<CodeAction> registered) =>
            registered.SelectMany(a => CodeWicket.Ide.CodeFixActions.Applicable(a)).ToList();

        /// <summary>Register and offer in one step, for callers that need only the result.</summary>
        internal static async Task<List<CodeAction>> OfferedFixesAsync(Document document, Diagnostic diagnostic) =>
            Offered(await RegisteredActionsAsync(document, diagnostic).ConfigureAwait(false));

        /// <summary>
        /// Whether the action yields a change when applied — asked exactly as <c>ApplyCodeFixAsync</c>
        /// asks it, including treating a throw as "cannot be applied" rather than letting it escape.
        /// </summary>
        internal static async Task<(bool Applies, Exception? Failure)> TryApplyAsync(CodeAction action)
        {
            try
            {
                var operations = await action.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
                return (operations.OfType<ApplyChangesOperation>().Any(), null);
            }
            catch (Exception ex)
            {
                return (false, ex);
            }
        }
    }
}
