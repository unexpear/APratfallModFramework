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

- bootstrap injection into the game (Cecil patches `GcManager._Ready()`, original `Pratfall.dll` backed up first)
- runtime DLL mod loading with Harmony patches
- a Pratfall-styled Mods UI in the main menu for enable/disable, persists across game ↔ menu
- a real Pratfall-backed multiplayer control plane for:
  - installed + enabled mod snapshots
  - host-authoritative mod votes
  - session-state reconciliation
- chunked P2P mod transfer (DLL + optional PCK side-file) with SHA-256 verification, order-independent reassembly, round-robin scheduling for fairness across concurrent transfers; manifest.json is written from the cached peer snapshot so the receive-side rescan can find the mod
- transfer is **never automatic** — when the lobby votes to enable a mod the local player doesn't have, an in-game prompt asks: **Download** (transfer the files), **Use settings only** (stretch — only when the mod supports it), or **Decline (leave lobby)**. The framework calls `LeaveLobby` on decline so the session doesn't drift out of sync.
- per-event peer authentication (claimed sender must be in the current lobby member list)
- automatic compatibility checking across the union of local + every known peer's mod set, fired on every state change
- conflict-resolution dialog when two locally-enabled mods declare each other incompatible — pick which one stays, loser is disabled and persisted
- *(trust modes / quarantine were removed in favor of the explicit per-peer acquisition prompt — every download is now a deliberate user choice, so there's nothing to silently auto-accept)*
- PCK loading for asset mods
- bubble around the game's official mod loader so framework state + official-style mods coexist
- offline-gated debug peer for solo testing without a second PC

## Current Status

### Verified solo (passes the local smoke + stress suite)

- Framework loads, Mods button injects, dialog opens with the correct rocky-frame styling
- Toggle persistence across launches via `user://modframework-state.json`
- Vote system fires from the park (offline session-start), not the main menu
- Official-loader bubble integrates cleanly with Tim's `example_mod`: toggling on + Load Enabled Mods → game's `ModEntry.ModInit()` runs, log confirms `Mod initialized!`
- Transfer chunker / reassembler / SHA-256 / disk write — full loopback pass, including reversed delivery, duplicate chunks, and 5 boundary payload sizes
- Tampered chunk mid-stream → receiver returns `FailedHashMismatch` (rejected before persist)
- Trust quarantine: trusted-only mode + unknown hash routes to `user://mods-quarantine/<id>/<id>.dll`
- Concurrent transfers (3 simultaneous) interleave fairly via round-robin scheduling, no chunk cross-talk
- Vote consensus math: 10 scenarios across 2/3/4 peers including ties (tied = fail, by contract)
- Compatibility checker detects all 5 issue categories on synthetic fixtures
- Conflict-resolution prompt fires on real conflicting mods, disables loser, syncs the dialog toggle visually

### Known limitations / waiting items

- **No real multiplayer test yet.** All transfer / vote / member-join behavior is verified by solo loopback or debug peer, never by two real Steam clients. Audit cleared the wire format against `Pratfall.ByteBufferWriter`'s 32 KB cap (chunks sized to 14 KB raw → ~20 KB JSON envelope).
- **`ModManager.EnableMod` returns false on second call within a session after `DisableMod`.** Confirmed game-side issue; Tim is shipping a fix that wraps the second call in try/catch. Workaround: restart Pratfall before re-enabling. The `tmp/stress-mods/` `StressHashGuardMod` and conflict-resolution flow surface this if exercised.
- **Workshop integration is stubbed.** `WorkshopHook.NotifyItemInstalled(folder, publishedFileId)` is a public entry point waiting for Tim to wire Steam's `OnItemInstalled` callback to it. When invoked, the framework rescans `user://mods/` and surfaces the new mod in the dialog.
- (was: PCK side-file transfer not implemented — *now shipped*. The host sends both `<modId>.dll` and `<modId>.pck` (when the manifest declares `pckFile`), and the receiver also writes `manifest.json` next to them from the cached peer snapshot, so the rescan finds the mod end-to-end.)
- **Stretch end-to-end** still needs a real multiplayer lobby to verify; the apply path is implemented and unit-clean.
- **Optional malware scan before first enable** — listed in the safety roadmap but not implemented (the trust/quarantine/hash layers are).

### Resolved with the dev (Tim, Robert)

- **Network event IDs `62000-62005`** — confirmed safe forever (game counts up from low numbers, never approaches that range).
- **Bubble approach blessed.** Per Tim: *"its not meant as a replacement for custom mod loader. its just a way to have an easy way to sideload code/packages."* The framework owns the policy layer; the official loader is the execution layer for official-style mods. Current full-stall of `enabled_mods.json` startup is acceptable long-term.
- **`RegisterCustomType` for runtime Godot Node registration** — withdrawn, not realistic. Robert's recommended pattern is to load `RandomWeightedDropPool` resources and add to the array directly. `ModDropPoolHelper.Register(poolPath, scene, weight)` and `RegisterIn(pool, scene, weight)` implement that pattern with proper unregister-on-unload.
- **Analytics suppression for mod exceptions** (Robert's request) — `ModExceptionFilter` is in place and active.

### Public template

[`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod/README.md) — minimal DLL mod with `OnLoad`/`OnUnload` and a build target that copies output into `%APPDATA%\Pratfall\mods\HelloWorldMod\`.

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

Current state:

- smoke-tested in-game with Tim's `example_mod`. Toggling on + Load Enabled Mods causes the game's `ModEntry.ModInit()` to run (verified via `Mod initialized!` in `godot.log`). Pre-session apply via `Host Game` / `Play Offline` also runs the init.
- known same-session limitation: re-enabling an official-style mod after disabling it returns false from the game's `ModManager.EnableMod`. Tim is fixing this game-side by wrapping in try/catch. Until that ships, the workaround is to restart Pratfall before re-enabling.

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

Public starter template at [`sample-mods/HelloWorldMod/`](sample-mods/HelloWorldMod/README.md). It builds against the framework, has a working post-build target that drops the DLL + manifest into your user mods folder, and is intentionally minimal — copy it, rename, add your own logic.

Two helpers worth knowing about:

- `ModDropPoolHelper.Register(poolResPath, scene, weight)` — the recommended pattern for content mods (per Robert on the dev side). Loads a `RandomWeightedDropPool` resource, appends a `RandomWeightedScene` entry, returns an `IDisposable` that removes it on dispose. Call from `OnLoad`, dispose from `OnUnload` and the pool is left exactly as it was.
- `WorkshopHook.OnWorkshopItemInstalled` — subscribe if you want to react when Steam Workshop drops a new mod folder (Tim is wiring the game-side callback).

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

Trust layers shipped:

- **No automatic downloads** — when the lobby agrees on a mod you don't have, the framework asks you per-mod: Download / Use settings only (stretch, when applicable) / Decline (leave lobby). Replaces the old "trusted-only quarantine" UX with a direct user choice every time.
- **Hash tracking** — manifests can pin `assemblySha256`. The framework refuses to load a DLL whose actual hash differs.
- **Peer authentication** — every framework network event is dropped if the claimed sender isn't a current lobby member.

Not shipped (low priority given lobbies are friends-only):

- Malware scan before first enable.

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
Pratfall.exe (patched by Cecil to call Bootstrap.Init on GcManager._Ready)
`- PratfallModFramework.dll
   ├─ Bootstrap                  entry point + startup status banner
   ├─ ModManager                 orchestrator: scan, load, enable/disable, vote, transfer
   ├─ ModAssemblyLoader          AssemblyLoadContext + Harmony, with SHA-256 hash gate
   ├─ ModManifest                dual-shape parser (framework + official-loader formats)
   ├─ ManifestManager            mod-folder scanner
   ├─ FrameworkModStateStore     persistent desired-enabled state (user://modframework-state.json)
   ├─ MainMenuIntegration        Mods button + dialog + conflict-resolution prompt
   ├─ ToggleSwitch               custom toggle widget used in the dialog
   ├─ ModNetworkLayer            Pratfall network bindings (events, lobby member auth)
   ├─ ModNetworkContracts        wire types: manifest snapshot, vote, transfer chunk
   ├─ ModVoteSession             tally + tie-break (yes > no, ties = fail)
   ├─ ModP2PTransfer             chunker, reassembler, round-robin scheduler
   ├─ ModNetworkStretch          settings-mode apply path (CustomGameSettings)
   ├─ ModCompatibilityResolver   peer-vs-local diff for vote/transfer planning
   ├─ ModCompatibilityChecker    local-set + union check (auto-runs on every state change)
   ├─ ModDropPoolHelper          register/unregister entries on RandomWeightedDropPool
   ├─ ModFrameworkSelfTest       public test API used by tmp/stress-mods/
   ├─ ModExceptionFilter         keeps mod exceptions out of game analytics
   ├─ SessionStartHooks          Harmony patches on Host Game / Play Offline
   ├─ OfficialModBridge          patches game's ModManager.Read/WriteLoadedModsToFile
   ├─ NativeDialogBridge         calls into the game's DialogUIViewController
   ├─ VoteUI                     vote prompt (uses native dialog when available)
   ├─ WorkshopHook               waiting for Tim's Steam Workshop callback to land
   └─ ModAPI                     thin public-API surface for mods
```
