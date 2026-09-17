// Enables C# record `init` accessors when targeting netstandard2.0,
// which does not ship the System.Runtime.CompilerServices.IsExternalInit type.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
#endif
