using System;
using System.Collections.Generic;

namespace CodeWicket.Core
{
    /// <summary>
    /// The set of available agent backends, keyed by <see cref="IAgentProvider.ProviderId"/>.
    /// The host registers providers at startup and resolves the active one for a session.
    /// </summary>
    public sealed class AgentProviderRegistry
    {
        private readonly Dictionary<string, IAgentProvider> _providers =
            new Dictionary<string, IAgentProvider>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Registers (or replaces) a provider.</summary>
        public void Register(IAgentProvider provider)
        {
            if (provider is null)
                throw new ArgumentNullException(nameof(provider));
            if (string.IsNullOrWhiteSpace(provider.ProviderId))
                throw new ArgumentException("Provider must have a non-empty ProviderId.", nameof(provider));

            _providers[provider.ProviderId] = provider;
        }

        /// <summary>Returns the provider with the given id, or null if none is registered.</summary>
        public IAgentProvider? Get(string providerId)
        {
            if (providerId is null)
                return null;
            return _providers.TryGetValue(providerId, out var provider) ? provider : null;
        }

        /// <summary>All registered providers.</summary>
        public IReadOnlyCollection<IAgentProvider> Providers => _providers.Values;
    }
}
