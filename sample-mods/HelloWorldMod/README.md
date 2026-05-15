# HelloWorldMod

Minimal Pratfall mod template using the framework. Prints a message when the mod is
enabled and disabled.

## What's here

- `manifest.json` — framework-format manifest. `id` must match the folder name and the
  built DLL name. `multiplayer.mode` is `local_only` so this mod never tries to
  negotiate with peers (no votes, no transfers).
- `HelloWorldMod.cs` — the mod itself. The framework calls `OnLoad` / `OnUnload`.
- `HelloWorldMod.csproj` — build config. Adjust the `GameDir` property at the top
  before building.

## Build & install

1. Set `GameDir` in `HelloWorldMod.csproj` to wherever Pratfall is installed (the
   folder that contains `Pratfall.exe`).
2. Run `dotnet build`. The post-build target copies the DLL + manifest into
   `%APPDATA%\Pratfall\mods\HelloWorldMod\`.
3. Launch Pratfall. The mod should appear in the **Mods** dialog from the main menu.
4. Toggle it on. Check the Godot log at `%APPDATA%\Pratfall\logs\godot.log` for the
   `[HelloWorldMod] Loaded.` line.

## Going further

- Add a Harmony patch: define a static class decorated with
  `[ModPatch(typeof(GameTypeYouWantToPatch), "MethodName")]` containing a static
  `Postfix` method, and the framework wires it on enable / unwires on disable.
- Set `multiplayer.mode` to `transfer` if you want lobby members without the mod to
  receive your DLL automatically (after a host-side vote passes). Transfers are
  SHA-256 verified by the framework.
- Pin your DLL's SHA-256 in the manifest via the `assemblySha256` field so the
  framework refuses to load a tampered build.
- Set `effects.needsRestart` to `true` for mods that genuinely require a restart to
  fully apply.
