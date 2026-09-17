using System;
using System.Collections.Generic;
using CodeWicket.Ipc;

namespace CodeWicket.Shell
{
    /// <summary>
    /// Folds the streamed <see cref="AgentEventDto"/>s into terminal-mirror writes (the tee's pure
    /// half; the VSIX wires the delegates to the actual pane). Command output reaches the pane two
    /// ways, matching how the backends behave:
    /// - Kiro streams a running command's stdout as <c>toolOutput</c> chunks: the first chunk lazily
    ///   writes the command's separator header (by then the enriching <c>toolUpdate</c> has usually
    ///   supplied the real title — Claude-style placeholders open with a generic one) and the
    ///   completion's result text is skipped (it is a superset of what already streamed).
    /// - Claude Code streams nothing mid-run: an execute-kind call that never produced chunks
    ///   mirrors its <c>toolDone</c> result text instead, so its commands still show up.
    /// A failed completion appends its stderr (<c>ErrorText</c>) and a red failure marker. All
    /// per-call state is keyed by ToolCallId and cleared on <c>turnDone</c> (ids are per-turn;
    /// id-reusing backends must not leak titles across turns — same rule as the edit dedupe).
    /// </summary>
    public sealed class TerminalMirrorTee
    {
        private readonly Action<string> _writeHeader; // takes the raw title; the sink sanitizes
        private readonly Action<string> _write;
        private readonly object _gate = new object();
        private readonly Dictionary<string, string?> _titles = new Dictionary<string, string?>();
        private readonly Dictionary<string, string?> _kinds = new Dictionary<string, string?>();
        // Streamed calls; the value records whether the last chunk ended with a newline (Kiro's
        // chunks are discrete blocks WITHOUT one — the tool row joins them with "\n", see
        // ToolItemViewModel.AppendLiveOutput — so the tee must restore the boundary the same way,
        // or adjacent blocks concatenate: a dir listing showed two rows fused mid-line, 2026-07-17).
        private readonly Dictionary<string, bool> _mirrored = new Dictionary<string, bool>();
        // The command each call is actually running, lifted from rawInput — see TitleFor for why the
        // row's own title will not do.
        private readonly Dictionary<string, string?> _commands = new Dictionary<string, string?>();

        public TerminalMirrorTee(Action<string> writeHeader, Action<string> write)
        {
            _writeHeader = writeHeader ?? throw new ArgumentNullException(nameof(writeHeader));
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }

        public void OnEvent(AgentEventDto ev)
        {
            if (ev is null)
                return;

            lock (_gate)
            {
                switch (ev.Type)
                {
                    case "toolStart":
                    case "toolUpdate":
                        if (ev.ToolCallId is { Length: > 0 } id)
                        {
                            if (!string.IsNullOrWhiteSpace(ev.Title))
                                _titles[id] = ev.Title;
                            if (!string.IsNullOrWhiteSpace(ev.Kind))
                                _kinds[id] = ev.Kind;
                            // Only ever LEARN a command, never unlearn one. Measured on a live
                            // Claude capture: the opening tool_call carries an EMPTY rawInput and the
                            // command arrives on a later tool_call_update - so storing whatever the
                            // latest frame parsed to would let an empty one erase a command already
                            // known, and the header would silently fall back to the prose title. The
                            // ordering happens to save us today, which is exactly why it should not be
                            // what we depend on.
                            if (TerminalMirrorText.CommandIn(ev.RawInputJson) is { Length: > 0 } cmd)
                                _commands[id] = cmd;
                        }
                        break;

                    case "toolOutput":
                        if (ev.ToolCallId is { Length: > 0 } outId && !string.IsNullOrEmpty(ev.Text))
                        {
                            // Not output at all, just the call describing itself again (#156).
                            if (IsEchoOfItsOwnLabel(outId, ev.Text!))
                                break;

                            if (_mirrored.TryGetValue(outId, out var endedWithNewline))
                            {
                                // Restore the chunk-boundary newline (conditionally, so a backend
                                // whose chunks do end with one never gets doubled blank lines).
                                if (!endedWithNewline)
                                    _write("\r\n");
                            }
                            else
                            {
                                _writeHeader(TitleFor(outId));
                            }
                            _mirrored[outId] = ev.Text!.EndsWith("\n", StringComparison.Ordinal);
                            _write(ev.Text!);
                        }
                        break;

                    case "toolDone":
                        OnToolDone(ev);
                        break;

                    case "turnDone":
                        _titles.Clear();
                        _kinds.Clear();
                        _mirrored.Clear();
                        _commands.Clear();
                        break;
                }
            }
        }

        private void OnToolDone(AgentEventDto ev)
        {
            var id = ev.ToolCallId;
            if (string.IsNullOrEmpty(id))
                return;

            // The VALUE as well as the fact. _mirrored records whether the last streamed chunk ended
            // with a newline, which is the whole reason it holds a bool rather than being a set - and
            // OnToolDone took Remove's return and threw the payload away.
            var streamed = _mirrored.TryGetValue(id!, out var lastChunkEndedWithNewline);
            _mirrored.Remove(id!);
            if (!streamed)
            {
                // No live chunks: only execute-kind completions with output are worth a card in the
                // pane (Claude Code's shell). Reads/edits/etc. stay out of the terminal.
                var isExecute = _kinds.TryGetValue(id!, out var kind) &&
                    string.Equals(kind, "execute", StringComparison.OrdinalIgnoreCase);
                var hasContent = !string.IsNullOrEmpty(ev.Message) || !string.IsNullOrEmpty(ev.ErrorText);
                if (!isExecute || !hasContent)
                {
                    _titles.Remove(id!);
                    _kinds.Remove(id!);
                    _commands.Remove(id!);
                    return;
                }
                _writeHeader(TitleFor(id!));
                if (!string.IsNullOrEmpty(ev.Message))
                    _write(EnsureTrailingNewline(ev.Message!));
            }

            // Stderr never rides the chunk stream, so a failure's ErrorText is new either way.
            if (ev.Success == false)
            {
                // Close the last streamed line before appending anything to it - the same rule the
                // streaming path applies between chunks, and for the same reason. A backend whose final
                // chunk carries no trailing newline (Kiro's streamed calls) otherwise gets its stderr,
                // or the failure marker itself, fused onto the last line of stdout: the two-rows-on-one
                // -line symptom this flag was added to fix, reappearing on the failure path because the
                // completion handler never read it.
                if (streamed && !lastChunkEndedWithNewline)
                    _write("\r\n");
                if (!string.IsNullOrEmpty(ev.ErrorText))
                    _write(EnsureTrailingNewline(ev.ErrorText!));
                _write("\x1b[31m[command failed]\x1b[0m\r\n");
            }
            else
            {
                _write("\r\n");
            }

            _titles.Remove(id!);
            _kinds.Remove(id!);
            // ...and the command, which only the early-return branch above was clearing. Left behind,
            // entries accumulate until turnDone, and a backend that reuses a tool call id within a turn
            // gets the PREVIOUS call's command in its separator header (see TitleFor).
            _commands.Remove(id!);
        }

        /// <summary>
        /// What the separator names: the COMMAND if we have one, else the row's title.
        /// </summary>
        /// <remarks>
        /// A terminal pane exists to show what ran, and <c>ev.Title</c> is not that for a Claude
        /// <c>Bash</c> call: issue #125 deliberately SWAPS the title to
        /// <c>_meta.claudeCode.title</c> — a prose label like "Show diff for Foo.cs" — because the
        /// ACP title is the raw command and the prose reads better on a transcript row. That trade is
        /// right for a row and wrong here, so this consumer takes the other half of it rather than
        /// the swap being undone. Issue #156, where the mirror showed a prose header above the same
        /// prose again, with the command nowhere on screen.
        /// </remarks>
        private string TitleFor(string id)
        {
            if (_commands.TryGetValue(id, out var command) && !string.IsNullOrWhiteSpace(command))
                return command!;
            return _titles.TryGetValue(id, out var title) && !string.IsNullOrWhiteSpace(title)
                ? title!
                : "command";
        }

        /// <summary>
        /// Whether <paramref name="text"/> is the call's own label rather than anything it produced.
        /// </summary>
        /// <remarks>
        /// The Claude adapter emits a content block whose text IS <c>rawInput.description</c>, which
        /// reaches us as <c>toolOutput</c> — so the pane printed the label, then printed it again as
        /// though the command had echoed it. A chunk equal to what we just put in the separator is
        /// not output, and dropping it here costs nothing: if it were genuinely also the command's
        /// first line, the line is still visible in the header directly above.
        /// </remarks>
        private bool IsEchoOfItsOwnLabel(string id, string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return false;
            return Matches(_titles, id, trimmed) || Matches(_commands, id, trimmed);
        }

        private static bool Matches(Dictionary<string, string?> map, string id, string text) =>
            map.TryGetValue(id, out var value)
            && !string.IsNullOrWhiteSpace(value)
            && string.Equals(value!.Trim(), text, StringComparison.Ordinal);

        private static string EnsureTrailingNewline(string text) =>
            text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n";
    }
}
