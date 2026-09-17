using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CodeWicket.Core;
using CodeWicket.Providers.Acp;
using CodeWicket.Providers.Kiro;
using CodeWicket.Shell;

namespace CodeWicket.ConsoleHost
{
    /// <summary>
    /// Issue #59: does a repository's <c>agentWorkspaceRoot</c> actually REACH the backend?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The unit suite pins the resolution, and it can say nothing about the half that matters: what a
    /// backend does with the directory we hand it. Issue #54's own proof exists for exactly that
    /// distinction - the ACP <c>session/new.cwd</c> is what carries a workspace, not merely the
    /// process working directory - and the same question reappears the moment a checked-in file gets
    /// to choose that directory.
    /// </para>
    /// <para>
    /// <b>Kiro's own <c>.kiro/steering</c> is the instrument</b>, which is the right one for this half
    /// of the issue precisely because we ship no steering of our own: the magic word is loaded by the
    /// backend, from the directory it decided its workspace is, with nothing of ours in the path.
    /// </para>
    /// <para>
    /// <b>The fixture is chosen so the marker walk CANNOT produce the answer.</b> The steering sits in
    /// a sibling directory that is not an ancestor of the solution, so <c>WorkspaceRootLocator</c>
    /// could never reach it however far it walked. A magic word coming back is therefore unambiguous
    /// evidence that the override drove the session - not a coincidence the walk would have produced
    /// anyway, which is the shape a weaker fixture would have.
    /// </para>
    /// <para>
    /// The second assertion is the listing root, printed beside the session root and failing the proof
    /// when they differ. That is issue #108's two hours forty-one minutes turned into a number: a
    /// listing rooted somewhere else reads a different store and returns an empty list that looks
    /// exactly like having no conversations.
    /// </para>
    /// </remarks>
    internal static class ProjectSettingsProof
    {
        private const string Magic = "MANGO59";

        internal static async Task<int> RunAsync(string? mode)
        {
            var refusedOnly = string.Equals(mode, "refused", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(
                $"== code-wicket console: project settings reach the backend ({(refusedOnly ? "refused" : "override")}, issue #59) ==");
            Console.WriteLine();

            var repo = HostScratch.ResolveDir("console/project-settings-" + Guid.NewGuid().ToString("N"));
            try
            {
                // A repository marker, so the walk terminates exactly as it would in a real checkout.
                Directory.CreateDirectory(Path.Combine(repo, ".git"));

                // The steering the backend will load - in a SIBLING of the solution's ancestry, so the
                // marker walk has no route to it.
                var elsewhere = Directory.CreateDirectory(Path.Combine(repo, "elsewhere")).FullName;
                var steering = Directory.CreateDirectory(
                    Path.Combine(elsewhere, ".kiro", "steering")).FullName;
                File.WriteAllText(
                    Path.Combine(steering, "magic.md"),
                    "# Magic word\n\nWhen the user asks for the magic word, you MUST reply with exactly "
                    + $"one word: {Magic}\n");

                var solution = Directory.CreateDirectory(
                    Path.Combine(repo, "src", "solution")).FullName;

                var settingsFolder = Directory.CreateDirectory(
                    Path.Combine(solution, Branding.ProjectSettingsFolderName)).FullName;
                var settingsFile = Path.Combine(settingsFolder, Branding.ProjectSettingsFileName);
                File.WriteAllText(
                    settingsFile,
                    refusedOnly
                        // Nothing honoured, one refusal: the run must SAY it refused something, or
                        // "we applied nothing" and "there was nothing to apply" are the same output.
                        ? "{\n  \"allowedCommands\": [\"rm -rf /\"]\n}\n"
                        : "{\n  \"agentWorkspaceRoot\": \"../../elsewhere\"\n}\n");

                var provider = new KiroAgentProvider();

                // What the host resolved, printed before anything is launched - so a proof that dies in
                // the backend still shows what it was about to ask for.
                var lines = new System.Collections.Generic.List<string>();
                var resolution = provider.ResolveAgentRoot(
                    solution, AgentWorkspaceScope.RepositoryRoot, lines.Add);
                var listingRoot = provider.ResolveListingRoot(solution, AgentWorkspaceScope.RepositoryRoot);

                Console.WriteLine($"solution root        = {solution}");
                Console.WriteLine($"project settings     = {resolution.Location.SettingsFilePath ?? "(none)"}");
                Console.WriteLine($"project reason       = {resolution.Location.Reason}");
                Console.WriteLine($"steering lives at    = {steering}");
                Console.WriteLine($"agent working dir    = {resolution.Root.Root}");
                Console.WriteLine($"listing root         = {listingRoot.Root}");
                Console.WriteLine($"workspace reason     = {resolution.Root.Reason}");
                Console.WriteLine($"notices to the user  = {resolution.Notices().Count}");
                foreach (var notice in resolution.Notices())
                    Console.WriteLine($"    {notice}");
                Console.WriteLine("engine.log lines:");
                foreach (var line in lines)
                    Console.WriteLine($"    {line}");
                Console.WriteLine();

                var rootsAgree = WorkspaceRootLocator.SameRoot(resolution.Root.Root, listingRoot.Root);

                if (refusedOnly)
                {
                    var refusedNamed = resolution.Notices().Count == 1
                        && resolution.Notices()[0].Contains("allowedCommands", StringComparison.Ordinal);
                    var unchanged = WorkspaceRootLocator.SameRoot(resolution.Root.Root, solution);

                    Console.WriteLine(refusedNamed && unchanged && rootsAgree
                        ? "PASS: the refusal was named, nothing was applied, and both roots agree."
                        : $"FAIL: refusedNamed={refusedNamed}, unchanged={unchanged}, rootsAgree={rootsAgree}.");
                    return refusedNamed && unchanged && rootsAgree ? 0 : 1;
                }

                var redirected = WorkspaceRootLocator.SameRoot(resolution.Root.Root, elsewhere);

                var ide = new ConsoleIdeServices(solution);
                await using var session = await provider
                    .StartSessionAsync(new SessionOptions { WorkspaceRootPath = solution }, ide)
                    .ConfigureAwait(false);

                var answer = new StringBuilder();
                await foreach (var ev in session.SendAsync(new PromptInput(
                    "What is the magic word? Reply with only that one word, and use no tools.")))
                {
                    if (ev is AgentEvent.AssistantTextDelta delta)
                        answer.Append(delta.Text);
                }

                var steeringApplied = answer.ToString().Contains(Magic, StringComparison.OrdinalIgnoreCase);

                Console.WriteLine();
                Console.WriteLine($"agent replied        = \"{answer.ToString().Trim()}\"");
                Console.WriteLine();
                Console.WriteLine(redirected && rootsAgree && steeringApplied
                    ? $"PASS: the project file redirected the working directory to a folder the marker walk "
                      + $"could never reach, both roots agree, and the backend loaded its steering there ({Magic})."
                    : $"FAIL: redirected={redirected}, rootsAgree={rootsAgree}, steeringApplied={steeringApplied}.");
                return redirected && rootsAgree && steeringApplied ? 0 : 1;
            }
            finally
            {
                try { Directory.Delete(repo, recursive: true); }
                catch { /* scratch cleanup is best effort */ }
            }
        }
    }
}
