using System.Security.Cryptography;
using Godot;

namespace PratfallModFramework;

// Public test surface for stress / smoke mods. Each method exercises a single framework
// code path in-process — no real network, no second peer needed. Bounded by design so
// nobody can accidentally torch a machine: no infinite loops, conservative byte caps,
// synchronous execution. Intended for `tmp/stress-mods/` consumers, but stable enough to
// stay in the public API.
public static class ModFrameworkSelfTest
{
    public sealed class TransferLoopbackResult
    {
        public bool Success;
        public string ErrorMessage = "";
        public int InputBytes;
        public int OutputBytes;
        public int ChunkCount;
        public string ExpectedSha256 = "";
        public string ActualSha256 = "";
        public string PersistedPath = "";
        public override string ToString() =>
            $"loopback success={Success} bytes={InputBytes}->{OutputBytes} chunks={ChunkCount} sha256={(string.IsNullOrEmpty(ActualSha256) ? "?" : ActualSha256[..16])}... err={ErrorMessage}";
    }

    // Drives the real chunker + reassembler + hash check + disk write on a source DLL,
    // start to finish. The receiver lands in user://mods/<modId>/<modId>.dll just like a
    // real transfer would.
    public static TransferLoopbackResult RunTransferLoopback(string modId, string modVersion, string sourceDllPath)
    {
        var result = new TransferLoopbackResult();

        if (!File.Exists(sourceDllPath))
        {
            result.ErrorMessage = $"source dll missing: {sourceDllPath}";
            return result;
        }

        var sourceBytes = File.ReadAllBytes(sourceDllPath);
        result.InputBytes = sourceBytes.Length;
        result.ExpectedSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes));

        var transfer = new ModP2PTransfer();
        var trust = new ModTrustConfig(); // open mode
        if (!transfer.BeginSend(targetUserId: "self-test-target", modId, modVersion, sourceDllPath))
        {
            result.ErrorMessage = "BeginSend returned false";
            return result;
        }

        const int maxIterations = 4096;
        for (var i = 0; i < maxIterations; i++)
        {
            var pending = transfer.TickOutgoing();
            if (pending == null)
            {
                result.ErrorMessage = "no chunk produced before completion";
                return result;
            }
            result.ChunkCount++;
            var rx = transfer.OnChunkReceived("self-test-source", pending.Value.Chunk, trust, out var persistedPath);
            if (rx == ModP2PTransfer.ReceiveResult.CompletedAndPersisted)
            {
                result.PersistedPath = persistedPath ?? "";
                result.OutputBytes = File.Exists(result.PersistedPath) ? (int)new FileInfo(result.PersistedPath).Length : 0;
                if (File.Exists(result.PersistedPath))
                    result.ActualSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.PersistedPath)));
                result.Success = string.Equals(result.ExpectedSha256, result.ActualSha256, StringComparison.OrdinalIgnoreCase);
                if (!result.Success)
                    result.ErrorMessage = "hash mismatch after roundtrip";
                return result;
            }
            if (rx != ModP2PTransfer.ReceiveResult.Continue)
            {
                result.ErrorMessage = $"receive failed: {rx}";
                return result;
            }
        }
        result.ErrorMessage = $"exceeded {maxIterations} chunk iterations without completion";
        return result;
    }

    // Verifies that the chunker rejects a tampered payload. Sends a small in-memory file
    // through the transfer pipeline, flips a byte in the middle chunk, and confirms the
    // receiver returns FailedHashMismatch instead of persisting.
    public static bool VerifyHashMismatchRejection()
    {
        var sourceBytes = new byte[64 * 1024]; // 64 KB -> ~5 chunks at the 14 KB chunk size
        new Random(0xBADBEEF).NextBytes(sourceBytes);

        var tempDir = ProjectSettings.GlobalizePath("user://stress-tmp");
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "tampered-source.bin");
        File.WriteAllBytes(sourcePath, sourceBytes);

        var transfer = new ModP2PTransfer();
        var trust = new ModTrustConfig();
        if (!transfer.BeginSend("tamper-target", "TamperTest", "1.0.0", sourcePath))
            return false;

        var middleIndex = -1;
        var chunkCounter = 0;
        for (var i = 0; i < 1024; i++)
        {
            var pending = transfer.TickOutgoing();
            if (pending == null) return false;
            var chunk = pending.Value.Chunk;
            chunkCounter++;
            // Flip a byte on chunk 1 (not first or last) to simulate corruption.
            if (chunk.ChunkIndex == 1 && middleIndex < 0)
            {
                middleIndex = chunkCounter;
                var bytes = Convert.FromBase64String(chunk.ChunkBase64);
                if (bytes.Length > 0) bytes[0] ^= 0xFF;
                chunk.ChunkBase64 = Convert.ToBase64String(bytes);
            }
            var rx = transfer.OnChunkReceived("tamper-source", chunk, trust, out _);
            if (chunk.IsLast)
                return rx == ModP2PTransfer.ReceiveResult.FailedHashMismatch;
            if (rx != ModP2PTransfer.ReceiveResult.Continue)
                return false;
        }
        return false;
    }

    // Drives a transfer of `payloadBytes` random bytes through the chunker. Used to hit
    // boundary sizes (1, ChunkSize-1, ChunkSize, ChunkSize+1, 2*ChunkSize) that uniform
    // random sizes wouldn't reliably exercise.
    public static TransferLoopbackResult RunBoundaryTransfer(int payloadBytes, string label)
    {
        var result = new TransferLoopbackResult();
        if (payloadBytes < 0 || payloadBytes > 4 * 1024 * 1024)
        {
            result.ErrorMessage = $"payloadBytes out of range: {payloadBytes}";
            return result;
        }

        var bytes = new byte[payloadBytes];
        new Random(payloadBytes ^ 0x5EED).NextBytes(bytes);

        var tempDir = ProjectSettings.GlobalizePath("user://stress-tmp");
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, $"boundary-{label}.bin");
        File.WriteAllBytes(sourcePath, bytes);
        return RunTransferLoopback($"BoundaryTest_{label}", "1.0.0", sourcePath);
    }

    public sealed class QuarantineRoutingResult
    {
        public bool Success;
        public string ErrorMessage = "";
        public string PersistedPath = "";
        public bool LandedInQuarantine;
        public override string ToString() => $"quarantine success={Success} landed_in_quarantine={LandedInQuarantine} path={PersistedPath} err={ErrorMessage}";
    }

    // Runs a transfer with a synthetic trusted-only ModTrustConfig where the source's hash
    // is NOT on the allowlist. The receiver should route the file into mods-quarantine/
    // instead of mods/ and return CompletedAndQuarantined.
    public static QuarantineRoutingResult RunQuarantineRouting()
    {
        var result = new QuarantineRoutingResult();

        var bytes = new byte[8 * 1024];
        new Random(unchecked((int)0xCAFEBABE)).NextBytes(bytes);

        var tempDir = ProjectSettings.GlobalizePath("user://stress-tmp");
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "quarantine-src.bin");
        File.WriteAllBytes(sourcePath, bytes);

        var transfer = new ModP2PTransfer();
        var trust = new ModTrustConfig { Mode = ModTrustConfig.ModeTrustedOnly };
        // Empty trusted-hash list = nothing trusted -> any incoming hash routes to quarantine.

        if (!transfer.BeginSend("q-target", "QuarantineTest", "1.0.0", sourcePath))
        {
            result.ErrorMessage = "BeginSend returned false";
            return result;
        }

        for (var i = 0; i < 1024; i++)
        {
            var pending = transfer.TickOutgoing();
            if (pending == null)
            {
                result.ErrorMessage = "no chunk produced before completion";
                return result;
            }
            var rx = transfer.OnChunkReceived("q-source", pending.Value.Chunk, trust, out var persistedPath);
            if (rx == ModP2PTransfer.ReceiveResult.CompletedAndQuarantined)
            {
                result.PersistedPath = persistedPath ?? "";
                result.LandedInQuarantine = !string.IsNullOrEmpty(result.PersistedPath) &&
                    result.PersistedPath.Replace('\\', '/').Contains("/mods-quarantine/", StringComparison.OrdinalIgnoreCase);
                result.Success = result.LandedInQuarantine && File.Exists(result.PersistedPath);
                if (!result.Success && string.IsNullOrEmpty(result.ErrorMessage))
                    result.ErrorMessage = "completed but file is not in mods-quarantine";
                return result;
            }
            if (rx == ModP2PTransfer.ReceiveResult.CompletedAndPersisted)
            {
                result.PersistedPath = persistedPath ?? "";
                result.ErrorMessage = "trusted-only mode persisted to mods/ instead of quarantining";
                return result;
            }
            if (rx != ModP2PTransfer.ReceiveResult.Continue)
            {
                result.ErrorMessage = $"receive failed: {rx}";
                return result;
            }
        }
        result.ErrorMessage = "exceeded chunk iterations without completion";
        return result;
    }

    public sealed class OutOfOrderResult
    {
        public bool Success;
        public string ErrorMessage = "";
        public int InputBytes;
        public int ChunksProduced;
        public string ExpectedSha256 = "";
        public string ActualSha256 = "";
        public override string ToString() =>
            $"out-of-order success={Success} bytes={InputBytes} chunks={ChunksProduced} sha_match={(ExpectedSha256.Equals(ActualSha256, StringComparison.OrdinalIgnoreCase))} err={ErrorMessage}";
    }

    // Drives a multi-chunk transfer, deliberately delivers chunks in REVERSE order with
    // a duplicate of chunk 0 thrown in. Asserts that the order-independent reassembler
    // still hashes correctly and persists.
    public static OutOfOrderResult RunOutOfOrderTransfer(int payloadBytes = 64 * 1024)
    {
        payloadBytes = Math.Clamp(payloadBytes, 1, 4 * 1024 * 1024);
        var result = new OutOfOrderResult { InputBytes = payloadBytes };

        var sourceBytes = new byte[payloadBytes];
        new Random(payloadBytes ^ 0x0D3D2).NextBytes(sourceBytes);
        result.ExpectedSha256 = Convert.ToHexString(SHA256.HashData(sourceBytes));

        var tempDir = ProjectSettings.GlobalizePath("user://stress-tmp");
        Directory.CreateDirectory(tempDir);
        var sourcePath = Path.Combine(tempDir, "outoforder-src.bin");
        File.WriteAllBytes(sourcePath, sourceBytes);

        var transfer = new ModP2PTransfer();
        var trust = new ModTrustConfig();
        if (!transfer.BeginSend("ooo-target", "OutOfOrderTest", "1.0.0", sourcePath))
        {
            result.ErrorMessage = "BeginSend returned false";
            return result;
        }

        // Drain everything into a buffer first so we control the delivery order.
        var produced = new List<ModTransferChunk>();
        for (var i = 0; i < 4096; i++)
        {
            var pending = transfer.TickOutgoing();
            if (pending == null) break;
            produced.Add(pending.Value.Chunk);
        }
        result.ChunksProduced = produced.Count;
        if (produced.Count < 2)
        {
            result.ErrorMessage = "need at least 2 chunks to test ordering";
            return result;
        }

        // Build the delivery sequence: reversed, then resend chunk 0 once at the end
        // to test duplicate-chunk idempotency.
        var delivery = new List<ModTransferChunk>(produced.Count + 1);
        for (var i = produced.Count - 1; i >= 0; i--) delivery.Add(produced[i]);
        delivery.Add(produced[0]);

        ModP2PTransfer.ReceiveResult last = ModP2PTransfer.ReceiveResult.Continue;
        string? persistedPath = null;
        foreach (var c in delivery)
        {
            last = transfer.OnChunkReceived("ooo-source", c, trust, out var p);
            if (p != null) persistedPath = p;
            if (last == ModP2PTransfer.ReceiveResult.CompletedAndPersisted ||
                last == ModP2PTransfer.ReceiveResult.CompletedAndQuarantined) break;
            if (last != ModP2PTransfer.ReceiveResult.Continue)
            {
                result.ErrorMessage = $"receive failed: {last}";
                return result;
            }
        }
        if (last != ModP2PTransfer.ReceiveResult.CompletedAndPersisted || persistedPath == null)
        {
            result.ErrorMessage = $"did not complete (last={last})";
            return result;
        }
        result.ActualSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(persistedPath)));
        result.Success = string.Equals(result.ExpectedSha256, result.ActualSha256, StringComparison.OrdinalIgnoreCase);
        if (!result.Success) result.ErrorMessage = "hash mismatch after out-of-order delivery";
        return result;
    }

    public sealed class ConcurrentTransferResult
    {
        public bool Success;
        public string ErrorMessage = "";
        public int TransferCount;
        public int CompletedCount;
        public int CrossContaminationDetected;
        public int MaxConsecutiveSameTransfer;
        public override string ToString() =>
            $"concurrent success={Success} {CompletedCount}/{TransferCount} completed cross_contamination={CrossContaminationDetected} max_consecutive_same={MaxConsecutiveSameTransfer} err={ErrorMessage}";
    }

    // Starts N concurrent transfers from a single ModP2PTransfer instance, drains all
    // chunks via TickOutgoing (which round-robins across active transfers), feeds each
    // chunk to OnChunkReceived, asserts every transfer completes with its own correct
    // hash. Catches cross-talk bugs where chunks from one transfer contaminate another.
    public static ConcurrentTransferResult RunConcurrentTransfers(int transferCount = 3, int payloadBytes = 32 * 1024)
    {
        transferCount = Math.Clamp(transferCount, 2, 10);
        payloadBytes = Math.Clamp(payloadBytes, 1, 1 * 1024 * 1024);
        var result = new ConcurrentTransferResult { TransferCount = transferCount };

        var tempDir = ProjectSettings.GlobalizePath("user://stress-tmp");
        Directory.CreateDirectory(tempDir);

        var transfer = new ModP2PTransfer();
        var trust = new ModTrustConfig();
        var expectedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < transferCount; i++)
        {
            var bytes = new byte[payloadBytes];
            new Random(0x10000 + i).NextBytes(bytes);
            var modId = $"ConcurrentTest_{i}";
            expectedHashes[modId] = Convert.ToHexString(SHA256.HashData(bytes));
            var src = Path.Combine(tempDir, $"concurrent-{i}.bin");
            File.WriteAllBytes(src, bytes);
            if (!transfer.BeginSend($"ct-target-{i}", modId, "1.0.0", src))
            {
                result.ErrorMessage = $"BeginSend returned false for {modId}";
                return result;
            }
        }

        var persistedByMod = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Track scheduling fairness: consecutive chunks from the same mod indicate the
        // round-robin starved siblings.
        var lastModId = "";
        var consecutive = 0;
        for (var i = 0; i < 8192; i++)
        {
            var pending = transfer.TickOutgoing();
            if (pending == null) break;
            var thisMod = pending.Value.Chunk.ModId;
            if (thisMod == lastModId) consecutive++;
            else { result.MaxConsecutiveSameTransfer = Math.Max(result.MaxConsecutiveSameTransfer, consecutive); consecutive = 1; lastModId = thisMod; }

            var rx = transfer.OnChunkReceived($"ct-source-{thisMod}", pending.Value.Chunk, trust, out var path);
            if (rx == ModP2PTransfer.ReceiveResult.CompletedAndPersisted)
            {
                persistedByMod[thisMod] = path ?? "";
                result.CompletedCount++;
            }
            else if (rx != ModP2PTransfer.ReceiveResult.Continue)
            {
                result.ErrorMessage = $"receive failed for {thisMod}: {rx}";
                return result;
            }
        }
        result.MaxConsecutiveSameTransfer = Math.Max(result.MaxConsecutiveSameTransfer, consecutive);

        // Verify each persisted file matches its own expected hash. Cross-contamination
        // would show as a hash mismatch (file contains bytes from a sibling transfer).
        foreach (var (modId, expected) in expectedHashes)
        {
            if (!persistedByMod.TryGetValue(modId, out var path) || !File.Exists(path))
            {
                result.ErrorMessage = $"missing persisted file for {modId}";
                return result;
            }
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                result.CrossContaminationDetected++;
                result.ErrorMessage = $"hash mismatch for {modId} — possible chunk cross-talk";
            }
        }

        // Fairness check: with N concurrent transfers and round-robin scheduling, the
        // scheduler should never serve more than 1 consecutive chunk from the same mod
        // (until others finish). Allow a small cushion (2) for the very last chunks where
        // siblings have already drained.
        const int fairnessCushion = 2;
        var fairnessOk = result.MaxConsecutiveSameTransfer <= fairnessCushion;
        result.Success = result.CompletedCount == transferCount &&
                         result.CrossContaminationDetected == 0 &&
                         fairnessOk;
        if (!fairnessOk && string.IsNullOrEmpty(result.ErrorMessage))
            result.ErrorMessage = $"unfair scheduling — {result.MaxConsecutiveSameTransfer} consecutive chunks from same transfer";
        return result;
    }

    public sealed class PckTransferResult
    {
        public bool Success;
        public string ErrorMessage = "";
        public bool DllArrived;
        public bool PckArrived;
        public string DllPath = "";
        public string PckPath = "";
        public override string ToString() =>
            $"pck-transfer success={Success} dll={DllArrived} pck={PckArrived} err={ErrorMessage}";
    }

    // Drives a transfer of TWO files (DLL + PCK) for the same mod through the chunker
    // concurrently. Asserts both files land at the right paths with correct hashes
    // and that side-file routing via FileSuffix doesn't cross-contaminate.
    public static PckTransferResult RunPckSideFileTransfer()
    {
        var result = new PckTransferResult();

        var tempDir = ProjectSettings.GlobalizePath("user://stress-tmp");
        Directory.CreateDirectory(tempDir);

        var dllBytes = new byte[24 * 1024];
        var pckBytes = new byte[40 * 1024];
        new Random(0x0DEAD).NextBytes(dllBytes);
        new Random(0x0BEEF).NextBytes(pckBytes);

        var dllSrc = Path.Combine(tempDir, "PckSideTest-src.dll");
        var pckSrc = Path.Combine(tempDir, "PckSideTest-src.pck");
        File.WriteAllBytes(dllSrc, dllBytes);
        File.WriteAllBytes(pckSrc, pckBytes);

        var dllExpectedHash = Convert.ToHexString(SHA256.HashData(dllBytes));
        var pckExpectedHash = Convert.ToHexString(SHA256.HashData(pckBytes));

        var transfer = new ModP2PTransfer();
        var trust = new ModTrustConfig();
        if (!transfer.BeginSend("pck-target", "PckSideTest", "1.0.0", dllSrc, ".dll"))
        { result.ErrorMessage = "BeginSend(.dll) returned false"; return result; }
        if (!transfer.BeginSend("pck-target", "PckSideTest", "1.0.0", pckSrc, ".pck"))
        { result.ErrorMessage = "BeginSend(.pck) returned false"; return result; }

        for (var i = 0; i < 4096; i++)
        {
            var pending = transfer.TickOutgoing();
            if (pending == null) break;
            var rx = transfer.OnChunkReceived("pck-source", pending.Value.Chunk, trust, out var path);
            if (rx == ModP2PTransfer.ReceiveResult.CompletedAndPersisted)
            {
                if (string.Equals(pending.Value.Chunk.FileSuffix, ".dll", StringComparison.OrdinalIgnoreCase))
                { result.DllArrived = true; result.DllPath = path ?? ""; }
                else if (string.Equals(pending.Value.Chunk.FileSuffix, ".pck", StringComparison.OrdinalIgnoreCase))
                { result.PckArrived = true; result.PckPath = path ?? ""; }
            }
            else if (rx != ModP2PTransfer.ReceiveResult.Continue)
            { result.ErrorMessage = $"receive failed for {pending.Value.Chunk.FileSuffix}: {rx}"; return result; }
        }

        if (!result.DllArrived) { result.ErrorMessage = "DLL never completed"; return result; }
        if (!result.PckArrived) { result.ErrorMessage = "PCK never completed"; return result; }

        // Verify path routing — DLL should land at <id>.dll, PCK at <id>.pck.
        if (!result.DllPath.EndsWith("PckSideTest.dll", StringComparison.OrdinalIgnoreCase))
        { result.ErrorMessage = $"DLL landed at wrong path: {result.DllPath}"; return result; }
        if (!result.PckPath.EndsWith("PckSideTest.pck", StringComparison.OrdinalIgnoreCase))
        { result.ErrorMessage = $"PCK landed at wrong path: {result.PckPath}"; return result; }

        // Verify content didn't cross-contaminate (FileSuffix routing in OnChunkReceived).
        var dllActual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.DllPath)));
        var pckActual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(result.PckPath)));
        if (!string.Equals(dllActual, dllExpectedHash, StringComparison.OrdinalIgnoreCase))
        { result.ErrorMessage = "DLL content hash mismatch — possible suffix routing bug"; return result; }
        if (!string.Equals(pckActual, pckExpectedHash, StringComparison.OrdinalIgnoreCase))
        { result.ErrorMessage = "PCK content hash mismatch — possible suffix routing bug"; return result; }

        result.Success = true;
        return result;
    }

    public sealed class CompatibilityCheckResult
    {
        public bool Success;
        public string ErrorMessage = "";
        public int CategoriesPassed;
        public int CategoriesTotal;
        public override string ToString() => $"compatibility {CategoriesPassed}/{CategoriesTotal} categories detected correctly err={ErrorMessage}";
    }

    // Verifies ModCompatibilityChecker fires on each known issue category against
    // synthetic fixtures: declared conflict, missing dependency, duplicate id, duplicate
    // assembly file, and a clean-set baseline (no false positives).
    public static CompatibilityCheckResult RunCompatibilityCheckerTests()
    {
        var result = new CompatibilityCheckResult { CategoriesTotal = 5 };

        // 1. Declared conflict between two enabled mods should produce a Conflict entry.
        var declaredConflict = new List<ModManifest>
        {
            new() { Id = "modA", Name = "A", Version = "1.0.0", Multiplayer = new ModMultiplayer { ConflictsWith = new List<string> { "modB" } } },
            new() { Id = "modB", Name = "B", Version = "1.0.0" },
        };
        foreach (var m in declaredConflict) m.Normalize();
        var r1 = ModCompatibilityChecker.Check(declaredConflict, new[] { "modA", "modB" });
        if (r1.Conflicts.Any(c => c.Reason.Contains("conflictsWith"))) result.CategoriesPassed++;
        else { result.ErrorMessage = "declared conflict not detected"; return result; }

        // 2. Missing dependency.
        var missingDep = new List<ModManifest>
        {
            new() { Id = "modX", Name = "X", Version = "1.0.0", Multiplayer = new ModMultiplayer { Requires = new List<string> { "modY" } } },
        };
        foreach (var m in missingDep) m.Normalize();
        var r2 = ModCompatibilityChecker.Check(missingDep, new[] { "modX" });
        if (r2.MissingDependencies.Any(d => d.MissingDependencyId == "modY")) result.CategoriesPassed++;
        else { result.ErrorMessage = "missing dependency not detected"; return result; }

        // 3. Duplicate id (across installed, regardless of enabled state).
        var dupId = new List<ModManifest>
        {
            new() { Id = "dupMod", Name = "First",  Version = "1.0.0" },
            new() { Id = "dupMod", Name = "Second", Version = "1.0.0" },
        };
        foreach (var m in dupId) m.Normalize();
        var r3 = ModCompatibilityChecker.Check(dupId, Array.Empty<string>());
        if (r3.Conflicts.Any(c => c.Reason.Contains("duplicate mod id"))) result.CategoriesPassed++;
        else { result.ErrorMessage = "duplicate id not detected"; return result; }

        // 4. Duplicate assembly filename.
        var dupAsm = new List<ModManifest>
        {
            new() { Id = "modP", Name = "P", Version = "1.0.0", AssemblyFile = "Shared.dll" },
            new() { Id = "modQ", Name = "Q", Version = "1.0.0", AssemblyFile = "Shared.dll" },
        };
        foreach (var m in dupAsm) m.Normalize();
        var r4 = ModCompatibilityChecker.Check(dupAsm, Array.Empty<string>());
        if (r4.Conflicts.Any(c => c.Reason.Contains("share assembly file"))) result.CategoriesPassed++;
        else { result.ErrorMessage = "duplicate assembly file not detected"; return result; }

        // 5. Clean set: no issues should be reported.
        var clean = new List<ModManifest>
        {
            new() { Id = "good1", Name = "Good 1", Version = "1.0.0" },
            new() { Id = "good2", Name = "Good 2", Version = "1.0.0" },
        };
        foreach (var m in clean) m.Normalize();
        var r5 = ModCompatibilityChecker.Check(clean, new[] { "good1", "good2" });
        if (!r5.HasIssues) result.CategoriesPassed++;
        else { result.ErrorMessage = $"false positives on clean set: {r5.Summarize()}"; return result; }

        result.Success = result.CategoriesPassed == result.CategoriesTotal;
        return result;
    }

    public sealed class DropPoolRoundtripResult
    {
        public bool Success;
        public int StartCount;
        public int PeakCount;
        public int EndCount;
        public override string ToString() => $"droppool success={Success} start={StartCount} peak={PeakCount} end={EndCount}";
    }

    // Adds N entries to an in-memory RandomWeightedDropPool through ModDropPoolHelper,
    // then disposes them and confirms the pool returned to its original size.
    public static DropPoolRoundtripResult RunDropPoolRoundtrip(int entryCount = 50)
    {
        entryCount = Math.Clamp(entryCount, 1, 200); // hard cap
        var pool = new RandomWeightedDropPool { Pool = Array.Empty<RandomWeightedScene>() };
        var result = new DropPoolRoundtripResult { StartCount = pool.Pool.Length };

        var dummyScene = new PackedScene();
        var registrations = new List<IDisposable>();
        for (var i = 0; i < entryCount; i++)
            registrations.Add(ModDropPoolHelper.RegisterIn(pool, dummyScene, weight: i + 1, label: "stress-test-pool"));
        result.PeakCount = pool.Pool?.Length ?? 0;

        foreach (var reg in registrations)
            reg.Dispose();
        result.EndCount = pool.Pool?.Length ?? 0;

        result.Success = result.PeakCount == result.StartCount + entryCount && result.EndCount == result.StartCount;
        return result;
    }
}
