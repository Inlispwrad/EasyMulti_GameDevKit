// netstandard2.1 predates records, so it lacks the marker type the compiler requires for
// `init` accessors (which every positional record property uses). Declaring it here is the
// standard shim; the runtime never looks at it.
//
// The guard matters because these files are meant to be copied straight into a game
// project: Godot compiles them as net8.0, where the BCL already provides the real type.

#if !NET5_0_OR_GREATER

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

#endif
