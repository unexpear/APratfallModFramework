# v1 Smoke-Test Plan

Pre-release verification checklist. Each section is one focused session — don't try to
combine. After each pass, capture the relevant log lines from
`%APPDATA%\Pratfall\logs\godot.log` and attach to the test record.

## Pre-flight

- [ ] `dotnet build framework/PratfallModFramework.sln -c Debug` succeeds, 0 warnings / 0 errors.
- [ ] Hash of deployed DLL at `<game>\data_Pratfall_windows_x86_64\PratfallModFramework.dll` matches `bin\Debug\PratfallModFramework.dll`.
- [ ] `user://modframework-debug-peer.json` is **deleted** before any real-multiplayer test.

## 1. Main-menu integration

- [ ] Launch game. Within ~5 seconds, the **Mods** button appears between **Play Offline** and **Options**.
- [ ] Clicking **Mods** opens the dialog over a dimmed main menu (rocky-frame panel, mod cards, Close + Load Enabled Mods).
- [ ] Pressing Esc dismisses the dialog. Reopening works.
- [ ] Click **Play Offline**, return to menu (via game's quit-to-menu). Mods button is still there.
- [ ] Toggle a mod off/on. Log shows `Enabled mod ...` / `Disabled mod ...`. State persists across menu close/reopen.

## 2. Local DLL load + hash pinning

- [ ] Build the sample at `sample-mods/HelloWorldMod/`. Verify `%APPDATA%\Pratfall\mods\HelloWorldMod\{HelloWorldMod.dll, manifest.json}` exist.
- [ ] Launch game. Log shows `Loaded assembly mod: HelloWorldMod` and `[HelloWorldMod] Loaded.`.
- [ ] In `manifest.json`, set `assemblySha256` to the correct hex hash of the DLL. Relaunch. Log shows `Verified mod HelloWorldMod DLL against manifest sha256`.
- [ ] Change one byte in the DLL (without updating the manifest hash). Relaunch. Log shows `Failed to enable mod ...: DLL hash mismatch ...` and the mod does **not** load.

## 3. Pratfall-schema (incl. Workshop) mod loading

(Was: "Official-loader bubble". Bubble retired 2026-05-18 when Tim's Workshop update privatized `GetModManifest` and we switched to the turn-off architecture. Pratfall-schema mods now load through the same framework pipeline as framework-schema mods — there's no separate "delayed apply" flow.)

- [ ] Install a Pratfall-schema mod (manifest with `Name`/`Assembly`/`PackageName`, no `id`). Drop in `<game>\mods\<modid>\` or `%APPDATA%\Pratfall\mods\<modid>\`.
- [ ] Launch game. Log shows `Native ModManager turned off (custom loader in charge); LoadAllModManifests + read/write neutered` and the mod appears in the framework's Mods dialog with an auto-synthesized `id` (folder name or `Name`).
- [ ] Open Mods dialog. Toggle the mod on. Click 🔍 to approve fingerprint. Mod's `ModEntry.ModInit` runs (verified via mod-side log line).
- [ ] Toggle off. Mod unloads cleanly (`AssemblyLoadContext.Unload` called).
- [ ] Toggle back on. **Should work without restart** (the 2026-05-15 same-session re-enable bug is no longer relevant — we don't call native `ModManager.EnableMod` anymore; our own loader handles the cycle).

## 3a. Workshop discovery (startup + live)

- [ ] Subscribe to a Pratfall Workshop mod via the Steam Workshop page.
- [ ] Wait for Steam to finish downloading.
- [ ] Launch Pratfall. Mod appears in framework's Mods dialog (toggled off, awaiting 🔍 approval). Inspector ℹ shows "📦 Steam Workshop (id …)".
- [ ] With game running, subscribe to a SECOND Workshop mod from the Steam overlay or Workshop website.
- [ ] After Steam downloads (typically a few seconds), open the Mods dialog. The new mod appears live — no restart required. Log line: `Workshop item installed: appId=… fileId=… — rescanning sources` then `Workshop mod added live: <id> (workshop=…)`.
- [ ] Toggle on + 🔍 approve. Mod loads.

## 4. Debug-peer offline-only gating

- [ ] Place `user://modframework-debug-peer.json` with `Enabled: true`.
- [ ] Launch game. On main menu: no auto-vote, no log lines about debug transport.
- [ ] Click **Play Offline**. Log shows `Session starting (Offline); network transport may now attach` then `Attached to local debug peer transport` and a vote fires.
- [ ] Quit. Relaunch. Click **Host Game** instead of Play Offline. Log shows `Session starting (Host); ...` but **does not** attach the debug peer (real-lobby path takes over).

## 5. Real-multiplayer vote path

Requires two PCs.

- [ ] Both players install the same DLL mod (same hash). Player A enables it; Player B does not.
- [ ] Player A clicks **Host Game**, Player B joins.
- [ ] Player B receives a vote prompt for the mod. Vote yes.
- [ ] Log on Player B: `Enabled local match for <id> after vote`. Mod is active.
- [ ] Repeat with Player B having a *different version* of the mod. Vote should still surface; on yes, the mismatch should resolve via transfer (next section) or fail clearly.

## 6. Chunked P2P transfer + SHA-256 verification

Requires two PCs.

- [ ] Player A has the mod installed and enabled (`multiplayer.mode: "transfer"`). Player B does **not** have it at all.
- [ ] Player A hosts, Player B joins. Vote surfaces; Player B votes yes.
- [ ] Player B's log: `Requesting transfer of <id> v<v> from <hostId>` then per-chunk `Transfer started: send ...` / `Transfer complete: received <id> v<v> (NNN bytes, sha256=...)`.
- [ ] `%APPDATA%\Pratfall\mods\<id>\<id>.dll` now exists on Player B.
- [ ] Mod is enabled on Player B in the same session.

## 7. Trust policy: trusted-only quarantine

- [ ] Create `user://modframework-trust.json` with `{"Mode":"trusted-only","TrustedSha256":[]}`.
- [ ] Run section 6 again. On the receiver: log says `Transfer quarantined: <id> ... -> user://mods-quarantine/`. DLL is **not** in `user://mods/`.
- [ ] Add the file's hash to `TrustedSha256` in the trust JSON, reset to mode `open` (or restart in `trusted-only` mode with the hash now allowlisted). Re-run section 6. Now the transfer persists into `user://mods/`.

## 8. PCK loading

- [ ] Ship a mod with both a DLL and a `.pck` next to it. Manifest has `"pckFile": "<name>.pck"`.
- [ ] Enable the mod. Log shows `Mounted PCK for <id>: <file>.pck`.
- [ ] Reference a resource inside the PCK from the mod's DLL. Confirm it resolves (`res://<modpath>/...`).
- [ ] Disable the mod. Log shows the "PCK content remains until restart" notice. PCK resources are still resolvable until next launch.

## 9. Crash / error recovery

- [ ] Manually delete `<game>\data_Pratfall_windows_x86_64\PratfallModFramework.dll` and relaunch. The game should boot cleanly (no framework, no Mods button) without crashing.
- [ ] Restore the DLL. Launch and confirm normal flow.
- [ ] Put a malformed manifest into `user://mods/BadMod/manifest.json`. Log should print a scan error and **continue** to load other mods.

## 10. Coordination follow-ups (not blocking, but track)

- [ ] **Event IDs**: framework uses `62000-62006` (62006 added for CSync). Get an officially-allocated range from Tim so future game updates don't clash.
- [ ] **DialogUI texture harvest**: `MainMenuIntegration` reaches into `DialogUI > .../TextureRect` for the dialog's body texture. If the game restructures `DialogUI`, the harvest fails silently and the dialog falls back to flat. Tim should be aware.
- (was: `enabled_mods.json` interception — *no longer relevant* as of 2026-05-18; the framework turns off the native ModManager's `LoadAllModManifests` entirely. `ReadLoadedModsFromFile`/`WriteLoadedModsToFile` are still patched as defense-in-depth in case anything else calls them post-Setup.)
- [ ] **Per-peer send efficiency**: `Network.EventManager.SendEvent` broadcasts every chunk to every lobby member; non-target peers discard at the receive side. Correct but wasteful — in a 4-player lobby a 2 MB transfer ships 8 MB total over the wire. Not a v1 blocker; consider a targeted-send path if Tim adds one.

## Known wire-format limits (audited)

- `Pratfall.ByteBufferWriter.Write(string)` hard-caps at 32768 bytes (ushort length prefix). Any single string field longer than that gets silently substituted with the default on the receiver. Our transfer chunk JSON envelope is sized at 14 KB raw payload (~20 KB base64-JSON) with an explicit assert at `ModTransferChunkNetworkEvent.Create` that throws if a future change pushes past the limit. Manifest snapshot events also pack mod lists as JSON — for very large `installedManifests` (~50+ mods) we should chunk those too eventually.
- `NetworkFrameEvent` carries no verified sender. Peer authentication relies on the claimed `SenderUserId` matching a current lobby member (best available with this transport).
