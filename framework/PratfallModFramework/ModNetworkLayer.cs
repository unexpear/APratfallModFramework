using Godot;

namespace PratfallModFramework;

public sealed class ModNetworkLayer : IDisposable
{
    // Reserve a high private range until the game exposes an official mod event hook.
    // TODO(coord-with-tim): Negotiate an officially-allocated range with the game devs so
    // these IDs don't collide if the base game expands its event ID space.
    private const ushort ManifestSnapshotEventId = 62000;
    private const ushort VoteRequestEventId = 62001;
    private const ushort VoteResponseEventId = 62002;
    private const ushort VoteResultEventId = 62003;
    private const ushort TransferRequestEventId = 62004;
    private const ushort TransferChunkEventId = 62005;
    private const ushort ConfigSyncEventId = 62006;

    private Func<ModLocalState>? _snapshotProvider;
    private Godot.Timer? _pollTimer;
    private bool _isHooked;
    private bool _sessionStarted;
    private SessionKind _sessionKind = SessionKind.Offline;
    private NetworkLobbyManagerBase? _hookedLobbyManager;
    private NetworkEventManager? _hookedEventManager;
    private TransportMode _transportMode;
    private DebugPeerConfig? _debugPeerConfig;

    public event Action<ModPeerSnapshot>? OnManifestReceived;
    public event Action<string, ModVoteRequest>? OnVoteRequestReceived;
    public event Action<string, ModVoteResponse>? OnVoteResponseReceived;
    public event Action<string, ModVoteResult>? OnVoteResultReceived;
    public event Action<string /*requesterUserId*/, ModTransferRequest>? OnTransferRequestReceived;
    public event Action<string /*sourceUserId*/, ModTransferChunk>? OnTransferChunkReceived;
    // CSync: host broadcasts ConfigEntry values for entries marked Description.Synced=true.
    // The string arg is the senderUserId (will be the lobby host's id under normal play).
    public event Action<string /*senderUserId*/, ModConfigSyncSnapshot>? OnConfigSyncReceived;
    public event Action<string>? OnMemberLeftLobby;
    public event Action? OnTransportReset;

    public bool IsNetworkReady => _transportMode == TransportMode.Debug || IsRealNetworkReady();
    public bool IsLocalHost => _transportMode == TransportMode.Debug || TryGetLobbyManager()?.IsLobbyOwner == true;
    public string? LocalUserId => _transportMode == TransportMode.Debug
        ? _debugPeerConfig?.LocalUserId
        : TryGetLobbyManager()?.LocalLobbyMember?.GetUserId();
    public string? LobbyOwnerUserId => _transportMode == TransportMode.Debug
        ? _debugPeerConfig?.LocalUserId
        : TryGetLobbyManager()?.LobbyOwner?.GetUserId();
    public byte LocalMemberIndex => _transportMode == TransportMode.Debug
        ? (byte)0
        : TryGetLobbyManager()?.LocalLobbyMember?.Index ?? 0;
    public int ConnectedPlayerCount => _transportMode == TransportMode.Debug
        ? 2
        : Math.Max(TryGetLobbyManager()?.LobbyMembers?.Count ?? 0, 1);

    public void Initialize(SceneTree tree, Func<ModLocalState> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
        _pollTimer = new Godot.Timer
        {
            Name = "ModFrameworkNetworkPoll",
            WaitTime = 0.5,
            OneShot = false
        };
        _pollTimer.Timeout += PollNetworkState;
        tree.Root.AddChild(_pollTimer);
        _pollTimer.Start();
        PollNetworkState();
    }

    public void Shutdown()
    {
        UnhookTransport();

        if (_pollTimer != null)
        {
            _pollTimer.Timeout -= PollNetworkState;
            _pollTimer.QueueFree();
            _pollTimer = null;
        }

        _snapshotProvider = null;
    }

    // IDisposable contract delegates to Shutdown — the Godot.Timer's true owner is the
    // scene tree (it's added via AddChild and freed via QueueFree). Dispose exists so
    // callers from a standard .NET context (tests, programmatic teardown) have a
    // familiar cleanup entry point. Safe to call multiple times.
    public void Dispose() => Shutdown();

    public void BroadcastManifest()
    {
        if (_transportMode == TransportMode.Debug)
        {
            BroadcastManifestToDebugPeer();
            return;
        }

        if (!_isHooked || _snapshotProvider == null)
            return;

        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId))
            return;

        var localState = _snapshotProvider();
        localState.Normalize();

        var payload = ModManifestSnapshotNetworkEvent.Create(localUserId, LocalMemberIndex, localState);
        SendReliableGlobalEvent(ManifestSnapshotEventId, payload, "ModFramework.ManifestSnapshot");

        GD.Print($"[ModFramework] Broadcast manifest snapshot: {localState.EnabledModIds.Count}/{localState.InstalledManifests.Count} enabled");
    }

    // Broadcast a CSync snapshot to all peers (host-side use). The host calls
    // this on (a) lobby join — full snapshot of all Synced entries, and
    // (b) every Value change on a Synced entry — single-entry delta snapshot.
    // Peers (non-host) calling this is a no-op modulo a warning — only the
    // host's values are authoritative.
    public void BroadcastConfigSync(ModConfigSyncSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Entries.Count == 0) return;

        if (_transportMode == TransportMode.Debug)
        {
            // In debug-peer mode there's no real peer to deliver to. Just log so
            // self-tests can verify the host-side path ran.
            GD.Print($"[ModFramework] CSync broadcast (debug-peer mode, no real delivery): {snapshot.Entries.Count} entries");
            return;
        }

        if (!_isHooked) return;
        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId)) return;

        try
        {
            var payload = ModConfigSyncNetworkEvent.Create(localUserId, LocalMemberIndex, snapshot);
            SendReliableGlobalEvent(ConfigSyncEventId, payload, "ModFramework.ConfigSync");
            GD.Print($"[ModFramework] Broadcast CSync snapshot: {snapshot.Entries.Count} entries");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] CSync broadcast failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void BroadcastVoteRequest(ModVoteRequest request)
    {
        if (_transportMode == TransportMode.Debug)
        {
            SimulateDebugVoteResponse(request);
            return;
        }

        if (!_isHooked)
            return;

        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId))
            return;

        request.Normalize();
        var payload = ModVoteRequestNetworkEvent.Create(localUserId, LocalMemberIndex, request);
        SendReliableGlobalEvent(VoteRequestEventId, payload, "ModFramework.VoteRequest");
    }

    public void SendVoteResponse(ModVoteResponse response)
    {
        if (!_isHooked)
            return;

        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId))
            return;

        response.Normalize();
        var payload = ModVoteResponseNetworkEvent.Create(localUserId, LocalMemberIndex, response);
        SendReliableGlobalEvent(VoteResponseEventId, payload, "ModFramework.VoteResponse");
    }

    public void BroadcastVoteResult(ModVoteResult result)
    {
        if (_transportMode == TransportMode.Debug)
        {
            ApplyDebugVoteResult(result);
            return;
        }

        if (!_isHooked)
            return;

        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId))
            return;

        result.Normalize();
        var payload = ModVoteResultNetworkEvent.Create(localUserId, LocalMemberIndex, result);
        SendReliableGlobalEvent(VoteResultEventId, payload, "ModFramework.VoteResult");
    }

    public void RequestModTransfer(string sourceUserId, string modId, string modVersion)
    {
        if (_transportMode == TransportMode.Debug)
        {
            GD.Print($"[ModFramework] Transfer requested for {modId} from {sourceUserId}, but debug peer mode does not simulate chunked transfer");
            return;
        }

        if (!_isHooked)
            return;
        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId) || string.IsNullOrWhiteSpace(sourceUserId))
            return;

        var request = new ModTransferRequest { ModId = modId, ModVersion = modVersion };
        request.Normalize();
        var payload = ModTransferRequestNetworkEvent.Create(localUserId, LocalMemberIndex, sourceUserId, request);
        SendReliableGlobalEvent(TransferRequestEventId, payload, "ModFramework.TransferRequest");
        GD.Print($"[ModFramework] Requesting transfer of {modId} v{modVersion} from {sourceUserId}");
    }

    public void SendTransferChunk(string targetUserId, ModTransferChunk chunk)
    {
        if (_transportMode != TransportMode.Real || !_isHooked)
            return;
        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId) || string.IsNullOrWhiteSpace(targetUserId))
            return;

        chunk.Normalize();
        var payload = ModTransferChunkNetworkEvent.Create(localUserId, LocalMemberIndex, targetUserId, chunk);
        SendReliableGlobalEvent(TransferChunkEventId, payload, "ModFramework.TransferChunk");
    }

    public void NotifySessionStarting(SessionKind kind)
    {
        _sessionKind = kind;
        if (_sessionStarted)
            return;
        _sessionStarted = true;
        GD.Print($"[ModFramework] Session starting ({kind}); network transport may now attach");
        PollNetworkState();
    }

    private void PollNetworkState()
    {
        if (IsRealNetworkReady())
        {
            if (_transportMode != TransportMode.Real)
            {
                UnhookTransport();
                HookRealNetwork();
            }
            return;
        }

        // Debug peer is ONLY allowed during an explicit offline session — it must never
        // race with real-Steam multiplayer (Host Game). When the host clicks Host Game,
        // the real lobby is forming but `IsRealNetworkReady()` is briefly false; without
        // this guard the debug peer would attach in the gap and fire bogus votes that the
        // real session inherits.
        if (_sessionStarted && _sessionKind == SessionKind.Offline)
        {
            var debugPeerConfig = DebugPeerConfig.TryLoad();
            if (debugPeerConfig != null)
            {
                if (_transportMode != TransportMode.Debug)
                {
                    UnhookTransport();
                    HookDebugTransport(debugPeerConfig);
                }
                return;
            }
        }

        if (_isHooked)
            UnhookTransport();
    }

    private void HookRealNetwork()
    {
        var lobbyManager = TryGetLobbyManager();
        var eventManager = TryGetEventManager();
        if (lobbyManager == null || eventManager == null)
            return;

        _hookedLobbyManager = lobbyManager;
        _hookedEventManager = eventManager;
        _hookedLobbyManager.OnMemberJoined += OnMemberJoined;
        _hookedLobbyManager.OnMemberLeft += OnMemberLeft;
        _hookedEventManager.OnNetworkEventReceived += OnNetworkEventReceived;
        _debugPeerConfig = null;
        _transportMode = TransportMode.Real;
        _isHooked = true;

        GD.Print("[ModFramework] Attached to Pratfall network transport");
        BroadcastManifest();
    }

    private void HookDebugTransport(DebugPeerConfig debugPeerConfig)
    {
        _hookedLobbyManager = null;
        _hookedEventManager = null;
        _debugPeerConfig = debugPeerConfig;
        _transportMode = TransportMode.Debug;
        _isHooked = true;

        GD.Print($"[ModFramework] Attached to local debug peer transport ({ProjectSettings.GlobalizePath(DebugPeerConfig.ConfigPath)})");
        BroadcastManifest();
    }

    private void UnhookTransport()
    {
        var wasHooked = _isHooked;

        if (_hookedLobbyManager != null)
        {
            _hookedLobbyManager.OnMemberJoined -= OnMemberJoined;
            _hookedLobbyManager.OnMemberLeft -= OnMemberLeft;
        }

        if (_hookedEventManager != null)
            _hookedEventManager.OnNetworkEventReceived -= OnNetworkEventReceived;

        _hookedLobbyManager = null;
        _hookedEventManager = null;
        _debugPeerConfig = null;
        _transportMode = TransportMode.None;
        _isHooked = false;

        if (wasHooked)
            OnTransportReset?.Invoke();
    }

    private void OnMemberJoined(INetworkLobbyMember member)
    {
        if (member == null)
            return;

        BroadcastManifest();
    }

    private void OnMemberLeft(INetworkLobbyMember member)
    {
        if (member == null)
            return;

        GD.Print($"[ModFramework] Lobby member left: {member.GetUserId()}");
        OnMemberLeftLobby?.Invoke(member.GetUserId());
    }

    // Asks Pratfall's lobby manager to leave the current lobby. Used when a peer
    // declines an acquisition prompt for a vote-passed mod they don't have — the
    // session can't continue with state divergence, so the framework opts the player
    // out cleanly rather than forcing a download.
    public static bool LeaveLobby()
    {
        var lobby = TryGetLobbyManager();
        if (lobby == null)
        {
            GD.PrintErr("[ModFramework] LeaveLobby: no lobby manager available");
            return false;
        }
        try
        {
            lobby.LeaveLobby();
            GD.Print("[ModFramework] LeaveLobby invoked (declined required mod)");
            return true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] LeaveLobby failed: {ex.Message}");
            return false;
        }
    }

    public static bool IsUserInLobby(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var lobby = TryGetLobbyManager();
        if (lobby?.LobbyMembers == null) return false;
        foreach (var member in lobby.LobbyMembers)
        {
            if (member != null && string.Equals(member.GetUserId(), userId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void OnNetworkEventReceived(ushort eventId, NetworkFrameEvent eventData)
    {
        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId))
            return;

        // Drop any framework event whose claimed sender isn't actually in the current lobby.
        // This stops a stale event (e.g. from a peer that already left) from being applied,
        // and is the cheap layer of peer-auth available without raw transport-level sender ids.
        bool SenderIsLobbyMember(string senderUserId)
        {
            if (string.IsNullOrWhiteSpace(senderUserId)) return false;
            if (string.Equals(senderUserId, localUserId, StringComparison.OrdinalIgnoreCase)) return false;
            return IsUserInLobby(senderUserId);
        }

        switch (eventId)
        {
            case ManifestSnapshotEventId:
                var snapshotEvent = eventData.GetEvent<ModManifestSnapshotNetworkEvent>();
                if (!SenderIsLobbyMember(snapshotEvent.SenderUserId)) return;
                OnManifestReceived?.Invoke(snapshotEvent.ToSnapshot());
                return;

            case VoteRequestEventId:
                var requestEvent = eventData.GetEvent<ModVoteRequestNetworkEvent>();
                if (!SenderIsLobbyMember(requestEvent.SenderUserId)) return;
                OnVoteRequestReceived?.Invoke(requestEvent.SenderUserId, requestEvent.ToRequest());
                return;

            case VoteResponseEventId:
                var responseEvent = eventData.GetEvent<ModVoteResponseNetworkEvent>();
                if (!SenderIsLobbyMember(responseEvent.SenderUserId)) return;
                OnVoteResponseReceived?.Invoke(responseEvent.SenderUserId, responseEvent.ToResponse());
                return;

            case VoteResultEventId:
                var resultEvent = eventData.GetEvent<ModVoteResultNetworkEvent>();
                if (!SenderIsLobbyMember(resultEvent.SenderUserId)) return;
                OnVoteResultReceived?.Invoke(resultEvent.SenderUserId, resultEvent.ToResult());
                return;

            case TransferRequestEventId:
                var transferRequestEvent = eventData.GetEvent<ModTransferRequestNetworkEvent>();
                if (!SenderIsLobbyMember(transferRequestEvent.SenderUserId)) return;
                if (!string.Equals(transferRequestEvent.TargetUserId, localUserId, StringComparison.OrdinalIgnoreCase))
                    return;
                OnTransferRequestReceived?.Invoke(transferRequestEvent.SenderUserId, transferRequestEvent.ToRequest());
                return;

            case TransferChunkEventId:
                var chunkEvent = eventData.GetEvent<ModTransferChunkNetworkEvent>();
                if (!SenderIsLobbyMember(chunkEvent.SenderUserId)) return;
                if (!string.Equals(chunkEvent.TargetUserId, localUserId, StringComparison.OrdinalIgnoreCase))
                    return;
                OnTransferChunkReceived?.Invoke(chunkEvent.SenderUserId, chunkEvent.ToChunk());
                return;

            case ConfigSyncEventId:
                var configSyncEvent = eventData.GetEvent<ModConfigSyncNetworkEvent>();
                if (!SenderIsLobbyMember(configSyncEvent.SenderUserId)) return;
                OnConfigSyncReceived?.Invoke(configSyncEvent.SenderUserId, configSyncEvent.ToSnapshot());
                return;
        }
    }

    private static void SendReliableGlobalEvent<T>(ushort eventId, T payload, string eventName)
        where T : INetworkEvent
    {
        Network.EventManager.SendEvent(eventId, payload, NetworkMessageSendOption.Reliable, eventName);
    }

    private static NetworkLobbyManagerBase? TryGetLobbyManager()
    {
        try
        {
            return Network.LobbyManager;
        }
        catch
        {
            return null;
        }
    }

    private static NetworkEventManager? TryGetEventManager()
    {
        try
        {
            return Network.EventManager;
        }
        catch
        {
            return null;
        }
    }

    private void BroadcastManifestToDebugPeer()
    {
        if (!_isHooked || _snapshotProvider == null || _debugPeerConfig == null)
            return;

        var localState = _snapshotProvider();
        localState.Normalize();

        var peerSnapshot = _debugPeerConfig.CreatePeerSnapshot(localState);
        OnManifestReceived?.Invoke(peerSnapshot);

        GD.Print($"[ModFramework] Broadcast manifest snapshot to debug peer: {peerSnapshot.EnabledModIds.Count}/{peerSnapshot.InstalledManifests.Count} enabled");
    }

    private void SimulateDebugVoteResponse(ModVoteRequest request)
    {
        if (!_isHooked || _debugPeerConfig == null)
            return;

        request.Normalize();
        var localUserId = LocalUserId;
        if (string.IsNullOrWhiteSpace(localUserId))
            return;

        var response = new ModVoteResponse
        {
            VoteId = request.VoteId,
            TargetUserId = localUserId,
            VoteYes = _debugPeerConfig.ResolveVote(request.Manifest.Id)
        };
        response.Normalize();
        OnVoteResponseReceived?.Invoke(_debugPeerConfig.PeerUserId, response);
    }

    private void ApplyDebugVoteResult(ModVoteResult result)
    {
        if (!_isHooked || _snapshotProvider == null || _debugPeerConfig == null)
            return;

        var localState = _snapshotProvider();
        localState.Normalize();
        _debugPeerConfig.ApplyApprovedResult(result, localState);
    }

    private static bool IsRealNetworkReady()
    {
        return TryGetLobbyManager() is { IsInLobby: true, IsSingleplayerLobby: false };
    }

    private enum TransportMode
    {
        None,
        Real,
        Debug
    }
}
