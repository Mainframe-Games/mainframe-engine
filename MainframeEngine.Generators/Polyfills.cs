// netstandard2.0 lacks the types the compiler needs for records and init-only setters.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;
}
