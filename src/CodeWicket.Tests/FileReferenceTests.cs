using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Documents;
using CodeWicket.Ipc;
using CodeWicket.Shell;
using CodeWicket.UI.Markdown;
using CodeWicket.UI.ViewModels;
using Xunit;

namespace CodeWicket.Tests
{
    /// <summary>
    /// Clickable file references in agent prose: what the matcher recognises, what the resolver will and
    /// will not turn into a link, and that a resolved one renders as a working hyperlink.
    /// <para>
    /// The strings are seeded from the real corpus the feature was measured against — both separator styles, bare filenames (71%
    /// of real references), and the two that must stay plain text: generated code that isn't on disk and
    /// an SDK file outside the workspace.
    /// </para>
    /// </summary>
    public sealed class FileReferenceTests : IDisposable
    {
        private readonly string _root;

        public FileReferenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "cwkt-filerefs-" + Guid.NewGuid().ToString("N"));
            Write("Shared", "CTestClass.cs");
            Write("Data", "BTestClass.cs");
            Write("Data", "Program.cs");
            Write("Web", "Program.cs");
            Write("bin", "CTestClass.cs");   // build output: skipped, so the bare name stays unambiguous
            Write(null, "README.md");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { /* best effort scratch cleanup */ }
        }

        private string Write(string? directory, string name)
        {
            var dir = directory is null ? _root : Path.Combine(_root, directory);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "// " + name);
            return path;
        }

        private FileReferenceResolver Resolver() => new FileReferenceResolver(_root);

        private static (string Path, int? Line) Single(string text)
        {
            var match = Assert.Single(FileReferenceMatcher.Scan(text));
            return (match.PathText, match.Line);
        }

        // ---- matching -------------------------------------------------------------------------

        [Theory]
        // The dominant form, both separator styles — the same corpus produced both.
        [InlineData("Fixed it in `Shared\\CTestClass.cs:8` for you.", "Shared\\CTestClass.cs", 8)]
        [InlineData("See Data/BTestClass.cs:5 for the assertion.", "Data/BTestClass.cs", 5)]
        // A bare filename: 71% of real references. No line means the top of the file.
        [InlineData("I updated BTestClass.cs to match.", "BTestClass.cs", null)]
        // Trailing sentence punctuation belongs to the sentence, not the path.
        [InlineData("Look at README.md.", "README.md", null)]
        // MSBuild's form, which shows up when the agent quotes a build error.
        [InlineData("error CS1002 in Data/Program.cs(12,7)", "Data/Program.cs", 12)]
        [InlineData("Program.cs(12): ; expected", "Program.cs", 12)]
        public void RecognisesTheFormsAgentsWrite(string text, string expectedPath, int? expectedLine)
        {
            var (path, line) = Single(text);
            Assert.Equal(expectedPath, path);
            Assert.Equal(expectedLine, line);
        }

        // The matcher is deliberately permissive — resolution is the gate — but it must not fire on
        // things that are structurally not a name.ext at all, or every prose sentence pays for a lookup.
        [Theory]
        [InlineData("Bump to net8.0 and MessagePack 2.5.301.")]      // versions: the ext must start with a letter
        [InlineData("Rendered at 99.9% of the viewport.")]
        [InlineData("Add a .gitignore entry.")]                       // a dotfile is a name, not an extension
        [InlineData("It targets .NET 10 now.")]
        public void IgnoresTokensThatAreNotFileShaped(string text)
            => Assert.Empty(FileReferenceMatcher.Scan(text));

        // The counter-example: these DO match the token shape, on purpose. Carrying an extension
        // whitelist to exclude them would be a maintenance list that omits whatever the user's repo
        // actually contains — and resolution rejects them for free (asserted below).
        [Fact]
        public void NamespacesMatchTheTokenShapeAndAreLeftToResolution()
            => Assert.Equal("System.Text.Json", Single("Use System.Text.Json here.").Path);

        // A range or a column after the line changes nothing about where we open — but it must be part
        // of the link's SPAN, or the reference renders as a link with a stray "-11" sitting after it.
        // Both forms are in the same corpus that scoped the matcher; the reported case was the first.
        [Theory]
        [InlineData("See src/CodeWicket.Core/Ide/IdeMcpServer.cs:9-11 for the guard.",
            "src/CodeWicket.Core/Ide/IdeMcpServer.cs:9-11", 9)]
        [InlineData("Fails at ATestClass.cs:8:52 with CS8602.", "ATestClass.cs:8:52", 8)]
        public void SwallowsALineRangeOrColumnIntoTheReference(string text, string expectedSpan, int expectedLine)
        {
            var match = Assert.Single(FileReferenceMatcher.Scan(text));
            Assert.Equal(expectedSpan, text.Substring(match.Index, match.Length));
            Assert.Equal(expectedLine, match.Line);
        }

        // The tail is swallowed only when it is a plausible number: anything else leaves the reference
        // exactly as it was before, rather than guessing at a form nothing writes.
        [Theory]
        [InlineData("Between BTestClass.cs:5-and Program.cs there is a gap.", "BTestClass.cs:5")]
        [InlineData("Run README.md:2- to see it.", "README.md:2")]
        public void LeavesANonNumericTailAlone(string text, string expectedSpan)
        {
            var match = FileReferenceMatcher.Scan(text)[0];
            Assert.Equal(expectedSpan, text.Substring(match.Index, match.Length));
        }

        [Fact]
        public void FindsEveryReferenceInOneLine()
        {
            var matches = FileReferenceMatcher.Scan("Moved `Shared\\CTestClass.cs:8` into Data/Program.cs:2.");
            Assert.Equal(2, matches.Count);
            Assert.Equal("Shared\\CTestClass.cs", matches[0].PathText);
            Assert.Equal("Data/Program.cs", matches[1].PathText);
            // The spans must not overlap, or the renderer's slicing produces garbage.
            Assert.True(matches[0].Index + matches[0].Length <= matches[1].Index);
        }

        // Same contract as CodeHighlighter: this runs over the partial text of a mid-stream delta, so a
        // truncated reference must produce an answer rather than an exception.
        [Fact]
        public void NeverFaultsOnPartialText()
        {
            const string full = "Fixed `Shared\\CTestClass.cs:812` and Data/Program.cs(4,2) as well.";
            for (var i = 0; i <= full.Length; i++)
                FileReferenceMatcher.Scan(full.Substring(0, i));
        }

        // ---- resolution -----------------------------------------------------------------------

        [Fact]
        public async Task ResolvesAPathWrittenRelativeToTheRoot()
        {
            Assert.Equal(
                Path.Combine(_root, "Shared", "CTestClass.cs"),
                await Resolver().ResolveAsync("Shared\\CTestClass.cs"));
        }

        [Fact]
        public async Task NormalisesTheSeparatorTheAgentHappenedToUse()
        {
            Assert.Equal(
                Path.Combine(_root, "Data", "BTestClass.cs"),
                await Resolver().ResolveAsync("Data/BTestClass.cs"));
        }

        // The majority path: a bare filename found by index, with build output skipped so the second
        // copy under bin\ can't make it ambiguous.
        [Fact]
        public async Task ResolvesABareFilenameByUniqueSearch()
        {
            Assert.Equal(
                Path.Combine(_root, "Shared", "CTestClass.cs"),
                await Resolver().ResolveAsync("CTestClass.cs"));
        }

        // Two real candidates: linking either one would be a guess, and a link that opens the wrong
        // file is worse than plain text.
        [Fact]
        public async Task DoesNotLinkAnAmbiguousFilename()
            => Assert.Null(await Resolver().ResolveAsync("Program.cs"));

        // ...but the same name with a directory in front names one of them, so it resolves by suffix.
        [Fact]
        public async Task DirectoriesDisambiguateASharedFilename()
        {
            Assert.Equal(
                Path.Combine(_root, "Web", "Program.cs"),
                await Resolver().ResolveAsync("Web/Program.cs"));
        }

        // The two references in the corpus that SHOULD stay plain text: generated code that was never
        // written to disk, and an SDK file outside the workspace. The existence check does its job.
        [Theory]
        [InlineData("FetchData_razor.g.cs")]
        [InlineData("Microsoft.NET.EolTargetFrameworks.targets")]
        [InlineData("System.Text.Json")]
        public async Task DoesNotLinkWhatIsNotThere(string reference)
            => Assert.Null(await Resolver().ResolveAsync(reference));

        // The security rule: a resolution that escapes the root is dropped, so a prompt-injected
        // absolute path can never become a one-click open — even though the file plainly exists.
        [Fact]
        public async Task NeverLinksOutsideTheRoot()
        {
            var outside = Path.Combine(Path.GetTempPath(), "cwkt-outside-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(outside, "secrets");
            try
            {
                var resolver = Resolver();
                Assert.Null(await resolver.ResolveAsync(outside));
                Assert.Null(await resolver.ResolveAsync(@"..\..\Windows\win.ini"));
            }
            finally
            {
                try { File.Delete(outside); } catch { }
            }
        }

        // A file the agent is about to CREATE doesn't exist when it first names it. Without this the
        // negative answer would stand for the rest of the conversation, leaving the reference plain
        // text exactly where it is most useful.
        [Fact]
        public async Task ForgetsANegativeOnceTheAgentWritesTheFile()
        {
            var resolver = Resolver();
            Assert.Null(await resolver.ResolveAsync("NewClass.cs"));

            var created = Write("Data", "NewClass.cs");
            resolver.NoteFileWritten(created);

            Assert.Equal(created, await resolver.ResolveAsync("NewClass.cs"));
        }

        // Rooting the resolver at a new working directory (a session reporting one, or a restore
        // supplying the saved one) must not leave answers from the old root standing.
        [Fact]
        public async Task RepointingTheRootDiscardsWhatWasLearned()
        {
            var resolver = Resolver();
            Assert.NotNull(await resolver.ResolveAsync("CTestClass.cs"));

            var other = Path.Combine(_root, "Data");
            resolver.SetRoot(other);

            Assert.Null(await resolver.ResolveAsync("CTestClass.cs"));
            Assert.Equal(Path.Combine(other, "Program.cs"), await resolver.ResolveAsync("Program.cs"));
        }

        // ---- rendering ------------------------------------------------------------------------

        [Fact]
        public async Task AResolvedReferenceRendersAsALinkThatOpensIt()
        {
            var resolver = Resolver();
            // The renderer only ever consumes an ALREADY-cached answer (no IO on the render path), so
            // prime it exactly as the async upgrade would before the re-render.
            await resolver.ResolveAsync("Shared\\CTestClass.cs");
            await resolver.ResolveAsync("FetchData_razor.g.cs");

            RunSta(() =>
            {
                (string Path, int Line)? opened = null;
                var links = new FileLinkContext(resolver, (path, line) =>
                {
                    opened = (path, line);
                    return Task.CompletedTask;
                });

                var viewer = new FlowDocumentScrollViewer();
                MarkdownText.SetLinks(viewer, links);
                MarkdownText.SetText(viewer, "Fixed `Shared\\CTestClass.cs:8`, but FetchData_razor.g.cs is generated.");

                var hyperlink = Assert.Single(Inlines(viewer.Document!).OfType<Hyperlink>());
                Assert.Equal("Shared\\CTestClass.cs:8", new TextRange(hyperlink.ContentStart, hyperlink.ContentEnd).Text);
                Assert.Equal(Path.Combine(_root, "Shared", "CTestClass.cs") + ":8", hyperlink.ToolTip);

                // The unresolvable one stays plain text — a link that does nothing teaches the user to
                // distrust all of them.
                Assert.Contains("FetchData_razor.g.cs", PlainText(viewer.Document!));

                hyperlink.RaiseEvent(new System.Windows.RoutedEventArgs(Hyperlink.ClickEvent));
                Assert.Equal((Path.Combine(_root, "Shared", "CTestClass.cs"), 8), opened);
            });
        }

        // A host with no way to open a file (the stub hosts) gets no context at all, and nothing in the
        // prose is decorated as if it were clickable.
        [Fact]
        public void WithoutAnOpenerEverythingStaysPlainText() => RunSta(() =>
        {
            var viewer = new FlowDocumentScrollViewer();
            MarkdownText.SetText(viewer, "Fixed `Shared\\CTestClass.cs:8` for you.");
            Assert.Empty(Inlines(viewer.Document!).OfType<Hyperlink>());
        });

        // ---- replay ---------------------------------------------------------------------------

        /// <summary>
        /// A restored conversation is replayed before any backend session exists, so its references have
        /// to resolve from the SAVED working directory. This is what makes the feature retroactively
        /// useful on transcripts recorded long before it existed.
        /// </summary>
        [Fact]
        public void ARestoredTranscriptResolvesAgainstTheSavedWorkingDirectory() => RunSta(() =>
        {
            var storeRoot = Path.Combine(_root, "sessions");
            var store = new FileSessionStore(storeRoot);
            var solutionRoot = Path.Combine(_root, "Web");   // deliberately NOT the agent's directory

            var session = new PersistedSession
            {
                WorkspaceRootPath = solutionRoot,
                AgentWorkingDirectory = _root,
                Title = "Restored",
            };
            session.Log.Add(new TranscriptEntry { Role = "user", Text = "where is it?" });
            session.Log.Add(new TranscriptEntry
            {
                Role = "agent",
                Event = new AgentEventDto { Type = "text", Text = "It's in `Shared\\CTestClass.cs:8`." },
            });
            store.Save(session);

            var vm = new ChatViewModel(
                new OfflineEngine(),
                new StartSessionRequest("fake", null, solutionRoot, "Prompt", null),
                sessionStore: store,
                openFile: (_, _) => Task.CompletedTask);

            vm.RestoreMostRecentSession();

            Assert.NotNull(vm.FileLinks);
            // The saved agent directory, not the solution root the history is keyed by.
            Assert.Equal(_root, vm.FileLinks!.Resolver.Root);
            Assert.Contains(vm.Items.OfType<MessageItemViewModel>(),
                m => m.IsAssistant && m.Text.Contains("CTestClass.cs:8"));
        });

        /// <summary>
        /// A ChatViewModel needs an engine to construct; the replay path never touches one, so this
        /// answers nothing and asserts by being unused.
        /// </summary>
        private sealed class OfflineEngine : IEngineConnection
        {
            public event Action<AgentEventDto>? AgentEvent { add { } remove { } }

            public event Action<ProviderModelsDto>? ProviderModelsRefreshed { add { } remove { } }

            // A fake with no handshake reports no session, which the panel renders as
            // "no agent session open yet" rather than as absent facts (issue #160).
            public Task<SessionInfoResponse?> SessionInfoAsync(CancellationToken cancellationToken = default)
                => Task.FromResult<SessionInfoResponse?>(null);

            public Task<ListProvidersResponse> ListProvidersAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new ListProvidersResponse(new List<ProviderInfoDto>()));

            public Task<StartSessionResponse> StartSessionAsync(StartSessionRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not open a backend session.");

            public Task<PromptResponse> PromptAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not prompt.");

            public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<SteerResponse> SteerAsync(string text, IReadOnlyList<PromptAttachmentDto>? attachments = null, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not steer.");

            public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) => Task.CompletedTask;

            public Task<ListBackendSessionsResponse> ListBackendSessionsAsync(
                ListBackendSessionsRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub lists no backend sessions.");

            public Task<TakeImportedHistoryResponse> TakeImportedHistoryAsync(
                TakeImportedHistoryRequest request, CancellationToken cancellationToken = default)
                => throw new System.NotSupportedException("This stub imports no history.");

            public Task<SummarizeResponse> SummarizeAsync(SummarizeRequest request, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("A restore must not summarize.");
        }

        // ---- helpers --------------------------------------------------------------------------

        private static IEnumerable<Inline> Inlines(FlowDocument document)
        {
            foreach (var block in document.Blocks)
            {
                if (block is not Paragraph paragraph)
                    continue;
                foreach (var inline in Flatten(paragraph.Inlines))
                    yield return inline;
            }
        }

        private static IEnumerable<Inline> Flatten(InlineCollection inlines)
        {
            foreach (var inline in inlines)
            {
                yield return inline;
                if (inline is Span span)
                {
                    foreach (var nested in Flatten(span.Inlines))
                        yield return nested;
                }
            }
        }

        private static string PlainText(FlowDocument document)
            => new TextRange(document.ContentStart, document.ContentEnd).Text;

        // WPF text elements demand an STA thread; xunit runs MTA.
        // One shared, GATED implementation - see StaTest. Two STA bodies from different test
        // classes used to run concurrently against process-global WPF and clipboard state.
        private static void RunSta(Action action) => StaTest.Run(action);

        /// <summary>
        /// Waiting for the filename index must never BLOCK a threadpool thread. Inside devenv that pool
        /// is shared with the whole IDE, and a restored transcript asks for on the order of a hundred
        /// references at once — nearly all bare filenames, which miss the direct-path check and land on
        /// the index — so a blocking wait parked ~100 work items behind one directory walk while VS was
        /// loading a solution on the same pool. Thread injection then trickles, so the stall lasted as
        /// long as the walk, which on a network- or sync-backed repo is not short.
        /// </summary>
        /// <remarks>
        /// The probe is a sentinel work item queued BEHIND the pending resolutions: if they are parked,
        /// it cannot run until the pool injects enough replacement threads, which takes tens of seconds.
        /// Verified load-bearing by restoring the blocking wait — the sentinel then misses its timeout.
        /// The gated builder also pins that concurrent resolutions share ONE walk rather than each
        /// starting their own.
        /// </remarks>
        [Fact]
        public void PendingReferences_DoNotBlockThreadPoolThreads()
        {
            using var indexGate = new ManualResetEventSlim(false);
            using var buildStarted = new ManualResetEventSlim(false);
            var builds = 0;

            var resolver = Resolver();
            resolver.IndexBuilder = (root, token) =>
            {
                Interlocked.Increment(ref builds);
                buildStarted.Set();
                indexGate.Wait(token);
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            };

            try
            {
                // Bare names that exist nowhere under the root, so every one misses the direct check.
                const int references = 256;
                for (var i = 0; i < references; i++)
                    resolver.RequestResolve($"Pending{i}.cs", () => { });

                Assert.True(buildStarted.Wait(TimeSpan.FromSeconds(10)), "the index build never started");

                // The signal is the QUEUE draining, not a sentinel racing the blocked items — a sentinel
                // queued globally just gets picked up by an idle thread and proves nothing (measured).
                //
                // Awaiting, each resolution's work item RUNS TO COMPLETION and parks nothing, so the
                // queue empties within milliseconds even though not one reference has resolved yet.
                // Blocking, only as many as the pool has threads can even start; the rest stay queued
                // and drain at the pool's injection trickle (~1-2 a second), so the queue is still
                // hundreds deep when this window closes.
                var drained = false;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (ThreadPool.PendingWorkItemCount == 0) { drained = true; break; }
                    Thread.Sleep(25);
                }

                Assert.True(
                    drained,
                    $"the threadpool queue never emptied ({ThreadPool.PendingWorkItemCount} still pending): " +
                    "the resolutions are holding threads while waiting for the index");
            }
            finally
            {
                indexGate.Set(); // let the parked builder (and anything behind it) finish
            }

            Assert.Equal(1, Volatile.Read(ref builds));
        }

        /// <summary>
        /// Repointing at a new root must CANCEL the walk it abandons, not merely drop the reference to it.
        /// The root genuinely flaps during startup — opening a solution closes the previous one first, so
        /// the default workspace is transited on the way — and each flap would otherwise leave another
        /// full recursive walk running against a root nobody is waiting on, competing for the same disk.
        /// </summary>
        [Fact]
        public void ChangingRoot_CancelsTheWalkItAbandons()
        {
            using var buildStarted = new ManualResetEventSlim(false);
            using var observedCancel = new ManualResetEventSlim(false);

            var resolver = Resolver();
            resolver.IndexBuilder = (root, token) =>
            {
                buildStarted.Set();
                // Returns early only if the token fires; otherwise it runs to the timeout, which is what
                // "abandoned but still walking" looks like.
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
                if (!token.IsCancellationRequested)
                    return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                observedCancel.Set();
                throw new OperationCanceledException(token);
            };

            _ = resolver.ResolveAsync("Pending.cs");
            Assert.True(buildStarted.Wait(TimeSpan.FromSeconds(10)), "the index build never started");

            resolver.SetRoot(Path.Combine(_root, "Data"));

            Assert.True(
                observedCancel.Wait(TimeSpan.FromSeconds(5)),
                "SetRoot abandoned the walk without cancelling it, so it kept running against the old root");
        }

        /// <summary>
        /// The walk reports its cost, because nothing else can: "the blue links take a while" is set by
        /// the machine (repo size, storage redirected onto a share, on-access scanning of every
        /// enumeration) and is invisible from here without a number and a size beside it.
        /// </summary>
        [Fact]
        public async Task TheIndexWalkReportsItsDurationAndSize()
        {
            var lines = new List<string>();
            var resolver = Resolver();
            resolver.IndexLog = line => { lock (lines) lines.Add(line); };

            await resolver.ResolveAsync("Program.cs");

            var line = Assert.Single(lines, l => l.StartsWith("[file-index] ", StringComparison.Ordinal));
            Assert.Contains(" ms, ", line, StringComparison.Ordinal);
            Assert.Contains(" paths, ", line, StringComparison.Ordinal);
            Assert.Contains(" names, root " + _root, line, StringComparison.Ordinal);
        }

        /// <summary>
        /// An ABANDONED walk is reported too. The root flaps during startup (see
        /// <see cref="ChangingRoot_CancelsTheWalkItAbandons"/>), so a log carrying only the surviving
        /// walk would attribute the whole wait to it and hide the full-tree enumerations that ran
        /// alongside — which is the shape of the report this instrumentation exists for.
        /// </summary>
        [Fact]
        public void AnAbandonedWalkIsReportedRatherThanVanishing()
        {
            using var buildStarted = new ManualResetEventSlim(false);
            var lines = new List<string>();

            var resolver = Resolver();
            resolver.IndexLog = line => { lock (lines) lines.Add(line); };
            resolver.IndexBuilder = (root, token) =>
            {
                buildStarted.Set();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
                token.ThrowIfCancellationRequested();
                return new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            };

            _ = resolver.ResolveAsync("Pending.cs");
            Assert.True(buildStarted.Wait(TimeSpan.FromSeconds(10)), "the index build never started");

            resolver.SetRoot(Path.Combine(_root, "Data"));

            var reported = false;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (lines)
                    reported = lines.Any(l => l.Contains("abandoned (root changed)", StringComparison.Ordinal));
                if (reported)
                    break;
                Thread.Sleep(25);
            }

            Assert.True(reported, "the abandoned walk left no trace of the time it spent");
        }

        /// <summary>With no sink the walk is not instrumented at all — the Desktop host's shape.</summary>
        [Fact]
        public async Task WithNoSinkTheWalkIsNotInstrumented()
        {
            var resolver = Resolver();

            var resolved = await resolver.ResolveAsync("README.md");

            Assert.NotNull(resolved);
        }
    }
}
