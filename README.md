# Pratfall Mod Framework

Runtime mod framework for [Pratfall](https://store.steampowered.com/app/4244510/Pratfall/) (Godot 4.6.1 C#).

## Install (players)

1. Quit Pratfall.
2. Download `PratfallModFramework-Installer.zip` from the **Releases** section of this repo and unzip it anywhere.
3. Run `PratfallModFramework.Installer.exe`. It auto-detects your Pratfall install via Steam.
4. Click **Install**. The installer backs up `Pratfall.dll` to `Pratfall.dll.original` before patching, so it's fully reversible — click **Uninstall** any time, or run Steam Verify and Steam will restore the original automatically.
5. Launch Pratfall. A **Mods** button appears in the main menu next to Options.

To install a mod, drop its folder into `%APPDATA%\Pratfall\mods\<modid>\` (or wait for the Steam Workshop integration). It'll appear in the Mods dialog on next launch.

## Build from source (devs)

Requires .NET 8 SDK. From the repo root:

```
dotnet build framework/PratfallModFramework.sln -c Release
```

The Release build artifacts land in `framework/Installer/bin/Release/net8.0-windows/` — the installer EXE plus the framework + bootstrap DLLs it needs.

## What it provides

- bootstrap injection into the game
- runtime DLL mod loading with Harmony patches
- a main-menu Mods UI for enable/disable
- a real Pratfall-backed multiplayer control plane for:
  - installed + enabled mod snapshots
  - host-authoritative mod votes
  - session-state reconciliation

It does **not** currently guarantee that arbitrary mods are compatible with each other.
The framework is meant to detect mismatches, attribute them to specific mods, and drive the lobby toward a shared session state.

## Current Status

What is implemented now:

- runtime DLL loading for framework-style mods
- manifest scan from the Pratfall `mods/` folder
- enable/disable UI from the main menu (Pratfall-styled, persists across game ↔ menu transitions)
- real lobby/network integration using Pratfall's own multiplayer stack
- peer authentication: framework events are dropped unless the claimed sender is a current lobby member
- vote transport for mod-state mismatches
- apply order that prefers:
  - already-installed local match first
  - stretch/settings path second
  - chunked P2P transfer when the mod is missing on a peer (SHA-256 verified)
- SHA-256 verification for transferred mods AND optional manifest-pinned `assemblySha256` for local mods
- trust policy at `user://modframework-trust.json` with `open` (default) and `trusted-only` modes; trusted-only routes unknown transfers into a quarantine folder
- PCK loading (`pckFile` manifest field) for asset mods
- real stretch apply for explicit `CustomGameSettings` overrides and `fixedSeedString`
- first-pass official loader bubble:
  - dual manifest parsing for framework-style and official-style mods
  - framework-owned desired enabled state in `user://modframework-state.json`
  - built-in `enabled_mods.json` startup read intercepted and stalled
  - official-style mods apply later through the game's own `EnableMod()` / `DisableMod()`
  - `Load Enabled Mods` action in the Mods menu
  - pending official-style mod apply before `Host Game` / `Play Offline`
- debug peer (`modframework-debug-peer.json`) gated to offline-only sessions so it never races real-Steam multiplayer
- public sample template at [`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod/README.md)

What is not finished yet:

- end-to-end stretch verification in a real multiplayer lobby
- in-game validation of the official-loader bubble path against a real official-style sample mod
- coordination with Tim on a stable network-event-ID range (currently uses private `62000-62005`)
- transfer of PCK side-files (only the DLL is transferred today; PCKs must be installed locally)

## Official Loader Direction

Pratfall now ships an early built-in mod loader that:

- creates `<game>/mods`
- stores enabled startup mods in `enabled_mods.json`
- reads a simple manifest with fields such as:
  - `Name`
  - `Version`
  - `Description`
  - `Author`
  - `PackageName`
  - `Assembly`
- loads `ModEntry.ModInit()`
- loads a `.pck`
- instantiates `res://<mod-folder>/root.tscn`

The framework is not planning to compete with that startup path directly.
The current implementation is a **bubble** around `enabled_mods.json`:

- the framework owns canonical desired mod state
- canonical desired state is stored in:
  - `user://modframework-state.json`
- the game loader sees an empty startup list during its first `ReadLoadedModsFromFile()` pass
- the game loader remains the execution layer for official-style mods
- the framework remains the policy layer for:
  - menu toggles
  - delayed load/apply timing
  - multiplayer compare/vote/reconcile

Current first-pass behavior:

- official-style mods are discovered and shown in the framework menu
- toggling an official-style mod updates desired state immediately, but does not auto-start it
- the `Load Enabled Mods` button applies pending official-style mods through the game's own `EnableMod()`
- pressing `Host Game` or `Play Offline` also applies pending desired state first
- the game's own `WriteLoadedModsToFile()` is no-op'ed inside the bubble so it does not overwrite framework policy

Current limitation:

- this first pass has been built and deployed, but still needs an in-game smoke test with a real official-style sample mod

Discovery scope for that bubble is intentionally limited. The framework should only scan Pratfall-relevant roots such as:

- the Pratfall install folder
- the game's `mods/` folder
- `user://mods`
- Pratfall-owned Steam/user data
- optional explicitly configured Pratfall profile roots

It should not scan unrelated folders on the machine.

## Quick Start

1. Download the latest `PratfallModFramework.Installer.exe` from Releases.
2. Run it. It patches `Pratfall.dll` and copies framework files.
3. Launch Pratfall from Steam.
4. Place mods in `<Pratfall>\mods\<ModName>\`.

Expected package shape for DLL mods:

```text
mods/MyMod/
|- manifest.json
`- MyMod.dll
```

Manifest-only mods are also valid when they do not require a DLL, such as explicit `stretch`/settings packages with no patches.

## Multiplayer Model

The framework reconciles **session state**, not code compatibility.

That means it tries to answer:

- do both players have the mod installed?
- is the version the same?
- is it enabled on both sides?
- does it declare conflicts or dependencies?

Then it drives the session toward one of these outcomes:

1. `already-installed local match`
   - both sides already have the same DLL mod installed
   - vote only decides whether to enable/disable for the session
2. `stretch`
   - settings/state-oriented mods
   - no file transfer
3. `transfer`
   - used only when a required mod is truly missing
4. `restart_required`
   - safe to stage, not safe to hot-apply
5. `disable/reject`
   - when equal session state cannot be reached safely

The framework does **not** try to merge incompatible code patches automatically.

## Local Debug Peer Mode

For solo testing, the framework can simulate one extra peer offline.

- This is **opt-in**.
- It only activates when there is **no real multiplayer lobby**.
- Real Pratfall/Steam lobby transport still takes priority when available.

Create `user://modframework-debug-peer.json` with:

```json
{
  "enabled": true,
  "mirrorLocalInstalledManifests": true,
  "enabledModIds": [],
  "defaultVoteYes": true,
  "voteResponses": {
    "SomeSpecificMod": false
  }
}
```

Behavior:

- `mirrorLocalInstalledManifests: true`
  - the simulated peer advertises the same installed non-`local_only` manifests as the local player
  - this is the easiest way to test the `already-installed local match` vote path
- `enabledModIds`
  - which of those installed mods the simulated peer starts with enabled
- `defaultVoteYes`
  - default answer from the simulated peer when the host sends a vote
- `voteResponses`
  - optional per-mod vote override map

This mode is for testing `compare -> vote -> apply` logic on one machine.
It does **not** simulate real chunked transfer.

## For Mod Developers

The repo does not currently ship a bundled public sample mod.

`ExampleMod` was removed because it introduced extra debugging variables, and `NoFallDamage` is already a built-in custom game setting in Pratfall rather than a representative DLL mod.

A private template/baseline is planned, but the public repo is not treating a placeholder sample as proof of multiplayer correctness.

### Official Loader Manifest

The game's built-in loader currently uses a different startup manifest shape than the framework's richer multiplayer manifest.

Official startup-loader example:

```json
{
  "Name": "Example Mod",
  "Version": "1.0.0",
  "Description": "Adds cool new gameplay features",
  "Author": "Tim",
  "PackageName": "ExampleMod.pck",
  "Assembly": "ExampleMod.dll"
}
```

The framework's direction is to understand both shapes.
That lets official-style mods appear in the Mods menu while the framework still owns delayed load policy and multiplayer reconciliation.

Current first-pass behavior for official-style mods:

- they are parsed and shown in the framework menu
- they use `DirectoryName` as the implicit fallback `id` when no framework `id` exists
- they are delayed until:
  - `Load Enabled Mods`
  - `Host Game`
  - `Play Offline`

### `manifest.json`

```json
{
  "id": "MyMod",
  "name": "My Mod",
  "version": "1.0.0",
  "author": "You",
  "description": "What it does",
  "type": "patch",
  "effects": {
    "settings": [],
    "patches": ["MainMenuUIViewController._Ready"],
    "nodes": [],
    "assets": [],
    "needsRestart": false
  },
  "multiplayer": {
    "mode": "local_only",
    "requires": [],
    "conflictsWith": []
  }
}
```

`type` is kept for backward compatibility and coarse fallback behavior.

`multiplayer.mode` is the explicit session-sync contract:

- `local_only`
  - never negotiated with peers
  - best default for new DLL mods until you know more
- `stretch`
  - sync over existing settings/network state
  - no file transfer
- `transfer`
  - peers may need the mod package to match
- `restart_required`
  - safe to stage, not safe to hot-apply
- `auto`
  - framework infers mode from `effects` and `type`

Recommended rule for mod authors:

- default to `local_only`
- only opt into `stretch`, `transfer`, or `restart_required` when you can describe the mod honestly

For `stretch`, use explicit settings objects when you expect the framework to apply values:

```json
{
  "type": "settings",
  "effects": {
    "settings": [
      { "name": "LowGravity", "value": 1 },
      { "name": "FallDamage", "clear": true },
      { "name": "UseFixedSeed", "value": 1 }
    ],
    "fixedSeedString": "12345",
    "patches": [],
    "nodes": [],
    "assets": [],
    "needsRestart": false
  },
  "multiplayer": {
    "mode": "stretch"
  }
}
```

Notes:

- string-only entries like `"settings": ["LowGravity"]` are still accepted for classification
- real stretch application requires an explicit `value` or `clear`
- `fixedSeedString` is applied directly and syncs through Pratfall's existing custom-settings event path

### Writing a Mod

Your DLL references `PratfallModFramework.dll` and any Pratfall game assemblies needed for the types you patch, then uses `[ModPatch]` to declare patches.

Minimal entry point:

```csharp
using Godot;
using PratfallModFramework;

public static class MyModMain
{
    public static void OnLoad()
    {
        GD.Print("[MyMod] Loaded");
    }

    public static void OnUnload()
    {
        GD.Print("[MyMod] Unloaded");
    }
}
```

Example patch:

```csharp
using PratfallModFramework;

[ModPatch(typeof(MainMenuUIViewController), "_Ready", PatchType.Postfix)]
public static class MainMenuPatch
{
    static void Postfix()
    {
        // run code after the main menu becomes ready
    }
}
```

The framework handles Harmony setup and teardown automatically.

## Design Constraints

Important current constraints:

- runtime registration of arbitrary new C# Godot node types should be treated as unavailable
- content mods should prefer:
  - resources
  - `PackedScene`
  - existing game systems
  - Harmony patches where needed
- DLL mods are arbitrary code
  - the framework can add trust checks and warnings
  - it cannot provide a real in-process sandbox for untrusted DLLs

## Security Note

This framework loads user-provided DLLs into the game process.

That means DLL mods should be treated as fully trusted code from the player's point of view.

Planned safety layers for broader release:

- quarantine for newly discovered mods
- hash tracking
- trusted-only mode
- optional malware scan before first enable

These are trust layers, not a real code sandbox.

## Building from Source

Requires the .NET 8 SDK.

```text
cd framework
dotnet build
```

Projects:

- `framework/PratfallModFramework/`
  - core framework DLL
- `framework/PratfallModFramework.Patcher/`
  - Mono.Cecil patcher for `Pratfall.dll`
- `framework/PratfallBootstrapLoader/`
  - zero-dependency loader injected by the patcher
- `framework/Installer/`
  - single-file GUI installer

## Architecture

```text
Pratfall.exe
`- PratfallModFramework.dll
   |- Bootstrap
   |- ModManager
   |- ModAssemblyLoader
   |- MainMenuIntegration
   |- ModNetworkLayer
   |- ModNetworkContracts
   |- ModVoteSession
   |- ModNetworkStretch
   |- ModExceptionFilter
   `- ModAPI
```
