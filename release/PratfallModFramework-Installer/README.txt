Pratfall Mod Framework — Installer (v1.2)
=========================================

Quick install
-------------
1. Quit Pratfall completely.
2. If you had a previous version installed, run the older installer first
   and click Uninstall (or use Steam Verify) so the framework state resets
   cleanly.
3. Double-click PratfallModFramework.Installer.exe
4. Click Install. The installer auto-detects your Pratfall folder via Steam.
5. Launch Pratfall. The native Pratfall "Mod" button is hidden and the
   framework's "Mods" button takes its slot in the main menu.

What's new in v1.2
------------------
- Mod-defined Godot Node / Resource types now register with the engine on
  load (matches the official loader's behavior). Content mods that ship
  .tscn files or instantiate custom scripts via PackedScene work properly
  instead of silently failing on type lookup.
- New manifest field: addAssemblyToGodot (default true). Opt out only if
  you have a specific reason to skip script registration.
- HelloWorldMod sample csproj uses $(MSBuildProgramFiles32) so the build
  works on non-English Windows and 32-bit MSBuild without editing paths.

What's still v1.1
-----------------
- Compatible with Pratfall 1.1.0.R2943 (the build with the native Mod button).
- Mods stay disabled until you check them. Each card has two buttons:
    i  Info  — read-only manifest, file list, declared patches.
    Q  Scan  — IL safety scanner (Mono.Cecil). Reports dangerous API calls
              like Process.Start, raw network, registry, P/Invoke, file
              deletion. Running it marks the mod as user-checked, releasing
              the gate so it can load.
- Toggle ON, Scan, or accepting a multiplayer Download all release the gate.
- Updating a mod (any byte change in its DLL or PCK) re-locks the gate.

Requirements
------------
- Windows 10/11 64-bit
- .NET 8 Desktop Runtime
  Download: https://dotnet.microsoft.com/download/dotnet/8.0/runtime
  (Pick "Desktop Runtime — x64" — about 55 MB.)

Uninstall
---------
Run the installer again and click Uninstall. The original Pratfall.dll is restored.
Steam Verify also works as a recovery: it'll re-download the original DLL automatically.

What it does to your install
----------------------------
- Backs up Pratfall.dll to Pratfall.dll.original (keeps it forever)
- Patches a single line in Pratfall.dll to call the framework on game boot
- Drops PratfallModFramework.dll + PratfallBootstrapLoader.dll into the game folder

That's it. No registry writes outside Steam's auto-detect read, no services, no admin required.

Mods folder
-----------
%APPDATA%\Pratfall\mods\<mod-id>\
  manifest.json
  <mod-id>.dll  (and optional .pck for asset mods)

If a mod folder is present, it appears in the Mods dialog. Toggle on, click
"Load Enabled Mods" or start a game.
