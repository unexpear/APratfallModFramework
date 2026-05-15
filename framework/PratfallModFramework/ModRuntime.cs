namespace PratfallModFramework;

public static class ModRuntime
{
    private static int _godotRuntimeReady;

    public static bool IsGodotRuntimeReady => Interlocked.CompareExchange(ref _godotRuntimeReady, 0, 0) == 1;

    public static void MarkGodotRuntimeReady()
    {
        Interlocked.Exchange(ref _godotRuntimeReady, 1);
    }

    public static void MarkGodotRuntimeStopped()
    {
        Interlocked.Exchange(ref _godotRuntimeReady, 0);
    }
}
