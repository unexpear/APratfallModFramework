# Framework audit notes

Working notes for deferred audit findings. Delete bullets when they are applied
or intentionally rejected. Do not keep a completed-history trail.

## Deferred Law-1/Law-2/Law-3 findings

### ModManager.cs

- Finding: EnableMod / DisableMod branch extraction.
  Why deferred: lifecycle and load-order behavior-adjacent.
  Fix sketch: extract official / framework / manifest-only branches into private helpers while preserving call order and every state-set.
  Re-trigger: focused lifecycle-readability pass after current audit completes.

- Finding: Initialize split into named startup phases.
  Why deferred: entry-point order is behavior-sensitive.
  Fix sketch: extract BootstrapSubsystems / LoadInitialModState / WireCallbacks / StartBackgroundTimers; preserve exact call order.
  Re-trigger: after ModManager split decision.

- Finding: LoadLocalAssemblyMods loop-body extraction.
  Why deferred: load-order critical; 6 exit paths each set state differently.
  Fix sketch: extract per-mod load attempt while preserving every flag transition.
  Re-trigger: lifecycle-readability pass.

- Finding: Per-mod init foreach pattern repeated across Initialize, NotifyWorkshopItemInstalled, OnTransferChunkReceived, OnWorkshopItemInstalled.
  Why deferred: variants differ (some set _modEnabled, some set DLL paths, some don't); helper signature may get ugly.
  Fix sketch: evaluate a NormalizeAndRegisterMod helper with explicit flags only if signature stays under 4 params.
  Re-trigger: after IsInstanceValid sweep or lifecycle pass.

- Finding: ApplyVoteResult extraction.
  Why deferred: vote resolution behavior boundaries.
  Fix sketch: extract ResolveLocalMatchEnable / ApplyStretchIfAvailable / HandlePassedVote / HandleFailedVote.
  Re-trigger: vote-cluster focused patch.

- Finding: BuildVoteRequestsForPeer yield-block dedup.
  Why deferred: yield-in-IEnumerable adds friction; near-identical ModVoteRequest construction at 2 sites.
  Fix sketch: extract BuildVoteRequest(manifest, sourceUserId, body, expectedVotes).
  Re-trigger: vote-cluster focused patch.

- Finding: Shutdown 3× try/catch wrap (WorkshopSubscriber, NativeModUiSuppressor, OfficialModBridge).
  Why deferred: part of cross-cutting HarmonyPatchSet lifecycle helper.
  Fix sketch: shared TryShutdownSubsystem(name, action) OR a HarmonyPatchSet base helper.
  Re-trigger: after cross-cutting lifecycle pass.

### ModConfig.cs

- Finding: Schema-version init JsonObject template repeated at 3 sites (lines 260, 268, 276).
  Why deferred: persistence-adjacent; safer in a focused persistence pass.
  Fix sketch: private static helper CreateConfigDocumentTemplate() returning new JsonObject { ["_schema_version"] = CurrentSchemaVersion }.
  Re-trigger: persistence-focused readability pass.

- Finding: Path-resolution dance repeated in EnsureLoaded + WriteFile.
  Why deferred: persistence-adjacent; small win, low urgency.
  Fix sketch: private EnsureFilePath() helper that returns whether a file path is now resolved.
  Re-trigger: persistence-focused readability pass.

### Bootstrap.cs

- Finding: SetFullRect and IsActionPressed helpers are duplicated between Bootstrap.cs and MainMenuIntegration.cs.
  Why deferred: Bootstrap creates startup overlays before MainMenuIntegration.Install runs, so it should not depend on MainMenuIntegration.
  Fix sketch: create a neutral framework-level helper file (e.g. GodotControlExtensions.cs) containing pure helpers SetFullRect(Control) and IsActionPressed(InputEvent, string).
  Re-trigger: cross-cutting cleanup pass.

- Finding: TrySetSentinelAttributes pattern repeats 3 times in WriteLoadedSentinel and RemoveLoadedSentinel.
  Why deferred: tiny dedup; not worth a helper unless sentinel attribute logic grows.
  Fix sketch: private static TrySetSentinelAttributes(string path, FileAttributes attrs) wrapping File.SetAttributes in catch(Exception).
  Re-trigger: if sentinel logic gains another attribute-setting site.

### MainMenuIntegration group

- Finding: TryInject step extraction.
  Why deferred: hot-path UI injection (polled every 0.5s); behavior-adjacent.
  Fix sketch: extract FindMenuButtonContainer / PickStyleDonor / CreateModsButton / LogInjectionFailureDiagnostics.
  Re-trigger: after MainMenuIntegration cross-partial pass completes.

- Finding: Section extraction pattern (RenderManifestSection / BuildModCard / etc.).
  Why deferred: section ordering affects UI output; needs ModsDialog (largest panel) to confirm shape.
  Fix sketch: per-section private helpers in InspectionPanel + ModsDialog. ScanPanel already has AddScanGroup. SettingsPanel needs different shape (widget factories, see below).
  Re-trigger: dedicated post-audit pass; do InspectionPanel + ModsDialog together for consistency.

- Finding: SettingsPanel CreateWidgetForEntry per-type extraction.
  Why deferred: 137-line dispatch with closure-captured refresh actions; callback-risk.
  Fix sketch: extract CreateBoolWidget / CreateStringChoicesWidget / CreateStringWidget / CreateEnumWidget / CreateRangeSliderWidget / CreateSpinBoxWidget / CreateFallbackWidget. Each preserves its own closure capture exactly.
  Re-trigger: focused SettingsPanel patch AFTER cross-partial pass.

- Finding: SettingsPanel focus chain.
  Why deferred: not a code-readability fix; suspected UX gap (multi-row layout without WireVerticalFocus).
  Fix sketch: design call (rely on Godot auto-traversal vs explicit per-row + Close chain). Needs runtime UX check.
  Re-trigger: when SettingsPanel gets UX polish or after a user report of broken keyboard nav.

- Finding: TryShowNextConflictPrompt sibling-conflict iteration vs single-shot.
  Why deferred: behavior-adjacent (changes how multiple conflicts are surfaced).
  Fix sketch: none yet — flagged for future UX discussion.
  Re-trigger: if conflict-prompt UX is reworked.

### ModCrashReporter.cs

- Finding: ResolveCrashReportFolder + ResolveModsRoot are near-identical, only differing in final folder name (6 sites of user-data-subfolder resolution: ModConfig.ResolveConfigFolder, ModCrashReporter.ResolveCrashReportFolder, ModCrashReporter.ResolveModsRoot, ModLogger.ResolveLogFolder, ModSaveDataHelper save-folder, ModLocalizationHelper.GetUserLocaleFolder; plus 2 test-only sites in ModFrameworkSelfTest).
  Why deferred: persistence/report-output-adjacent file path resolution.
  Fix sketch: extract private ResolveUserDataSubfolder(string subfolderName), or promote to PathUtil if cross-file reuse is justified.
  Re-trigger: persistence/report-format readability pass.

- Finding: Sanitize() filesystem-safe name transform duplicated across ModConfig, ModCrashReporter, ModLocalizationHelper, ModLogger, and ModSaveDataHelper (5 sites).
  Why deferred: persistence/report-output-adjacent filename construction.
  Fix sketch: PathUtil.SanitizeForFilename(string) shared helper.
  Re-trigger: persistence-focused readability pass.

### ModLogger.cs

- Finding: ResolveLogFolder is near-identical to ResolveConfigFolder, ResolveCrashReportFolder, ResolveModsRoot, ModSaveDataHelper's save-folder, and ModLocalizationHelper.GetUserLocaleFolder (6 sites of user-data-subfolder resolution; plus 2 test-only sites in ModFrameworkSelfTest).
  Why deferred: persistence/report-output-adjacent user-data path resolution.
  Fix sketch: shared PathUtil.ResolveUserDataSubfolder(name, createIfMissing) helper; account for create-dir variance (ModLogger + ModConfig + ModSaveDataHelper + ModLocalizationHelper create, ModCrashReporter doesn't).
  Re-trigger: persistence-focused readability pass gated by format-contract regression coverage.

- Finding: Sanitize filesystem-safe name transform is duplicated across ModConfig, ModLocalizationHelper, ModCrashReporter, ModLogger, and ModSaveDataHelper (5 sites).
  Why deferred: persistence/report-output-adjacent filename construction; behavior drift could orphan config/log/report/localization/savedata files.
  Fix sketch: PathUtil.SanitizeForFilename(string) shared helper.
  Re-trigger: persistence-focused readability pass gated by format-contract regression coverage.

### ModAssemblyLoader.cs

- Finding: Harmony patch failures in ApplyDeclaredPatches are uncaught; one bad patch fails the whole mod load.
  Why deferred: behavior decision; fail-fast is defensible because a half-patched mod may be unsafe, but it hurts mod authors when one bad patch prevents otherwise valid patches from loading.
  Fix sketch: consider per-patch try/catch around harmony.Patch, with logging/crash-report routing, after load-regression coverage exists.
  Re-trigger: mod-load regression coverage or mod-author reports about one bad patch killing the whole mod.

### ModLocalizationHelper.cs

- Finding: GetUserLocaleFolder is another user-data-subfolder resolution site, bringing the shared path-resolution duplicate count to 6.
  Why deferred: persistence-adjacent; gated by PathUtil.ResolveUserDataSubfolder consolidation.
  Fix sketch: replace GetUserLocaleFolder with PathUtil.ResolveUserDataSubfolder("localization", createIfMissing: true) once the shared helper lands.
  Re-trigger: persistence-focused readability pass.

- Finding: Sanitize is duplicated across 5 sites; all 5 are behaviorally EQUIVALENT. (Verified: each keeps letters/digits/hyphen and maps everything else to '_'. ModSaveDataHelper additionally lists '_' explicitly, but the fallback also yields '_', so the output is identical for every input.) The earlier "ModLocalizationHelper diverges on hyphens, others may not" note was WRONG — they all keep hyphens.
  Why deferred: PathUtil.SanitizeForFilename consolidation is still persistence-sensitive (outputs feed user file paths), but it is NO LONGER blocked by char-set disagreement.
  Coverage: sanitize golden + cross-impl equivalence exists (ModFrameworkSelfTest.RunFilenameSanitizeTests, commit fbb7f14). Execution status: compiled + behavior-verified by reading; NOT yet executed in-game.
  Re-trigger: persistence-focused readability pass; consolidation now gated only by an in-game run of the sanitize coverage + a one-time check that no live user filenames change. Do NOT apply the PathUtil.SanitizeForFilename refactor yet.

- Finding: ForceEnableUserLocales is intentionally one-way with no Shutdown/reset path.
  Why deferred: documentation/lifecycle note; it should be excluded from the canonical HarmonyPatchSet lifecycle pattern.
  Fix sketch: document that ForceEnableUserLocales is process-lifetime persistent because user locales loaded mid-session cannot be cleanly unloaded.
  Re-trigger: HarmonyPatchSet lifecycle pass.

### ReflectionHelper.cs

- Finding: GetTypesSafe adoption is incomplete; raw Assembly.GetTypes() remains in ModAssemblyLoader.cs and ModExceptionFilter.cs.
  Why deferred: behavior decision, not pure dedup. ModAssemblyLoader currently fails fast if a mod assembly has missing dependencies, which may be safer than partial type loading. ModExceptionFilter scans the game assembly and is lower-risk.
  Fix sketch: decide per site. Keep ModAssemblyLoader raw if fail-fast remains policy; consider GetTypesSafe for ModExceptionFilter as defensive robustness. Gate ModAssemblyLoader changes behind load/unload regression coverage.
  Re-trigger: ModAssemblyLoader fail-fast-vs-partial-load decision and load/unload regression coverage.

### ModExceptionFilter.cs

- Finding: Missing Shutdown method; inconsistent with WorkshopSubscriber, NativeModUiSuppressor, OfficialModBridge, and SessionStartHooks.
  Why deferred: lifecycle change; patches currently survive process lifetime.
  Fix sketch: add Shutdown that calls harmony.UnpatchAll(harmony.Id) and resets _patched; wire into ModManager.Shutdown alongside the other Harmony-patch lifecycle classes.
  Re-trigger: cross-cutting HarmonyPatchSet lifecycle pass.

- Finding: No outer try/catch on Install.
  Why deferred: lifecycle-adjacent; changes init failure semantics if Pratfall renames Log.OnException.
  Fix sketch: wrap reflection lookup and harmony.Patch in try/catch matching OfficialModBridge.Install; log on failure and leave _patched = false so a future Install can retry.
  Re-trigger: alongside Shutdown method addition.

### ModVoteSession.cs

- Finding: VoteState.Manifest is stored on StartVote but never read by ModVoteSession itself; ModManager keeps its own copy in _activeVoteRequests[voteId].
  Why deferred: internal-api change to StartVote signature; should be paired with VoteCoordinator split work.
  Status: UNBLOCKED by vote-tally coverage (commit fa4c730); not yet applied.
  Fix sketch: remove manifest parameter from StartVote and Manifest field from VoteState; update ModManager.QueueVoteRequest caller.
  Re-trigger: VoteCoordinator / vote-system cleanup pass (coverage gate now satisfied).

- Finding: Vote behavior contract is undocumented.
  Why deferred: documentation pass, not a 3-laws readability patch.
  Fix sketch: document strict-majority pass rule, ties-fail behavior, no-timeout behavior, case-insensitive voter dedup, and ClearAllVotes external reset contract.
  Re-trigger: pre-v1.0 documentation pass.

### OfficialModBridge.cs

- Finding: Install's 3× AccessTools.Method + null-check + Patch block is the prototype for the cross-cutting HarmonyPatchSet helper.
  Why deferred: callback + internal-api; should land with the broader HarmonyPatchSet lifecycle helper, not standalone.
  Fix sketch: TryPatchPrefix(string methodName, string prefixName, bool logOnMissing) returning bool; first call uses logOnMissing=true, later optional patches use false.
  Re-trigger: cross-cutting HarmonyPatchSet lifecycle pass.

- Finding: Install partial-failure followed by later Install may create a new Harmony instance with the same ID while earlier patches might still exist.
  Why deferred: behavior; needs Harmony behavior verification, not readability work.
  Fix sketch: investigate whether duplicate patching with the same Harmony ID is harmless or throws; if unsafe, Install should clean partial state or call Shutdown first.
  HarmonyPatchSet design requirements (partial-failure recovery):
    - TryInstall must be reentrant after a failed install.
    - Partial patch application must be rolled back or tracked per-patch.
    - A failed install must not leave one patched method active while _installed remains false.
    - Later Install calls must not double-patch already-patched methods with a fresh Harmony instance.
  Re-trigger: HarmonyPatchSet lifecycle pass.

- Finding: OfficialModBridge.Install / Shutdown are the canonical Harmony-patchset lifecycle pattern.
  Why deferred: documentation cross-link, not code change.
  Fix sketch: when adding lifecycle handling to SessionStartHooks / ModExceptionFilter / WorkshopHook, copy the OfficialModBridge shape: outer Install try/catch, degraded log, installed flag, Shutdown UnpatchAll try/catch, state reset.
  Re-trigger: cross-cutting lifecycle pass.

### NativeModUiSuppressor.cs

- Finding: `_applied = true` is set before the Apply try block, while OfficialModBridge sets `_installed = true` only after patch success.
  Why deferred: HarmonyPatchSet design choice; lifecycle cluster currently uses inconsistent idempotency models.
  Fix sketch: choose one canonical model during the HarmonyPatchSet lifecycle pass: retry-on-failure, explicit-Shutdown-to-retry, or rollback-on-partial-failure.
  Re-trigger: HarmonyPatchSet lifecycle pass.

- Finding: Silent half-applied state if Apply fails after `_applied = true`.
  Why deferred: behavior-sensitive; tied to the idempotency and partial-failure recovery model.
  Fix sketch: track per-patch applied state, rollback partial patches, or set `_applied` only after successful patch application.
  Re-trigger: alongside the HarmonyPatchSet idempotency-model decision.

### NativeModListMirror.cs

- Finding: native ModManifest.LoadedAssembly setter side-effects are unverified.
  Why deferred: behavior verification, not readability.
  Fix sketch: Cecil-dump native ModManifest and confirm LoadedAssembly is an auto-property/backing field with no setter side effects.
  Re-trigger: Pratfall update / Cecil-verification sweep.

- Finding: Sync mutates ModManager.Mods and LoadedMods in place.
  Why deferred: theoretical; no current evidence of non-main-thread iteration.
  Fix sketch: only address if Pratfall or vanilla mods introduce multi-threaded list iteration; possible future fix is snapshot/double-buffered swap.
  Re-trigger: multi-thread mod-list access pattern observed.

- Finding: NativeModListMirror and OfficialModBridge are a paired suppress-and-mirror design.
  Why deferred: documentation cross-link, not code change.
  Fix sketch: when applying any OfficialModBridge lifecycle work, verify NativeModListMirror.Sync still runs after OfficialModBridge.Install and after framework scan/load state is ready.
  Re-trigger: HarmonyPatchSet lifecycle pass.

### SessionStartHooks.cs

- Finding: Missing Shutdown method; inconsistent with WorkshopSubscriber, NativeModUiSuppressor, and OfficialModBridge.
  Why deferred: lifecycle change; patches currently survive process lifetime, and adding unpatch behavior changes shutdown semantics.
  Fix sketch: add Shutdown that calls harmony.UnpatchAll(harmony.Id), nulls _beforeSessionStart, and resets _installed; wire into ModManager.Shutdown.
  Re-trigger: cross-cutting HarmonyPatchSet lifecycle pass.

- Finding: No outer try/catch on Install.
  Why deferred: lifecycle-adjacent; changes initialization failure semantics if future Pratfall signatures drift.
  Fix sketch: wrap harmony.Patch calls in try/catch matching OfficialModBridge.Install; log and leave _installed false on failure.
  Re-trigger: alongside Shutdown method addition.

### ModFrameworkSelfTest.cs

- Finding: try/catch wrapper `r.ErrorMessage = $"{ex.GetType().Name}: {ex.Message}"; return r;` repeats across helper-test methods.
  Why deferred: self-test harness refactor; should wait until test-runner output shape is formalized.
  Fix sketch: private RunHelperTest wrapper that owns try/catch and standardized error formatting.
  Re-trigger: when more helper-test methods are added or test-runner output becomes formalized.

- Finding: temp-dir setup for `user://stress-tmp` repeats across stress tests.
  Why deferred: self-test harness refactor.
  Fix sketch: private EnsureStressTmpDir() helper returning the globalized path.
  Re-trigger: self-test organization pass.

- Finding: `Convert.ToHexString(SHA256.HashData(...))` repeats across hash assertions.
  Why deferred: small dedup; inline is currently clear at assertion sites.
  Fix sketch: private Sha256Hex(byte[]) helper if hash-checking tests expand.
  Re-trigger: if another hash-heavy test family lands.

- Finding: ResolveCrashReportFolderForTest and ResolveUserLocaleFolderForTest duplicate user-data-subfolder resolution.
  Why deferred: gated by broader PathUtil.ResolveUserDataSubfolder consolidation.
  Fix sketch: replace both with PathUtil.ResolveUserDataSubfolder once shared helper exists.
  Re-trigger: PathUtil.ResolveUserDataSubfolder rollout.

- Finding: helper-test catch paths can leak event subscriptions / IDisposable handles on exception.
  Why deferred: behavior change to test failure semantics; should be paired with helper-cluster subscription/disposal coverage.
  Fix sketch: use try/finally or using-style cleanup around registrations and event subscriptions.
  Re-trigger: helper-cluster subscription/disposal regression coverage.

- Finding: ModFrameworkSelfTest is missing several safety-gate coverage families.
  Why deferred: coverage work, not readability work.
  Fix sketch: add tests for filename sanitize golden cases, ModLogger log format, crash-report golden sample, lifecycle hooks, ModAssemblyLoader unload, vote tally, vote-flow/UI, network lifecycle, and the missing NetworkEvent wrapper roundtrips.
  Re-trigger: before any refactor gated by those coverage families.

### ModAPI.cs

- Finding: BackupResource / RestoreResource / SaveResource accept arbitrary absolute paths; a malicious mod could touch files outside Pratfall's intended data area.
  Why deferred: api + behavior; path-scoping policy decision needed before changing public behavior.
  Fix sketch: add an allowed-root check for Pratfall-controlled paths, then reject or throw on paths outside the allowed roots.
  Re-trigger: api hardening pass.

- Finding: Exception contract is undocumented for LoadResource / BackupResource / RestoreResource / SaveResource.
  Why deferred: documentation policy decision; current behavior lets exceptions bubble to mod callers.
  Fix sketch: document null-return / thrown-exception behavior in XmlDoc and MOD_AUTHORS_GUIDE_FRAMEWORK.md.
  Re-trigger: pre-v1.0 docs pass.

- Finding: No XmlDoc on public mod-author API.
  Why deferred: documentation pass; no behavior change, but weak IntelliSense support for mod authors.
  Fix sketch: add XmlDoc comments to all public methods with usage notes and exception behavior.
  Re-trigger: pre-v1.0 docs pass.

- Finding: RestoreResource deletes the .bak after restore, while BackupResource does not overwrite an existing .bak.
  Why deferred: behavior contract decision; single-restore and first-backup-wins semantics need confirmation before documenting or changing.
  Fix sketch: confirm intended semantics, then document them in XmlDoc and the framework guide.
  Re-trigger: pre-v1.0 docs pass.

### DebugPeerConfig.cs

- Finding: JSON schema for user://modframework-debug-peer.json is not covered by format-contract regression coverage.
  Why deferred: coverage work, not readability; debug-only, but still a developer-visible persistence contract.
  Fix sketch: add DebugPeerConfig roundtrip coverage: load/save/normalize idempotence, Enabled=false short-circuit, missing-field defaults, and debug-peer snapshot generation.
  Re-trigger: format-contract regression coverage pass.

### NativeDialogBridge.cs

- Finding: TryShow has silent false-return branches without diagnostics.
  Why deferred: diagnostic logging policy decision; logging every failed lookup could add noise.
  Fix sketch: add specific GD.PrintErr messages, likely log-once-per-process per failure reason.
  Re-trigger: diagnostics-policy pass or native-dialog-not-showing bug report.

- Finding: SetField cascade uses magic field-name strings and silently ignores missing fields.
  Why deferred: reflection-failure diagnostic behavior change.
  Fix sketch: make SetField return bool, aggregate missing fields, and log missing Pratfall field names.
  Re-trigger: alongside native-dialog diagnostics work.

- Finding: No outer try/catch on TryShow.
  Why deferred: callback + behavior; reflection failures currently bubble.
  Fix sketch: wrap reflection block in try/catch + GD.PrintErr and preserve false-return contract.
  Re-trigger: vote-flow/UI regression coverage.

- Finding: wrappedOnComplete does not catch exceptions from onComplete(accepted).
  Why deferred: callback contract change.
  Fix sketch: clear static state first, then wrap onComplete in try/catch + GD.PrintErr.
  Re-trigger: vote-flow/UI regression coverage.

- Finding: FindNodeByName is manual recursion even though Godot has Node.FindChild.
  Why deferred: behavior; built-in traversal semantics may differ.
  Fix sketch: investigate semantic equivalence, then replace only if behavior matches.
  Re-trigger: investigation pass after vote-flow regression coverage exists.

### WorkshopHook.cs

- Finding: ModManager subscribes via `WorkshopHook.OnWorkshopItemInstalled += OnWorkshopItemInstalled` but never unsubscribes.
  Why deferred: lifecycle change; production impact is likely low, but editor/hot-restart could retain a stale ModManager reference.
  Fix sketch: add `WorkshopHook.OnWorkshopItemInstalled -= OnWorkshopItemInstalled` to ModManager.Shutdown alongside other event unsubscriptions.
  Re-trigger: cross-cutting lifecycle cleanup pass.

- Finding: No try/catch around `OnWorkshopItemInstalled?.Invoke(...)`.
  Why deferred: WorkshopHook is still a stub awaiting real Workshop wiring; exception behavior should be decided when the Steam/Workshop callback is connected.
  Fix sketch: either isolate subscribers with foreach over GetInvocationList + try/catch + GD.PrintErr, or document that subscriber exceptions propagate.
  Re-trigger: when Tim wires the Workshop install callback.

- Finding: No Clear / Shutdown method on WorkshopHook.
  Why deferred: static event survives process lifetime; currently low practical impact, but inconsistent with the broader lifecycle-cleanup direction.
  Fix sketch: add `WorkshopHook.Clear()` that nulls the event, then call it from ModManager.Shutdown.
  Re-trigger: cross-cutting lifecycle cleanup pass.

### ModNetworkLayer.cs

- Finding: Broadcast/Send methods share repeated happy-path shape (transport check, hooked check, localUserId check, Normalize, wrapper Create, SendReliableGlobalEvent).
  Why deferred: wire + behavior + callback; each method has method-specific debug behavior that does not factor cleanly.
  Fix sketch: consider BroadcastReliable<T>(payload, eventId, eventName) for the real-transport happy path while leaving debug paths inline.
  Re-trigger: wire-format coverage now EXISTS (8836d16) so the helper investigation is unblocked, but full application also needs network-lifecycle coverage (separate gate, not yet written).

- Finding: OnNetworkEventReceived switch repeats get-event → sender-validation → optional target-validation → invoke.
  Why deferred: wire + behavior; dispatch changes could alter peer-auth or receive semantics.
  Fix sketch: consider DispatchIfValidSender<T>() with separate targeted-transfer variant.
  Re-trigger: wire-format coverage now EXISTS (8836d16); dispatch refactor also needs network-lifecycle coverage (peer-auth / target-validation paths) before application.

### VoteUI.cs

- Finding: Vote-resolution dispatch repeats at 3 sites: SubmitVote, OnTimeout, and OnNativeDialogCompleted.
  Why deferred: callback-flow risk; each path differs in native-dialog cleanup behavior (DismissActive needed vs just clearing flag vs no native involvement).
  Fix sketch: extract private Resolve(bool voteYes, bool needNativeDismiss) only after vote-flow/timer regression coverage exists.
  Re-trigger: vote-tally / vote-flow regression coverage.

### ModP2PTransfer.cs

- Finding: TickOutgoing has two near-identical remove-completed-transfer + cursor-adjustment blocks with subtly different cursor math.
  Why deferred: behavior-adjacent; round-robin scheduler fairness depends on exact cursor adjustment per scenario.
  Fix sketch: extract RemoveCompletedTransfer(key, idx, isEntryStale) helper with parameterized cursor-adjustment mode; verify with scheduler-fairness test before approval.
  Re-trigger: transfer protocol regression coverage.

- Finding: OnChunkReceived is ~100 lines doing validate/register, chunk validation, decode/store, reassemble/verify, and persist.
  Why deferred: wire + persistence behavior; ReceiveResult return paths protect transfer protocol invariants.
  Fix sketch: extract ValidateIncomingChunk, DecodeAndStoreChunk, AssembleAndVerifyPayload, and PersistTransferredFile while preserving every ReceiveResult path byte-for-byte.
  Re-trigger: transfer protocol regression coverage.

### ModNetworkContracts.cs

Wire-format coverage: EXISTS (ModFrameworkSelfTest.RunWireFormatRoundtripTests + RunWireFormatCapTests, commit 8836d16). Covers Create -> Serialize(ByteBufferWriter) -> Deserialize(ByteBufferReader) -> ToX roundtrip for all 7 wrappers (ModManifestSnapshot, ModConfigSync, ModVoteRequest, ModVoteResponse, ModVoteResult, ModTransferRequest, ModTransferChunk) plus the 32700-byte cap throw on the two wrappers that enforce it. Execution status: compiled + behavior-verified by reading; NOT yet executed in-game. This is roundtrip-equality coverage ONLY — there is NO golden cross-version payload coverage yet (that waits on the _proto_version field decision).

- Finding: 7 NetworkEvent wrapper classes share verbatim 4-method shape: Create / ToX / Serialize / Deserialize.
  Why deferred: wire-format-adjacent; generic base or template approach needs regression coverage to confirm serialized shape remains unchanged.
  Status: UNBLOCKED IN PRINCIPLE by wire-format coverage (8836d16); not yet applied.
  Fix sketch: evaluate NetworkEventBase<TPayload> or static NetworkEventJson<TPayload> delegation helper, then verify byte-for-byte ByteBufferWriter output equivalence.
  Re-trigger: NetworkEvent dedup refactor pass (coverage gate satisfied).

- Finding: 32700-byte JSON cap constant and check duplicated in ModTransferChunkNetworkEvent and ModConfigSyncNetworkEvent.
  Why deferred: wire-format-adjacent and should be paired with the NetworkEvent dedup decision.
  Status: UNBLOCKED IN PRINCIPLE by wire-format coverage (8836d16); not yet applied.
  Fix sketch: ModNetworkJson.EnsureJsonFitsWireLimit(json, contextName) shared helper.
  Re-trigger: with NetworkEvent dedup.

## Split candidates

- Finding: Vote logic from ModManager.cs (~370 lines).
  Why deferred: architecture-level move; cleanest-bounded cluster.
  Fix sketch: partial-class split first (ModManager.Vote.cs), evaluate true VoteCoordinator composition only after partial split reveals coupling.
  Re-trigger: after readability audit completes.

- Finding: Workshop integration from ModManager.cs (~150 lines).
  Why deferred: separate concern, smaller than Vote.
  Fix sketch: partial-class split (ModManager.Workshop.cs) after Vote split.
  Re-trigger: after Vote split or next major ModManager rework.

- Finding: CSync (~100), Transfer (~150), Compatibility (~100) clusters.
  Why deferred: bounded but lower priority; Transfer is lifecycle-coupled (Finalize calls EnableMod).
  Fix sketch: partial-class splits only after Vote/Workshop. Compatibility (RefreshCompatibilityReport + BuildModIssueTooltip + TryShowNextConflictPrompt + AppendCompatibilityWarnings) is the cleanest of the three.
  Re-trigger: if ModManager remains hard to navigate after Vote/Workshop split.

- Finding: MainMenuIntegration base partial sub-splits.
  Why deferred: lower priority than ModManager splits.
  Fix sketch: MainMenuIntegration.cs (lifecycle) + .UiHelpers.cs (labels/theme/host/sizing) + .TreeUtils.cs (tree walkers + theme harvest + diagnostics).
  Re-trigger: after ModManager split if MainMenuIntegration.cs is still feeling overloaded.

## Safety tasks

- Task #2 (GodotObject lifetime / IsInstanceValid sweep): COMPLETE.
  One fix applied: NativeDialogBridge.DismissActive now guards GodotObject.IsInstanceValid before invoking the cached DialogUI node's cancel method (a scene teardown can free the node while the C# ref stays non-null).
  Cleared (do not re-flag):
    - ModNetworkLayer._hookedLobbyManager / _hookedEventManager — Cecil-verified plain System.Object, not GodotObject; GC-managed; existing null checks correct.
    - ModDropPoolHelper._pool / _entry — Cecil-verified Godot.Resource (RefCounted); live C# refs keep refcount >= 1, can't be freed.
    - ModManager._voteUI — persistent tree.Root child, freed only in Dispose with atomic null, never self-frees.
    - VoteUI child controls — owned children, share VoteUI lifetime.
    - ModButtonPromptHelper — fetches ButtonPrompBarController.Instance fresh per call, no cached ref.
    - Timer Timeout captures — all method-group bindings on long-lived `this`; Bootstrap auto-close lambda already IsInstanceValid-guarded; grab_focus deferrals use fresh local refs.
    - Show*Panel QueueFree race — out of scope (fresh GetNodeOrNull lookups, not cached refs; deferred-free name-collision already mitigated in ModsDialog via rename-before-free).
    - MainMenuIntegration._dialogToggles — already protected (SyncDialogToggle uses IsInstanceValid).
  Key rule (future sweeps): IsInstanceValid applies ONLY to cached Node/GodotObject refs that can be freed under a still-live C# wrapper. It does NOT apply to plain C# objects (GC-managed) or Resource/RefCounted refs held alive by C# references.

- Finding: No format-contract regression coverage.
  Why deferred: not readability work; infrastructure needed before touching persistence, wire, report-output, or filename-sanitization behavior.
  Fix sketch: add to ModFrameworkSelfTest — (a) config persistence roundtrip (save/reload/corrupt-fallback/type-mismatch/schema), (b) NetworkEvent wire-format roundtrip + golden cross-version payloads for all 7 wrappers; transfer-specific subtests covering in-order chunks, out-of-order chunks, duplicate chunks, hash mismatch, size cap, write failure, and scheduler fairness; network-lifecycle subtests covering transport mode transitions, hook/unhook, debug-peer mode guard (Offline-session-only), peer-auth rejection (non-lobby sender), targeted transfer rejection (wrong TargetUserId), and OnTransportReset firing exactly once per transition, (c) crash-report golden sample with timestamp normalization, (d) filename Sanitize golden inputs/outputs, (e) ModLogger log-line format + file output (timestamp format, padded level tags, exception join format, UTF-8 file append, Environment.NewLine terminator, ring buffer order/capacity), (f) lifecycle-hook coverage: SessionStartHooks install idempotence, Host/Offline dispatch, callback exception isolation, last-install-wins behavior, Bootstrap startup/shutdown sentinel behavior, and ModManager subsystem teardown order, (g) DebugPeerConfig roundtrip / schema-default tests for user://modframework-debug-peer.json (load/save/normalize idempotence, Enabled=false short-circuit, missing-field defaults, debug-peer snapshot generation).
  Re-trigger: before any persistence / wire / report-format / path-sanitization / log-format refactor. Gates these deferred refactors:
    - ModConfig CreateConfigDocumentTemplate + EnsureFilePath extractions
    - ModNetworkContracts NetworkEvent dedup + 32700-byte cap helper
    - ModCrashReporter ResolveUserDataSubfolder + Sanitize family extraction
    - cross-file PathUtil.SanitizeForFilename promotion
    - cross-file PathUtil.ResolveUserDataSubfolder promotion

- Finding: UsesOfficialLoader always returns false; dead-branch scaffolding.
  Why deferred: cleanup-only; comment in ModManifest.cs documents it.
  Fix sketch: delete dead UsesOfficialLoader branches in ModManager.cs (5+ call sites all gated by always-false), then delete the no-op OfficialModBridge.EnableMod / DisableMod / IsEnabled / CanResolveManifest bridges, then delete the UsesOfficialLoader method itself.
  Re-trigger: lifecycle pass or cleanup pass.

- Helper-cluster subscription/disposal regression coverage: EXISTS (ModFrameworkSelfTest.RunGameEventDispatchTests + RunDropPoolSelectiveDisposeTest, commit 3894e3e).
  Covered: ModGameEventHelper subscribe->fire->handler called, Dispose->fire->not called, double-Dispose safe, throwing-handler isolation, string tag filtering, GameplayTag.Equals filtering across separate instances, duplicate subscriptions fire twice (no dedup), invalid args throw (ArgumentNullException / ArgumentException); ModDropPoolHelper selective Dispose removes only its own entry.
  Already covered by existing tests: ModSaveDataHelper (RunSaveDataHelperTest), ModLocalizationHelper (RunLocalizationHelperTest), ModButtonPromptHelper (RunButtonPromptHelperTest) cleanup models.
  Execution status: compiled + behavior-verified by reading; NOT yet executed in-game (runs via the stress-mod harness; uses GameEventBus reflection + GameplayTag/PackedScene construction which need the Godot runtime). Upgrade to "executed successfully" after the first in-game harness run.
  Unblocks (refactors NOT yet applied): ModGameEventHelper Subscribe* dedup discussion; helper-cluster subscription/disposal cleanup decisions.

- Finding: No ModAssemblyLoader load/unload regression coverage.
  Why deferred: not readability work; AssemblyLoadContext unload behavior is fragile and needs explicit tests before any loader refactor.
  Fix sketch: add tests for Harmony apply/remove across load/unload cycle, OnUnload fires exactly once after OnLoad throw, reload-same-id leaves no stale state, shared assembly refs use host context not ALC, SnapshotLoadedAssemblies doesn't prevent unload, ALC.Unload + GC actually frees the assembly (use WeakReference + force GC to verify).
  Re-trigger: before any ModAssemblyLoader refactor (gates the deferred Harmony-per-patch try/catch item).

- Finding: MOD_AUTHORS_GUIDE_FRAMEWORK.md lacks an exception-handling-in-framework-helpers section.
  Why deferred: needs full helper-cluster audit to classify each helper callback by frequency.
  Fix sketch: add "Exception handling in framework helpers" section explaining frequency-based exception routing — rare lifecycle callbacks (OnLoad, OnUnload, settings widget creation) route to ModCrashReporter; high-frequency callbacks (game events, button presses, per-frame work) log to godot.log only to avoid crash-report flooding. Include per-helper classification table.
  Re-trigger: pre-v1.0 docs pass (gating helper audits are complete).

- Vote-tally regression coverage: EXISTS (ModFrameworkSelfTest.RunVoteTallyTests, commit fa4c730).
  Covered: strict-majority pass, ties fail, no resolution before ExpectedVotes reached, duplicate voter ignored, case-insensitive voter dedup, totalPlayers clamped to >= 1, ClearAllVotes mid-tally fires no resolution, late vote after resolution fires no second result, duplicate StartVote no-op.
  Execution status: compiled + behavior-verified by reading ModVoteSession; NOT yet executed in-game (runs via the stress-mod harness; ModVoteSession uses GD.Print which needs the Godot runtime). Upgrade this line to "executed successfully" after the first in-game harness run.
  Unblocks (refactors NOT yet applied): ModVoteSession dead VoteState.Manifest cleanup; VoteCoordinator split planning.

- Finding: No vote-flow/UI regression coverage.
  Why deferred: not readability work; VoteUI behavior depends on timers, native-dialog fallback, callback ordering, and scene-tree UI state.
  Fix sketch: add tests/manual harness for native-dialog fallback after retry window, 15s timeout auto-NO, DismissVote no-callback behavior, MouseFilter visible/hidden behavior, and callback dispatch ordering across SubmitVote / OnTimeout / OnNativeDialogCompleted.
  Re-trigger: before any VoteUI callback/timer/native-dialog refactor (gates the deferred VoteUI Resolve helper extraction).

- Finding: No Pratfall API contract verification / Cecil sweep.
  Why deferred: infrastructure verification, not readability work.
  Fix sketch: Cecil-verify native Pratfall contracts the framework relies on: ModManifest.LoadedAssembly setter behavior, ModManager.Mods / LoadedMods accessor semantics, DialogUIShowOptions fields, NetworkLobbyManagerBase / NetworkEventManager lifetime, and other reflected/native API assumptions.
  Re-trigger: before refactors that depend on native Pratfall member semantics, or after Pratfall updates.

- Finding: No protocol version field on NetworkEvent wrappers.
  Why deferred: protocol design decision, not pure readability.
  Fix sketch: add explicit `_proto_version` field to each NetworkEvent's JSON payload + receiver-side compatibility checks. Define explicit semver-style compatibility rules.
  Re-trigger: before any breaking wire-format change OR before framework v1.0 if peer-version-mismatch should be a first-class concept.

## Watchlists

- Finding: CreateActionButton multi-purpose helper.
  Status: REJECTED LOCKED.
  Why: variance dimensions = 7+ across 6 satellites (text, width formula, height formula, parent layout, SizeFlagsHorizontal, focus handling, callback). No reasonable single helper. Inline construction is more readable.
  Re-trigger: only if a future redesign narrows variance to ≤4 dimensions.

- Finding: Fire-once dismiss helper (bool fired + Resolve + canvasLayer.QueueFree).
  Status: REJECTED.
  Why: re-entry guard + QueueFree are UI lifecycle behavior that should stay visible at the call site. Inline form is the domain idiom.
  Re-trigger: only if a future site proves a clearly safer pattern that doesn't hide the lifecycle.

- Finding: AddCardIconButton (icon-button row helper).
  Why deferred: confirmed 3 sites in ModsDialog only.
  Status: KEEP IN-FILE for ModsDialog. Reconsider promotion to base partial only if a third icon-button site emerges anywhere (SettingsPanel Reset uses 40×0.7·refH vs ModsDialog's 46×0.7·refH — close but not identical).
  Re-trigger: if a 4th icon-button site appears.

## Helper-cluster watchlist

All 5 helpers audited:
- ModGameEventHelper.cs — IDisposable handle, unsubscribe from GameEventBus
- ModButtonPromptHelper.cs — context-string cleanup via ClearContext(context)
- ModDropPoolHelper.cs — IDisposable handle, removes pool entry by reference equality; caches Godot.Resource refs (Task #2 suspect)
- ModSaveDataHelper.cs — IDisposable handle, unsubscribe from SavegameManager.OnGameWillSave; persistence-sensitive by default
- ModLocalizationHelper.cs — IDisposable handle, deletes locale file + triggers localization rescan (Register side); ForceEnableUserLocales is a separate one-way Harmony patch, excluded from this cluster

**Helper-cluster final verdict:**

- Shared ModSubscription / ModRegistrationHandle abstraction: REJECTED.
  Reason: 4 of 5 helpers use IDisposable, but the Dispose bodies are semantically different:
  - ModGameEventHelper: unsubscribe from GameEventBus
  - ModDropPoolHelper: remove a specific pool entry by reference equality
  - ModSaveDataHelper: unsubscribe from SavegameManager.OnGameWillSave
  - ModLocalizationHelper: delete locale file + trigger localization rescan
  Named registration classes are clearer than a generic cleanup wrapper. The 5th helper makes the Dispose-body diversity more pronounced, not less.
- Shared try/catch + log wrapper: REJECTED.
  Reason: external-call sites differ by what they wrap (Pratfall API call vs user-callback dispatch) and exception routing depends on frequency policy; a generic wrapper hides that distinction.
- Shared argument-validation helper: REJECTED.
  Reason: per-helper validation details (which args, which exception types, which messages) are clearer inline; no single-call-site simplification.

**Outliers / per-file notes:**
- ModButtonPromptHelper: context-string cleanup outlier (no IDisposable); keep inline cleanup pattern.
- ModDropPoolHelper: Task #2 suspect for cached Godot.Resource refs (_pool, _entry); verify lifetime semantics before any pool-helper refactor.
- ModSaveDataHelper: persistence-sensitive (writes save-data JSON); any future refactor gated by format-contract regression coverage.

**Handler exception policy (frequency-based, applies cluster-wide):**
- Rare lifecycle callbacks (~2 per mod, e.g., OnLoad/OnUnload, settings-widget creation) → log + ModCrashReporter.
- User-action frequency (1-100 per session, e.g., ModButtonPromptHelper.Show, settings value-changed) → log only.
- High-frequency (>100 per session, e.g., ModGameEventHelper handlers, per-frame) → log only.
- Helper wrapping a Pratfall API call (no user code dispatched) → log only regardless of frequency (the exception came from Pratfall, not user code; crash report has no actionable info).

**Documentation policy for helper-cluster files:**
- Pre-v1.0 docs pass should add an "Exception handling in framework helpers" section to MOD_AUTHORS_GUIDE_FRAMEWORK.md with the frequency-based policy table + per-helper classification.
- Decision deferred: where per-method exception-policy doc lives (XML doc for IntelliSense, file-level comment for grep, or both).

Re-trigger: only if a 6th helper appears or one of the existing 5 grows substantially.

## Helper approval rule (reference)

Approve a helper only if the call site becomes strictly simpler than inline.
Reject if signature needs: 5+ positional params, more than 1 callback param,
out/ref params, both a config object AND a callback, unclear Func/Action params,
or hidden UI side effects that matter during audit.

## Lifecycle order reference

Canonical startup/shutdown sequences and cross-file invariants. Reference
material for the deferred lifecycle-readability refactors; not a task list.

### Startup (Bootstrap.Init → InitOnMainThread)

1. Bootstrap.Init() — Interlocked guard prevents double-init
2. CallDeferred → InitOnMainThread on main thread
3. Engine.GetMainLoop() as SceneTree
4. ModRuntime.MarkGodotRuntimeReady()         ← marks BEFORE manager construction
5. new ModManager() + Initialize(tree)         ← see ModManager.Initialize sub-order
6. WriteLoadedSentinel()                       ← only on success
7. ShowStartupStatus(...)                      ← user-visible confirmation

### ModManager.Initialize sub-order

1. NativeModUiSuppressor.Apply
2. OfficialModBridge.Install
3. WorkshopSubscriber.Apply
4. SessionStartHooks.Install
5. ManifestManager.ScanLocalMods               ← scans disk; depends on FrameworkProfile
6. FrameworkModStateStore.LoadState
7. Per-mod normalize/register loop
8. VoteUI created + tree.Root.AddChild
9. MainMenuIntegration.Install                 ← UI delegates wired
10. ModExceptionFilter.Install
11. Network layer event subscriptions
12. CSync subscription (ModConfig.OnSyncedValueChanged)
13. LoadLocalAssemblyMods                      ← loads DLLs; runs user code
14. PersistDesiredEnabledState
15. SyncNativeModList                          ← vanilla ModManager.Mods mirror
16. _networkLayer.Initialize
17. Background timers (Mods button re-inject, transfer pump)

### Shutdown (Bootstrap.Shutdown)

1. _instance.Shutdown() — see ModManager.Shutdown sub-order
2. ModRuntime.MarkGodotRuntimeStopped()
3. _instance = null
4. _initialized = 0
5. RemoveLoadedSentinel()

### ModManager.Shutdown sub-order

1. _networkLayer.Shutdown
2. UnloadAllMods
3. WorkshopSubscriber.Shutdown (try/catch wrap)
4. NativeModUiSuppressor.Shutdown (try/catch wrap)
5. OfficialModBridge.Shutdown (try/catch wrap)

### Paired-design invariants

When modifying one side of these pairs, verify the other still satisfies the invariant:

- OfficialModBridge ↔ NativeModListMirror — OfficialModBridge suppresses native loader file I/O; NativeModListMirror mirrors framework state into native ModManager.Mods / LoadedMods. Mirror.Sync must run after Bridge.Install and after framework scan/load state is ready.

- OfficialModBridge ↔ NativeModUiSuppressor — both neuter native ModManager surfaces: loader/file I/O vs UI/anti-cheat-facing counts. They should share the same Harmony lifecycle policy.

- WorkshopHook ↔ ManifestManager.ScanWorkshopMods — Workshop install notification should trigger framework rescan/refresh behavior.

- SessionStartHooks ↔ ModManager.PersistDesiredEnabledState — session start is the last safe point for applying/persisting desired enabled state before gameplay.

- VoteUI ↔ NativeDialogBridge — VoteUI may use native dialog presentation; NativeDialogBridge.DismissActive must remain safe when a peer resolves before the user responds.

### Cross-file invariants (DO NOT REORDER)

- **MarkGodotRuntimeReady BEFORE new ModManager** — ModManager construction / Initialize uses Godot APIs that crash without runtime
- **WriteLoadedSentinel AFTER successful Initialize** — sentinel claims success; writing on failure would mislead users
- **Network layer event subscriptions BEFORE LoadLocalAssemblyMods** — mods loading may fire events that subscribers need to catch
- **MainMenuIntegration.Install BEFORE _networkLayer.Initialize** — network init may broadcast events that UI must display
- **LoadLocalAssemblyMods AFTER per-mod normalize/register loop** — mod load needs _desiredEnabled / _modSessionAvailable populated
- **SyncNativeModList AFTER LoadLocalAssemblyMods** — vanilla ModManager.Mods needs loaded-assembly handles populated
- **_instance.Shutdown BEFORE MarkGodotRuntimeStopped** — Shutdown calls Godot APIs (QueueFree, etc.); needs runtime alive

## Risk labels (reference)

- **none**: rename / extract-only, no behavior / visual / external-format impact
- **callback**: callback order, QueueFree timing, event wiring, focus chain, closure capture
- **visual**: node hierarchy, sizing, stylebox, color, font, CanvasLayer, layout
- **persistence**: saved config/state shape, defaults, schema, file format (failure mode: corrupted/un-loadable user files)
- **wire**: network serialization, peer compatibility, protocol shape (failure mode: peers can't talk, silent desync)
- **api**: public mod-author surface — constructors, public properties, attribute fields, public-method signatures (failure mode: mods break on framework update)
- **internal-api**: framework-internal cross-file surface — enum values, struct shapes, method signatures consumed by another framework file (failure mode: framework doesn't compile until coordinated edits land; verify with full build clean)
- **both / compound**: multiple categories at once (e.g. "wire + callback")

**Risks compose additively.** A patch labeled "wire + callback" needs both wire-format regression coverage AND callback-order review. A patch labeled "internal-api + behavior" needs build verification AND behavior regression coverage. A patch labeled "persistence + api" needs roundtrip tests AND public-API stability check. Apply each label's verification independently.
