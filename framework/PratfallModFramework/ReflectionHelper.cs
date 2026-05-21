using System.Reflection;

namespace PratfallModFramework;

// Small shared reflection utilities. Sized to stay boring — only add to this
// file when the same reflection pattern appears in 2+ places and the helper
// signature is obvious. Don't grow it into a "ReflectionUtils kitchen sink."
internal static class ReflectionHelper
{
    // Returns the assembly's loadable types, surviving partial-load failures.
    // Plain Assembly.GetTypes() throws ReflectionTypeLoadException when some
    // types fail to resolve (a mod referencing an absent dependency, a Cecil-
    // produced binary with stripped metadata, etc.) — and at that point the
    // exception's `.Types` array still holds every type that DID load with
    // null gaps for the ones that didn't. We accept the partial list rather
    // than abandoning the whole assembly.
    //
    // Shared by ModInspector (declared-patch enumeration) and
    // ModCompatibilityChecker (Harmony patch-overlap detection). Same logic
    // in both places before this helper existed.
    public static Type[] GetTypesSafe(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).ToArray()!;
        }
    }
}
