using System;
using System.Collections.Generic;
using CodeWicket.UI.Mvvm;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>
    /// When messages typed mid-turn are delivered. A property of the TRAY, not of a message — see
    /// <c>ChatViewModel.PendingReleaseMode</c> for why. These are Kiro's two follow-up modes:
    /// <see cref="NextStep"/> is its "steer", <see cref="TurnEnd"/> its "queue".
    /// </summary>
    public enum PendingRelease
    {
        /// <summary>
        /// At the next moment no tool call is known to be running — the next safe point in the agent's
        /// work. Honoured on every backend: where steering exists the message is injected there, and
        /// where it does not the turn is ended and the message delivered, which measures the same.
        /// </summary>
        NextStep,

        /// <summary>When the current turn genuinely ends (a real <c>IsBusy</c> transition, never a timer).</summary>
        TurnEnd,
    }

    /// <summary>
    /// The configured names for <see cref="PendingRelease"/> — the vocabulary the tray's pill already
    /// uses, so a setting and the control it presets read alike (issue #190).
    /// <para>Named rather than the enum's own members because the stored value is the user's word for
    /// it: "Steer" is what the pill says, while <c>NextStep</c> is what the release point is called
    /// internally. Round-tripped through <see cref="Parse"/> on the way out as well as in, so a
    /// hand-edited config.json shows the Settings UI the mode that is actually in force.</para>
    /// </summary>
    public static class PendingReleaseModes
    {
        /// <summary>After the turn ends — the shipped default.</summary>
        public const string Queue = "Queue";

        /// <summary>At the agent's next step.</summary>
        public const string Steer = "Steer";

        /// <summary>
        /// The mode a stored name asks for. Anything unrecognised — including null and a blank, which
        /// is what a config written before this setting existed carries — is Queue, which is what the
        /// window opened on before there was anything to read.
        /// </summary>
        public static PendingRelease Parse(string? name) =>
            string.Equals(name, Steer, StringComparison.OrdinalIgnoreCase)
                ? PendingRelease.NextStep
                : PendingRelease.TurnEnd;

        /// <summary>The stored name for a mode.</summary>
        public static string ToName(PendingRelease mode) =>
            mode == PendingRelease.NextStep ? Steer : Queue;
    }

    /// <summary>
    /// One message typed while the agent was working, waiting in the tray above the composer.
    /// <para>
    /// A plain class, not a record — this project has no <c>IsExternalInit</c> polyfill and must not
    /// gain one (see AGENTS.md).
    /// </para>
    /// <para>
    /// Deliberately a row with an action area rather than a line of text: remove is here already, and
    /// inline editing is the next thing it has to carry. What it deliberately does NOT carry is its own
    /// release point OR its own send-now — both were tried, and per-message urgency implies an order the
    /// batch cannot express, since everything released together is delivered as one joined message. It
    /// also could not be honoured evenly: without steering the delivery rides the turn-end release,
    /// which takes the whole tray, so "send just this one" sent one message on Claude and all of them on
    /// Kiro. Urgency is the tray's; only removal is a message's own.
    /// </para>
    /// </summary>
    public sealed class PendingMessageViewModel : ObservableObject
    {
        public PendingMessageViewModel(
            string text,
            Action<PendingMessageViewModel> remove,
            IReadOnlyList<AttachmentViewModel>? attachments = null,
            IReadOnlyList<ContextItemViewModel>? contexts = null)
        {
            Text = text;
            Attachments = attachments ?? Array.Empty<AttachmentViewModel>();
            Contexts = contexts ?? Array.Empty<ContextItemViewModel>();
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        /// <summary>The message exactly as typed. Immutable.</summary>
        public string Text { get; }

        /// <summary>
        /// Images held with it. They travel as part of the message rather than as tray state, because
        /// removing one held message must not take another's picture with it — the release point is
        /// tray-wide, but the content is not.
        /// </summary>
        public IReadOnlyList<AttachmentViewModel> Attachments { get; }

        public bool HasAttachments => Attachments.Count > 0;

        /// <summary>
        /// IDE context held with it, on the same rule as the images: the payload belongs to the
        /// MESSAGE, not to the tray, so removing one held message cannot take another's capture.
        /// </summary>
        public IReadOnlyList<ContextItemViewModel> Contexts { get; }

        public bool HasContexts => Contexts.Count > 0;

        /// <summary>Drop it without sending.</summary>
        public RelayCommand RemoveCommand { get; }
    }
}
