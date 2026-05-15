using Godot;

namespace PratfallModFramework;

// Reception point for Steam Workshop downloads. Robert offered on Discord (5/13/26) to
// "expose a delegate that we call when an item was downloaded" — this is where that call
// should land. The framework's job once notified is to pick up the dropped folder, scan
// its manifest, and add the mod to the desired list (without auto-enabling — user toggles
// from the Mods dialog, just like for any other mod).
//
// Wiring from the game (when Steam Workshop ships):
//
//   PratfallModFramework.WorkshopHook.NotifyItemInstalled(installedFolderPath);
//
// The framework then:
//   1. Validates the folder contains a recognized manifest.json
//   2. Optionally copies/moves into user://mods/<id>/ (out of Workshop's content folder
//      so the mod survives if the user unsubscribes mid-session).
//   3. Re-scans local mods so the new entry appears in the Mods dialog.
//
// Today this is a no-op-with-logging stub so Tim has a callable target while building
// the Workshop side. Wire it up when Workshop is enabled.
public static class WorkshopHook
{
    public delegate void WorkshopItemInstalledHandler(string installedFolderPath, ulong publishedFileId);

    public static event WorkshopItemInstalledHandler? OnWorkshopItemInstalled;

    // Game-side call site: Steam's `OnItemInstalled` callback should forward here.
    // `publishedFileId` is the Steam Workshop ID; pass 0 if unknown.
    public static void NotifyItemInstalled(string installedFolderPath, ulong publishedFileId = 0)
    {
        if (string.IsNullOrWhiteSpace(installedFolderPath))
        {
            GD.PrintErr("[ModFramework] WorkshopHook.NotifyItemInstalled: empty path");
            return;
        }
        GD.Print($"[ModFramework] Workshop notified: item {publishedFileId} at {installedFolderPath}");
        OnWorkshopItemInstalled?.Invoke(installedFolderPath, publishedFileId);
    }
}
