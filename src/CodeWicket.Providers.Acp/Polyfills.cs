// Types present in netstandard2.1+/.NET Core 3+ but missing from the netstandard2.0 surface.
#if !NETSTANDARD2_1_OR_GREATER && !NETCOREAPP3_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    using System;
    using System.ComponentModel;

    /// <summary>Enables C# record <c>init</c> accessors on netstandard2.0.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }

    // Note: EnumeratorCancellationAttribute is supplied by Microsoft.Bcl.AsyncInterfaces.
}
#endif
