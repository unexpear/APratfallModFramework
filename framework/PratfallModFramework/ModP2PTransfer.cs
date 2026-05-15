using System.Security.Cryptography;
using Godot;

namespace PratfallModFramework;

// Orchestrates chunked DLL transfer between peers over the framework network layer.
// Per active transfer we keep a small state struct; sender pre-chunks the file and emits
// one chunk per Tick(), receiver appends incoming chunks into a buffer and verifies the
// SHA-256 supplied in the final chunk before persisting to user://mods/<id>/<id>.dll.
internal sealed class ModP2PTransfer
{
    // Pratfall's ByteBufferWriter.Write(string) hard-caps strings at 32768 bytes (ushort
    // length prefix); past that the receiver silently substitutes the default ("{}") and
    // the transfer stalls forever. Raw 14 KB -> ~19 KB base64 -> ~20 KB JSON envelope,
    // leaving ~12 KB of headroom for the SHA-256 + JSON keys on the final chunk.
    private const int ChunkSize = 14 * 1024;
    private const int MaxJsonEnvelopeBytes = 30 * 1024; // sanity-check guard on the wire payload
    private const int MaxModBytes = 20 * 1024 * 1024;

    private readonly Dictionary<string, OutgoingTransfer> _outgoing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IncomingTransfer> _incoming = new(StringComparer.OrdinalIgnoreCase);
    // Round-robin order of active outgoing transfer keys. We advance through this each
    // TickOutgoing call so a 5 MB transfer can't starve a sibling 100 KB transfer.
    private readonly List<string> _outgoingOrder = new();
    private int _outgoingCursor;

    public event Action<string /*modId*/, float /*0..1*/>? OnSendProgress;
    public event Action<string /*modId*/, float /*0..1*/>? OnReceiveProgress;

    public bool HasOutgoing(string targetUserId, string modId) =>
        _outgoing.ContainsKey(BuildKey(targetUserId, modId));

    // Begin sending a mod to a target peer. Returns false if the file is missing or oversized.
    public bool BeginSend(string targetUserId, string modId, string modVersion, string dllPath)
    {
        if (string.IsNullOrWhiteSpace(targetUserId) || string.IsNullOrWhiteSpace(modId) || !File.Exists(dllPath))
            return false;

        byte[] bytes;
        try
        {
            var info = new FileInfo(dllPath);
            if (info.Length > MaxModBytes)
            {
                GD.PrintErr($"[ModFramework] Refusing to send {modId}: {info.Length} bytes exceeds {MaxModBytes} byte cap");
                return false;
            }
            bytes = File.ReadAllBytes(dllPath);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to read {dllPath} for transfer: {ex.Message}");
            return false;
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var totalChunks = Math.Max(1, (bytes.Length + ChunkSize - 1) / ChunkSize);
        var key = BuildKey(targetUserId, modId);
        var isNew = !_outgoing.ContainsKey(key);
        _outgoing[key] = new OutgoingTransfer(targetUserId, modId, modVersion, bytes, hash, totalChunks);
        if (isNew) _outgoingOrder.Add(key);
        GD.Print($"[ModFramework] Transfer started: send {modId} v{modVersion} -> {targetUserId} ({bytes.Length} bytes, {totalChunks} chunks)");
        return true;
    }

    public readonly struct PendingChunk
    {
        public PendingChunk(string targetUserId, ModTransferChunk chunk) { TargetUserId = targetUserId; Chunk = chunk; }
        public string TargetUserId { get; }
        public ModTransferChunk Chunk { get; }
    }

    // Drain one outgoing transfer step. Returns the next (target, chunk) pair to send, or
    // null if nothing is pending right now. Caller invokes this in a loop until null.
    //
    // Round-robin scheduling: each call advances `_outgoingCursor` past the previously
    // served transfer so concurrent transfers interleave 1-chunk-at-a-time. Without this,
    // a 5 MB transfer would fully drain before any sibling 100 KB transfer began.
    public PendingChunk? TickOutgoing()
    {
        if (_outgoingOrder.Count == 0) return null;

        for (var probe = 0; probe < _outgoingOrder.Count; probe++)
        {
            var idx = (_outgoingCursor + probe) % _outgoingOrder.Count;
            var key = _outgoingOrder[idx];
            if (!_outgoing.TryGetValue(key, out var t) || t.NextChunkIndex >= t.TotalChunks)
            {
                _outgoing.Remove(key);
                _outgoingOrder.RemoveAt(idx);
                if (_outgoingOrder.Count == 0) return null;
                if (idx <= _outgoingCursor && _outgoingCursor > 0) _outgoingCursor--;
                probe--; // re-probe at the same logical position now that we shifted
                continue;
            }

            var offset = t.NextChunkIndex * ChunkSize;
            var len = Math.Min(ChunkSize, t.Bytes.Length - offset);
            var slice = new byte[len];
            Array.Copy(t.Bytes, offset, slice, 0, len);

            var chunk = new ModTransferChunk
            {
                ModId = t.ModId,
                ModVersion = t.ModVersion,
                ChunkIndex = t.NextChunkIndex,
                TotalChunks = t.TotalChunks,
                TotalBytes = t.Bytes.Length,
                ChunkBase64 = Convert.ToBase64String(slice),
                IsLast = t.NextChunkIndex == t.TotalChunks - 1,
                Sha256Hex = (t.NextChunkIndex == t.TotalChunks - 1) ? t.Sha256Hex : "",
            };

            t.NextChunkIndex++;
            OnSendProgress?.Invoke(t.ModId, (float)t.NextChunkIndex / t.TotalChunks);

            // Advance cursor so the NEXT TickOutgoing serves a different transfer if any.
            _outgoingCursor = (idx + 1) % Math.Max(_outgoingOrder.Count, 1);

            if (t.NextChunkIndex >= t.TotalChunks)
            {
                _outgoing.Remove(key);
                _outgoingOrder.RemoveAt(idx);
                if (_outgoingOrder.Count == 0) _outgoingCursor = 0;
                else if (idx < _outgoingCursor) _outgoingCursor--;
                else if (_outgoingCursor >= _outgoingOrder.Count) _outgoingCursor = 0;
            }
            return new PendingChunk(t.TargetUserId, chunk);
        }
        return null;
    }

    public enum ReceiveResult
    {
        Continue,
        CompletedAndPersisted,
        CompletedAndQuarantined,
        FailedSizeExceeded,
        FailedHashMismatch,
        FailedWriteError,
        FailedDecodeError,
    }

    // Receive a chunk; when ALL chunks arrive (in any order, with possible duplicates)
    // we assemble in index order, verify SHA-256, consult the trust policy, and either
    // persist to user://mods/<id>/<id>.dll (open mode / trusted hash) or to
    // user://mods-quarantine/<id>/<id>.dll (trusted-only mode with unknown hash).
    // Caller re-scans local mods only on CompletedAndPersisted.
    //
    // Order/duplicate handling: Pratfall's `Reliable` send mode guarantees delivery but
    // not strict in-order delivery across flushes. We bucket chunks by ChunkIndex and
    // ignore duplicates so a re-sent or reordered chunk doesn't corrupt the assembly.
    public ReceiveResult OnChunkReceived(string sourceUserId, ModTransferChunk chunk, ModTrustConfig trust, out string? persistedDllPath)
    {
        persistedDllPath = null;
        chunk.Normalize();
        var key = BuildKey(sourceUserId, chunk.ModId);

        if (!_incoming.TryGetValue(key, out var t))
        {
            if (chunk.TotalBytes > MaxModBytes)
            {
                GD.PrintErr($"[ModFramework] Reject incoming {chunk.ModId}: declared size {chunk.TotalBytes} exceeds {MaxModBytes} cap");
                return ReceiveResult.FailedSizeExceeded;
            }
            if (chunk.TotalChunks <= 0)
            {
                GD.PrintErr($"[ModFramework] Reject incoming {chunk.ModId}: invalid TotalChunks={chunk.TotalChunks}");
                return ReceiveResult.FailedDecodeError;
            }
            t = new IncomingTransfer(sourceUserId, chunk.ModId, chunk.ModVersion, chunk.TotalBytes, chunk.TotalChunks);
            _incoming[key] = t;
        }

        // Validate index against the agreed-upon shape. A peer that sends contradictory
        // metadata across chunks is misbehaving — drop the whole transfer.
        if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= t.TotalChunks ||
            chunk.TotalChunks != t.TotalChunks || chunk.TotalBytes != t.TotalBytes)
        {
            GD.PrintErr($"[ModFramework] Reject {chunk.ModId} chunk: bad index/shape (idx={chunk.ChunkIndex}, total={chunk.TotalChunks}/{t.TotalChunks}, bytes={chunk.TotalBytes}/{t.TotalBytes})");
            _incoming.Remove(key);
            return ReceiveResult.FailedDecodeError;
        }

        // Duplicate chunk: ignore. Idempotent — protects against retransmits.
        if (t.ChunksByIndex[chunk.ChunkIndex] != null)
            return ReceiveResult.Continue;

        byte[] decoded;
        try { decoded = Convert.FromBase64String(chunk.ChunkBase64); }
        catch (FormatException) { _incoming.Remove(key); return ReceiveResult.FailedDecodeError; }

        if (t.ReceivedBytes + decoded.Length > MaxModBytes)
        {
            _incoming.Remove(key);
            return ReceiveResult.FailedSizeExceeded;
        }

        t.ChunksByIndex[chunk.ChunkIndex] = decoded;
        t.ReceivedBytes += decoded.Length;
        t.ReceivedChunks++;

        // The last chunk is the only one carrying the full-payload SHA-256. Remember it
        // even if it arrived early (out-of-order delivery is allowed).
        if (chunk.IsLast || !string.IsNullOrEmpty(chunk.Sha256Hex))
            t.FinalSha256Hex = chunk.Sha256Hex ?? "";

        OnReceiveProgress?.Invoke(chunk.ModId, t.TotalBytes == 0 ? 1f : Math.Clamp((float)t.ReceivedBytes / t.TotalBytes, 0f, 1f));

        // Wait until every chunk has arrived AND we've seen the trailer with the hash.
        if (t.ReceivedChunks < t.TotalChunks || string.IsNullOrEmpty(t.FinalSha256Hex))
            return ReceiveResult.Continue;

        // Reassemble in index order.
        var totalLen = 0;
        for (var i = 0; i < t.TotalChunks; i++) totalLen += t.ChunksByIndex[i]!.Length;
        var bytes = new byte[totalLen];
        var offset = 0;
        for (var i = 0; i < t.TotalChunks; i++)
        {
            var part = t.ChunksByIndex[i]!;
            Buffer.BlockCopy(part, 0, bytes, offset, part.Length);
            offset += part.Length;
        }

        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, t.FinalSha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            GD.PrintErr($"[ModFramework] Hash mismatch for {chunk.ModId} from {sourceUserId}: expected {t.FinalSha256Hex} got {actualHash}");
            _incoming.Remove(key);
            return ReceiveResult.FailedHashMismatch;
        }

        var quarantine = trust.IsTrustedOnly && !trust.IsHashTrusted(actualHash);
        var rootDir = quarantine ? "user://mods-quarantine" : "user://mods";

        try
        {
            var modDir = ProjectSettings.GlobalizePath($"{rootDir}/{chunk.ModId}/");
            Directory.CreateDirectory(modDir);
            persistedDllPath = Path.Combine(modDir, $"{chunk.ModId}.dll");
            File.WriteAllBytes(persistedDllPath, bytes);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to persist transferred mod {chunk.ModId}: {ex.Message}");
            _incoming.Remove(key);
            return ReceiveResult.FailedWriteError;
        }

        if (quarantine)
            GD.Print($"[ModFramework] Transfer quarantined: {chunk.ModId} v{chunk.ModVersion} sha256={actualHash[..16]}... -> {rootDir}/. Add the hash to {ModTrustConfig.ConfigPath} to trust.");
        else
            GD.Print($"[ModFramework] Transfer complete: received {chunk.ModId} v{chunk.ModVersion} ({bytes.Length} bytes, sha256={actualHash[..16]}...)");

        _incoming.Remove(key);
        return quarantine ? ReceiveResult.CompletedAndQuarantined : ReceiveResult.CompletedAndPersisted;
    }

    public void Reset()
    {
        _outgoing.Clear();
        _outgoingOrder.Clear();
        _outgoingCursor = 0;
        _incoming.Clear();
    }

    private static string BuildKey(string userId, string modId) => $"{userId}|{modId}";

    private sealed class OutgoingTransfer
    {
        public string TargetUserId { get; }
        public string ModId { get; }
        public string ModVersion { get; }
        public byte[] Bytes { get; }
        public string Sha256Hex { get; }
        public int TotalChunks { get; }
        public int NextChunkIndex;

        public OutgoingTransfer(string targetUserId, string modId, string modVersion, byte[] bytes, string sha256Hex, int totalChunks)
        {
            TargetUserId = targetUserId;
            ModId = modId;
            ModVersion = modVersion;
            Bytes = bytes;
            Sha256Hex = sha256Hex;
            TotalChunks = totalChunks;
            NextChunkIndex = 0;
        }
    }

    private sealed class IncomingTransfer
    {
        public string SourceUserId { get; }
        public string ModId { get; }
        public string ModVersion { get; }
        public int TotalBytes { get; }
        public int TotalChunks { get; }
        // ChunksByIndex[i] is null until chunk i has been received. Bucketing by index
        // makes reassembly order-independent and turns duplicate chunks into a no-op.
        public byte[]?[] ChunksByIndex;
        public int ReceivedChunks;
        public int ReceivedBytes;
        public string FinalSha256Hex = "";

        public IncomingTransfer(string sourceUserId, string modId, string modVersion, int totalBytes, int totalChunks)
        {
            SourceUserId = sourceUserId;
            ModId = modId;
            ModVersion = modVersion;
            TotalBytes = totalBytes;
            TotalChunks = totalChunks;
            ChunksByIndex = new byte[totalChunks][];
        }
    }
}
