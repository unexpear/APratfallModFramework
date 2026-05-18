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
