Pratfall Mod Framework — Installer
===================================

Quick install
-------------
1. Quit Pratfall completely.
2. Double-click PratfallModFramework.Installer.exe
3. Click Install. The installer auto-detects your Pratfall folder via Steam.
4. Launch Pratfall — a "Mods" button appears in the main menu next to Options.

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
