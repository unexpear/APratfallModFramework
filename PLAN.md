# Development Plan

## Goal

Build a practical Pratfall mod ecosystem that:

- loads DLL mods at runtime
- lets players enable or disable mods from the menu
- treats the framework as the policy layer for which mods should actually become active
- compares installed and enabled mod state when players join a lobby
- identifies mismatches by specific mod
- drives the lobby toward a shared session state by vote
- prefers:
  - already-installed local match first
  - stretch/settings sync second
  - transfer only when a mod is truly missing

The framework is a negotiation layer, not a promise that arbitrary mods are mutually compatible.

## Done

- [x] DLL injection via Mono.Cecil patcher + BootstrapLoader
- [x] `0Harmony` embedded as a framework resource
- [x] `[ModPatch]` attribute so mods do not need to reference Harmony directly
- [x] Mod discovery from `mods/*/manifest.json`
- [x] Collectible `AssemblyLoadContext` per mod with `AssemblyDependencyResolver`
- [x] Runtime Harmony patch load/unload via `[ModPatch]`
- [x] Main menu injection with a Mods button and toggle UI
- [x] Startup status popup and analytics exception suppression
- [x] Resource backup/restore helpers (`ModAPI`)
- [x] Single-file GUI installer
- [x] Manifest contract for:
  - `multiplayer.mode`
  - `requires`
  - `conflictsWith`
- [x] Local mod state modeled as:
  - installed manifests
  - enabled mod ids
- [x] Pratfall-backed multiplayer control plane on the real network stack:
  - manifest snapshots over `Network.EventManager`
  - host-authoritative vote request / response / result events
  - lobby attach via `Network.LobbyManager`
- [x] Vote/apply flow that prefers:
  - installed local match first
  - stretch second
  - transfer last
- [x] Real stretch apply for explicit `CustomGameSettings` overrides and `fixedSeedString`
- [x] Local debug peer mode for solo `compare -> vote -> apply` testing without a second player
- [x] First-pass official loader bubble:
  - parse official startup manifests alongside framework manifests
  - framework-owned desired enabled state in `user://modframework-state.json`
  - intercept built-in `enabled_mods.json` startup reads
  - keep official mod activation delayed until framework apply/start
  - apply pending official mods before `Host Game` / `Play Offline`
  - `Load Enabled Mods` action in the framework UI
- [x] Local-only research docs for:
  - Pratfall network stack
  - DLL load/unload path
  - Discord modding-channel context

## In Progress

- [ ] Two-player in-game verification of the real vote path using a trusted DLL mod baseline
- [ ] In-game smoke test of the official-loader bubble using a real official-style sample mod
- [ ] Decide whether the bubble should later present a filtered non-empty startup list, or stay on the current fully stalled startup-read model

## Next

- [ ] Verify this private multiplayer path end-to-end:
  - both players have the same DLL mod installed
  - only one side has it enabled
  - host detects mismatch
  - vote appears
  - passed vote enables the local match on the other side
- [ ] Verify chunked DLL transfer end-to-end with a real second peer
- [ ] Verify stretch manifests end-to-end against Pratfall's real multiplayer path
- [ ] Verify official-style mod apply path end-to-end:
  - discovered in menu
  - not auto-started on boot
  - toggled in desired state
  - loaded by `Load Enabled Mods`
  - loaded automatically before `Host Game` / `Play Offline`
- [ ] Decide whether official-style disable/unload is stable enough for repeated same-session toggles
- [ ] Coordinate with Tim on a stable network-event-ID range (currently squatting `62000-62005`)
- [ ] PCK side-file transfer (only DLL is sent today; PCKs must be installed locally)
- [ ] Optional Windows Defender scan hook before first-enable of a transferred mod

## Done in v1

- [x] Cleanup: deleted dead `ModNodeRegistry.cs` and unused `ModSettingsUI.cs`
- [x] Debug peer gated to offline-only sessions; never races a real Steam lobby
- [x] Chunked P2P transfer implemented over the framework network layer (32 KB chunks, 4-chunk-per-tick pump at 30 Hz)
- [x] SHA-256 verification of transferred DLL bytes; manifest-pinned `assemblySha256` honored on local loads too
- [x] Peer authentication: framework events from non-lobby-members are dropped
- [x] Trust policy at `user://modframework-trust.json` with `open` and `trusted-only` modes (quarantine folder for unknown transfers in trusted-only)
- [x] PCK loading via manifest `pckFile` field
- [x] Restart-required mods surface a clear "may not fully apply until next launch" notice
- [x] Public sample template at `sample-mods/HelloWorldMod/`

## Not Yet Done

- [ ] Stretch is implemented for explicit settings payloads, but still needs real-lobby verification
- [ ] Public-lobby policy is not finalized (default is `open` trust mode; harden later)
- [ ] Workshop integration is future work, not on the critical path

## Constraints

- Runtime Godot C# node-type registration is effectively unavailable in this game path.
- Official built-in mod activation should be treated as an execution layer, not the canonical source of desired mod state.
- The current bubble implementation stalls the built-in startup enabled list completely and applies official-style mods later through the game's own `EnableMod()` / `DisableMod()` methods.
- Content mods should be modeled around:
  - `PackedScene`
  - resources
  - existing game systems
  - Harmony patches where needed
- Discovery scope should stay inside Pratfall-relevant roots:
  - install folder
  - game `mods/`
  - `user://mods`
  - Pratfall-owned Steam/user data
  - explicitly configured Pratfall profile roots
- DLL mods are arbitrary code. The framework can add trust checks and warnings, but not a real in-process sandbox.

## Backlog from comparable mod ecosystems (research 2026-05-17)

Gaps identified by surveying BepInEx, GDWeave, Godot Mod Loader, Lethal Company API stack, Thunderstore, MelonLoader, r2modman, and REPO modding scene. Ordered roughly by value-per-effort. None of these are blocking the v1 framework — they're force multipliers for the mod-author ecosystem.

### Low effort

- [ ] **Per-mod logging** — `ModLogger.For(modId)` writing `<userData>/logs/<modid>.log` plus tee'd into Godot's console with `[modid]` prefix. Unblocks structured crash diagnosis. (~1 day)
- [ ] **Crash log per mod** — when a `[ModPatch]` or `ModInit` throws, write `<userData>/crash_reports/<modid>_<timestamp>.txt` with manifest + stack + recent log lines from the per-mod logger above. Authors can ask users for the file. (~1 day)
- [ ] **Zip-drop install** — detect `.zip` in the mods folder on launch, unpack to a same-named folder, delete the zip. Unblocks any future Thunderstore / r2modman integration with near-zero work. (~half day)
- [ ] **Cross-mod message bus** — `ModMessageBus.Publish("modid.event", obj)` / `.Subscribe(...)` that's framework-internal (NOT routed through `GameEventBus`). Lets mod A pass typed events to mod B without a hard reference. (~1 day)
- [ ] **Save-data versioning** — add `SaveDataVersion` int + `Migrate(int from, JsonNode data)` callback to `ModSaveDataHelper`. Authors that change their save format get a clean upgrade path. (~half day)
- [ ] **Thunderstore manifest convention support** — accept `manifest.json` with snake_case keys (`name`, `version_number`, `description`, `dependencies` as `{team}-{package}-{version}` strings) alongside our current PascalCase. Authors copying conventions from neighbor games (REPO, Lethal Company) write Pratfall mods faster, and a future Thunderstore listing is near-zero work. (~half day)
- [ ] **Accidental Harmony patch conflict detection** — hook our Harmony lifecycle to log "Mod A and Mod B both patch `Player.Jump`; declared deps: none" when two mods land on the same `MethodInfo` without declaring a conflict. Feeds into the existing Conflict prompt. (~1 day)
- [ ] **Debug env vars** — `PRATFALL_MODS_DEBUG=1` bumps log verbosity; `PRATFALL_MODS_CONSOLE=1` allocates a stdout console on Windows. GDWeave-style. (~few hours)
- [ ] **Verify dependency semantics** — confirm our manifest deps support soft-vs-hard and version-range (`>=2.1.0`) like BepInDependency does. Add if missing. (~half day to audit, 1 day to fix)

### Medium effort

- [ ] **Config system: `ConfigEntry<T>`** — `ModConfig.Bind<T>("Section", "Key", default, description, AcceptableValueRange/List)` + JSON file at `<userData>/modframework-config/<modid>.json` + `OnChange` event. No UI yet, just the data layer. Authors that want settings get a persistent typed key/value with change events. (~3 days)
- [ ] **In-game config editor** — per-mod "Settings" tab in the existing Mods dialog that reflects over registered `ConfigEntry` objects and auto-generates sliders / dropdowns / toggles from `AcceptableValue*`. Every existing mod gets a UI for free. **The single biggest author-facing quality jump available** — every comparable framework has this (BepInEx ConfigurationManager, MelonPreferences, LethalConfig, REPOConfig). Depends on the config system landing first. (~3-5 days)
- [ ] **Host-config-pushed-to-clients (CSync pattern)** — mark a `ConfigEntry` as `Synced = true`; framework auto-sends host's value on join and on change. Critical for physics co-op — "host turned floor friction to 0.1, all clients use 0.1." Depends on config system + existing `ModNetworkLayer`. (~2 days)
- [ ] **Mod profiles** — loadout presets in the Mods dialog: `<userData>/profiles/<name>.json` listing enabled mod IDs + per-mod config snapshot. UI: Profiles tab with [+ New], [Activate], [Export Modpack]. Players running 20+ mods will demand this. Our enable/disable persistence does 80% of the work. (~2-3 days)
- [ ] **Keybind registration with rebind UI** — `ModInputHelper.RegisterAction("modid.action", InputEventKey)` persisted per-user, surfaced in a unified keybind list. Every gameplay mod wants this. (~2-3 days)
- [ ] **Typed network sync helpers** — `ModNetMessage<T>` and `ModNetVar<T>` wrappers over `ModNetworkLayer` (a la LethalNetworkAPI). Removes the per-mod boilerplate of "pack args → send → unpack". (~2-3 days)
- [ ] **`IModContext` injected into `ModInit(ctx)` overload** — give mods a service container (Logger, Config, UserDataPath, ModId, NetworkLayer) instead of static globals. Makes mods testable without spinning up the framework. Keeps the parameterless `ModInit()` overload for backward compat. (~1-2 days)
- [ ] **Resource override helper** — `ModAssetHelper.OverrideTexture(path, Texture2D)` / `OverrideSound(...)` / `OverrideScene(...)` hooking `ResourceLoader` to return the mod's resource when the game requests the vanilla path. Effort depends on whether Godot's `ResourceLoader` has a clean override extension point. (~2-3 days, scope risk)
- [ ] **Per-mod locale files** — verify `ModLocalizationHelper` cleanly handles `<modfolder>/locales/en.json`, `de.json`, etc. with multi-mod no-collision + fallback to en. May already work — needs audit. (~1 day audit + ~1 day fix if needed)
- [ ] **In-game dev console** — backtick-toggled overlay accepting `<modid> <command> <args>` dispatched to mod-registered commands. Pays for itself in dev iteration speed alone. (~3-5 days)
- [ ] **Content registration helpers beyond drop pools** — props, characters, whatever Pratfall's content surface offers. Depends on game internals; size unknown. Co-design with Tim. (sizing TBD)

### Explicitly NOT doing

Researched and deprioritized:
- Hot reload — low user demand; nobody in the comparable ecosystems ships this as first-class
- Preloader IL patching (BepInEx-style Cecil patches before JIT) — Harmony covers 95% of our cases; rare to need field-add or static-cctor rewrite
- Telemetry / Sentry-style crash uploads — privacy concerns + low value; structured local crash logs (above) cover the same diagnostic need
- In-house UI-builder lib (`ModUI.AddSettingsTab(...)`) — let the community converge first; ship config editor + Mods dialog and see what mod authors actually want before standardizing
- Steam Workshop — already on the roadmap below; no change in priority

## External Updates

- [x] May 12, 2026: Tim said package encryption was removed and confirmed the game is Godot `4.6.1` with C#.
- [x] May 13, 2026: Tim reported a first internal test loading a Godot package plus a C# DLL worked and said he is aiming to get a very simple official solution out quickly.
- [x] May 14, 2026: Robert linked Quentin's public `GodotDoorstop` Pratfall Windows proof-of-concept and said the game could potentially ship something similar out of the box.
- [x] May 14, 2026: Tim shipped an early built-in loader using `<game>/mods`, `enabled_mods.json`, `ModEntry.ModInit()`, `.pck`, and `root.tscn`, and explicitly said other loader paths are still acceptable.
