using Godot;

namespace PratfallModFramework;

public class ModManager
{
    private readonly ModAssemblyLoader _loader = new();
    private readonly ModNetworkLayer _networkLayer = new();
    private readonly ModVoteSession _voteSession = new();
    private readonly ModP2PTransfer _transfer = new();
    private ModTrustConfig _trust = new();
    private VoteUI? _voteUI;
    private List<ModManifest> _localMods = new();
    private readonly Dictionary<string, bool> _desiredEnabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _modEnabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _modDllPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _modSessionAvailable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _modsWithMountedPck = new(StringComparer.OrdinalIgnoreCase);
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
        _trust = ModTrustConfig.LoadOrDefault();
        GD.Print($"[ModFramework] Trust mode: {_trust.Mode} ({_trust.TrustedSha256.Count} trusted hashes)");
        OfficialModBridge.Install();
        SessionStartHooks.Install(kind =>
        {
            ApplyDesiredModsForSession();
            _networkLayer.NotifySessionStarting(kind);
        });
        _localMods = ManifestManager.ScanLocalMods();
        GD.Print($"[ModFramework] Found {_localMods.Count} local mods");
        var desiredEnabledIds = FrameworkModStateStore.LoadEnabledIds(_localMods);

        foreach (var mod in _localMods)
        {
            mod.Normalize();
            _desiredEnabled[mod.Id] = desiredEnabledIds.Contains(mod.Id);
            _modEnabled[mod.Id] = mod.UsesOfficialLoader() && OfficialModBridge.IsEnabled(mod);
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
            isModEnabled: id => IsModDesiredEnabled(id),
            onToggleMod: (id, enabled) => ToggleMod(id, enabled));

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
        PersistDesiredEnabledState();

        if (!TryGetLocalManifest(id, out var manifest))
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

            try
            {
                _loader.LoadMod(id, dllPath, manifest.AssemblySha256);
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

            try
            {
                _modDllPaths[mod.Id] = dllPath;
                _loader.LoadMod(mod.Id, dllPath, mod.AssemblySha256);
                MountModPckIfAny(mod);
                _modSessionAvailable[mod.Id] = true;
                _modEnabled[mod.Id] = true;
                GD.Print($"[ModFramework] Loaded assembly mod: {mod.Id}");
                if (!_desiredEnabled.GetValueOrDefault(mod.Id, false))
                    DisableMod(mod.Id, broadcast: false);
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
                .Select(entry => entry.Key));
    }

    private void OnPeerManifestReceived(ModPeerSnapshot peerSnapshot)
    {
        if (!_networkLayer.IsLocalHost)
            return;

        foreach (var request in BuildVoteRequestsForPeer(peerSnapshot))
            QueueVoteRequest(request, isHostLocalPrompt: true);
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

        _transfer.BeginSend(requesterUserId, manifest.Id, manifest.Version, dllPath);
    }

    private void OnTransferChunkReceived(string sourceUserId, ModTransferChunk chunk)
    {
        var result = _transfer.OnChunkReceived(sourceUserId, chunk, _trust, out var persistedDllPath);
        if (result != ModP2PTransfer.ReceiveResult.CompletedAndPersisted || persistedDllPath == null)
            return;

        try
        {
            // Re-scan to pick up the newly-arrived manifest + DLL, then enable.
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

            if (TryGetLocalManifest(chunk.ModId, out var manifest) && UsesFrameworkAssemblyLoader(manifest))
            {
                _modDllPaths[manifest.Id] = persistedDllPath;
                _modSessionAvailable[manifest.Id] = true;
                EnableMod(manifest.Id, broadcast: true);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to integrate transferred mod {chunk.ModId}: {ex.Message}");
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
            // auto-enable — the user toggles in the Mods dialog as with any other mod.
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
                enabledLocalMatch = EnableMod(localMatch.Id, broadcast: false);
            else
            {
                _modEnabled[localMatch.Id] = true;
                _modSessionAvailable[localMatch.Id] = true;
                enabledLocalMatch = true;
            }

            if (enabledLocalMatch)
                GD.Print($"[ModFramework] Enabled local match for {localMatch.Id} after vote");
        }

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
        else if (!enabledLocalMatch)
        {
            _networkLayer.RequestModTransfer(result.SourceUserId, result.Manifest.Id, result.Manifest.Version);
        }

        _networkLayer.BroadcastManifest();
        TryShowNextVote();
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
        lines.Add(RequiresAssembly(manifest)
            ? "Enable this host mod for the session?"
            : "Apply this host-backed session mod?");

        return string.Join("\n\n", lines);
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
}
