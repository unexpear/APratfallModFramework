using System.Runtime.Loader;
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
            var rx = transfer.OnChunkReceived("self-test-source", pending.Value.Chunk, out var persistedPath);
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
            var rx = transfer.OnChunkReceived("tamper-source", chunk, out _);
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
            last = transfer.OnChunkReceived("ooo-source", c, out var p);
            if (p != null) persistedPath = p;
            if (last == ModP2PTransfer.ReceiveResult.CompletedAndPersisted) break;
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

            var rx = transfer.OnChunkReceived($"ct-source-{thisMod}", pending.Value.Chunk, out var path);
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
        if (!transfer.BeginSend("pck-target", "PckSideTest", "1.0.0", dllSrc, ".dll"))
        { result.ErrorMessage = "BeginSend(.dll) returned false"; return result; }
        if (!transfer.BeginSend("pck-target", "PckSideTest", "1.0.0", pckSrc, ".pck"))
        { result.ErrorMessage = "BeginSend(.pck) returned false"; return result; }

        for (var i = 0; i < 4096; i++)
        {
            var pending = transfer.TickOutgoing();
            if (pending == null) break;
            var rx = transfer.OnChunkReceived("pck-source", pending.Value.Chunk, out var path);
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

    // Test-fixture enabled-mod-id arrays. Hoisted to static readonly fields per CA1861
    // (the analyzer dislikes constant array literals as method arguments; pre-allocating
    // once is the recommended pattern even for one-shot test fixtures).
    private static readonly string[] s_enabledIdsAB = ["modA", "modB"];
    private static readonly string[] s_enabledIdsX = ["modX"];
    private static readonly string[] s_enabledIdsGood = ["good1", "good2"];

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
        var r1 = ModCompatibilityChecker.Check(declaredConflict, s_enabledIdsAB);
        if (r1.Conflicts.Any(c => c.Reason.Contains("conflictsWith"))) result.CategoriesPassed++;
        else { result.ErrorMessage = "declared conflict not detected"; return result; }

        // 2. Missing dependency.
        var missingDep = new List<ModManifest>
        {
            new() { Id = "modX", Name = "X", Version = "1.0.0", Multiplayer = new ModMultiplayer { Requires = new List<string> { "modY" } } },
        };
        foreach (var m in missingDep) m.Normalize();
        var r2 = ModCompatibilityChecker.Check(missingDep, s_enabledIdsX);
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
        var r5 = ModCompatibilityChecker.Check(clean, s_enabledIdsGood);
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

    // --- v1.3 extension-helper self-tests ---
    //
    // These exercise the four helpers shipped in v1.3 (ModLocalizationHelper,
    // ModSaveDataHelper, ModGameEventHelper, ModButtonPromptHelper). Verifies
    // file ops and subscription mechanics WITHOUT triggering real game side
    // effects (no save flow, no UI mutation). Subscription tests use reflection
    // to count delegate invocation lists before/after Register + Dispose.

    public sealed class HelperTestResult
    {
        public bool Success;
        public string ErrorMessage = "";
        public List<string> StepsPassed = new();
        public override string ToString()
        {
            // On failure include the steps that DID pass so a diagnostic line
            // (e.g. "AllowUserLocalization=false ...") in the last step is visible
            // in the log. Success path stays concise.
            if (Success) return $"success=True steps={StepsPassed.Count}";
            var trail = StepsPassed.Count > 0 ? " trail=[" + string.Join(" | ", StepsPassed) + "]" : "";
            return $"success=False steps={StepsPassed.Count} err={ErrorMessage}{trail}";
        }
    }

    public static HelperTestResult RunLocalizationHelperTest()
    {
        var r = new HelperTestResult();
        const string modId = "SelfTestLocalization";
        const string locale = "test_xx";
        try
        {
            var translations = new Dictionary<string, string>
            {
                { "SELFTEST_KEY", "selftest_value" },
                { "ANOTHER", "another value" },
            };
            var reg = ModLocalizationHelper.Register(modId, locale, translations);
            r.StepsPassed.Add("Register returned non-null disposable");

            var folder = ResolveUserLocaleFolderForTest();
            if (folder == null) { r.ErrorMessage = "could not resolve user locale folder"; return r; }
            // Loader skips files starting with '_' (per LoadJsonFiles IL); helper must
            // write a filename WITHOUT leading underscore for the loader to pick it up.
            var expectedFile = Path.Combine(folder, $"{modId}_{locale}.json");
            if (!File.Exists(expectedFile)) { r.ErrorMessage = $"file not written at expected path: {expectedFile}"; return r; }
            r.StepsPassed.Add($"file exists at {expectedFile}");

            var content = File.ReadAllText(expectedFile);
            if (!content.Contains("SELFTEST_KEY") || !content.Contains("selftest_value"))
            { r.ErrorMessage = "file content missing expected key/value"; return r; }
            r.StepsPassed.Add("file content contains expected translations");

            // CRITICAL — verify the loader actually loaded our locale into the
            // game's AvailableLocales. Per LoadJsonFiles IL: the registered locale
            // ID is "zuser" + nameWithoutExtension (Pratfall namespaces user-
            // installed locales so they can't collide with system locales). So a
            // file `MyMod_es.json` becomes locale ID `"zuserMyMod_es"`, NOT
            // `"es"` or `"MyMod_es"`. Test against the actual prefixed ID.
            var expectedRegisteredLocale = ModLocalizationHelper.ComputeRegisteredLocaleId(modId, locale);
            var mgr = global::LocalizationManager.Instance;
            if (mgr != null && mgr.AvailableLocales != null)
            {
                // Pratfall gates LoadUserLocalizations on Game.Config.AllowUserLocalization
                // AND Game.Platform.IsSupportingDirectFileAccess(). If either is false the
                // loader is a no-op and no user locale will EVER appear. That's a Pratfall
                // build/platform config issue, not a helper bug — treat as PASS-WITH-NOTE.
                var allowUserLoc = global::Game.Config.AllowUserLocalization;
                bool platformSupports;
                try { platformSupports = global::Game.Platform?.IsSupportingDirectFileAccess() ?? false; }
                catch { platformSupports = false; }

                if (!allowUserLoc || !platformSupports)
                {
                    r.StepsPassed.Add($"Pratfall gate closed — AllowUserLocalization={allowUserLoc} IsSupportingDirectFileAccess={platformSupports}; loader skips ALL user locales on this build. Helper file ops verified — actual load can't be tested.");
                    reg.Dispose();
                    if (File.Exists(expectedFile)) { r.ErrorMessage = "file not removed after Dispose"; return r; }
                    r.StepsPassed.Add("file cleaned up on Dispose");
                    r.Success = true;
                    return r;
                }

                if (!mgr.IsLocaleAvailable(expectedRegisteredLocale))
                {
                    var available = string.Join(", ", mgr.AvailableLocales);
                    r.ErrorMessage = $"gate is open but IsLocaleAvailable('{expectedRegisteredLocale}') returns false. AvailableLocales=[{available}]";
                    return r;
                }
                r.StepsPassed.Add($"LocalizationManager.IsLocaleAvailable('{expectedRegisteredLocale}') == true");
            }
            else
            {
                r.StepsPassed.Add("LocalizationManager.Instance not yet ready; load-acceptance check skipped");
            }

            reg.Dispose();
            if (File.Exists(expectedFile)) { r.ErrorMessage = "file not removed after Dispose"; return r; }
            r.StepsPassed.Add("file cleaned up on Dispose");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    public static HelperTestResult RunSaveDataHelperTest()
    {
        var r = new HelperTestResult();
        const string modId = "SelfTestSaveData";
        const string sampleJson = "{\"selftest\":\"sample\"}";
        try
        {
            ModSaveDataHelper.Delete(modId); // pre-clean
            if (ModSaveDataHelper.LoadIfPresent(modId) != null) { r.ErrorMessage = "Delete failed to clear prior save"; return r; }
            r.StepsPassed.Add("LoadIfPresent returns null before any Register");

            var beforeCount = GetStaticDelegateCount(typeof(global::SavegameManager), "OnGameWillSave");
            var fireCount = 0;
            var reg = ModSaveDataHelper.Register(modId, () => { fireCount++; return sampleJson; });
            var afterCount = GetStaticDelegateCount(typeof(global::SavegameManager), "OnGameWillSave");

            if (afterCount != beforeCount + 1) { r.ErrorMessage = $"subscriber count expected {beforeCount + 1}, got {afterCount}"; return r; }
            r.StepsPassed.Add($"OnGameWillSave subscriber count went {beforeCount} -> {afterCount}");

            // Write a file directly via the public path and verify LoadIfPresent reads it.
            var path = ModSaveDataHelper.GetModSaveFilePath(modId);
            if (path == null) { r.ErrorMessage = "GetModSaveFilePath returned null"; return r; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sampleJson);
            var loaded = ModSaveDataHelper.LoadIfPresent(modId);
            if (loaded != sampleJson) { r.ErrorMessage = $"LoadIfPresent='{loaded}' expected '{sampleJson}'"; return r; }
            r.StepsPassed.Add("LoadIfPresent round-trips manually-written content");

            if (!ModSaveDataHelper.Delete(modId)) { r.ErrorMessage = "Delete returned false on existing file"; return r; }
            if (ModSaveDataHelper.LoadIfPresent(modId) != null) { r.ErrorMessage = "Delete did not actually remove file"; return r; }
            r.StepsPassed.Add("Delete removed the save file");

            reg.Dispose();
            var afterDisposeCount = GetStaticDelegateCount(typeof(global::SavegameManager), "OnGameWillSave");
            if (afterDisposeCount != beforeCount) { r.ErrorMessage = $"subscriber count after Dispose expected {beforeCount}, got {afterDisposeCount}"; return r; }
            r.StepsPassed.Add($"OnGameWillSave subscriber count after Dispose back to {beforeCount}");

            // Belt-and-suspenders: fireCount should still be 0 since we never triggered the event.
            if (fireCount != 0) { r.ErrorMessage = $"serializer fired {fireCount} times unexpectedly"; return r; }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    public static HelperTestResult RunGameEventHelperTest()
    {
        var r = new HelperTestResult();
        try
        {
            var beforeCount = GetStaticDelegateCount(typeof(global::GameEventBus), "OnGameEventReceived");

            var allHits = 0;
            var subAll = ModGameEventHelper.SubscribeAll((_, _) => allHits++);
            var afterAll = GetStaticDelegateCount(typeof(global::GameEventBus), "OnGameEventReceived");
            if (afterAll != beforeCount + 1) { r.ErrorMessage = $"SubscribeAll count {beforeCount} -> {afterAll}, expected +1"; return r; }
            r.StepsPassed.Add("SubscribeAll added one subscriber");

            var tagHits = 0;
            var subTag = ModGameEventHelper.SubscribeToTag("selftest.tag", (_, _) => tagHits++);
            var afterTag = GetStaticDelegateCount(typeof(global::GameEventBus), "OnGameEventReceived");
            if (afterTag != beforeCount + 2) { r.ErrorMessage = $"SubscribeToTag count {afterAll} -> {afterTag}, expected +1"; return r; }
            r.StepsPassed.Add("SubscribeToTag added one subscriber");

            subAll.Dispose();
            subTag.Dispose();
            var afterDispose = GetStaticDelegateCount(typeof(global::GameEventBus), "OnGameEventReceived");
            if (afterDispose != beforeCount) { r.ErrorMessage = $"after both Dispose: {afterDispose}, expected {beforeCount}"; return r; }
            r.StepsPassed.Add($"OnGameEventReceived subscriber count back to {beforeCount} after Dispose");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    public static HelperTestResult RunButtonPromptHelperTest()
    {
        var r = new HelperTestResult();
        try
        {
            // Pre-check Instance — ButtonPrompBarController is HUD-attached and may not
            // exist on the main menu. If it's null, we can only verify the helper
            // tolerates that case (logs and returns) without throwing.
            var instance = global::ButtonPrompBarController.Instance;
            if (instance == null)
            {
                ModButtonPromptHelper.Show("selftest_action", "Self Test", "selftest.ctx");
                ModButtonPromptHelper.ClearContext("selftest.ctx");
                r.StepsPassed.Add("Show + ClearContext tolerated null Instance (HUD not loaded)");
                r.Success = true; // partial success — full path needs HUD context
                return r;
            }

            ModButtonPromptHelper.Show("selftest_action", "Self Test", "selftest.ctx");
            r.StepsPassed.Add("Show against live HUD did not throw");
            ModButtonPromptHelper.ClearContext("selftest.ctx");
            r.StepsPassed.Add("ClearContext against live HUD did not throw");
            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Verifies ModLogger + ModCrashReporter end-to-end. Creates a logger for a
    // disposable mod id, writes a few lines (verifying file write + ring buffer),
    // then triggers ModCrashReporter.Report with a synthetic exception and confirms
    // the report file appeared. Cleans up after itself.
    public static HelperTestResult RunLoggerAndCrashReporterTest()
    {
        var r = new HelperTestResult();
        const string testModId = "SelfTestLoggerCrash";
        string? logFile = null;
        string? crashFolder = null;
        try
        {
            var logger = ModLogger.For(testModId);
            if (logger == null)
            {
                r.ErrorMessage = "ModLogger.For returned null";
                return r;
            }
            r.StepsPassed.Add("ModLogger.For returned an instance");

            logger.Info("self-test info line");
            logger.Warn("self-test warn line");
            logger.Error("self-test error line", new InvalidOperationException("synthetic — for ring-buffer test"));
            r.StepsPassed.Add("Logger.Info / Warn / Error did not throw");

            // Verify ring buffer contains the lines we just wrote.
            var recent = ModLogger.GetRecentLines(testModId);
            if (recent.Count < 3)
            {
                r.ErrorMessage = $"Ring buffer expected ≥3 entries, got {recent.Count}";
                return r;
            }
            r.StepsPassed.Add($"Ring buffer contains {recent.Count} recent entries");

            // Verify the log file exists where we expect.
            var logFolder = ModLogger.ResolveLogFolder();
            if (logFolder != null)
            {
                logFile = Path.Combine(logFolder, $"{testModId}.log");
                if (!File.Exists(logFile))
                {
                    r.ErrorMessage = $"Expected log file at {logFile} but it was not created";
                    return r;
                }
                var fileBytes = new FileInfo(logFile).Length;
                if (fileBytes <= 0)
                {
                    r.ErrorMessage = $"Log file exists but is empty (0 bytes)";
                    return r;
                }
                r.StepsPassed.Add($"Log file written: {fileBytes} bytes at {logFile}");
            }
            else
            {
                r.StepsPassed.Add("Log folder not resolvable (Game.Platform not up yet?) — ring buffer fallback validated");
            }

            // Trigger a synthetic crash report and confirm the file lands.
            crashFolder = ResolveCrashReportFolderForTest();
            int crashFilesBefore = crashFolder != null && Directory.Exists(crashFolder)
                ? Directory.GetFiles(crashFolder, $"{testModId}_*.txt").Length
                : 0;

            ModCrashReporter.Report(testModId, "self-test synthetic crash", new ApplicationException("self-test — no real failure"));

            if (crashFolder != null && Directory.Exists(crashFolder))
            {
                var crashFilesAfter = Directory.GetFiles(crashFolder, $"{testModId}_*.txt");
                if (crashFilesAfter.Length <= crashFilesBefore)
                {
                    r.ErrorMessage = $"Crash report not written (before={crashFilesBefore}, after={crashFilesAfter.Length})";
                    return r;
                }
                // Read the most recent and verify it has the recent log lines we wrote.
                var latest = crashFilesAfter.OrderByDescending(File.GetLastWriteTimeUtc).First();
                var crashText = File.ReadAllText(latest);
                if (!crashText.Contains("self-test info line") || !crashText.Contains("self-test synthetic crash"))
                {
                    r.ErrorMessage = $"Crash report missing expected content (path={latest})";
                    return r;
                }
                r.StepsPassed.Add($"Crash report written + contains ring-buffer history: {Path.GetFileName(latest)}");

                // Clean up so we don't accumulate self-test reports across runs.
                try
                {
                    foreach (var f in crashFilesAfter) File.Delete(f);
                    r.StepsPassed.Add("Cleaned up self-test crash report file(s)");
                }
                catch { /* best-effort */ }
            }
            else
            {
                r.StepsPassed.Add("Crash report folder not resolvable — verified Report() didn't throw");
            }

            // Clean up the log file too.
            try { if (logFile != null && File.Exists(logFile)) File.Delete(logFile); } catch { }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Verifies ModConfig end-to-end:
    //  - Bind round-trips through the JSON file
    //  - Constraint enforcement on .Value setter
    //  - OnChange fires on mutations
    //  - Reload picks up file changes
    //  - GetAllEntries discovery for the future UI
    //  - Cleanup after itself
    public static HelperTestResult RunConfigSystemTest()
    {
        var r = new HelperTestResult();
        const string testModId = "SelfTestConfigSystem";
        string? configFilePath = null;
        try
        {
            var folder = ModConfig.ResolveConfigFolder();
            if (folder != null)
            {
                configFilePath = Path.Combine(folder, $"{testModId}.json");
                // Wipe any leftover from a previous run.
                if (File.Exists(configFilePath)) File.Delete(configFilePath);
                if (File.Exists(configFilePath + ".bad")) File.Delete(configFilePath + ".bad");
            }

            var cfg = ModConfig.For(testModId);
            r.StepsPassed.Add("ModConfig.For returned a ModConfigFile");

            var enabled = cfg.Bind("General", "Enabled", true);
            var maxFlares = cfg.Bind("Combat", "MaxFlares", 3, new ConfigDescription
            {
                Tooltip = "How many flares to allow",
                Constraint = new AcceptableValueRange<int>(1, 100)
            });
            var name = cfg.Bind("General", "Name", "default-name");
            r.StepsPassed.Add("Bind() created 3 entries (bool / int with constraint / string)");

            // Verify defaults are in place.
            if (enabled.Value != true || maxFlares.Value != 3 || name.Value != "default-name")
            {
                r.ErrorMessage = "Default values not set correctly after Bind";
                return r;
            }
            r.StepsPassed.Add("Default values match expected (true / 3 / \"default-name\")");

            // OnChange handler — verify it fires on mutation.
            int changeCount = 0;
            int lastSeenValue = 0;
            maxFlares.OnChange += v => { changeCount++; lastSeenValue = v; };

            maxFlares.Value = 50;
            if (changeCount != 1 || lastSeenValue != 50)
            {
                r.ErrorMessage = $"OnChange did not fire correctly (count={changeCount}, last={lastSeenValue})";
                return r;
            }
            r.StepsPassed.Add("OnChange fired exactly once with new value 50");

            // Constraint enforcement — setting out-of-range should throw.
            bool threw = false;
            try { maxFlares.Value = 9999; } catch (ArgumentOutOfRangeException) { threw = true; }
            if (!threw)
            {
                r.ErrorMessage = "Constraint did not enforce (expected ArgumentOutOfRangeException for 9999)";
                return r;
            }
            // Value should still be 50 (constraint rejected the bad value before it landed).
            if (maxFlares.Value != 50)
            {
                r.ErrorMessage = $"Value changed despite constraint failure (now {maxFlares.Value})";
                return r;
            }
            r.StepsPassed.Add("Constraint threw on out-of-range + value unchanged");

            // File written?
            if (configFilePath != null)
            {
                if (!File.Exists(configFilePath))
                {
                    r.ErrorMessage = $"Config file not written at {configFilePath}";
                    return r;
                }
                var text = File.ReadAllText(configFilePath);
                if (!text.Contains("\"MaxFlares\"") || !text.Contains("50"))
                {
                    r.ErrorMessage = $"Config file missing expected content. Body:\n{text}";
                    return r;
                }
                r.StepsPassed.Add($"Config file written + contains expected fields ({new FileInfo(configFilePath).Length} bytes)");
            }
            else
            {
                r.StepsPassed.Add("Config folder not resolvable — verified API didn't throw");
            }

            // GetAllEntries discovery.
            var all = cfg.GetAllEntries();
            if (all.Count != 3)
            {
                r.ErrorMessage = $"GetAllEntries expected 3 entries, got {all.Count}";
                return r;
            }
            r.StepsPassed.Add($"GetAllEntries returned 3 entries");

            // Reload — verify file values override in-memory if changed externally.
            if (configFilePath != null)
            {
                var text = File.ReadAllText(configFilePath);
                text = text.Replace("\"MaxFlares\": 50", "\"MaxFlares\": 7");
                File.WriteAllText(configFilePath, text);

                int changesBeforeReload = changeCount;
                cfg.Reload();
                if (maxFlares.Value != 7)
                {
                    r.ErrorMessage = $"Reload did not pick up external file change (Value={maxFlares.Value}, expected 7)";
                    return r;
                }
                if (changeCount != changesBeforeReload + 1)
                {
                    r.ErrorMessage = $"Reload did not fire OnChange (count {changeCount}, expected {changesBeforeReload + 1})";
                    return r;
                }
                r.StepsPassed.Add("Reload picked up external file change + fired OnChange");
            }

            // ResetToDefault.
            maxFlares.ResetToDefault();
            if (maxFlares.Value != 3)
            {
                r.ErrorMessage = $"ResetToDefault didn't restore default (Value={maxFlares.Value})";
                return r;
            }
            r.StepsPassed.Add("ResetToDefault restored default value");

            // Cleanup.
            try { if (configFilePath != null && File.Exists(configFilePath)) File.Delete(configFilePath); } catch { }
            try { if (configFilePath != null && File.Exists(configFilePath + ".bad")) File.Delete(configFilePath + ".bad"); } catch { }
            r.StepsPassed.Add("Cleaned up self-test config file");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Verifies the CSync wire format end-to-end without needing a real second peer.
    // Tests:
    //  - ModConfigSyncNetworkEvent serializes + deserializes intact (JSON wrapper)
    //  - EncodeValueForSync / DecodeValueFromSync round-trip every supported type
    //  - SetFromHost applies values without firing the broadcast cycle
    //    (verified by counting OnSyncedValueChanged events before + after)
    //  - SetFromHost still fires the mod's OnChange handler (so reactive code reacts)
    public static HelperTestResult RunConfigSyncTest()
    {
        var r = new HelperTestResult();
        const string testModId = "SelfTestConfigSync";
        try
        {
            var cleanupFolder = ModConfig.ResolveConfigFolder();
            string? cleanupPath = cleanupFolder == null ? null : Path.Combine(cleanupFolder, $"{testModId}.json");
            if (cleanupPath != null && File.Exists(cleanupPath)) File.Delete(cleanupPath);

            var cfg = ModConfig.For(testModId);

            // Bind 3 Synced entries covering bool / int with range / string with list.
            // Plus 1 non-Synced entry to verify it doesn't get into snapshots.
            var syncBool = cfg.Bind("Sync", "EnableThing", true, new ConfigDescription { Synced = true });
            var syncInt = cfg.Bind("Sync", "Count", 5, new ConfigDescription
            {
                Synced = true,
                Constraint = new AcceptableValueRange<int>(0, 100)
            });
            var syncString = cfg.Bind("Sync", "Mode", "Auto", new ConfigDescription
            {
                Synced = true,
                Constraint = new AcceptableValueList<string>("Auto", "Manual", "Off")
            });
            var localOnly = cfg.Bind("Local", "Volume", 0.5f); // not Synced — must be skipped in EnumerateSyncedEntries
            r.StepsPassed.Add("Bind 3 Synced + 1 non-Synced entry");

            // Verify EnumerateSyncedEntries returns ONLY the Synced ones.
            var synced = ModConfig.EnumerateSyncedEntries().Where(t => t.ModId == testModId).ToList();
            if (synced.Count != 3)
            {
                r.ErrorMessage = $"EnumerateSyncedEntries returned {synced.Count} entries for testModId (expected 3 — the non-Synced one should be skipped)";
                return r;
            }
            r.StepsPassed.Add("EnumerateSyncedEntries skipped the non-Synced entry");

            // Build a synthetic snapshot, send it through Serialize -> wire bytes ->
            // Deserialize, verify intact. This validates the JSON wire format.
            var origSnapshot = new ModConfigSyncSnapshot
            {
                Entries =
                {
                    new ModConfigSyncEntry { ModId = testModId, Section = "Sync", Key = "EnableThing", TypeName = "bool", StringValue = "False" },
                    new ModConfigSyncEntry { ModId = testModId, Section = "Sync", Key = "Count",       TypeName = "int",  StringValue = "42" },
                    new ModConfigSyncEntry { ModId = testModId, Section = "Sync", Key = "Mode",        TypeName = "string", StringValue = "Manual" }
                }
            };
            var wireEvent = ModConfigSyncNetworkEvent.Create("self-test-sender", 0, origSnapshot);
            var roundtripped = wireEvent.ToSnapshot();
            if (roundtripped.Entries.Count != 3)
            {
                r.ErrorMessage = $"Snapshot wire round-trip: expected 3 entries, got {roundtripped.Entries.Count}";
                return r;
            }
            if (roundtripped.Entries[1].Key != "Count" || roundtripped.Entries[1].StringValue != "42")
            {
                r.ErrorMessage = $"Snapshot wire round-trip: entry data corrupted (Entry[1]={roundtripped.Entries[1].Key}/{roundtripped.Entries[1].StringValue})";
                return r;
            }
            r.StepsPassed.Add("Snapshot JSON wire format round-trips correctly");

            // SetFromHost behavior: applies value, fires OnChange, does NOT fire the
            // broadcast hook (which would cycle infinitely between host and peer in
            // an actual lobby).
            int broadcastFireCount = 0;
            Action<string, string, string, object?> broadcastWatcher = (m, s, k, v) =>
            {
                if (m == testModId) broadcastFireCount++;
            };
            ModConfig.OnSyncedValueChanged += broadcastWatcher;

            int onChangeFireCount = 0;
            syncInt.OnChange += _ => onChangeFireCount++;

            // Apply via SetFromHost (simulates a host pushing a new value to us as peer).
            if (syncInt is IConfigEntryInternal internalIface)
            {
                internalIface.SetFromHost(99);
            }
            else
            {
                r.ErrorMessage = "ConfigEntry<int> doesn't implement IConfigEntryInternal";
                ModConfig.OnSyncedValueChanged -= broadcastWatcher;
                return r;
            }

            if (syncInt.Value != 99)
            {
                r.ErrorMessage = $"SetFromHost didn't apply (Value={syncInt.Value}, expected 99)";
                ModConfig.OnSyncedValueChanged -= broadcastWatcher;
                return r;
            }
            if (onChangeFireCount != 1)
            {
                r.ErrorMessage = $"SetFromHost should fire OnChange exactly once, got {onChangeFireCount}";
                ModConfig.OnSyncedValueChanged -= broadcastWatcher;
                return r;
            }
            if (broadcastFireCount != 0)
            {
                r.ErrorMessage = $"SetFromHost should NOT fire OnSyncedValueChanged (would cycle), got {broadcastFireCount}";
                ModConfig.OnSyncedValueChanged -= broadcastWatcher;
                return r;
            }
            r.StepsPassed.Add("SetFromHost: applied + fired OnChange + did NOT fire broadcast hook (no cycle)");

            // Now verify a NORMAL setter (host-side) DOES fire the broadcast hook.
            // This is the case where mod code (or our UI) sets the value on the host
            // and we want it pushed to peers.
            syncInt.Value = 7;
            if (broadcastFireCount != 1)
            {
                r.ErrorMessage = $"Normal setter should fire OnSyncedValueChanged once, got {broadcastFireCount}";
                ModConfig.OnSyncedValueChanged -= broadcastWatcher;
                return r;
            }
            r.StepsPassed.Add("Normal setter fires broadcast hook (host-side push)");

            ModConfig.OnSyncedValueChanged -= broadcastWatcher;

            // Cleanup.
            try { if (cleanupPath != null && File.Exists(cleanupPath)) File.Delete(cleanupPath); } catch { }
            r.StepsPassed.Add("Cleaned up self-test config file");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Vote-tally regression coverage for ModVoteSession. Pure in-memory tally logic —
    // no scene tree, no filesystem, no network. Locks in the resolution contract before
    // any vote-system refactor (dead VoteState.Manifest cleanup, VoteCoordinator split).
    public static HelperTestResult RunVoteTallyTests()
    {
        var r = new HelperTestResult();
        try
        {
            // 1. Strict majority passes: 3 players, 2 yes / 1 no -> PASS.
            {
                var s = new ModVoteSession();
                bool? outcome = null;
                s.OnVoteResolved += (_, passed) => outcome = passed;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 3);
                s.CastVote("v", "a", true);
                s.CastVote("v", "b", true);
                s.CastVote("v", "c", false);
                if (outcome != true) { r.ErrorMessage = $"strict majority expected PASS, got {OutcomeText(outcome)}"; return r; }
                r.StepsPassed.Add("strict majority (2y/1n) passes");
            }

            // 2. Ties fail: 2 players, 1 yes / 1 no -> FAIL.
            {
                var s = new ModVoteSession();
                bool? outcome = null;
                s.OnVoteResolved += (_, passed) => outcome = passed;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 2);
                s.CastVote("v", "a", true);
                s.CastVote("v", "b", false);
                if (outcome != false) { r.ErrorMessage = $"tie expected FAIL, got {OutcomeText(outcome)}"; return r; }
                r.StepsPassed.Add("tie (1y/1n) fails");
            }

            // 3. No resolution before ExpectedVotes reached.
            {
                var s = new ModVoteSession();
                bool? outcome = null;
                s.OnVoteResolved += (_, passed) => outcome = passed;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 3);
                s.CastVote("v", "a", true);
                s.CastVote("v", "b", true);
                if (outcome != null) { r.ErrorMessage = $"resolved early after 2/3 votes ({OutcomeText(outcome)})"; return r; }
                r.StepsPassed.Add("no resolution before ExpectedVotes reached");
            }

            // 4. Duplicate voter ignored: same voterId twice counts once.
            {
                var s = new ModVoteSession();
                bool? outcome = null;
                s.OnVoteResolved += (_, passed) => outcome = passed;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 2);
                s.CastVote("v", "a", true);
                s.CastVote("v", "a", true); // duplicate -> ignored, only 1 unique voter
                if (outcome != null) { r.ErrorMessage = "duplicate voter resolved the vote (not deduped)"; return r; }
                r.StepsPassed.Add("duplicate voter ignored");
            }

            // 5. Case-insensitive voter dedup: "Alice" == "alice".
            {
                var s = new ModVoteSession();
                bool? outcome = null;
                s.OnVoteResolved += (_, passed) => outcome = passed;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 2);
                s.CastVote("v", "Alice", true);
                s.CastVote("v", "alice", true); // same voter, different case -> ignored
                if (outcome != null) { r.ErrorMessage = "case-variant voter not deduped"; return r; }
                r.StepsPassed.Add("case-insensitive voter dedup");
            }

            // 6. totalPlayers clamped to at least 1: a single vote resolves.
            {
                var s = new ModVoteSession();
                bool? outcome = null;
                s.OnVoteResolved += (_, passed) => outcome = passed;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 0);
                s.CastVote("v", "a", true);
                if (outcome != true) { r.ErrorMessage = $"totalPlayers=0 should clamp to 1 and resolve on one vote, got {OutcomeText(outcome)}"; return r; }
                r.StepsPassed.Add("totalPlayers clamped to >= 1");
            }

            // 7. ClearAllVotes mid-tally does not fire OnVoteResolved.
            {
                var s = new ModVoteSession();
                var resolveCount = 0;
                s.OnVoteResolved += (_, _) => resolveCount++;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 3);
                s.CastVote("v", "a", true);
                s.CastVote("v", "b", true);
                s.ClearAllVotes();
                s.CastVote("v", "c", true); // vote was cleared -> no-op
                if (resolveCount != 0) { r.ErrorMessage = $"ClearAllVotes/late vote fired {resolveCount} resolution(s)"; return r; }
                r.StepsPassed.Add("ClearAllVotes mid-tally fires no resolution");
            }

            // 8. Late vote after resolution does not fire a second result.
            {
                var s = new ModVoteSession();
                var resolveCount = 0;
                s.OnVoteResolved += (_, _) => resolveCount++;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 2);
                s.CastVote("v", "a", true);
                s.CastVote("v", "b", true); // resolves here
                s.CastVote("v", "c", true); // late -> no-op (voteId already removed)
                if (resolveCount != 1) { r.ErrorMessage = $"expected exactly 1 resolution, got {resolveCount}"; return r; }
                r.StepsPassed.Add("late vote after resolution fires no second result");
            }

            // 9. Duplicate StartVote for same voteId is a no-op (does not reset state).
            {
                var s = new ModVoteSession();
                bool? outcome = null;
                s.OnVoteResolved += (_, passed) => outcome = passed;
                s.StartVote("v", NewVoteManifest("Mod1"), totalPlayers: 2);
                s.StartVote("v", NewVoteManifest("Mod2"), totalPlayers: 5); // ignored: ExpectedVotes stays 2
                s.CastVote("v", "a", true);
                s.CastVote("v", "b", true);
                if (outcome != true) { r.ErrorMessage = $"duplicate StartVote changed state; expected resolve at 2 votes, got {OutcomeText(outcome)}"; return r; }
                r.StepsPassed.Add("duplicate StartVote is a no-op");
            }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    private static ModManifest NewVoteManifest(string name) =>
        new() { Id = name, Name = name, Version = "1.0.0" };

    private static string OutcomeText(bool? outcome) =>
        outcome switch { true => "PASS", false => "FAIL", null => "no-resolution" };

    // Helper-cluster dispatch/exception/cleanup coverage for ModGameEventHelper.
    // Fires only the test's OWN bus subscriptions (the delta from a captured baseline)
    // so real game subscribers are never invoked with a synthetic event. All test
    // subscriptions are disposed in the finally block so a failure can't leak handlers
    // onto the shared GameEventBus.
    public static HelperTestResult RunGameEventDispatchTests()
    {
        var r = new HelperTestResult();
        var subs = new List<IDisposable>();
        try
        {
            // 1. SubscribeAll: subscribe -> fire -> handler called.
            var allHits = 0;
            var baseAll = BusInvocationList();
            var subAll = ModGameEventHelper.SubscribeAll((_, _) => allHits++);
            subs.Add(subAll);
            FireDelta(baseAll, NewTag("selftest.any"), null);
            if (allHits != 1) { r.ErrorMessage = $"SubscribeAll fire: expected 1 hit, got {allHits}"; return r; }
            r.StepsPassed.Add("SubscribeAll: fire invokes handler");

            // 2. Dispose -> callback removed from bus -> fire does not invoke handler.
            subAll.Dispose();
            if (BusInvocationList().Where(d => Array.IndexOf(baseAll, d) < 0).Any())
            { r.ErrorMessage = "after Dispose, callback still on bus"; return r; }
            FireDelta(baseAll, NewTag("selftest.any"), null); // delta now empty
            if (allHits != 1) { r.ErrorMessage = $"after Dispose, handler still fired (hits={allHits})"; return r; }
            r.StepsPassed.Add("Dispose: callback removed, fire is a no-op");

            // 3. double Dispose is safe (no throw).
            subAll.Dispose();
            r.StepsPassed.Add("double Dispose is safe");

            // 4. Tag filtering: matching tag fires, non-matching tag does not.
            var tagHits = 0;
            var baseTag = BusInvocationList();
            subs.Add(ModGameEventHelper.SubscribeToTag("selftest.match", (_, _) => tagHits++));
            FireDelta(baseTag, NewTag("selftest.match"), null);
            if (tagHits != 1) { r.ErrorMessage = $"tag match: expected 1 hit, got {tagHits}"; return r; }
            FireDelta(baseTag, NewTag("selftest.other"), null);
            if (tagHits != 1) { r.ErrorMessage = $"non-matching tag fired handler (hits={tagHits})"; return r; }
            r.StepsPassed.Add("SubscribeToTag: matches tag, rejects non-matching tag");

            // 4b. Subscribe(GameplayTag): Equals-based filtering matches a separate tag
            // instance with the same .Tag string, and rejects a different .Tag.
            var eqHits = 0;
            var baseEq = BusInvocationList();
            subs.Add(ModGameEventHelper.Subscribe(NewTag("selftest.eq"), (_, _) => eqHits++));
            FireDelta(baseEq, NewTag("selftest.eq"), null);
            if (eqHits != 1) { r.ErrorMessage = $"Subscribe(GameplayTag) same-tag instance: expected 1 hit, got {eqHits}"; return r; }
            FireDelta(baseEq, NewTag("selftest.neq"), null);
            if (eqHits != 1) { r.ErrorMessage = $"Subscribe(GameplayTag) non-matching tag fired (hits={eqHits})"; return r; }
            r.StepsPassed.Add("Subscribe(GameplayTag): Equals-based filtering across instances");

            // 5. Duplicate subscriptions: same handler subscribed twice fires twice (no dedup).
            var dupHits = 0;
            Action<global::GameplayTag, global::IGameEvent> dupHandler = (_, _) => dupHits++;
            var baseDup = BusInvocationList();
            subs.Add(ModGameEventHelper.SubscribeAll(dupHandler));
            subs.Add(ModGameEventHelper.SubscribeAll(dupHandler));
            FireDelta(baseDup, NewTag("selftest.any"), null);
            if (dupHits != 2) { r.ErrorMessage = $"duplicate subscriptions: expected 2 hits, got {dupHits}"; return r; }
            r.StepsPassed.Add("duplicate subscriptions both fire (no dedup)");

            // 6. Throwing handler does not prevent another handler from firing (per-callback try/catch).
            var survivorHits = 0;
            var baseThrow = BusInvocationList();
            subs.Add(ModGameEventHelper.SubscribeAll((_, _) => throw new InvalidOperationException("selftest boom")));
            subs.Add(ModGameEventHelper.SubscribeAll((_, _) => survivorHits++));
            FireDelta(baseThrow, NewTag("selftest.any"), null);
            if (survivorHits != 1) { r.ErrorMessage = $"throwing handler broke isolation; survivor hits={survivorHits}"; return r; }
            r.StepsPassed.Add("throwing handler isolated (survivor still fires)");

            // 7. Invalid args throw the expected exception types.
            if (!Throws<ArgumentNullException>(() => ModGameEventHelper.SubscribeAll(null!)))
            { r.ErrorMessage = "SubscribeAll(null) did not throw ArgumentNullException"; return r; }
            if (!Throws<ArgumentException>(() => ModGameEventHelper.SubscribeToTag("", (_, _) => { })))
            { r.ErrorMessage = "SubscribeToTag(empty) did not throw ArgumentException"; return r; }
            if (!Throws<ArgumentNullException>(() => ModGameEventHelper.SubscribeToTag("x", null!)))
            { r.ErrorMessage = "SubscribeToTag(tag, null) did not throw ArgumentNullException"; return r; }
            r.StepsPassed.Add("invalid args throw expected exception types");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
        finally
        {
            foreach (var s in subs) s.Dispose();
        }
    }

    // Verifies ModDropPoolHelper.Dispose removes ONLY the entry that registration added,
    // leaving sibling registrations intact (reference-equality removal, not content match).
    public static HelperTestResult RunDropPoolSelectiveDisposeTest()
    {
        var r = new HelperTestResult();
        try
        {
            var pool = new RandomWeightedDropPool { Pool = Array.Empty<RandomWeightedScene>() };
            var scene = new PackedScene();
            var reg1 = ModDropPoolHelper.RegisterIn(pool, scene, weight: 1, label: "selftest-1");
            var reg2 = ModDropPoolHelper.RegisterIn(pool, scene, weight: 2, label: "selftest-2");
            if ((pool.Pool?.Length ?? 0) != 2) { r.ErrorMessage = $"expected 2 entries after 2 registers, got {pool.Pool?.Length ?? 0}"; return r; }
            r.StepsPassed.Add("two registrations add two entries");

            reg1.Dispose();
            if ((pool.Pool?.Length ?? 0) != 1) { r.ErrorMessage = $"expected 1 entry after disposing reg1, got {pool.Pool?.Length ?? 0}"; return r; }
            if (pool.Pool![0].Weight != 2) { r.ErrorMessage = $"wrong entry removed; remaining Weight={pool.Pool[0].Weight}, expected 2"; return r; }
            r.StepsPassed.Add("Dispose removed only reg1's entry; reg2 intact");

            reg2.Dispose();
            if ((pool.Pool?.Length ?? 0) != 0) { r.ErrorMessage = $"expected 0 entries after disposing reg2, got {pool.Pool?.Length ?? 0}"; return r; }
            r.StepsPassed.Add("disposing reg2 returns pool to empty");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Reads the GameEventBus.OnGameEventReceived multicast delegate's invocation list
    // (same reflection target as GetStaticDelegateCount, returning the entries).
    private static Delegate[] BusInvocationList()
    {
        var field = typeof(global::GameEventBus).GetField("OnGameEventReceived",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        var del = field?.GetValue(null) as Delegate;
        return del?.GetInvocationList() ?? Array.Empty<Delegate>();
    }

    // Invokes only the bus callbacks added since `baseline` (the test's own subscriptions),
    // never the real game subscribers already on the bus.
    private static void FireDelta(Delegate[] baseline, global::GameplayTag? tag, global::IGameEvent? ev)
    {
        foreach (var cb in BusInvocationList())
            if (Array.IndexOf(baseline, cb) < 0)
                cb.DynamicInvoke(tag, ev);
    }

    // GameplayTag.Tag has a non-public setter; set the auto-property backing field
    // directly so the test can build a synthetic tag without a real GameplayTags constant.
    private static global::GameplayTag NewTag(string tag)
    {
        var t = new global::GameplayTag();
        typeof(global::GameplayTag)
            .GetField("<Tag>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(t, tag);
        return t;
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); return false; }
        catch (TException) { return true; }
    }

    // Filename-sanitize golden coverage. Locks in the current input->output mapping for
    // every Sanitize site and asserts all 5 implementations agree, so the deferred
    // PathUtil.SanitizeForFilename consolidation can't silently change a filename and
    // orphan a user's config/log/crash/locale/savedata files.
    public static HelperTestResult RunFilenameSanitizeTests()
    {
        var r = new HelperTestResult();
        try
        {
            (string input, string expected)[] golden =
            {
                ("MyMod", "MyMod"),
                ("My.Mod.Config", "My_Mod_Config"),
                ("my-mod_v1.2", "my-mod_v1_2"),
                ("a b\tc", "a_b_c"),
                ("../../secret", "______secret"),
                ("", ""),
            };
            (string name, Type type)[] owners =
            {
                ("ModConfig", typeof(ModConfig)),
                ("ModLogger", typeof(ModLogger)),
                ("ModCrashReporter", typeof(ModCrashReporter)),
                ("ModLocalizationHelper", typeof(ModLocalizationHelper)),
                ("ModSaveDataHelper", typeof(ModSaveDataHelper)),
            };

            foreach (var (input, expected) in golden)
                foreach (var (name, type) in owners)
                {
                    var actual = CallSanitize(type, input);
                    if (actual != expected)
                    { r.ErrorMessage = $"{name}.Sanitize(\"{input}\") = \"{actual}\", expected \"{expected}\""; return r; }
                }

            r.StepsPassed.Add($"all {owners.Length} Sanitize impls match {golden.Length} golden cases (cross-impl equivalent)");
            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    private static string CallSanitize(Type owner, string input)
    {
        var m = FindStaticStringMethod(owner, "Sanitize")
            ?? throw new InvalidOperationException($"Sanitize(string) not found on {owner.Name}");
        return (string)m.Invoke(null, new object[] { input })!;
    }

    // Finds a `static string Method(string)` on the type or any nested type. The Sanitize
    // helpers are private (ModConfig's is internal); ModLogger's lives on a nested type.
    private static System.Reflection.MethodInfo? FindStaticStringMethod(Type owner, string name)
    {
        const System.Reflection.BindingFlags F =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var m = owner.GetMethod(name, F, null, new[] { typeof(string) }, null);
        if (m != null) return m;
        foreach (var nested in owner.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        {
            m = nested.GetMethod(name, F, null, new[] { typeof(string) }, null);
            if (m != null) return m;
        }
        return null;
    }

    // Wire-format roundtrip coverage for all 7 NetworkEvent wrappers. Serializes through
    // Pratfall's ByteBufferWriter, deserializes through ByteBufferReader, converts back via
    // the wrapper's ToX method, and asserts every important field survives. Gates the
    // deferred NetworkEvent dedup + 32700-byte cap helper: a dedup that reorders or drops a
    // serialized field would break peer compatibility silently, and this catches it.
    public static HelperTestResult RunWireFormatRoundtripTests()
    {
        var r = new HelperTestResult();
        try
        {
            // 1. ModManifestSnapshotNetworkEvent
            {
                var state = new ModLocalState
                {
                    InstalledManifests = { NewVoteManifest("moda"), NewVoteManifest("modb") },
                    EnabledModIds = { "moda" },
                };
                var ev = ModManifestSnapshotNetworkEvent.Create("sender-1", 7, state);
                var copy = new ModManifestSnapshotNetworkEvent();
                WireRoundtrip(ev.Serialize, copy.Deserialize);
                if (copy.SenderUserId != "sender-1" || copy.SenderIndex != 7)
                { r.ErrorMessage = $"manifest envelope: sender={copy.SenderUserId}/{copy.SenderIndex}"; return r; }
                var snap = copy.ToSnapshot();
                if (snap.InstalledManifests.Count != 2 || snap.EnabledModIds.Count != 1 || snap.EnabledModIds[0] != "moda")
                { r.ErrorMessage = $"manifest snapshot: installed={snap.InstalledManifests.Count} enabled=[{string.Join(",", snap.EnabledModIds)}]"; return r; }
                r.StepsPassed.Add("ModManifestSnapshotNetworkEvent roundtrips");
            }

            // 2. ModVoteRequestNetworkEvent
            {
                var req = new ModVoteRequest { VoteId = "v1", SourceUserId = "host", Title = "T", Body = "B", ExpectedVotes = 3, Manifest = NewVoteManifest("moda") };
                var ev = ModVoteRequestNetworkEvent.Create("sender-1", 1, req);
                var copy = new ModVoteRequestNetworkEvent();
                WireRoundtrip(ev.Serialize, copy.Deserialize);
                var rt = copy.ToRequest();
                if (rt.VoteId != "v1" || rt.SourceUserId != "host" || rt.Title != "T" || rt.Body != "B" || rt.ExpectedVotes != 3 || rt.Manifest.Id != "moda")
                { r.ErrorMessage = $"vote request: {rt.VoteId}/{rt.SourceUserId}/{rt.Title}/{rt.Body}/{rt.ExpectedVotes}/{rt.Manifest.Id}"; return r; }
                r.StepsPassed.Add("ModVoteRequestNetworkEvent roundtrips");
            }

            // 3. ModVoteResponseNetworkEvent
            {
                var resp = new ModVoteResponse { VoteId = "v1", TargetUserId = "host", VoteYes = true };
                var ev = ModVoteResponseNetworkEvent.Create("sender-2", 2, resp);
                var copy = new ModVoteResponseNetworkEvent();
                WireRoundtrip(ev.Serialize, copy.Deserialize);
                var rt = copy.ToResponse();
                if (rt.VoteId != "v1" || rt.TargetUserId != "host" || rt.VoteYes != true)
                { r.ErrorMessage = $"vote response: {rt.VoteId}/{rt.TargetUserId}/{rt.VoteYes}"; return r; }
                r.StepsPassed.Add("ModVoteResponseNetworkEvent roundtrips");
            }

            // 4. ModVoteResultNetworkEvent
            {
                var res = new ModVoteResult { VoteId = "v1", SourceUserId = "host", Passed = true, Manifest = NewVoteManifest("moda") };
                var ev = ModVoteResultNetworkEvent.Create("sender-1", 1, res);
                var copy = new ModVoteResultNetworkEvent();
                WireRoundtrip(ev.Serialize, copy.Deserialize);
                var rt = copy.ToResult();
                if (rt.VoteId != "v1" || rt.SourceUserId != "host" || rt.Passed != true || rt.Manifest.Id != "moda")
                { r.ErrorMessage = $"vote result: {rt.VoteId}/{rt.SourceUserId}/{rt.Passed}/{rt.Manifest.Id}"; return r; }
                r.StepsPassed.Add("ModVoteResultNetworkEvent roundtrips");
            }

            // 5. ModTransferRequestNetworkEvent (carries TargetUserId)
            {
                var req = new ModTransferRequest { ModId = "moda", ModVersion = "1.0.0" };
                var ev = ModTransferRequestNetworkEvent.Create("sender-1", 1, "peer-2", req);
                var copy = new ModTransferRequestNetworkEvent();
                WireRoundtrip(ev.Serialize, copy.Deserialize);
                if (copy.TargetUserId != "peer-2") { r.ErrorMessage = $"transfer request target={copy.TargetUserId}"; return r; }
                var rt = copy.ToRequest();
                if (rt.ModId != "moda" || rt.ModVersion != "1.0.0") { r.ErrorMessage = $"transfer request: {rt.ModId}/{rt.ModVersion}"; return r; }
                r.StepsPassed.Add("ModTransferRequestNetworkEvent roundtrips (with TargetUserId)");
            }

            // 6. ModTransferChunkNetworkEvent (carries TargetUserId + all chunk fields)
            {
                var chunk = new ModTransferChunk
                {
                    ModId = "moda", ModVersion = "1.0.0", ChunkIndex = 2, TotalChunks = 5, TotalBytes = 1000,
                    ChunkBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }), Sha256Hex = "abc123", IsLast = true, FileSuffix = ".pck",
                };
                var ev = ModTransferChunkNetworkEvent.Create("sender-1", 1, "peer-2", chunk);
                var copy = new ModTransferChunkNetworkEvent();
                WireRoundtrip(ev.Serialize, copy.Deserialize);
                if (copy.TargetUserId != "peer-2") { r.ErrorMessage = $"transfer chunk target={copy.TargetUserId}"; return r; }
                var rt = copy.ToChunk();
                if (rt.ModId != "moda" || rt.ChunkIndex != 2 || rt.TotalChunks != 5 || rt.TotalBytes != 1000 ||
                    rt.ChunkBase64 != chunk.ChunkBase64 || rt.Sha256Hex != "abc123" || rt.IsLast != true || rt.FileSuffix != ".pck")
                { r.ErrorMessage = $"transfer chunk: idx={rt.ChunkIndex} total={rt.TotalChunks} bytes={rt.TotalBytes} b64={rt.ChunkBase64} sha={rt.Sha256Hex} last={rt.IsLast} suffix={rt.FileSuffix}"; return r; }
                r.StepsPassed.Add("ModTransferChunkNetworkEvent roundtrips (all chunk fields)");
            }

            // 7. ModConfigSyncNetworkEvent
            {
                var snapshot = new ModConfigSyncSnapshot
                {
                    Entries =
                    {
                        new ModConfigSyncEntry { ModId = "moda", Section = "S", Key = "K1", TypeName = "int", StringValue = "42" },
                        new ModConfigSyncEntry { ModId = "moda", Section = "S", Key = "K2", TypeName = "string", StringValue = "hello" },
                    },
                };
                var ev = ModConfigSyncNetworkEvent.Create("sender-1", 1, snapshot);
                var copy = new ModConfigSyncNetworkEvent();
                WireRoundtrip(ev.Serialize, copy.Deserialize);
                var rt = copy.ToSnapshot();
                if (rt.Entries.Count != 2 || rt.Entries[1].Key != "K2" || rt.Entries[1].StringValue != "hello")
                { r.ErrorMessage = $"config sync: entries={rt.Entries.Count}"; return r; }
                r.StepsPassed.Add("ModConfigSyncNetworkEvent roundtrips");
            }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Verifies the 32700-byte serialized-JSON cap that ModTransferChunkNetworkEvent and
    // ModConfigSyncNetworkEvent enforce in Create (Pratfall's ByteBufferWriter silently
    // truncates strings over 32768 bytes, so the wrappers throw loudly instead).
    public static HelperTestResult RunWireFormatCapTests()
    {
        var r = new HelperTestResult();
        try
        {
            var oversizeChunk = new ModTransferChunk { ModId = "moda", ModVersion = "1.0.0", ChunkBase64 = new string('A', 40000) };
            if (!Throws<InvalidOperationException>(() => ModTransferChunkNetworkEvent.Create("s", 1, "t", oversizeChunk)))
            { r.ErrorMessage = "oversize chunk did not throw InvalidOperationException"; return r; }
            r.StepsPassed.Add("ModTransferChunkNetworkEvent.Create throws over 32700 bytes");

            var oversizeSnapshot = new ModConfigSyncSnapshot
            {
                Entries = { new ModConfigSyncEntry { ModId = "moda", Section = "S", Key = "K", TypeName = "string", StringValue = new string('x', 40000) } },
            };
            if (!Throws<InvalidOperationException>(() => ModConfigSyncNetworkEvent.Create("s", 1, oversizeSnapshot)))
            { r.ErrorMessage = "oversize config snapshot did not throw InvalidOperationException"; return r; }
            r.StepsPassed.Add("ModConfigSyncNetworkEvent.Create throws over 32700 bytes");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Serializes via ByteBufferWriter, copies the written bytes into a ByteBufferReader,
    // and deserializes — the real wire path for an INetworkEvent.
    private static void WireRoundtrip(Action<global::ByteBufferWriter> serialize, Action<global::ByteBufferReader> deserialize)
    {
        var writer = new global::ByteBufferWriter(512);
        serialize(writer);
        var bytes = writer.Buffer.ToMemory().ToArray();

        var reader = new global::ByteBufferReader(512);
        reader.Replace(bytes);
        reader.SeekZero();
        deserialize(reader);
    }

    // Config-format gap-fill for ModConfig: schema-version write/preserve, corrupt-file
    // fallback (+ .bad backup), and type-mismatch fallback that doesn't poison future writes.
    // Complements RunConfigSystemTest (which covers bind/reload/constraint/OnChange). Each
    // case uses a fresh GUID-suffixed mod id so ModConfig's static instance cache doesn't
    // short-circuit the load path on re-runs.
    public static HelperTestResult RunConfigFormatTests()
    {
        var r = new HelperTestResult();
        try
        {
            var folder = ModConfig.ResolveConfigFolder();
            if (folder == null)
            {
                r.StepsPassed.Add("ResolveConfigFolder null (platform not up) — config-format file checks skipped");
                r.Success = true;
                return r;
            }

            // 1. Schema version created on first write and preserved across a value change.
            {
                var id = $"SelfTestCfgSchema_{Guid.NewGuid():N}";
                var path = Path.Combine(folder, ModConfig.Sanitize(id) + ".json");
                try
                {
                    var entry = ModConfig.For(id).Bind("S", "K", 1);
                    if (!File.ReadAllText(path).Contains("_schema_version"))
                    { r.ErrorMessage = "schema version not written on first save"; return r; }
                    entry.Value = 2; // triggers another write
                    if (!File.ReadAllText(path).Contains("_schema_version"))
                    { r.ErrorMessage = "schema version not preserved after value change"; return r; }
                    r.StepsPassed.Add("schema version created + preserved across writes");
                }
                finally { TryDeleteFile(path); }
            }

            // 2. Corrupt config file -> .bad backup + defaults recovered.
            {
                var id = $"SelfTestCfgCorrupt_{Guid.NewGuid():N}";
                var path = Path.Combine(folder, ModConfig.Sanitize(id) + ".json");
                try
                {
                    File.WriteAllText(path, "{ this is not valid json");
                    var entry = ModConfig.For(id).Bind("S", "K", 7);
                    if (entry.Value != 7) { r.ErrorMessage = $"corrupt fallback expected default 7, got {entry.Value}"; return r; }
                    if (!File.Exists(path + ".bad")) { r.ErrorMessage = "corrupt file not backed up to .bad"; return r; }
                    r.StepsPassed.Add("corrupt config -> .bad backup + defaults recovered");
                }
                finally { TryDeleteFile(path); TryDeleteFile(path + ".bad"); }
            }

            // 3. Type mismatch -> default + bad value overwritten (no poison of future writes).
            {
                var id = $"SelfTestCfgType_{Guid.NewGuid():N}";
                var path = Path.Combine(folder, ModConfig.Sanitize(id) + ".json");
                try
                {
                    File.WriteAllText(path, "{ \"Combat\": { \"MaxFlares\": \"notanint\" }, \"_schema_version\": 1 }");
                    var entry = ModConfig.For(id).Bind("Combat", "MaxFlares", 99);
                    if (entry.Value != 99) { r.ErrorMessage = $"type-mismatch expected default 99, got {entry.Value}"; return r; }
                    if (File.ReadAllText(path).Contains("notanint")) { r.ErrorMessage = "bad value not overwritten (poisoned future writes)"; return r; }
                    r.StepsPassed.Add("type mismatch -> default + bad value overwritten");
                }
                finally { TryDeleteFile(path); }
            }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // Log-format coverage for ModLogger: line shape, padded level tags, exception join,
    // ring-buffer order/capacity, per-mod isolation, and UTF-8 file output. Locks in the
    // format that ModCrashReporter embeds before any log/report-format refactor. Uses
    // GUID-suffixed mod ids (fresh ring per run); all .log files cleaned up in finally.
    public static HelperTestResult RunLogFormatTests()
    {
        var r = new HelperTestResult();
        var ids = new List<string>();
        var folder = ModLogger.ResolveLogFolder();
        try
        {
            // 1. Line format: timestamp shape + padded level tag + message.
            {
                var id = $"SelfTestLogFmt_{Guid.NewGuid():N}"; ids.Add(id);
                var log = ModLogger.For(id);
                log.Info("hello"); log.Warn("watch"); log.Debug("dbg"); log.Error("err");
                var lines = ModLogger.GetRecentLines(id);
                if (lines.Count != 4) { r.ErrorMessage = $"expected 4 ring lines, got {lines.Count}"; return r; }
                if (!HasTimestampPrefix(lines[0]) || !lines[0].Contains("[INFO ] hello")) { r.ErrorMessage = $"info line: \"{lines[0]}\""; return r; }
                if (!lines[1].Contains("[WARN ] watch")) { r.ErrorMessage = $"warn line: \"{lines[1]}\""; return r; }
                if (!lines[2].Contains("[DEBUG] dbg")) { r.ErrorMessage = $"debug line: \"{lines[2]}\""; return r; }
                if (!lines[3].Contains("[ERROR] err")) { r.ErrorMessage = $"error line: \"{lines[3]}\""; return r; }
                r.StepsPassed.Add("line format: timestamp shape + padded level tags");
            }

            // 2. Exception join format: TypeName + message.
            {
                var id = $"SelfTestLogExc_{Guid.NewGuid():N}"; ids.Add(id);
                ModLogger.For(id).Error("boom", new InvalidOperationException("the why"));
                var last = ModLogger.GetRecentLines(id)[^1];
                if (!last.Contains("| InvalidOperationException: the why")) { r.ErrorMessage = $"exception join: \"{last}\""; return r; }
                r.StepsPassed.Add("exception join format: TypeName: message");
            }

            // 3. Ring buffer order: oldest -> newest.
            {
                var id = $"SelfTestLogOrder_{Guid.NewGuid():N}"; ids.Add(id);
                var log = ModLogger.For(id);
                log.Info("a"); log.Info("b"); log.Info("c");
                var lines = ModLogger.GetRecentLines(id);
                if (lines.Count != 3 || !lines[0].EndsWith("] a", StringComparison.Ordinal) || !lines[1].EndsWith("] b", StringComparison.Ordinal) || !lines[2].EndsWith("] c", StringComparison.Ordinal))
                { r.ErrorMessage = $"ring order: [{string.Join(" | ", lines)}]"; return r; }
                r.StepsPassed.Add("ring buffer preserves oldest->newest order");
            }

            // 4. Ring buffer capacity / eviction (cap = 200).
            {
                var id = $"SelfTestLogCap_{Guid.NewGuid():N}"; ids.Add(id);
                var log = ModLogger.For(id);
                for (var i = 0; i < 250; i++) log.Info($"line{i}");
                var lines = ModLogger.GetRecentLines(id);
                if (lines.Count != 200) { r.ErrorMessage = $"ring cap expected 200, got {lines.Count}"; return r; }
                if (!lines[0].EndsWith("] line50", StringComparison.Ordinal) || !lines[^1].EndsWith("] line249", StringComparison.Ordinal))
                { r.ErrorMessage = $"eviction: first=\"{lines[0]}\" last=\"{lines[^1]}\""; return r; }
                r.StepsPassed.Add("ring buffer caps at 200 + evicts oldest");
            }

            // 5. GetRecentLines is per-mod isolated.
            {
                var id1 = $"SelfTestLogA_{Guid.NewGuid():N}"; ids.Add(id1);
                var id2 = $"SelfTestLogB_{Guid.NewGuid():N}"; ids.Add(id2);
                ModLogger.For(id1).Info("only-in-1");
                ModLogger.For(id2).Info("only-in-2");
                var l1 = ModLogger.GetRecentLines(id1);
                var l2 = ModLogger.GetRecentLines(id2);
                if (l1.Count != 1 || !l1[0].Contains("only-in-1") || l1[0].Contains("only-in-2")) { r.ErrorMessage = "GetRecentLines(id1) not isolated"; return r; }
                if (l2.Count != 1 || !l2[0].Contains("only-in-2")) { r.ErrorMessage = "GetRecentLines(id2) not isolated"; return r; }
                r.StepsPassed.Add("GetRecentLines isolated per mod id");
            }

            // 6. File output: UTF-8 append + Environment.NewLine terminator.
            if (folder == null)
            {
                r.StepsPassed.Add("ResolveLogFolder null — log file output check skipped");
            }
            else
            {
                var id = $"SelfTestLogFile{Guid.NewGuid():N}"; ids.Add(id);
                var log = ModLogger.For(id);
                log.Info("fileline1"); log.Info("fileline2");
                var path = Path.Combine(folder, id + ".log");
                if (!File.Exists(path)) { r.ErrorMessage = $"log file not written at {path}"; return r; }
                var text = File.ReadAllText(path, System.Text.Encoding.UTF8);
                if (!text.Contains("fileline1") || !text.Contains("fileline2")) { r.ErrorMessage = "log file missing expected lines"; return r; }
                if (!text.Contains("fileline1" + System.Environment.NewLine)) { r.ErrorMessage = "log file not Environment.NewLine-terminated"; return r; }
                r.StepsPassed.Add("log file: UTF-8 append + Environment.NewLine terminator");
            }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
        finally
        {
            if (folder != null)
                foreach (var id in ids)
                    TryDeleteFile(Path.Combine(folder, id + ".log"));
        }
    }

    private static bool HasTimestampPrefix(string line) =>
        System.Text.RegularExpressions.Regex.IsMatch(line, @"^[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3} ");

    // Crash-report structural golden for ModCrashReporter. Locks in report structure
    // (section order, field labels, exception chain incl. InnerException, embedded
    // ModLogger lines) + normalized timestamp/filename shapes before any report-format
    // refactor. Structural-in-order (not byte-exact) so it's robust to whitespace; the
    // exception-chain markers are matched exactly because that format IS the contract.
    public static HelperTestResult RunCrashReportGoldenTests()
    {
        var r = new HelperTestResult();
        try
        {
            var id = $"SelfTestCrash{Guid.NewGuid():N}"; // alphanumeric -> Sanitize identity
            ModLogger.For(id).Info("warming up");

            // Non-thrown exceptions -> null StackTrace -> deterministic body (no stack frames
            // to normalize); nested inner exercises the InnerException chain formatting.
            var inner = new InvalidOperationException("inner boom");
            var outer = new InvalidOperationException("outer boom", inner);

            var bodyMethod = typeof(ModCrashReporter).GetMethod("BuildReportBody",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(string), typeof(Exception) }, null);
            if (bodyMethod == null) { r.ErrorMessage = "BuildReportBody(string,string,Exception) not found"; return r; }
            var body = (string)bodyMethod.Invoke(null, new object[] { id, "OnLoad", outer })!;

            string[] markers =
            {
                "Pratfall Mod Framework",
                "Mod id",
                id,
                "Context",
                "OnLoad",
                "UTC time",
                "Local time",
                "Manifest",
                "manifest not available",
                "Exception",
                "System.InvalidOperationException: outer boom",
                "InnerException[1] -> System.InvalidOperationException: inner boom",
                "Recent log lines (from ModLogger ring buffer)",
                "[INFO ] warming up",
            };
            var bad = FirstOutOfOrderMarker(body, markers);
            if (bad >= 0) { r.ErrorMessage = $"crash-report structure: marker missing/out-of-order: \"{markers[bad]}\""; return r; }
            r.StepsPassed.Add("report structure: header + manifest + exception chain + log section in order");

            if (!System.Text.RegularExpressions.Regex.IsMatch(body, @"UTC time\s*:\s*\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}"))
            { r.ErrorMessage = "UTC time header not ISO-8601"; return r; }
            if (!System.Text.RegularExpressions.Regex.IsMatch(body, @"Local time\s*:\s*\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}"))
            { r.ErrorMessage = "Local time header not ISO-8601"; return r; }
            r.StepsPassed.Add("report header timestamps are ISO-8601 (normalized shape)");

            if (!System.Text.RegularExpressions.Regex.IsMatch(body, @"(?m)^\d{2}:\d{2}:\d{2}\.\d{3} \[INFO \] warming up"))
            { r.ErrorMessage = "embedded log line missing normalized HH:mm:ss.fff timestamp shape"; return r; }
            r.StepsPassed.Add("embedded log line carries normalized timestamp");

            // Filename shape via the real Report path: <sanitized_modid>_<yyyy-MM-ddTHH.mm.ss>.txt
            var folder = ResolveCrashReportFolderForTest();
            if (folder == null)
            {
                r.StepsPassed.Add("crash-report folder null — filename-shape check skipped");
            }
            else
            {
                ModCrashReporter.Report(id, "OnLoad", outer);
                var files = Directory.Exists(folder) ? Directory.GetFiles(folder, id + "_*.txt") : Array.Empty<string>();
                try
                {
                    if (files.Length == 0) { r.ErrorMessage = "no crash-report file written"; return r; }
                    var name = Path.GetFileName(files[0]);
                    var pattern = "^" + System.Text.RegularExpressions.Regex.Escape(id) + @"_\d{4}-\d{2}-\d{2}T\d{2}\.\d{2}\.\d{2}\.txt$";
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, pattern))
                    { r.ErrorMessage = $"crash-report filename shape: \"{name}\""; return r; }
                    r.StepsPassed.Add("crash-report filename: <modid>_<utc-timestamp>.txt");
                }
                finally { foreach (var f in files) TryDeleteFile(f); }
            }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Network-lifecycle coverage (achievable subset — the real-transport dispatch / peer-auth
    // path is a GAP, see AUDIT_NOTES: OnNetworkEventReceived is private, takes a Pratfall
    // NetworkFrameEvent, only runs in Real mode, and its accept-path needs a real lobby).
    // What's covered here, via the public API: fresh layer starts not-ready, the debug-peer
    // guard (attaches only during an Offline session, never for Host), OnTransportReset firing
    // once on a Debug->unhook, and Shutdown cleaning transport state.
    public static HelperTestResult RunNetworkLifecycleTests()
    {
        var r = new HelperTestResult();
        var tree = Engine.GetMainLoop() as SceneTree;
        if (tree == null)
        {
            r.StepsPassed.Add("SceneTree unavailable (not in-game) — network-lifecycle checks skipped");
            r.Success = true;
            return r;
        }

        // If a real lobby is already active we can't isolate the debug-peer guard — skip.
        var probe = new ModNetworkLayer();
        if (probe.IsNetworkReady)
        {
            r.StepsPassed.Add("real network already active — debug-peer guard test skipped (can't isolate from a live lobby)");
            r.Success = true;
            return r;
        }
        r.StepsPassed.Add("fresh ModNetworkLayer starts not-ready");

        var realPath = ProjectSettings.GlobalizePath(DebugPeerConfig.ConfigPath);
        string? backup = null;
        ModNetworkLayer? layerA = null;
        ModNetworkLayer? layerB = null;
        try
        {
            if (!string.IsNullOrEmpty(realPath) && File.Exists(realPath)) { backup = realPath + ".selftest-bak"; File.Copy(realPath, backup, overwrite: true); }
            if (!string.IsNullOrEmpty(realPath)) File.WriteAllText(realPath, "{ \"Enabled\": true, \"LocalUserId\": \"dbg-local\" }");

            // Scenario A: Offline session attaches the debug peer; Shutdown fires OnTransportReset once.
            layerA = new ModNetworkLayer();
            layerA.Initialize(tree, () => new ModLocalState());
            layerA.NotifySessionStarting(SessionKind.Offline);
            if (!layerA.IsNetworkReady) { r.ErrorMessage = "Offline session + debug config should attach Debug transport"; return r; }
            if (layerA.LocalUserId != "dbg-local") { r.ErrorMessage = $"Debug LocalUserId expected dbg-local, got {layerA.LocalUserId}"; return r; }
            r.StepsPassed.Add("debug-peer attaches during Offline session (LocalUserId from config)");

            var resets = 0;
            layerA.OnTransportReset += () => resets++;
            layerA.Shutdown();
            if (resets != 1) { r.ErrorMessage = $"OnTransportReset expected 1 on Debug->unhook, got {resets}"; return r; }
            if (layerA.IsNetworkReady) { r.ErrorMessage = "layer should not be ready after Shutdown"; return r; }
            r.StepsPassed.Add("OnTransportReset fires once on Debug -> unhook (Shutdown); state cleaned");

            // Scenario B: Host session does NOT attach the debug peer (the guard that keeps debug
            // votes out of real multiplayer). Fresh layer because NotifySessionStarting only polls
            // on the first call.
            layerB = new ModNetworkLayer();
            layerB.Initialize(tree, () => new ModLocalState());
            layerB.NotifySessionStarting(SessionKind.Host);
            if (layerB.IsNetworkReady) { r.ErrorMessage = "Host session must not attach the debug peer"; return r; }
            r.StepsPassed.Add("debug-peer does NOT attach for Host session (guard holds)");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
        finally
        {
            try { layerA?.Shutdown(); } catch { }
            try { layerB?.Shutdown(); } catch { }
            if (!string.IsNullOrEmpty(realPath))
            {
                try { if (File.Exists(realPath)) File.Delete(realPath); } catch { }
                if (backup != null) { try { File.Copy(backup, realPath, overwrite: true); File.Delete(backup); } catch { } }
            }
        }
    }

    // ModAssemblyLoader coverage (achievable subset — no fixture mod DLL exists, so the full
    // LoadMod->OnUnload-fires->reload-no-stale cycle is NOT covered here; see AUDIT_NOTES).
    // Covers: bookkeeping no-ops on a fresh loader, the hash-pin tamper-protection refusal,
    // and the collectible-ALC unload mechanic (WeakReference dies after Unload + GC) that
    // ModAssemblyLoader.UnloadMod depends on.
    public static HelperTestResult RunModAssemblyLoaderTests()
    {
        var r = new HelperTestResult();
        try
        {
            var loader = new ModAssemblyLoader();

            // 1. Bookkeeping no-ops on a fresh loader.
            if (loader.IsLoaded("nope")) { r.ErrorMessage = "IsLoaded(unknown) should be false"; return r; }
            loader.UnloadMod("nope"); // must not throw
            if (loader.SnapshotLoadedAssemblies().Count != 0) { r.ErrorMessage = "fresh SnapshotLoadedAssemblies should be empty"; return r; }
            r.StepsPassed.Add("fresh loader: IsLoaded false, UnloadMod no-op, snapshot empty");

            // 2. Hash-pin mismatch refuses to load (tamper protection, throws before assembly load).
            var tmp = Path.Combine(Path.GetTempPath(), $"selftest-alc-{Guid.NewGuid():N}.bin");
            try
            {
                File.WriteAllText(tmp, "not a real assembly");
                if (!Throws<InvalidOperationException>(() => loader.LoadMod("hashtest", tmp, new string('0', 64))))
                { r.ErrorMessage = "LoadMod with mismatched sha256 should throw InvalidOperationException"; return r; }
                if (loader.IsLoaded("hashtest")) { r.ErrorMessage = "mod should not be loaded after hash mismatch"; return r; }
                r.StepsPassed.Add("hash-pin mismatch refuses to load (tamper protection)");
            }
            finally { TryDeleteFile(tmp); }

            // 3. Collectible-ALC unload mechanic: load a sidecar DLL into a collectible ALC,
            //    Unload + GC, assert the WeakReference dies (the guarantee UnloadMod relies on).
            var sidecar = Path.Combine(Path.GetDirectoryName(typeof(ModAssemblyLoader).Assembly.Location) ?? "", "0Harmony.dll");
            if (!File.Exists(sidecar))
            {
                r.StepsPassed.Add("0Harmony sidecar not found — collectible-ALC unload mechanic check skipped");
            }
            else
            {
                var weak = LoadAndUnloadCollectible(sidecar);
                for (var i = 0; i < 10 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
                if (weak.IsAlive) { r.ErrorMessage = "collectible ALC did not unload (WeakReference alive after Unload + GC)"; return r; }
                r.StepsPassed.Add("collectible-ALC unload mechanic: WeakReference dies after Unload + GC");
            }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Loads dllPath into a fresh collectible ALC and unloads it, returning a WeakReference to
    // the ALC. NoInlining so the ALC local can't linger on the caller's frame and block GC.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndUnloadCollectible(string dllPath)
    {
        var alc = new AssemblyLoadContext("selftest-collectible", isCollectible: true);
        alc.LoadFromAssemblyPath(dllPath);
        var weak = new WeakReference(alc);
        alc.Unload();
        return weak;
    }

    // Path-resolution coverage for the user-data-subfolder resolvers, before the deferred
    // PathUtil.ResolveUserDataSubfolder consolidation. Asserts every resolver shares the
    // same user-data root + ends with its expected subfolder, and locks in the create-vs-
    // no-create variance the consolidation must preserve (config/logs create their dir;
    // crash-reports/mods/localization/saves do not). Skips with a note when platform is down.
    public static HelperTestResult RunPathResolutionTests()
    {
        var r = new HelperTestResult();
        try
        {
            var platform = global::Game.Platform;
            string? root = null;
            if (platform != null)
            {
                var raw = platform.GetUserDataPath();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    root = ProjectSettings.GlobalizePath(raw);
                    if (string.IsNullOrWhiteSpace(root)) root = raw;
                }
            }
            if (root == null)
            {
                r.StepsPassed.Add("user-data path unavailable (platform not up) — path-resolution checks skipped");
                r.Success = true;
                return r;
            }

            (string method, Type type, string subfolder, bool creates)[] folderResolvers =
            {
                ("ResolveConfigFolder", typeof(ModConfig), "modframework-config", true),
                ("ResolveLogFolder", typeof(ModLogger), "modframework-logs", true),
                ("ResolveCrashReportFolder", typeof(ModCrashReporter), "modframework-crash-reports", false),
                ("ResolveModsRoot", typeof(ModCrashReporter), "mods", false),
                ("GetUserLocaleFolder", typeof(ModLocalizationHelper), "localization", false),
            };

            foreach (var (method, type, subfolder, creates) in folderResolvers)
            {
                var folder = InvokeStaticStringResolver(type, method);
                if (folder == null) { r.ErrorMessage = $"{type.Name}.{method}() returned null while platform is up"; return r; }
                if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                { r.ErrorMessage = $"{method}: \"{folder}\" not under user-data root \"{root}\""; return r; }
                if (!string.Equals(Path.GetFileName(folder.TrimEnd('/', '\\')), subfolder, StringComparison.Ordinal))
                { r.ErrorMessage = $"{method}: expected subfolder \"{subfolder}\", got \"{Path.GetFileName(folder)}\""; return r; }
                if (creates && !Directory.Exists(folder))
                { r.ErrorMessage = $"{method} should create its directory but {folder} does not exist"; return r; }
            }
            r.StepsPassed.Add("5 folder resolvers share user-data root + expected subfolders; config/logs create their dir");

            // ModSaveDataHelper.GetModSaveFilePath -> <root>/modframework-saves/<id>.json
            var savePath = ModSaveDataHelper.GetModSaveFilePath("SelfTestPathMod");
            if (savePath == null) { r.ErrorMessage = "GetModSaveFilePath returned null while platform is up"; return r; }
            if (!savePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { r.ErrorMessage = $"save path not under root: {savePath}"; return r; }
            if (!savePath.EndsWith(".json", StringComparison.Ordinal)) { r.ErrorMessage = $"save path not .json: {savePath}"; return r; }
            var saveDir = Path.GetFileName(Path.GetDirectoryName(savePath)!);
            if (!string.Equals(saveDir, "modframework-saves", StringComparison.Ordinal)) { r.ErrorMessage = $"save subfolder: {saveDir}"; return r; }
            r.StepsPassed.Add("GetModSaveFilePath -> <root>/modframework-saves/<id>.json");

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Invokes a parameterless static string-returning resolver by name (type or nested type).
    // Handles both internal (ModConfig/ModLogger) and private (ModCrashReporter/Localization) resolvers.
    private static string? InvokeStaticStringResolver(Type owner, string name)
    {
        const System.Reflection.BindingFlags F =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var m = owner.GetMethod(name, F, null, Type.EmptyTypes, null);
        if (m == null)
            foreach (var nested in owner.GetNestedTypes(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                m = nested.GetMethod(name, F, null, Type.EmptyTypes, null);
                if (m != null) break;
            }
        return (string?)m?.Invoke(null, null);
    }

    // DebugPeerConfig coverage. Object-level logic (defaults, normalize idempotence,
    // snapshot, vote resolution, apply-result, mirror) is exercised by constructing the
    // config directly; the TryLoad file path is exercised against the real ConfigPath with
    // a backup/restore (ordered to end deleted, the safe default) since the path is a const.
    public static HelperTestResult RunDebugPeerConfigTests()
    {
        var r = new HelperTestResult();
        try
        {
            // 1. Missing-field defaults.
            {
                var c = new DebugPeerConfig();
                c.Normalize();
                if (c.LocalUserId != "debug-host" || c.PeerUserId != "debug-peer" || !c.MirrorLocalInstalledManifests || !c.DefaultVoteYes)
                { r.ErrorMessage = $"defaults: local={c.LocalUserId} peer={c.PeerUserId} mirror={c.MirrorLocalInstalledManifests} voteYes={c.DefaultVoteYes}"; return r; }
                if (c.InstalledManifests == null || c.EnabledModIds == null || c.VoteResponses == null)
                { r.ErrorMessage = "default collections must be non-null after Normalize"; return r; }
                r.StepsPassed.Add("missing-field defaults applied");
            }

            // 2. Normalize idempotence + self-loop reset (PeerUserId == LocalUserId).
            {
                var c = new DebugPeerConfig
                {
                    LocalUserId = "  Host  ",
                    PeerUserId = "  Host  ",
                    EnabledModIds = new List<string> { " ModA ", "ModA" },
                    VoteResponses = new Dictionary<string, bool> { [" v1 "] = true },
                };
                c.Normalize();
                if (c.LocalUserId != "Host" || c.PeerUserId != "debug-peer")
                { r.ErrorMessage = $"self-loop reset: local={c.LocalUserId} peer={c.PeerUserId}"; return r; }
                if (c.EnabledModIds.Count != 1) { r.ErrorMessage = $"enabled dedup expected 1, got {c.EnabledModIds.Count}"; return r; }
                var local1 = c.LocalUserId; var peer1 = c.PeerUserId; var enabled1 = string.Join(",", c.EnabledModIds); var votes1 = string.Join(",", c.VoteResponses.Keys);
                c.Normalize();
                if (c.LocalUserId != local1 || c.PeerUserId != peer1 || string.Join(",", c.EnabledModIds) != enabled1 || string.Join(",", c.VoteResponses.Keys) != votes1)
                { r.ErrorMessage = "Normalize not idempotent"; return r; }
                r.StepsPassed.Add("normalize idempotent + self-loop PeerUserId reset");
            }

            // 3. CreatePeerSnapshot mirrors local manifests; enabled filtered to installed.
            {
                var local = new ModLocalState { InstalledManifests = { NewVoteManifest("moda"), NewVoteManifest("modb") }, EnabledModIds = { "moda" } };
                var c = new DebugPeerConfig { Enabled = true, PeerUserId = "peerX", MirrorLocalInstalledManifests = true, EnabledModIds = new List<string> { "moda" } };
                var snap = c.CreatePeerSnapshot(local);
                if (snap.UserId != "peerX" || snap.MemberIndex != 1) { r.ErrorMessage = $"snapshot id/index: {snap.UserId}/{snap.MemberIndex}"; return r; }
                if (snap.InstalledManifests.Count != 2) { r.ErrorMessage = $"snapshot mirror installed expected 2, got {snap.InstalledManifests.Count}"; return r; }
                if (snap.EnabledModIds.Count != 1 || snap.EnabledModIds[0] != "moda") { r.ErrorMessage = $"snapshot enabled: [{string.Join(",", snap.EnabledModIds)}]"; return r; }
                r.StepsPassed.Add("CreatePeerSnapshot mirrors local + filters enabled to installed");
            }

            // 4. MirrorLocalInstalledManifests=false uses the config's own manifests.
            {
                var c = new DebugPeerConfig { Enabled = true, MirrorLocalInstalledManifests = false, InstalledManifests = new List<ModManifest> { NewVoteManifest("ownmod") } };
                var snap = c.CreatePeerSnapshot(new ModLocalState { InstalledManifests = { NewVoteManifest("localmod") } });
                if (snap.InstalledManifests.Count != 1 || snap.InstalledManifests[0].Id != "ownmod")
                { r.ErrorMessage = $"mirror=false should use own manifests: [{string.Join(",", snap.InstalledManifests.Select(m => m.Id))}]"; return r; }
                r.StepsPassed.Add("MirrorLocalInstalledManifests=false uses own manifests");
            }

            // 5. ResolveVote: explicit response wins, unknown falls to DefaultVoteYes.
            {
                var c = new DebugPeerConfig { DefaultVoteYes = false, VoteResponses = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["yesmod"] = true } };
                c.Normalize();
                if (!c.ResolveVote("yesmod")) { r.ErrorMessage = "ResolveVote explicit true failed"; return r; }
                if (c.ResolveVote("unknownmod")) { r.ErrorMessage = "ResolveVote unknown should fall to DefaultVoteYes=false"; return r; }
                r.StepsPassed.Add("ResolveVote: explicit response + DefaultVoteYes fallback");
            }

            // 6. ApplyApprovedResult: passed adds the mod; failed is a no-op.
            {
                var local = new ModLocalState { InstalledManifests = { NewVoteManifest("moda") } };
                var passed = new DebugPeerConfig { Enabled = true, MirrorLocalInstalledManifests = true, EnabledModIds = new List<string>() };
                passed.ApplyApprovedResult(new ModVoteResult { VoteId = "v", SourceUserId = "h", Passed = true, Manifest = NewVoteManifest("moda") }, local);
                if (!passed.EnabledModIds.Contains("moda")) { r.ErrorMessage = "ApplyApprovedResult(passed) did not enable moda"; return r; }
                var failed = new DebugPeerConfig { Enabled = true, MirrorLocalInstalledManifests = true, EnabledModIds = new List<string>() };
                failed.ApplyApprovedResult(new ModVoteResult { VoteId = "v", SourceUserId = "h", Passed = false, Manifest = NewVoteManifest("moda") }, local);
                if (failed.EnabledModIds.Contains("moda")) { r.ErrorMessage = "ApplyApprovedResult(failed) should be a no-op"; return r; }
                r.StepsPassed.Add("ApplyApprovedResult: passed enables, failed no-op");
            }

            // 7. TryLoad file path (real ConfigPath, backed up + restored).
            var realPath = ProjectSettings.GlobalizePath(DebugPeerConfig.ConfigPath);
            if (string.IsNullOrEmpty(realPath))
            {
                r.StepsPassed.Add("debug-peer ConfigPath not resolvable — TryLoad file checks skipped");
            }
            else
            {
                string? backup = null;
                try
                {
                    if (File.Exists(realPath)) { backup = realPath + ".selftest-bak"; File.Copy(realPath, backup, overwrite: true); }
                    File.WriteAllText(realPath, "{ \"Enabled\": false }");
                    if (DebugPeerConfig.TryLoad() != null) { r.ErrorMessage = "TryLoad(Enabled=false) should return null"; return r; }
                    File.WriteAllText(realPath, "{ \"Enabled\": true, \"PeerUserId\": \"peerX\" }");
                    var loaded = DebugPeerConfig.TryLoad();
                    if (loaded == null || loaded.PeerUserId != "peerX") { r.ErrorMessage = $"TryLoad(representative): {(loaded == null ? "null" : loaded.PeerUserId)}"; return r; }
                    File.Delete(realPath);
                    if (DebugPeerConfig.TryLoad() != null) { r.ErrorMessage = "TryLoad(missing file) should return null"; return r; }
                    r.StepsPassed.Add("TryLoad: Enabled=false -> null, representative -> loaded, missing -> null");
                }
                finally
                {
                    try { if (File.Exists(realPath)) File.Delete(realPath); } catch { }
                    if (backup != null) { try { File.Copy(backup, realPath, overwrite: true); File.Delete(backup); } catch { } }
                }
            }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    // Returns the index of the first marker not found at or after the previous marker's
    // position (i.e. missing or out of order), or -1 if all markers appear in order.
    private static int FirstOutOfOrderMarker(string text, string[] markers)
    {
        var pos = 0;
        for (var i = 0; i < markers.Length; i++)
        {
            var idx = text.IndexOf(markers[i], pos, StringComparison.Ordinal);
            if (idx < 0) return i;
            pos = idx + markers[i].Length;
        }
        return -1;
    }

    // Pure SessionModResolver coverage (P1): the session-scoped mod-matching decision matrix.
    // No network/UI — constructs host state + peer snapshots directly and asserts the plan.
    public static HelperTestResult RunSessionModResolverTests()
    {
        var r = new HelperTestResult();
        try
        {
            // 1. All players compatible -> both auto-enabled, no warnings/votes.
            {
                var host = new ModLocalState { InstalledManifests = { NewManifestV("moda", "1.0.0"), NewManifestV("modb", "1.0.0") }, EnabledModIds = { "moda", "modb" } };
                var peer = new ModPeerSnapshot { UserId = "p1", InstalledManifests = { NewManifestV("moda", "1.0.0"), NewManifestV("modb", "1.0.0") } };
                var plan = SessionModResolver.Resolve(host, new[] { peer });
                if (plan.Warnings.Count != 0 || plan.PendingVotes.Count != 0) { r.ErrorMessage = $"all-compatible: warnings={plan.Warnings.Count} votes={plan.PendingVotes.Count}"; return r; }
                if (plan.EffectiveSessionModSet.Count != 2 || !plan.EffectiveSessionModSet.Contains("moda") || !plan.EffectiveSessionModSet.Contains("modb"))
                { r.ErrorMessage = $"all-compatible: effective=[{string.Join(",", plan.EffectiveSessionModSet)}]"; return r; }
                r.StepsPassed.Add("all compatible -> both auto-enabled, no warnings/votes");
            }

            // 2. A player is missing the mod -> disabled-for-session + unsafe unanimous override.
            {
                var host = new ModLocalState { InstalledManifests = { NewManifestV("moda", "1.0.0") }, EnabledModIds = { "moda" } };
                var peer = new ModPeerSnapshot { UserId = "p1" }; // installs nothing
                var plan = SessionModResolver.Resolve(host, new[] { peer });
                if (plan.EffectiveSessionModSet.Contains("moda")) { r.ErrorMessage = "missing: moda must not be effective"; return r; }
                if (!plan.DisabledForSession.Contains("moda")) { r.ErrorMessage = "missing: moda must be disabled-for-session"; return r; }
                if (!plan.Warnings.Any(w => w.Kind == SessionWarningKind.MissingForPlayer && w.ModId == "moda" && w.AffectedPlayers.Contains("p1")))
                { r.ErrorMessage = "missing: expected MissingForPlayer(moda, p1)"; return r; }
                var vote = plan.PendingVotes.FirstOrDefault(d => d.ModId == "moda");
                if (vote == null || vote.Safety != SessionDecisionSafety.Unsafe || vote.VoteRule != SessionVoteRule.Unanimous)
                { r.ErrorMessage = "missing: expected unsafe unanimous pending vote"; return r; }
                r.StepsPassed.Add("missing-for-player -> disabled + unsafe unanimous override");
            }

            // 3. Version mismatch -> disabled + VersionMismatch warning.
            {
                var host = new ModLocalState { InstalledManifests = { NewManifestV("moda", "2.0.0") }, EnabledModIds = { "moda" } };
                var peer = new ModPeerSnapshot { UserId = "p1", InstalledManifests = { NewManifestV("moda", "1.0.0") } };
                var plan = SessionModResolver.Resolve(host, new[] { peer });
                if (plan.EffectiveSessionModSet.Contains("moda")) { r.ErrorMessage = "version: moda must not be effective"; return r; }
                if (!plan.Warnings.Any(w => w.Kind == SessionWarningKind.VersionMismatch && w.ModId == "moda")) { r.ErrorMessage = "version: expected VersionMismatch(moda)"; return r; }
                r.StepsPassed.Add("version-mismatch -> disabled + VersionMismatch warning");
            }

            // 4. Missing dependency (required mod not in the host's enabled set) -> disabled + warning.
            {
                var depMod = new ModManifest { Id = "moda", Name = "moda", Version = "1.0.0", Multiplayer = new ModMultiplayer { Requires = new List<string> { "modlib" } } };
                var host = new ModLocalState { InstalledManifests = { depMod }, EnabledModIds = { "moda" } };
                var peer = new ModPeerSnapshot { UserId = "p1", InstalledManifests = { NewManifestV("moda", "1.0.0") } };
                var plan = SessionModResolver.Resolve(host, new[] { peer });
                if (!plan.Warnings.Any(w => w.Kind == SessionWarningKind.MissingDependency && w.ModId == "moda")) { r.ErrorMessage = "dep: expected MissingDependency(moda)"; return r; }
                if (plan.EffectiveSessionModSet.Contains("moda")) { r.ErrorMessage = "dep: moda must not be effective"; return r; }
                r.StepsPassed.Add("missing-dependency -> disabled + MissingDependency warning");
            }

            // 5. Declared conflict -> later-listed mod disabled (unanimous keep-both), earlier stays effective.
            {
                var a = NewManifestV("moda", "1.0.0");
                var b = new ModManifest { Id = "modb", Name = "modb", Version = "1.0.0", Multiplayer = new ModMultiplayer { ConflictsWith = new List<string> { "moda" } } };
                var host = new ModLocalState { InstalledManifests = { a, b }, EnabledModIds = { "moda", "modb" } };
                var peer = new ModPeerSnapshot { UserId = "p1", InstalledManifests = { NewManifestV("moda", "1.0.0"), NewManifestV("modb", "1.0.0") } };
                var plan = SessionModResolver.Resolve(host, new[] { peer });
                if (!plan.EffectiveSessionModSet.Contains("moda")) { r.ErrorMessage = "conflict: moda (earlier) should stay effective"; return r; }
                if (plan.EffectiveSessionModSet.Contains("modb")) { r.ErrorMessage = "conflict: modb (later) should be disabled"; return r; }
                if (!plan.Warnings.Any(w => w.Kind == SessionWarningKind.DeclaredConflict && w.ModId == "modb")) { r.ErrorMessage = "conflict: expected DeclaredConflict(modb)"; return r; }
                var vote = plan.PendingVotes.FirstOrDefault(d => d.ModId == "modb");
                if (vote == null || vote.VoteRule != SessionVoteRule.Unanimous) { r.ErrorMessage = "conflict: expected unanimous keep-both vote for modb"; return r; }
                r.StepsPassed.Add("declared conflict -> later disabled (unanimous keep-both), earlier effective");
            }

            // 6. Resolve never mutates the host's saved enabled state.
            {
                var host = new ModLocalState { InstalledManifests = { NewManifestV("moda", "1.0.0") }, EnabledModIds = { "moda" } };
                host.Normalize();
                var before = new HashSet<string>(host.EnabledModIds, StringComparer.OrdinalIgnoreCase);
                SessionModResolver.Resolve(host, Array.Empty<ModPeerSnapshot>());
                if (!before.SetEquals(host.EnabledModIds)) { r.ErrorMessage = $"host enabled state mutated: now [{string.Join(",", host.EnabledModIds)}]"; return r; }
                r.StepsPassed.Add("Resolve does not mutate host saved enabled state");
            }

            r.Success = true;
            return r;
        }
        catch (Exception ex)
        {
            r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    private static ModManifest NewManifestV(string id, string version) =>
        new() { Id = id, Name = id, Version = version };

    private static string? ResolveCrashReportFolderForTest()
    {
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var raw = platform.GetUserDataPath();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var globalized = ProjectSettings.GlobalizePath(raw);
            if (string.IsNullOrWhiteSpace(globalized)) globalized = raw;
            return Path.Combine(globalized, "modframework-crash-reports");
        }
        catch { return null; }
    }

    // Helpers for the helper tests above.

    private static string? ResolveUserLocaleFolderForTest()
    {
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var userData = platform.GetUserDataPath();
            if (string.IsNullOrWhiteSpace(userData)) return null;
            var globalized = ProjectSettings.GlobalizePath(userData);
            if (string.IsNullOrWhiteSpace(globalized)) globalized = userData;
            return Path.Combine(globalized, "localization");
        }
        catch { return null; }
    }

    // Reflection-only — reads a private static delegate field and counts its
    // invocation list. Used to verify subscribe/unsubscribe mechanics without
    // having to invoke the delegate (which would trigger real game side effects).
    private static int GetStaticDelegateCount(Type t, string fieldName)
    {
        var field = t.GetField(fieldName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        if (field == null) throw new InvalidOperationException($"{t.FullName}.{fieldName} not found");
        var del = field.GetValue(null) as Delegate;
        return del?.GetInvocationList().Length ?? 0;
    }
}
