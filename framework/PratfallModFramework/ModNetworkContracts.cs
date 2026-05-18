using System.Text.Json;

namespace PratfallModFramework;

public sealed class ModLocalState
{
    public List<ModManifest> InstalledManifests { get; set; } = new();
    public List<string> EnabledModIds { get; set; } = new();

    public void Normalize()
    {
        InstalledManifests ??= new List<ModManifest>();
        EnabledModIds ??= new List<string>();

        foreach (var manifest in InstalledManifests)
            manifest.Normalize();

        EnabledModIds = ModManifestJson.NormalizeIdentifiers(EnabledModIds);
    }
}

public sealed class ModPeerSnapshot
{
    public string UserId { get; set; } = "";
    public byte MemberIndex { get; set; }
    public List<ModManifest> InstalledManifests { get; set; } = new();
    public List<string> EnabledModIds { get; set; } = new();

    public void Normalize()
    {
        UserId = UserId.Trim();
        InstalledManifests ??= new List<ModManifest>();
        EnabledModIds ??= new List<string>();

        foreach (var manifest in InstalledManifests)
            manifest.Normalize();

        EnabledModIds = ModManifestJson.NormalizeIdentifiers(EnabledModIds);
    }

    public IReadOnlyDictionary<string, ModManifest> GetInstalledManifestMap()
    {
        return InstalledManifests
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, ModManifest> GetEnabledManifestMap()
    {
        var installedById = GetInstalledManifestMap();
        var enabled = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);

        foreach (var modId in EnabledModIds)
        {
            if (installedById.TryGetValue(modId, out var manifest))
                enabled[modId] = manifest;
        }

        return enabled;
    }
}

public sealed class ModVoteRequest
{
    public string VoteId { get; set; } = "";
    public string SourceUserId { get; set; } = "";
    public string EffectiveMode { get; set; } = ModNetworkModes.Auto;
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public int ExpectedVotes { get; set; } = 1;
    public ModManifest Manifest { get; set; } = new();

    public void Normalize()
    {
        VoteId = VoteId.Trim();
        SourceUserId = SourceUserId.Trim();
        EffectiveMode = ModNetworkModes.Normalize(EffectiveMode);
        Title = Title.Trim();
        Body = Body.Trim();
        ExpectedVotes = Math.Max(ExpectedVotes, 1);
        Manifest ??= new ModManifest();
        Manifest.Normalize();
    }
}

public sealed class ModVoteResponse
{
    public string VoteId { get; set; } = "";
    public string TargetUserId { get; set; } = "";
    public bool VoteYes { get; set; }

    public void Normalize()
    {
        VoteId = VoteId.Trim();
        TargetUserId = TargetUserId.Trim();
    }
}

public sealed class ModVoteResult
{
    public string VoteId { get; set; } = "";
    public string SourceUserId { get; set; } = "";
    public string EffectiveMode { get; set; } = ModNetworkModes.Auto;
    public bool Passed { get; set; }
    public ModManifest Manifest { get; set; } = new();

    public void Normalize()
    {
        VoteId = VoteId.Trim();
        SourceUserId = SourceUserId.Trim();
        EffectiveMode = ModNetworkModes.Normalize(EffectiveMode);
        Manifest ??= new ModManifest();
        Manifest.Normalize();
    }
}

public sealed class ModTransferRequest
{
    public string ModId { get; set; } = "";
    public string ModVersion { get; set; } = "";

    public void Normalize()
    {
        ModId = ModId.Trim();
        ModVersion = ModVersion.Trim();
    }
}

public sealed class ModTransferChunk
{
    public string ModId { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public int TotalBytes { get; set; }
    public string ChunkBase64 { get; set; } = "";
    // Sha256 of the FULL reassembled payload; populated on last chunk only.
    public string Sha256Hex { get; set; } = "";
    // True when this is the last chunk (also implies Sha256Hex is set).
    public bool IsLast { get; set; }
    // File this chunk belongs to within the mod folder. Default ".dll" for back-compat
    // with v1.0 chunks. Recognized values: ".dll", ".pck". Manifests use a separate
    // path (TransferRequest.ManifestJson) since they're tiny.
    public string FileSuffix { get; set; } = ".dll";

    public void Normalize()
    {
        ModId = ModId.Trim();
        ModVersion = ModVersion.Trim();
        ChunkBase64 ??= "";
        Sha256Hex = (Sha256Hex ?? "").Trim();
        FileSuffix = string.IsNullOrWhiteSpace(FileSuffix) ? ".dll" : FileSuffix.Trim();
        if (ChunkIndex < 0) ChunkIndex = 0;
        if (TotalChunks < 1) TotalChunks = 1;
        if (TotalBytes < 0) TotalBytes = 0;
    }
}

public sealed class ModManifestSnapshotNetworkEvent : INetworkEvent
{
    public string SenderUserId { get; set; } = "";
    public byte SenderIndex { get; set; }
    public string SnapshotJson { get; set; } = "{}";

    public static ModManifestSnapshotNetworkEvent Create(string senderUserId, byte senderIndex, ModLocalState state)
    {
        state.Normalize();
        return new ModManifestSnapshotNetworkEvent
        {
            SenderUserId = senderUserId,
            SenderIndex = senderIndex,
            SnapshotJson = JsonSerializer.Serialize(new SnapshotEnvelope
            {
                InstalledManifests = state.InstalledManifests,
                EnabledModIds = state.EnabledModIds
            }, ModNetworkJson.Options)
        };
    }

    public ModPeerSnapshot ToSnapshot()
    {
        var envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(SnapshotJson, ModNetworkJson.Options) ?? new SnapshotEnvelope();
        var snapshot = new ModPeerSnapshot
        {
            UserId = SenderUserId,
            MemberIndex = SenderIndex,
            InstalledManifests = envelope.InstalledManifests ?? new List<ModManifest>(),
            EnabledModIds = envelope.EnabledModIds ?? new List<string>()
        };
        snapshot.Normalize();
        return snapshot;
    }

    public void Serialize(ByteBufferWriter writer)
    {
        writer.Write(SenderUserId);
        writer.Write(SenderIndex);
        writer.Write(SnapshotJson);
    }

    public void Deserialize(ByteBufferReader reader)
    {
        SenderUserId = reader.ReadString("");
        SenderIndex = reader.ReadByte();
        SnapshotJson = reader.ReadString("{}");
    }
}

public sealed class ModVoteRequestNetworkEvent : INetworkEvent
{
    public string SenderUserId { get; set; } = "";
    public byte SenderIndex { get; set; }
    public string RequestJson { get; set; } = "{}";

    public static ModVoteRequestNetworkEvent Create(string senderUserId, byte senderIndex, ModVoteRequest request)
    {
        request.Normalize();
        return new ModVoteRequestNetworkEvent
        {
            SenderUserId = senderUserId,
            SenderIndex = senderIndex,
            RequestJson = JsonSerializer.Serialize(request, ModNetworkJson.Options)
        };
    }

    public ModVoteRequest ToRequest()
    {
        var request = JsonSerializer.Deserialize<ModVoteRequest>(RequestJson, ModNetworkJson.Options) ?? new ModVoteRequest();
        request.Normalize();
        return request;
    }

    public void Serialize(ByteBufferWriter writer)
    {
        writer.Write(SenderUserId);
        writer.Write(SenderIndex);
        writer.Write(RequestJson);
    }

    public void Deserialize(ByteBufferReader reader)
    {
        SenderUserId = reader.ReadString("");
        SenderIndex = reader.ReadByte();
        RequestJson = reader.ReadString("{}");
    }
}

public sealed class ModVoteResponseNetworkEvent : INetworkEvent
{
    public string SenderUserId { get; set; } = "";
    public byte SenderIndex { get; set; }
    public string ResponseJson { get; set; } = "{}";

    public static ModVoteResponseNetworkEvent Create(string senderUserId, byte senderIndex, ModVoteResponse response)
    {
        response.Normalize();
        return new ModVoteResponseNetworkEvent
        {
            SenderUserId = senderUserId,
            SenderIndex = senderIndex,
            ResponseJson = JsonSerializer.Serialize(response, ModNetworkJson.Options)
        };
    }

    public ModVoteResponse ToResponse()
    {
        var response = JsonSerializer.Deserialize<ModVoteResponse>(ResponseJson, ModNetworkJson.Options) ?? new ModVoteResponse();
        response.Normalize();
        return response;
    }

    public void Serialize(ByteBufferWriter writer)
    {
        writer.Write(SenderUserId);
        writer.Write(SenderIndex);
        writer.Write(ResponseJson);
    }

    public void Deserialize(ByteBufferReader reader)
    {
        SenderUserId = reader.ReadString("");
        SenderIndex = reader.ReadByte();
        ResponseJson = reader.ReadString("{}");
    }
}

public sealed class ModVoteResultNetworkEvent : INetworkEvent
{
    public string SenderUserId { get; set; } = "";
    public byte SenderIndex { get; set; }
    public string ResultJson { get; set; } = "{}";

    public static ModVoteResultNetworkEvent Create(string senderUserId, byte senderIndex, ModVoteResult result)
    {
        result.Normalize();
        return new ModVoteResultNetworkEvent
        {
            SenderUserId = senderUserId,
            SenderIndex = senderIndex,
            ResultJson = JsonSerializer.Serialize(result, ModNetworkJson.Options)
        };
    }

    public ModVoteResult ToResult()
    {
        var result = JsonSerializer.Deserialize<ModVoteResult>(ResultJson, ModNetworkJson.Options) ?? new ModVoteResult();
        result.Normalize();
        return result;
    }

    public void Serialize(ByteBufferWriter writer)
    {
        writer.Write(SenderUserId);
        writer.Write(SenderIndex);
        writer.Write(ResultJson);
    }

    public void Deserialize(ByteBufferReader reader)
    {
        SenderUserId = reader.ReadString("");
        SenderIndex = reader.ReadByte();
        ResultJson = reader.ReadString("{}");
    }
}

public sealed class ModTransferRequestNetworkEvent : INetworkEvent
{
    public string SenderUserId { get; set; } = "";
    public byte SenderIndex { get; set; }
    public string TargetUserId { get; set; } = "";
    public string RequestJson { get; set; } = "{}";

    public static ModTransferRequestNetworkEvent Create(string senderUserId, byte senderIndex, string targetUserId, ModTransferRequest request)
    {
        request.Normalize();
        return new ModTransferRequestNetworkEvent
        {
            SenderUserId = senderUserId,
            SenderIndex = senderIndex,
            TargetUserId = targetUserId,
            RequestJson = JsonSerializer.Serialize(request, ModNetworkJson.Options)
        };
    }

    public ModTransferRequest ToRequest()
    {
        var request = JsonSerializer.Deserialize<ModTransferRequest>(RequestJson, ModNetworkJson.Options) ?? new ModTransferRequest();
        request.Normalize();
        return request;
    }

    public void Serialize(ByteBufferWriter writer)
    {
        writer.Write(SenderUserId);
        writer.Write(SenderIndex);
        writer.Write(TargetUserId);
        writer.Write(RequestJson);
    }

    public void Deserialize(ByteBufferReader reader)
    {
        SenderUserId = reader.ReadString("");
        SenderIndex = reader.ReadByte();
        TargetUserId = reader.ReadString("");
        RequestJson = reader.ReadString("{}");
    }
}

public sealed class ModTransferChunkNetworkEvent : INetworkEvent
{
    public string SenderUserId { get; set; } = "";
    public byte SenderIndex { get; set; }
    public string TargetUserId { get; set; } = "";
    public string ChunkJson { get; set; } = "{}";

    // Pratfall's ByteBufferWriter caps string fields at 32768 bytes (ushort length). If we
    // exceed that, the receiver silently substitutes the default value and the transfer
    // stalls. Throw at the sender instead so the regression is loud, not silent.
    private const int MaxSerializedJsonBytes = 32700;

    public static ModTransferChunkNetworkEvent Create(string senderUserId, byte senderIndex, string targetUserId, ModTransferChunk chunk)
    {
        chunk.Normalize();
        var json = JsonSerializer.Serialize(chunk, ModNetworkJson.Options);
        var byteLen = System.Text.Encoding.UTF8.GetByteCount(json);
        if (byteLen > MaxSerializedJsonBytes)
            throw new InvalidOperationException(
                $"Chunk JSON is {byteLen} bytes; exceeds Pratfall ByteBufferWriter limit of 32768. Lower ChunkSize in ModP2PTransfer.");
        return new ModTransferChunkNetworkEvent
        {
            SenderUserId = senderUserId,
            SenderIndex = senderIndex,
            TargetUserId = targetUserId,
            ChunkJson = json
        };
    }

    public ModTransferChunk ToChunk()
    {
        var chunk = JsonSerializer.Deserialize<ModTransferChunk>(ChunkJson, ModNetworkJson.Options) ?? new ModTransferChunk();
        chunk.Normalize();
        return chunk;
    }

    public void Serialize(ByteBufferWriter writer)
    {
        writer.Write(SenderUserId);
        writer.Write(SenderIndex);
        writer.Write(TargetUserId);
        writer.Write(ChunkJson);
    }

    public void Deserialize(ByteBufferReader reader)
    {
        SenderUserId = reader.ReadString("");
        SenderIndex = reader.ReadByte();
        TargetUserId = reader.ReadString("");
        ChunkJson = reader.ReadString("{}");
    }
}

internal static class ModNetworkJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ModManifest CloneManifest(ModManifest manifest)
    {
        var clone = JsonSerializer.Deserialize<ModManifest>(
            JsonSerializer.Serialize(manifest, Options),
            Options) ?? new ModManifest();
        clone.Normalize();
        return clone;
    }
}

internal sealed class SnapshotEnvelope
{
    public List<ModManifest>? InstalledManifests { get; set; }
    public List<string>? EnabledModIds { get; set; }
}

// CSync — host-config-pushed-to-clients wire format. Carries one or more
// ConfigEntry value bindings the host wants peers to apply. Value is encoded
// as (TypeName, StringValue) discriminator pair so the receiver can deserialize
// to the right target type even without compile-time knowledge of T.
public sealed class ModConfigSyncEntry
{
    public string ModId { get; set; } = "";
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    // Type discriminator: "bool", "int", "long", "float", "double", "string", "enum".
    // For enums, the StringValue is the enum-name string; receiver looks up the target
    // ConfigEntry to discover the actual enum type at apply time.
    public string TypeName { get; set; } = "";
    // String-encoded value. Bool serializes as "True"/"False" via bool.ToString;
    // numerics use InvariantCulture; strings pass through; enums use Enum.GetName.
    public string StringValue { get; set; } = "";
}

public sealed class ModConfigSyncSnapshot
{
    public List<ModConfigSyncEntry> Entries { get; set; } = new();
}

public sealed class ModConfigSyncNetworkEvent : INetworkEvent
{
    public string SenderUserId { get; set; } = "";
    public byte SenderIndex { get; set; }
    public string SnapshotJson { get; set; } = "{}";

    // Pratfall's ByteBufferWriter caps strings at 32768 bytes. Snapshots that
    // exceed this need to be split into batches by the sender (caller's job;
    // a typical mod has a handful of synced entries so this won't fire in practice).
    private const int MaxSerializedJsonBytes = 32700;

    public static ModConfigSyncNetworkEvent Create(string senderUserId, byte senderIndex, ModConfigSyncSnapshot snapshot)
    {
        var json = JsonSerializer.Serialize(snapshot, ModNetworkJson.Options);
        var byteLen = System.Text.Encoding.UTF8.GetByteCount(json);
        if (byteLen > MaxSerializedJsonBytes)
            throw new InvalidOperationException(
                $"ModConfigSync snapshot JSON is {byteLen} bytes; exceeds Pratfall ByteBufferWriter limit of 32768. Split the snapshot or trim synced entries.");
        return new ModConfigSyncNetworkEvent
        {
            SenderUserId = senderUserId,
            SenderIndex = senderIndex,
            SnapshotJson = json
        };
    }

    public ModConfigSyncSnapshot ToSnapshot()
    {
        return JsonSerializer.Deserialize<ModConfigSyncSnapshot>(SnapshotJson, ModNetworkJson.Options)
               ?? new ModConfigSyncSnapshot();
    }

    public void Serialize(ByteBufferWriter writer)
    {
        writer.Write(SenderUserId);
        writer.Write(SenderIndex);
        writer.Write(SnapshotJson);
    }

    public void Deserialize(ByteBufferReader reader)
    {
        SenderUserId = reader.ReadString("");
        SenderIndex = reader.ReadByte();
        SnapshotJson = reader.ReadString("{}");
    }
}
