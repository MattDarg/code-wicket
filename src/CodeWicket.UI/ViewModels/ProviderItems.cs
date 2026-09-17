using System.Collections.Generic;
using System.Globalization;

namespace CodeWicket.UI.ViewModels
{
    /// <summary>A backend choice in the provider dropdown, carrying the models it advertises.</summary>
    public sealed class ProviderItemViewModel
    {
        public ProviderItemViewModel(
            string id, string displayName, IReadOnlyList<ModelItemViewModel> models,
            bool supportsResume = false, bool supportsModelSelection = false,
            string? resumeCommand = null, bool supportsMcp = false)
        {
            Id = id;
            DisplayName = displayName;
            Models = models;
            SupportsResume = supportsResume;
            SupportsModelSelection = supportsModelSelection;
            ResumeCommand = resumeCommand;
            SupportsMcp = supportsMcp;
        }

        /// <summary>Whether the engine hosts our IDE-tool bridge for this backend (the provider's
        /// <c>Mcp</c> capability). What the first-send wait for the IDE tools is gated on: a backend
        /// that takes no bridge would otherwise wait the whole timeout for a signal that cannot come.</summary>
        public bool SupportsMcp { get; }

        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>The models offered for this provider. Mutable because some backends (Claude Code)
        /// only reveal their real model list once a session opens, replacing the initial seed.</summary>
        public IReadOnlyList<ModelItemViewModel> Models { get; set; }

        /// <summary>True when the backend can reload a prior conversation (ACP session/load).</summary>
        public bool SupportsResume { get; }

        /// <summary>True when the backend can switch models on a live session (ACP session/set_model),
        /// so a model change applies to the current conversation instead of starting a new one.</summary>
        public bool SupportsModelSelection { get; }

        /// <summary>How this backend's CLI resumes a conversation, with <c>{id}</c> where the id goes.
        /// Null when it has no verified resume command, which hides the affordance entirely.</summary>
        public string? ResumeCommand { get; }
    }

    /// <summary>A model choice in the model dropdown.</summary>
    public sealed class ModelItemViewModel
    {
        public ModelItemViewModel(string id, string displayName, double? rateMultiplier = null)
        {
            Id = id;
            DisplayName = displayName;
            RateMultiplier = rateMultiplier;
        }

        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>Relative cost of this model (1.0 = baseline). Null when the backend doesn't report one.</summary>
        public double? RateMultiplier { get; }

        /// <summary>The rate shown muted next to the name, e.g. "1.3×"; empty when there's no rate to show.</summary>
        public string RateLabel =>
            RateMultiplier is { } rate
                ? rate.ToString("0.##", CultureInfo.CurrentCulture) + "×"
                : string.Empty;
    }
}
