using System.Security.Cryptography;
using Godot;

namespace PratfallModFramework;

public class ModManager : IDisposable
{
    // Cached per CA1869 — JsonSerializerOptions is expensive to construct and
    // safe to share across threads when not mutated after construction.
    private static readonly System.Text.Json.JsonSerializerOptions s_indentedJsonOptions =
        new() { WriteIndented = true };

    private readonly ModAssemblyLoader _loader = new();
    private readonly ModNetworkLayer _networkLayer = new();
    private readonly ModVoteSession _voteSession = new();
    private readonly ModP2PTransfer _transfer = new();
    private VoteUI? _voteUI;
    private List<ModManifest> _localMods = new();
    private readonly Dictionary<string, bool> _desiredEnabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _modEnabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _modDllPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _modSessionAvailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _modsWithMountedPck = new(StringComparer.OrdinalIgnoreCase);
    // SHA-256 fingerprints the user has explicitly approved (toggled on, inspected,
    // or accepted via Download prompt). The fingerprint covers DLL + PCK together,
    // so tampering with either voids the check. Framework-loaded mods whose current
    // fingerprint isn't here won't auto-load.
    private readonly HashSet<string> _checkedFingerprints = new(StringComparer.OrdinalIgnoreCase);
    // Cached current fingerprint per mod id, computed lazily; cleared when files
    // change on disk (transfer, workshop install).
    private readonly Dictionary<string, string> _modCurrentFingerprint = new(StringComparer.OrdinalIgnoreCase);
    // Most-recent manifest snapshot per peer, keyed by user id. Used by the compatibility
    // checker to evaluate the UNION of local + remote mod sets.
    private readonly Dictionary<string, ModPeerSnapshot> _peerSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private ModCompatibilityChecker.Report? _latestCompatibilityReport;
    // Pairs the user has already resolved (or deferred) this session, so we don't loop
    // re-prompting the same conflict every state change. Key is "sortedA|sortedB".
    private readonly HashSet<string> _conflictPairsHandled = new(StringComparer.OrdinalIgnoreCase);
    private bool _conflictPromptOpen;
    private SceneTree? _tree;
    private readonly Dictionary<string, ActiveVoteRequest> _activeVoteRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _voteKeysByVoteId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<PendingVotePrompt> _voteQueue = new();
    private readonly HashSet<string> _activeVoteKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queuedVoteIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processedVoteIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeVoteId;
    private long _nextVoteSequence;

    public void Initialize(SceneTree tree)
    {
        GD.Print("[ModFramework] ModManager.Initialize()");
        _tree = tree;
        // Hide the game's native ModButton (added 2026-05-15) — our framework dialog
        // supersedes it. Apply early so the menu picks it up on first _EnterTree.
        NativeModUiSuppressor.Apply();
        OfficialModBridge.Install();
        SessionStartHooks.Install(kind =>
        {
            ApplyDesiredModsForSession();
            _networkLayer.NotifySessionStarting(kind);
        });
        _localMods = ManifestManager.ScanLocalMods();
        GD.Print($"[ModFramework] Found {_localMods.Count} local mods");
        var loadedState = FrameworkModStateStore.LoadState(_localMods);
        foreach (var fp in loadedState.CheckedModFingerprints)
            _checkedFingerprints.Add(fp);

        foreach (var mod in _localMods)
        {
            mod.Normalize();
            _desiredEnabled[mod.Id] = loadedState.EnabledIds.Contains(mod.Id);
            _modEnabled[mod.Id] = mod.UsesOfficialLoader() && OfficialModBridge.IsEnabled(mod);
            // Framework-loaded DLL mods start as session-unavailable until they pass
            // the user-check gate; LoadLocalAssemblyMods flips that on after verifying.
            _modSessionAvailable[mod.Id] = !UsesFrameworkAssemblyLoader(mod);
            ModExceptionFilter.RegisterKnownModAssembly(mod.Id);
            if (!string.IsNullOrWhiteSpace(mod.AssemblyFile))
                ModExceptionFilter.RegisterKnownModAssembly(Path.GetFileNameWithoutExtension(mod.AssemblyFile));
        }

        _voteUI = new VoteUI();
        tree.Root.AddChild(_voteUI);

        MainMenuIntegration.Install(tree,
            onModsPressed: () => GD.Print("[ModFramework] Mods dialog opened"),
            onApplySelectedMods: ApplyDesiredModsForSession,
            getMods: () => _localMods,
            // Dialog reflects ACTUAL loaded state, so unchecked-but-desired mods show OFF
            // (matching reality) until the user checks them.
            isModEnabled: id => IsModEnabled(id),
            onToggleMod: (id, enabled) => ToggleMod(id, enabled),
            getModIssueTooltip: BuildModIssueTooltip,
            inspectMod: id => InspectMod(id),
            scanMod: id => ScanMod(id));

        try { ModExceptionFilter.Install(); }
        catch (Exception ex) { GD.PrintErr($"[ModFramework] Exception filter failed: {ex.Message}"); }

        _networkLayer.OnManifestReceived += OnPeerManifestReceived;
        _networkLayer.OnVoteRequestReceived += OnVoteRequestReceived;
        _networkLayer.OnVoteResponseReceived += OnVoteResponseReceived;
        _networkLayer.OnVoteResultReceived += OnVoteResultReceived;
        _networkLayer.OnTransferRequestReceived += OnTransferRequestReceived;
        _networkLayer.OnTransferChunkReceived += OnTransferChunkReceived;
        WorkshopHook.OnWorkshopItemInstalled += OnWorkshopItemInstalled;
        _networkLayer.OnMemberLeftLobby += OnLobbyMemberLeft;
        _networkLayer.OnTransportReset += OnTransportReset;
        _voteSession.OnVoteResolved += OnVoteResolved;

        LoadLocalAssemblyMods();
        PersistDesiredEnabledState();
        _networkLayer.Initialize(tree, BuildLocalState);

        // Poll forever so the Mods button is re-injected after the main menu reloads
        // (e.g. when the player returns from a game). TryInject is a no-op once the button
        // exists, so the steady-state cost is one cheap tree walk every 0.5s.
        var pollTimer = new Godot.Timer { WaitTime = 0.5, OneShot = false };
        pollTimer.Timeout += () => MainMenuIntegration.TryInject();
        tree.Root.AddChild(pollTimer);
        pollTimer.Start();

        // Tick outgoing transfers at 30Hz so chunks get pumped out steadily without
        // blocking the network frame budget. One chunk per tick keeps memory steady too.
        var transferTimer = new Godot.Timer
        {
            Name = "ModFrameworkTransferPump",
            WaitTime = 1.0 / 30.0,
            OneShot = false,
        };
        transferTimer.Timeout += PumpOutgoingTransfers;
        tree.Root.AddChild(transferTimer);
        transferTimer.Start();
    }

    public bool IsModEnabled(string id) => _modEnabled.GetValueOrDefault(id, false);
    public bool IsModDesiredEnabled(string id) => _desiredEnabled.GetValueOrDefault(id, false);

    public void ToggleMod(string id, bool enable)
    {
        _desiredEnabled[id] = enable;

        if (TryGetLocalManifest(id, out var manifest) && enable && UsesFrameworkAssemblyLoader(manifest))
        {
            // Manually flipping the toggle ON counts as user verification — mark the
            // current DLL hash as checked so future sessions can auto-load it.
            MarkFingerprintChecked(id);
        }

        PersistDesiredEnabledState();

        if (!TryGetLocalManifest(id, out manifest))
            return;

        if (manifest.UsesOfficialLoader())
        {
            GD.Print($"[ModFramework] Deferred official mod {id} => {(enable ? "enabled" : "disabled")} until apply/start");
            return;
        }

        if (enable)
            EnableMod(id);
        else
            DisableMod(id);
    }

    public void Shutdown()
    {
        _networkLayer.Shutdown();
        UnloadAllMods();
        GD.Print("[ModFramework] Framework shut down");
    }

    private bool EnableMod(string id, bool broadcast = true)
    {
        if (_modEnabled.GetValueOrDefault(id, false))
            return true;

        if (!TryGetLocalManifest(id, out var manifest))
        {
            GD.PrintErr($"[ModFramework] Cannot enable mod {id}: manifest is missing");
            return false;
        }

        if (manifest.UsesOfficialLoader())
        {
            if (!OfficialModBridge.EnableMod(manifest))
            {
                GD.PrintErr($"[ModFramework] Failed to enable official mod {id}");
                _modSessionAvailable[id] = false;
                _modEnabled[id] = false;
                return false;
            }

            _modSessionAvailable[id] = true;
            _modEnabled[id] = true;
            GD.Print($"[ModFramework] Enabled official mod {id}");
        }
        else if (UsesFrameworkAssemblyLoader(manifest))
        {
            if (!_modDllPaths.TryGetValue(id, out var dllPath) || !File.Exists(dllPath))
            {
                GD.PrintErr($"[ModFramework] Cannot enable mod {id}: DLL path is missing");
                _modSessionAvailable[id] = false;
                _modEnabled[id] = false;
                return false;
            }

            // Defense in depth: refuse to load a framework DLL whose current bytes
            // haven't been user-approved. ToggleMod/Inspect/Download are the paths
            // that should have already marked it checked before reaching here.
            if (!IsModChecked(id))
            {
                GD.Print($"[ModFramework] Refusing to load {id} — DLL hasn't been checked yet (click 🔍 in Mods dialog or toggle on)");
                _modSessionAvailable[id] = false;
                _modEnabled[id] = false;
                return false;
            }

            try
            {
                _loader.LoadMod(id, dllPath, manifest.AssemblySha256, manifest.AddAssemblyToGodot);
                MountModPckIfAny(manifest);
                _modSessionAvailable[id] = true;
                _modEnabled[id] = true;
                GD.Print($"[ModFramework] Enabled mod {id}");
                if (manifest.Effects.NeedsRestart)
                    GD.Print($"[ModFramework] NOTE: {id} declares NeedsRestart; some effects may not fully apply until next launch");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModFramework] Failed to enable mod {id}: {ex.Message}");
                _modSessionAvailable[id] = false;
                _modEnabled[id] = false;
                return false;
            }
        }
        else
        {
            _modSessionAvailable[id] = true;
            _modEnabled[id] = true;
            GD.Print($"[ModFramework] Enabled manifest-only mod {id}");
        }

        if (broadcast)
            _networkLayer.BroadcastManifest();

        RefreshCompatibilityReport();
        return true;
    }

    private void DisableMod(string id, bool broadcast = true)
    {
        if (!_modEnabled.GetValueOrDefault(id, false))
            return;

        if (TryGetLocalManifest(id, out var manifest))
        {
            if (manifest.UsesOfficialLoader())
            {
                if (!OfficialModBridge.DisableMod(manifest))
                    GD.PrintErr($"[ModFramework] Failed to disable official mod {id}");
            }
            else if (_loader.IsLoaded(id))
            {
                _loader.UnloadMod(id);
            }
        }

        if (_modsWithMountedPck.Contains(id))
            GD.Print($"[ModFramework] NOTE: {id} mounted a .pck; resources remain on res:// until restart (Godot 4 cannot unmount PCKs)");

        _modEnabled[id] = false;
        GD.Print($"[ModFramework] Disabled mod {id}");
        if (broadcast)
            _networkLayer.BroadcastManifest();
        RefreshCompatibilityReport();
    }

    private void MountModPckIfAny(ModManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.PckFile)) return;
        if (string.IsNullOrWhiteSpace(manifest.DirectoryPath)) return;
        if (_modsWithMountedPck.Contains(manifest.Id)) return; // already mounted this session

        var pckPath = Path.Combine(manifest.DirectoryPath, manifest.PckFile);
        if (!File.Exists(pckPath))
        {
            GD.PrintErr($"[ModFramework] PCK file not found for {manifest.Id}: {pckPath}");
            return;
        }

        try
        {
            var ok = ProjectSettings.LoadResourcePack(pckPath);
            if (!ok)
            {
                GD.PrintErr($"[ModFramework] LoadResourcePack returned false for {manifest.Id}: {pckPath}");
                return;
            }
            _modsWithMountedPck.Add(manifest.Id);
            GD.Print($"[ModFramework] Mounted PCK for {manifest.Id}: {manifest.PckFile}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to mount PCK for {manifest.Id}: {ex.Message}");
        }
    }

    private void LoadLocalAssemblyMods()
    {
        foreach (var mod in _localMods)
        {
            if (mod.UsesOfficialLoader())
            {
                _modSessionAvailable[mod.Id] = OfficialModBridge.CanResolveManifest(mod);
                _modEnabled[mod.Id] = OfficialModBridge.IsEnabled(mod);
                GD.Print($"[ModFramework] Delaying official mod {mod.Id} until framework apply/start");
                continue;
            }

            var dllPath = TryFindModDll(mod);
            if (dllPath == null)
            {
                if (UsesFrameworkAssemblyLoader(mod))
                {
                    _modSessionAvailable[mod.Id] = false;
                    _modEnabled[mod.Id] = false;
                    GD.PrintErr($"[ModFramework] DLL not found for mod {mod.Id}");
                }
                else
                {
                    _modSessionAvailable[mod.Id] = true;
                    _modEnabled[mod.Id] = _desiredEnabled.GetValueOrDefault(mod.Id, false);
                    GD.Print($"[ModFramework] Skipping {mod.Id} - manifest-only mod");
                }

                continue;
            }

            // Record the DLL path BEFORE the check gate so IsModChecked can hash it.
            _modDllPaths[mod.Id] = dllPath;

            // User-check gate: never auto-load a framework DLL whose hash isn't on the
            // approved list. The mod still appears in the dialog; the user clicks 🔍 or
            // toggles it on (both mark the hash as checked) to actually run its OnLoad.
            //
            // Session-available stays TRUE so peers still see we have the bits (avoids
            // redundant downloads when a vote passes). The vote-enable path marks-and-
            // enables on consent — see ApplyVoteResult.
            if (UsesFrameworkAssemblyLoader(mod) && !IsModChecked(mod.Id))
            {
                _modSessionAvailable[mod.Id] = true;
                _modEnabled[mod.Id] = false;
                GD.Print($"[ModFramework] {mod.Id} is unchecked — will stay disabled until you click 🔍 or toggle it on");
                continue;
            }

            // Honor desired-disabled by skipping load entirely (don't run OnLoad just
            // to immediately unload).
            if (!_desiredEnabled.GetValueOrDefault(mod.Id, false))
            {
                _modSessionAvailable[mod.Id] = true;
                _modEnabled[mod.Id] = false;
                GD.Print($"[ModFramework] {mod.Id} present but desired-disabled; not loaded");
                continue;
            }

            try
            {
                _loader.LoadMod(mod.Id, dllPath, mod.AssemblySha256, mod.AddAssemblyToGodot);
                MountModPckIfAny(mod);
                _modSessionAvailable[mod.Id] = true;
                _modEnabled[mod.Id] = true;
                GD.Print($"[ModFramework] Loaded assembly mod: {mod.Id}");
            }
            catch (Exception ex)
            {
                _modSessionAvailable[mod.Id] = false;
                _modEnabled[mod.Id] = false;
                GD.PrintErr($"[ModFramework] Failed to load mod {mod.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ApplyDesiredModsForSession()
    {
        var changed = false;

        foreach (var mod in _localMods)
        {
            var desiredEnabled = _desiredEnabled.GetValueOrDefault(mod.Id, false);
            var isEnabled = _modEnabled.GetValueOrDefault(mod.Id, false);
            if (desiredEnabled == isEnabled)
                continue;

            if (desiredEnabled)
                changed |= EnableMod(mod.Id, broadcast: false);
            else
            {
                DisableMod(mod.Id, broadcast: false);
                changed = true;
            }
        }

        if (changed)
            _networkLayer.BroadcastManifest();

        RefreshCompatibilityReport();
    }

    private void UnloadAllMods()
    {
        foreach (var id in _modDllPaths.Keys.ToList())
        {
            if (_modEnabled.GetValueOrDefault(id, false) || _loader.IsLoaded(id))
                _loader.UnloadMod(id);
        }

        _modDllPaths.Clear();
        _modSessionAvailable.Clear();
        _modEnabled.Clear();
        GD.Print("[ModFramework] All mods unloaded");
    }

    private void PersistDesiredEnabledState()
    {
        FrameworkModStateStore.SaveState(
            _desiredEnabled.Keys,
            _desiredEnabled
                .Where(entry => entry.Value)
                .Select(entry => entry.Key),
            _checkedFingerprints);
    }

    // Composite fingerprint of every executable file in the mod (DLL + PCK if
    // declared). Computed as SHA-256 over the concatenation of per-file SHA-256s in
    // a fixed order, so any byte change in either file produces a new fingerprint.
    // Returns null for non-DLL mods or when the DLL file can't be read.
    private string? GetCurrentModFingerprint(string modId)
    {
        if (_modCurrentFingerprint.TryGetValue(modId, out var cached))
            return cached;
        if (!_modDllPaths.TryGetValue(modId, out var dllPath) || !File.Exists(dllPath))
            return null;
        if (!TryGetLocalManifest(modId, out var manifest))
            return null;
        try
        {
            var dllHash = HashFile(dllPath);
            var pckHash = "";
            if (!string.IsNullOrWhiteSpace(manifest.PckFile) && !string.IsNullOrWhiteSpace(manifest.DirectoryPath))
            {
                var pckPath = Path.Combine(manifest.DirectoryPath, manifest.PckFile);
                if (File.Exists(pckPath))
                    pckHash = HashFile(pckPath);
            }

            var fp = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes($"dll:{dllHash}|pck:{pckHash}")));
            _modCurrentFingerprint[modId] = fp;
            return fp;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to fingerprint {modId}: {ex.Message}");
            return null;
        }
    }

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs));
    }

    // True when the mod doesn't need the gate (official-loader / manifest-only) or
    // when its current DLL+PCK bytes are on the approved list.
    public bool IsModChecked(string modId)
    {
        if (!TryGetLocalManifest(modId, out var manifest)) return false;
        if (!UsesFrameworkAssemblyLoader(manifest)) return true;
        var fp = GetCurrentModFingerprint(modId);
        return fp != null && _checkedFingerprints.Contains(fp);
    }

    // Adds the mod's current fingerprint to the in-memory checked set. Caller is
    // responsible for persistence (usually via PersistDesiredEnabledState).
    private void MarkFingerprintChecked(string modId)
    {
        var fp = GetCurrentModFingerprint(modId);
        if (fp == null) return;
        if (_checkedFingerprints.Add(fp))
            GD.Print($"[ModFramework] Marked {modId} as user-checked (fingerprint {fp[..Math.Min(12, fp.Length)]}…)");
    }

    private void OnPeerManifestReceived(ModPeerSnapshot peerSnapshot)
    {
        // Track every peer's view, host or not, so the compatibility checker can reason
        // over the full lobby's mod set on either side. Vote queueing still only happens
        // on the host.
        peerSnapshot.Normalize();
        if (!string.IsNullOrWhiteSpace(peerSnapshot.UserId))
            _peerSnapshots[peerSnapshot.UserId] = peerSnapshot;

        RefreshCompatibilityReport();

        if (!_networkLayer.IsLocalHost)
            return;

        foreach (var request in BuildVoteRequestsForPeer(peerSnapshot))
            QueueVoteRequest(request, isHostLocalPrompt: true);
    }

    // Recomputes the local-AND-remote compatibility picture over the union of local
    // installed mods + every known peer's installed manifests, with the union of
    // enabled-id sets. Caches the latest report so vote prompts and UI can read it.
    // Cheap enough to run on every state change.
    private void RefreshCompatibilityReport()
    {
        try
        {
            var unionInstalledById = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _localMods)
                if (!string.IsNullOrWhiteSpace(m.Id)) unionInstalledById[m.Id] = m;
            foreach (var peer in _peerSnapshots.Values)
                foreach (var pm in peer.InstalledManifests)
                    if (!string.IsNullOrWhiteSpace(pm.Id) && !unionInstalledById.ContainsKey(pm.Id))
                        unionInstalledById[pm.Id] = pm;

            var unionEnabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, on) in _modEnabled) if (on) unionEnabled.Add(id);
            foreach (var peer in _peerSnapshots.Values)
                foreach (var id in peer.EnabledModIds) unionEnabled.Add(id);

            _latestCompatibilityReport = ModCompatibilityChecker.Check(
                unionInstalledById.Values.ToList(),
                unionEnabled,
                _loader.SnapshotLoadedAssemblies());

            if (_latestCompatibilityReport.HasIssues)
                ModCompatibilityChecker.LogReport(_latestCompatibilityReport);

            TryShowNextConflictPrompt();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] RefreshCompatibilityReport failed: {ex.Message}");
        }
    }

    public ModCompatibilityChecker.Report? GetLatestCompatibilityReport() => _latestCompatibilityReport;

    // Pure read-only manifest + file listing + declared-patch info. No side effects.
    // For the user-consent "I've reviewed this mod" gate, see ScanMod below.
    public ModInspector.Report? InspectMod(string modId)
    {
        if (!TryGetLocalManifest(modId, out var manifest)) return null;
        System.Reflection.Assembly? loaded = null;
        if (_loader.SnapshotLoadedAssemblies().TryGetValue(modId, out var asm))
            loaded = asm;
        return ModInspector.Inspect(manifest, loaded);
    }

    // Static IL safety scan via Mono.Cecil. Walks the mod's DLL and reports calls
    // to dangerous APIs (Process, raw networking, registry, P/Invoke, code gen,
    // file deletion, etc). This is the user-consent action — running it counts as
    // "I've reviewed this mod" and marks the current fingerprint as checked.
    public ModScanner.Report? ScanMod(string modId)
    {
        if (!TryGetLocalManifest(modId, out var manifest)) return null;

        var report = ModScanner.Scan(manifest);

        if (UsesFrameworkAssemblyLoader(manifest))
        {
            var wasChecked = IsModChecked(modId);
            MarkFingerprintChecked(modId);
            if (!wasChecked)
            {
                PersistDesiredEnabledState();
                if (_desiredEnabled.GetValueOrDefault(modId, false) && !_modEnabled.GetValueOrDefault(modId, false))
                {
                    if (EnableMod(modId))
                        MainMenuIntegration.SyncDialogToggle(modId, true);
                }
            }
        }

        return report;
    }

    // Used by the Mods dialog to decide whether to show a ⚠ badge next to a mod card.
    // Returns null if there are no relevant issues, otherwise a short multi-line summary.
    private string? BuildModIssueTooltip(string modId)
    {
        var report = _latestCompatibilityReport;
        if (report == null || !report.HasIssues) return null;

        var lines = new List<string>();
        foreach (var c in report.Conflicts)
            if (string.Equals(c.ModA, modId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.ModB, modId, StringComparison.OrdinalIgnoreCase))
                lines.Add($"Conflict: {c.ModA} vs {c.ModB} — {c.Reason}");
        foreach (var w in report.Warnings)
            if (w.InvolvedMods.Any(id => string.Equals(id, modId, StringComparison.OrdinalIgnoreCase)))
                lines.Add($"Warning: {w.Detail}");
        foreach (var d in report.MissingDependencies)
            if (string.Equals(d.ModId, modId, StringComparison.OrdinalIgnoreCase))
                lines.Add($"Missing dependency: requires {d.MissingDependencyId}");

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    // Walks the latest report for actionable local conflicts (both mods enabled here)
    // and pops the resolution prompt for the first one we haven't already handled. Only
    // one prompt at a time; the next is offered after the current one resolves.
    private void TryShowNextConflictPrompt()
    {
        if (_tree == null || _conflictPromptOpen) return;
        var report = _latestCompatibilityReport;
        if (report == null || report.Conflicts.Count == 0) return;

        foreach (var c in report.Conflicts)
        {
            if (string.Equals(c.ModA, c.ModB, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_modEnabled.GetValueOrDefault(c.ModA, false) || !_modEnabled.GetValueOrDefault(c.ModB, false)) continue;

            var pairKey = string.CompareOrdinal(c.ModA, c.ModB) < 0
                ? $"{c.ModA}|{c.ModB}" : $"{c.ModB}|{c.ModA}";
            if (_conflictPairsHandled.Contains(pairKey)) continue;

            var nameA = TryGetLocalManifest(c.ModA, out var ma) ? ma.Name : c.ModA;
            var nameB = TryGetLocalManifest(c.ModB, out var mb) ? mb.Name : c.ModB;
            var modAId = c.ModA;
            var modBId = c.ModB;

            _conflictPromptOpen = true;
            MainMenuIntegration.ShowConflictPrompt(_tree, modAId, nameA, modBId, nameB, c.Reason, keepId =>
            {
                _conflictPromptOpen = false;
                _conflictPairsHandled.Add(pairKey);
                if (string.IsNullOrEmpty(keepId))
                {
                    GD.Print($"[ModFramework] Conflict deferred: {modAId} vs {modBId}");
                }
                else
                {
                    var disableId = string.Equals(keepId, modAId, StringComparison.OrdinalIgnoreCase) ? modBId : modAId;
                    GD.Print($"[ModFramework] Conflict resolved: keep {keepId}, disable {disableId}");
                    ToggleMod(disableId, false);
                    // Push the new state into the dialog so the user sees the toggle
                    // flip — they may still have the Mods dialog open behind the prompt.
                    MainMenuIntegration.SyncDialogToggle(disableId, false);
                }
                // After this prompt resolves, see if a sibling conflict needs prompting.
                TryShowNextConflictPrompt();
            });
            return;
        }
    }

    private void OnVoteRequestReceived(string senderUserId, ModVoteRequest request)
    {
        if (_networkLayer.IsLocalHost)
            return;

        var lobbyOwnerUserId = _networkLayer.LobbyOwnerUserId;
        if (string.IsNullOrWhiteSpace(lobbyOwnerUserId) ||
            !string.Equals(lobbyOwnerUserId, senderUserId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        request.Normalize();
        if (!_queuedVoteIds.Add(request.VoteId))
            return;

        _voteQueue.Enqueue(new PendingVotePrompt(
            request.VoteId,
            request.Title,
            request.Body,
            voteYes => _networkLayer.SendVoteResponse(new ModVoteResponse
            {
                VoteId = request.VoteId,
                TargetUserId = lobbyOwnerUserId,
                VoteYes = voteYes
            })));
        TryShowNextVote();
    }

    private void OnVoteResponseReceived(string senderUserId, ModVoteResponse response)
    {
        if (!_networkLayer.IsLocalHost)
            return;

        var localUserId = _networkLayer.LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId) ||
            !string.Equals(response.TargetUserId, localUserId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _voteSession.CastVote(response.VoteId, senderUserId, response.VoteYes);
    }

    private void OnVoteResultReceived(string senderUserId, ModVoteResult result)
    {
        var lobbyOwnerUserId = _networkLayer.LobbyOwnerUserId;
        if (!_networkLayer.IsLocalHost &&
            (string.IsNullOrWhiteSpace(lobbyOwnerUserId) ||
             !string.Equals(lobbyOwnerUserId, senderUserId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ApplyVoteResult(result);
    }

    private void OnTransferRequestReceived(string requesterUserId, ModTransferRequest request)
    {
        request.Normalize();
        if (string.IsNullOrWhiteSpace(request.ModId) || string.IsNullOrWhiteSpace(requesterUserId))
            return;

        if (!TryGetLocalManifest(request.ModId, out var manifest))
        {
            GD.Print($"[ModFramework] Cannot serve transfer for {request.ModId}: not installed locally");
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.ModVersion) &&
            !string.Equals(manifest.Version, request.ModVersion, StringComparison.OrdinalIgnoreCase))
        {
            GD.Print($"[ModFramework] Cannot serve transfer for {request.ModId}: local v{manifest.Version} != requested v{request.ModVersion}");
            return;
        }

        if (!_modDllPaths.TryGetValue(manifest.Id, out var dllPath) || !File.Exists(dllPath))
        {
            GD.Print($"[ModFramework] Cannot serve transfer for {request.ModId}: DLL path missing");
            return;
        }

        _transfer.BeginSend(requesterUserId, manifest.Id, manifest.Version, dllPath, ".dll");

        // If the mod ships a side-file PCK, send that too. Receiver writes both into
        // mods/<id>/ and only enables the mod once the DLL has arrived.
        if (!string.IsNullOrWhiteSpace(manifest.PckFile) && !string.IsNullOrWhiteSpace(manifest.DirectoryPath))
        {
            var pckPath = Path.Combine(manifest.DirectoryPath, manifest.PckFile);
            if (File.Exists(pckPath))
                _transfer.BeginSend(requesterUserId, manifest.Id, manifest.Version, pckPath, ".pck");
            else
                GD.PrintErr($"[ModFramework] Mod {manifest.Id} declares PckFile={manifest.PckFile} but file missing at {pckPath}");
        }
    }

    private void OnTransferChunkReceived(string sourceUserId, ModTransferChunk chunk)
    {
        var result = _transfer.OnChunkReceived(sourceUserId, chunk, out var persistedPath);
        if (result != ModP2PTransfer.ReceiveResult.CompletedAndPersisted || persistedPath == null)
            return;

        try
        {
            // Each completed file (DLL or PCK) lands in user://mods/<id>/. Write the
            // peer's manifest into the same folder if not already present so the scan
            // can find the mod, then attempt to finalize. Finalize is a no-op until
            // every declared file (DLL + PCK if any) is on disk — that way the
            // fingerprint we mark-checked covers the whole mod, not just the first
            // file to land.
            EnsureTransferredManifestOnDisk(sourceUserId, chunk.ModId, persistedPath);

            _localMods = ManifestManager.ScanLocalMods();
            foreach (var mod in _localMods)
            {
                mod.Normalize();
                if (!_desiredEnabled.ContainsKey(mod.Id))
                    _desiredEnabled[mod.Id] = false;
                if (!_modSessionAvailable.ContainsKey(mod.Id))
                    _modSessionAvailable[mod.Id] = !UsesFrameworkAssemblyLoader(mod);
                ModExceptionFilter.RegisterKnownModAssembly(mod.Id);
            }

            // Invalidate any cached fingerprint for this mod since its file set just
            // changed. Recomputed lazily on next IsModChecked / GetCurrentModFingerprint.
            _modCurrentFingerprint.Remove(chunk.ModId);

            TryFinalizeTransferredMod(chunk.ModId);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to integrate transferred mod {chunk.ModId}: {ex.Message}");
        }
    }

    // Called after each transferred file lands. Idempotent — does nothing until all
    // declared files (DLL plus PCK when manifested) are present, and short-circuits
    // once the mod is enabled. Marks the composite fingerprint and enables exactly
    // once when the mod is fully on disk.
    private void TryFinalizeTransferredMod(string modId)
    {
        if (!TryGetLocalManifest(modId, out var manifest)) return;
        if (!UsesFrameworkAssemblyLoader(manifest)) return;
        if (_modEnabled.GetValueOrDefault(modId, false)) return;

        var dllPath = TryFindModDll(manifest);
        if (dllPath == null || !File.Exists(dllPath))
        {
            GD.Print($"[ModFramework] {modId}: waiting for DLL before finalizing");
            return;
        }

        if (!string.IsNullOrWhiteSpace(manifest.PckFile))
        {
            if (string.IsNullOrWhiteSpace(manifest.DirectoryPath)) return;
            var pckPath = Path.Combine(manifest.DirectoryPath, manifest.PckFile);
            if (!File.Exists(pckPath))
            {
                GD.Print($"[ModFramework] {modId}: DLL on disk, waiting for declared PCK before finalizing");
                return;
            }
        }

        _modDllPaths[modId] = dllPath;
        _modCurrentFingerprint.Remove(modId);
        _modSessionAvailable[modId] = true;
        // The user explicitly chose Download in the acquisition prompt — verified
        // consent for these exact bytes. Mark the composite fingerprint so the gate
        // lets EnableMod through.
        MarkFingerprintChecked(modId);
        _desiredEnabled[modId] = true;
        PersistDesiredEnabledState();
        EnableMod(modId, broadcast: true);
        GD.Print($"[ModFramework] Finalized transferred mod {modId}");
    }

    // The manifest doesn't travel as a transfer chunk — it's already in the peer's
    // broadcast snapshot. Write it to disk next to the freshly-arrived file (DLL or
    // PCK; both live in the same mod folder) so the local scan can find the mod.
    private void EnsureTransferredManifestOnDisk(string sourceUserId, string modId, string persistedFilePath)
    {
        if (!_peerSnapshots.TryGetValue(sourceUserId, out var snapshot)) return;
        var manifest = snapshot.InstalledManifests
            .FirstOrDefault(m => string.Equals(m.Id, modId, StringComparison.OrdinalIgnoreCase));
        if (manifest == null) return;

        try
        {
            var dir = Path.GetDirectoryName(persistedFilePath);
            if (string.IsNullOrEmpty(dir)) return;
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (File.Exists(manifestPath)) return; // don't overwrite a local manifest

            var json = System.Text.Json.JsonSerializer.Serialize(manifest, s_indentedJsonOptions);
            File.WriteAllText(manifestPath, json);
            GD.Print($"[ModFramework] Wrote transferred manifest for {modId} to {manifestPath}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to write transferred manifest for {modId}: {ex.Message}");
        }
    }

    private void OnWorkshopItemInstalled(string installedFolderPath, ulong publishedFileId)
    {
        try
        {
            if (!Directory.Exists(installedFolderPath))
            {
                GD.PrintErr($"[ModFramework] Workshop folder missing: {installedFolderPath}");
                return;
            }

            // Re-scan mods so a Workshop drop is picked up without a restart. We do not
            // auto-enable — the user toggles in the Mods dialog as with any other mod,
            // and the user-check gate still applies to any framework-loader DLL.
            _localMods = ManifestManager.ScanLocalMods();
            foreach (var mod in _localMods)
            {
                mod.Normalize();
                if (!_desiredEnabled.ContainsKey(mod.Id))
                    _desiredEnabled[mod.Id] = false;
                ModExceptionFilter.RegisterKnownModAssembly(mod.Id);

                // Resolve and record the DLL path for any framework-loader mod that
                // doesn't have one yet, OR re-resolve when the path moved (mod folder
                // changed). Without this, the user can toggle the new mod ON in the
                // dialog but EnableMod has no DLL path to load. They'd be forced to
                // restart.
                if (UsesFrameworkAssemblyLoader(mod))
                {
                    var dllPath = TryFindModDll(mod);
                    if (dllPath != null)
                    {
                        if (!_modDllPaths.TryGetValue(mod.Id, out var existing) || existing != dllPath)
                        {
                            _modDllPaths[mod.Id] = dllPath;
                            // Path or bytes may have changed under us; force re-fingerprint.
                            _modCurrentFingerprint.Remove(mod.Id);
                        }
                        else
                        {
                            // Same path — bytes may still have changed if Workshop overwrote
                            // an existing mod in place. Cheap to invalidate; recomputed lazily.
                            _modCurrentFingerprint.Remove(mod.Id);
                        }
                        _modSessionAvailable[mod.Id] = true;
                    }
                    else
                    {
                        if (!_modSessionAvailable.ContainsKey(mod.Id))
                            _modSessionAvailable[mod.Id] = false;
                    }
                }
                else if (!_modSessionAvailable.ContainsKey(mod.Id))
                {
                    _modSessionAvailable[mod.Id] = true;
                }
            }
            GD.Print($"[ModFramework] Workshop install integrated; {_localMods.Count} local mods after rescan");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Workshop install handler failed: {ex.Message}");
        }
    }

    private void PumpOutgoingTransfers()
    {
        // Process up to a handful of chunks per tick to keep big mods moving without
        // blowing the per-frame send budget. 4 chunks * 32 KB raw = 128 KB/tick max at 30Hz
        // = ~3.7 MB/s ceiling, comfortable for a typical Pratfall mod DLL.
        for (var i = 0; i < 4; i++)
        {
            var pending = _transfer.TickOutgoing();
            if (pending == null) return;
            _networkLayer.SendTransferChunk(pending.Value.TargetUserId, pending.Value.Chunk);
        }
    }

    private void OnVoteResolved(string voteId, bool passed)
    {
        if (!_activeVoteRequests.Remove(voteId, out var activeRequest))
            return;

        var result = new ModVoteResult
        {
            VoteId = voteId,
            SourceUserId = activeRequest.SourceUserId,
            EffectiveMode = activeRequest.Request.EffectiveMode,
            Passed = passed,
            Manifest = activeRequest.Request.Manifest
        };
        result.Normalize();

        _networkLayer.BroadcastVoteResult(result);
        ApplyVoteResult(result);
    }

    private void ApplyVoteResult(ModVoteResult result)
    {
        result.Normalize();
        if (!_processedVoteIds.Add(result.VoteId))
            return;

        _queuedVoteIds.Remove(result.VoteId);
        _activeVoteRequests.Remove(result.VoteId);
        RemoveActiveVoteKey(result.VoteId);

        if (string.Equals(_activeVoteId, result.VoteId, StringComparison.OrdinalIgnoreCase))
            _activeVoteId = null;

        if (!result.Passed)
        {
            GD.Print($"[ModFramework] Vote failed for {result.Manifest.Id}");
            TryShowNextVote();
            return;
        }

        foreach (var conflictingModId in GetConflictingEnabledLocalMods(result.Manifest))
            DisableMod(conflictingModId, broadcast: false);

        var enabledLocalMatch = false;
        if (TryGetCompatibleInstalledManifest(result.Manifest, out var localMatch))
        {
            if (RequiresAssembly(localMatch))
            {
                // A YES vote is explicit consent for this mod. If the local copy is
                // sitting unchecked, voting passes the gate — otherwise we'd be stuck
                // in a half-state where the lobby thinks we have it loaded but we
                // refused to actually load it.
                if (UsesFrameworkAssemblyLoader(localMatch) && !IsModChecked(localMatch.Id))
                {
                    MarkFingerprintChecked(localMatch.Id);
                    PersistDesiredEnabledState();
                }
                _desiredEnabled[localMatch.Id] = true;
                enabledLocalMatch = EnableMod(localMatch.Id, broadcast: false);
            }
            else
            {
                _modEnabled[localMatch.Id] = true;
                _modSessionAvailable[localMatch.Id] = true;
                enabledLocalMatch = true;
            }

            if (enabledLocalMatch)
                GD.Print($"[ModFramework] Enabled local match for {localMatch.Id} after vote");
        }

        if (enabledLocalMatch)
        {
            // We already have it — no acquisition needed. Stretch was the lazy default
            // before; keep that auto-apply now since it's free and the user already has
            // the bits.
            if (ModNetworkStretch.CanStretch(result.Manifest))
            {
                try
                {
                    ModNetworkStretch.ApplyStretch(result.Manifest);
                    GD.Print($"[ModFramework] Applied {result.Manifest.Id} via network stretch");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[ModFramework] Failed to apply stretch for {result.Manifest.Id}: {ex.Message}");
                }
            }
        }
        else
        {
            // We don't have the mod — never auto-download. Ask the player how they want
            // to acquire it. Decline leaves the lobby because the session can't proceed
            // with state divergence.
            PromptModAcquisition(result);
        }

        _networkLayer.BroadcastManifest();
        TryShowNextVote();
    }

    private void PromptModAcquisition(ModVoteResult result)
    {
        if (_tree == null)
        {
            GD.PrintErr($"[ModFramework] No SceneTree to host acquisition prompt for {result.Manifest.Id}; falling back to leave-lobby");
            _networkLayer.LeaveLobby();
            return;
        }

        var canStretch = ModNetworkStretch.CanStretch(result.Manifest);
        var modName = string.IsNullOrWhiteSpace(result.Manifest.Name) ? result.Manifest.Id : result.Manifest.Name;
        var sourceUserId = result.SourceUserId;
        var modId = result.Manifest.Id;
        var modVersion = result.Manifest.Version;
        var manifest = result.Manifest;

        MainMenuIntegration.ShowAcquisitionPrompt(_tree,
            modName: modName,
            modVersion: modVersion,
            canStretch: canStretch,
            approxDownloadBytes: null, // host doesn't pre-announce file size in the request
            onDownload: () =>
            {
                GD.Print($"[ModFramework] Player chose Download for {modId} from {sourceUserId}");
                _networkLayer.RequestModTransfer(sourceUserId, modId, modVersion);
            },
            onStretch: () =>
            {
                try
                {
                    ModNetworkStretch.ApplyStretch(manifest);
                    GD.Print($"[ModFramework] Player chose Stretch for {modId}");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[ModFramework] Stretch apply failed for {modId}: {ex.Message}");
                }
            },
            onDecline: () =>
            {
                GD.Print($"[ModFramework] Player declined {modId}; leaving lobby");
                _networkLayer.LeaveLobby();
            });
    }

    private void QueueVoteRequest(ModVoteRequest request, bool isHostLocalPrompt)
    {
        request.Normalize();
        var voteKey = BuildVoteKey(request.Manifest, request.SourceUserId);
        if (!_activeVoteKeys.Add(voteKey))
            return;

        if (!_queuedVoteIds.Add(request.VoteId))
        {
            _activeVoteKeys.Remove(voteKey);
            return;
        }

        _activeVoteRequests[request.VoteId] = new ActiveVoteRequest(request.SourceUserId, request);
        _voteKeysByVoteId[request.VoteId] = voteKey;
        _voteSession.StartVote(request.VoteId, request.Manifest, request.ExpectedVotes);

        if (isHostLocalPrompt)
        {
            _voteQueue.Enqueue(new PendingVotePrompt(
                request.VoteId,
                request.Title,
                request.Body,
                voteYes =>
                {
                    var localUserId = _networkLayer.LocalUserId;
                    if (string.IsNullOrWhiteSpace(localUserId))
                        return;
                    _voteSession.CastVote(request.VoteId, localUserId, voteYes);
                }));
        }

        _networkLayer.BroadcastVoteRequest(request);
        TryShowNextVote();
    }

    private void TryShowNextVote()
    {
        if (_activeVoteId != null || _voteUI == null)
            return;

        if (!_voteQueue.TryDequeue(out var prompt))
            return;

        _activeVoteId = prompt.VoteId;
        _voteUI.ShowVote(
            prompt.VoteId,
            prompt.Title,
            prompt.Body,
            1,
            (_, voteYes) => prompt.OnComplete(voteYes));
    }

    private IEnumerable<ModVoteRequest> BuildVoteRequestsForPeer(ModPeerSnapshot peerSnapshot)
    {
        peerSnapshot.Normalize();

        var localUserId = _networkLayer.LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId))
            yield break;

        var localEnabled = GetEnabledLocalMods()
            .Where(ShouldAdvertiseManifest)
            .OrderBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var localEnabledById = localEnabled.ToDictionary(mod => mod.Id, StringComparer.OrdinalIgnoreCase);
        var peerInstalledById = peerSnapshot.GetInstalledManifestMap();
        var peerEnabledById = peerSnapshot.GetEnabledManifestMap();
        var expectedVotes = _networkLayer.ConnectedPlayerCount;

        foreach (var local in localEnabled)
        {
            if (peerEnabledById.TryGetValue(local.Id, out var remoteEnabled) &&
                VersionsMatch(local, remoteEnabled))
            {
                continue;
            }

            var unavailableDependencies = GetUnavailableDependencies(local, peerInstalledById);
            if (unavailableDependencies.Count > 0)
            {
                GD.PrintErr($"[ModFramework] Cannot sync {local.Id}: peer is missing required mods {string.Join(", ", unavailableDependencies)}");
                continue;
            }

            var body = BuildHostVoteBody(local, peerInstalledById, peerEnabledById);
            yield return new ModVoteRequest
            {
                VoteId = CreateVoteId(local, localUserId),
                SourceUserId = localUserId,
                EffectiveMode = local.GetEffectiveNetworkMode(),
                Title = $"{local.Name} {local.Version}",
                Body = body,
                ExpectedVotes = expectedVotes,
                Manifest = local
            };
        }

        foreach (var remote in peerEnabledById.Values.OrderBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (localEnabledById.ContainsKey(remote.Id) || !ShouldAdvertiseManifest(remote))
                continue;

            var unavailableDependencies = GetUnavailableDependencies(remote, GetAdvertisedLocalManifestMap());
            if (unavailableDependencies.Count > 0)
            {
                GD.PrintErr($"[ModFramework] Cannot sync {remote.Id}: missing required mods {string.Join(", ", unavailableDependencies)}");
                continue;
            }

            yield return new ModVoteRequest
            {
                VoteId = CreateVoteId(remote, peerSnapshot.UserId),
                SourceUserId = peerSnapshot.UserId,
                EffectiveMode = remote.GetEffectiveNetworkMode(),
                Title = $"{remote.Name} {remote.Version}",
                Body = BuildPeerVoteBody(remote, peerInstalledById, peerEnabledById),
                ExpectedVotes = expectedVotes,
                Manifest = remote
            };
        }
    }

    private ModLocalState BuildLocalState()
    {
        var installed = _localMods
            .Where(IsManifestSessionAvailable)
            .Select(CloneManifest)
            .ToList();

        return new ModLocalState
        {
            InstalledManifests = installed,
            EnabledModIds = installed
                .Where(mod => IsModEnabled(mod.Id))
                .Select(mod => mod.Id)
                .ToList()
        };
    }

    private IReadOnlyDictionary<string, ModManifest> GetAdvertisedLocalManifestMap()
    {
        return _localMods
            .Where(IsManifestSessionAvailable)
            .GroupBy(mod => mod.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private List<ModManifest> GetEnabledLocalMods()
    {
        return _localMods
            .Where(mod => _modEnabled.GetValueOrDefault(mod.Id, false))
            .ToList();
    }

    private List<string> GetConflictingEnabledLocalMods(ModManifest manifest)
    {
        var conflicts = new List<string>();

        foreach (var local in GetEnabledLocalMods())
        {
            if (string.Equals(local.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!manifest.DeclaresConflictWith(local.Id) && !local.DeclaresConflictWith(manifest.Id))
                continue;

            conflicts.Add(local.Id);
        }

        return ModManifestJson.NormalizeIdentifiers(conflicts);
    }

    private List<string> GetUnavailableDependencies(ModManifest manifest, IReadOnlyDictionary<string, ModManifest> installedById)
    {
        return manifest.Multiplayer.Requires
            .Where(id => !installedById.ContainsKey(id))
            .ToList();
    }

    private string BuildHostVoteBody(
        ModManifest manifest,
        IReadOnlyDictionary<string, ModManifest> peerInstalledById,
        IReadOnlyDictionary<string, ModManifest> peerEnabledById)
    {
        var lines = new List<string>();

        if (!peerInstalledById.TryGetValue(manifest.Id, out var peerInstalled))
        {
            lines.Add($"{manifest.Name} {manifest.Version} is enabled on the host and the peer does not have it installed.");
        }
        else if (peerEnabledById.ContainsKey(manifest.Id))
        {
            lines.Add($"{manifest.Name} {manifest.Version} is enabled on the host and conflicts with the peer version {peerInstalled.Version}.");
        }
        else if (VersionsMatch(manifest, peerInstalled))
        {
            lines.Add($"{manifest.Name} {manifest.Version} is installed on the peer but currently disabled.");
        }
        else
        {
            lines.Add($"{manifest.Name} {manifest.Version} is enabled on the host and the peer has a different local version {peerInstalled.Version}.");
        }

        lines.Add($"Sync mode: {manifest.GetEffectiveNetworkMode().Replace('_', ' ')}.");
        AppendDependencyLines(lines, manifest, peerInstalledById);
        AppendCompatibilityWarnings(lines, manifest.Id);
        lines.Add(RequiresAssembly(manifest)
            ? "Enable this host mod for the session?"
            : "Apply this host-backed session mod?");

        return string.Join("\n\n", lines);
    }

    // Surface any cached compatibility report entries that involve `forModId` directly
    // into the vote prompt, so voters see "this conflicts with X" before they vote yes.
    private void AppendCompatibilityWarnings(List<string> lines, string forModId)
    {
        var report = _latestCompatibilityReport;
        if (report == null || !report.HasIssues) return;

        var notes = new List<string>();
        foreach (var c in report.Conflicts)
            if (string.Equals(c.ModA, forModId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.ModB, forModId, StringComparison.OrdinalIgnoreCase))
                notes.Add($"⚠ Conflict: {c.ModA} vs {c.ModB} — {c.Reason}");
        foreach (var w in report.Warnings)
            if (w.InvolvedMods.Any(id => string.Equals(id, forModId, StringComparison.OrdinalIgnoreCase)))
                notes.Add($"⚠ {w.Detail} — {w.Reason}");
        foreach (var d in report.MissingDependencies)
            if (string.Equals(d.ModId, forModId, StringComparison.OrdinalIgnoreCase))
                notes.Add($"⚠ Missing dependency: requires {d.MissingDependencyId}");

        if (notes.Count > 0)
            lines.Add(string.Join("\n", notes));
    }

    private string BuildPeerVoteBody(
        ModManifest manifest,
        IReadOnlyDictionary<string, ModManifest> peerInstalledById,
        IReadOnlyDictionary<string, ModManifest> peerEnabledById)
    {
        var lines = new List<string>();
        var localInstalledById = GetAdvertisedLocalManifestMap();

        if (!localInstalledById.TryGetValue(manifest.Id, out var localInstalled))
        {
            lines.Add($"{manifest.Name} {manifest.Version} is enabled on another player and is not installed locally.");
        }
        else if (VersionsMatch(localInstalled, manifest) && !IsModEnabled(manifest.Id))
        {
            lines.Add($"{manifest.Name} {manifest.Version} is enabled on another player and already installed locally.");
        }
        else
        {
            lines.Add($"{manifest.Name} {manifest.Version} conflicts with the local version {localInstalled.Version}.");
        }

        lines.Add($"Sync mode: {manifest.GetEffectiveNetworkMode().Replace('_', ' ')}.");
        AppendDependencyLines(lines, manifest, localInstalledById);
        AppendCompatibilityWarnings(lines, manifest.Id);
        lines.Add(RequiresAssembly(manifest)
            ? "Enable this mod for the session?"
            : "Apply this session mod?");

        return string.Join("\n\n", lines);
    }

    private static void AppendDependencyLines(List<string> lines, ModManifest manifest, IReadOnlyDictionary<string, ModManifest> installedById)
    {
        var missingDependencies = manifest.Multiplayer.Requires
            .Where(id => !installedById.ContainsKey(id))
            .ToList();

        if (missingDependencies.Count > 0)
            lines.Add($"Also needs: {string.Join(", ", missingDependencies)}.");

        if (manifest.GetEffectiveNetworkMode() == ModNetworkModes.RestartRequired)
            lines.Add("This mod is not safe to hot-apply and may need a restart after transfer.");
    }

    private bool TryGetCompatibleInstalledManifest(ModManifest manifest, out ModManifest localMatch)
    {
        localMatch = _localMods.FirstOrDefault(local =>
            string.Equals(local.Id, manifest.Id, StringComparison.OrdinalIgnoreCase) &&
            IsManifestSessionAvailable(local) &&
            VersionsMatch(local, manifest)) ?? new ModManifest();

        return !string.IsNullOrWhiteSpace(localMatch.Id);
    }

    private bool TryGetLocalManifest(string id, out ModManifest manifest)
    {
        manifest = _localMods.FirstOrDefault(mod => string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? new ModManifest();
        return !string.IsNullOrWhiteSpace(manifest.Id);
    }

    private bool IsManifestSessionAvailable(ModManifest manifest)
    {
        if (!ShouldAdvertiseManifest(manifest))
            return false;

        if (manifest.UsesOfficialLoader())
            return _modSessionAvailable.GetValueOrDefault(manifest.Id, false);

        if (!RequiresAssembly(manifest))
            return true;

        if (!UsesFrameworkAssemblyLoader(manifest))
            return _modSessionAvailable.GetValueOrDefault(manifest.Id, false);

        if (!_modSessionAvailable.GetValueOrDefault(manifest.Id, false))
            return false;

        return _modDllPaths.TryGetValue(manifest.Id, out var dllPath) && File.Exists(dllPath);
    }

    private static bool ShouldAdvertiseManifest(ModManifest manifest)
    {
        return manifest.GetEffectiveNetworkMode() != ModNetworkModes.LocalOnly;
    }

    private static bool UsesFrameworkAssemblyLoader(ModManifest manifest)
    {
        return !manifest.UsesOfficialLoader() && RequiresAssembly(manifest);
    }

    private static bool RequiresAssembly(ModManifest manifest)
    {
        return !string.IsNullOrWhiteSpace(manifest.AssemblyFile) ||
               manifest.Type == "patch" ||
               manifest.Effects.Patches.Count > 0;
    }

    private static bool VersionsMatch(ModManifest left, ModManifest right)
    {
        return string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
    }

    private static ModManifest CloneManifest(ModManifest manifest)
    {
        return ModNetworkJson.CloneManifest(manifest);
    }

    private static string? TryFindModDll(ModManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.DirectoryPath))
        {
            var fileName = !string.IsNullOrWhiteSpace(manifest.AssemblyFile)
                ? manifest.AssemblyFile
                : $"{manifest.Id}.dll";
            var directPath = Path.Combine(manifest.DirectoryPath, fileName);
            if (File.Exists(directPath)) return directPath;
        }

        var path = Path.Combine(
            ProjectSettings.GlobalizePath("user://mods/"), manifest.Id, $"{manifest.Id}.dll");
        if (File.Exists(path)) return path;

        path = Path.Combine(
            ProjectSettings.GlobalizePath("res://mods/"), manifest.Id, $"{manifest.Id}.dll");
        if (File.Exists(path)) return path;

        var gameDir = Path.GetDirectoryName(OS.GetExecutablePath());
        if (gameDir != null)
        {
            path = Path.Combine(gameDir, "mods", manifest.Id, $"{manifest.Id}.dll");
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private sealed record ActiveVoteRequest(string SourceUserId, ModVoteRequest Request);

    private sealed record PendingVotePrompt(string VoteId, string Title, string Body, Action<bool> OnComplete);

    private string CreateVoteId(ModManifest manifest, string sourceUserId)
    {
        var sequence = Interlocked.Increment(ref _nextVoteSequence);
        return $"{manifest.Id}@{manifest.Version}@{sourceUserId}@{sequence}";
    }

    private static string BuildVoteKey(ModManifest manifest, string sourceUserId)
    {
        return $"{manifest.Id}@{manifest.Version}@{sourceUserId}";
    }

    private void RemoveActiveVoteKey(string voteId)
    {
        if (!_voteKeysByVoteId.Remove(voteId, out var voteKey))
            return;

        _activeVoteKeys.Remove(voteKey);
    }

    private void OnLobbyMemberLeft(string userId)
    {
        ResetPendingVotes($"lobby member left ({userId})");
    }

    private void OnTransportReset()
    {
        ResetPendingVotes("network transport reset");
    }

    private void ResetPendingVotes(string reason)
    {
        if (_activeVoteRequests.Count == 0 &&
            _voteQueue.Count == 0 &&
            _activeVoteId == null)
        {
            return;
        }

        GD.Print($"[ModFramework] Clearing pending mod votes: {reason}");
        _voteSession.ClearAllVotes();
        _activeVoteRequests.Clear();
        _voteKeysByVoteId.Clear();
        _activeVoteKeys.Clear();
        _queuedVoteIds.Clear();
        _processedVoteIds.Clear();
        _voteQueue.Clear();
        _activeVoteId = null;
        _voteUI?.DismissVote();
    }

    // IDisposable: detach the Godot-tree-owned VoteUI control and tear down the
    // network layer. Godot owns the real lifetime of _voteUI (added via AddChild),
    // so we use QueueFree rather than .Dispose() on it. Safe to call multiple times.
    public void Dispose()
    {
        if (_voteUI != null)
        {
            _voteUI.QueueFree();
            _voteUI = null;
        }
        _networkLayer.Dispose();
        GC.SuppressFinalize(this);
    }
}
