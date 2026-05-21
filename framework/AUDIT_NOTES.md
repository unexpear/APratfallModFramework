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

- Finding: ResolveCrashReportFolder + ResolveModsRoot are near-identical, only differing in final folder name.
  Why deferred: persistence/report-output-adjacent file path resolution.
  Fix sketch: extract private ResolveUserDataSubfolder(string subfolderName), or promote to PathUtil if cross-file reuse is justified.
  Re-trigger: persistence/report-format readability pass.

- Finding: Sanitize() filesystem-safe name transform duplicated across ModCrashReporter, ModConfig, and ModLocalizationHelper.
  Why deferred: persistence/report-output-adjacent filename construction.
  Fix sketch: PathUtil.SanitizeForFilename(string) shared helper.
  Re-trigger: persistence-focused readability pass.

### ModLogger.cs

- Finding: ResolveLogFolder is near-identical to ResolveConfigFolder, ResolveCrashReportFolder, and ResolveModsRoot (4 sites of user-data-subfolder resolution).
  Why deferred: persistence/report-output-adjacent user-data path resolution.
  Fix sketch: shared PathUtil.ResolveUserDataSubfolder(name, createIfMissing) helper; account for create-dir variance (ModLogger + ModConfig create, ModCrashReporter doesn't).
  Re-trigger: persistence-focused readability pass gated by format-contract regression coverage.

- Finding: Sanitize filesystem-safe name transform is duplicated across ModConfig, ModLocalizationHelper, ModCrashReporter, and ModLogger.
  Why deferred: persistence/report-output-adjacent filename construction; behavior drift could orphan config/log/report/localization files.
  Fix sketch: PathUtil.SanitizeForFilename(string) shared helper.
  Re-trigger: persistence-focused readability pass gated by format-contract regression coverage.

### ModAssemblyLoader.cs

- Finding: Harmony patch failures in ApplyDeclaredPatches are uncaught; one bad patch fails the whole mod load.
  Why deferred: behavior decision; fail-fast is defensible because a half-patched mod may be unsafe, but it hurts mod authors when one bad patch prevents otherwise valid patches from loading.
  Fix sketch: consider per-patch try/catch around harmony.Patch, with logging/crash-report routing, after load-regression coverage exists.
  Re-trigger: mod-load regression coverage or mod-author reports about one bad patch killing the whole mod.

### ModVoteSession.cs

- Finding: VoteState.Manifest is stored on StartVote but never read by ModVoteSession itself; ModManager keeps its own copy in _activeVoteRequests[voteId].
  Why deferred: internal-api change to StartVote signature; should be paired with VoteCoordinator split work.
  Fix sketch: remove manifest parameter from StartVote and Manifest field from VoteState; update ModManager.QueueVoteRequest caller.
  Re-trigger: VoteCoordinator / vote-system cleanup pass.

- Finding: Vote behavior contract is undocumented.
  Why deferred: documentation pass, not a 3-laws readability patch.
  Fix sketch: document strict-majority pass rule, ties-fail behavior, no-timeout behavior, case-insensitive voter dedup, and ClearAllVotes external reset contract.
  Re-trigger: pre-v1.0 documentation pass.

### ModNetworkLayer.cs

- Finding: Broadcast/Send methods share repeated happy-path shape (transport check, hooked check, localUserId check, Normalize, wrapper Create, SendReliableGlobalEvent).
  Why deferred: wire + behavior + callback; each method has method-specific debug behavior that does not factor cleanly.
  Fix sketch: consider BroadcastReliable<T>(payload, eventId, eventName) for the real-transport happy path while leaving debug paths inline.
  Re-trigger: with wire-format regression coverage.

- Finding: OnNetworkEventReceived switch repeats get-event → sender-validation → optional target-validation → invoke.
  Why deferred: wire + behavior; dispatch changes could alter peer-auth or receive semantics.
  Fix sketch: consider DispatchIfValidSender<T>() with separate targeted-transfer variant.
  Re-trigger: with wire-format regression coverage.

- Finding: _hookedLobbyManager and _hookedEventManager are cached Pratfall network-manager references with unverified lifetime semantics.
  Why deferred: needs Cecil verification of NetworkLobbyManagerBase / NetworkEventManager lifetime.
  Fix sketch: verify whether they can become invalid/freed during lobby teardown or host migration; if yes, add GodotObject.IsInstanceValid guards in UnhookTransport before -= operations.
  Re-trigger: Task #15 IsInstanceValid sweep.

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

- Finding: 7 NetworkEvent wrapper classes share verbatim 4-method shape: Create / ToX / Serialize / Deserialize.
  Why deferred: wire-format-adjacent; generic base or template approach needs regression coverage to confirm serialized shape remains unchanged.
  Fix sketch: evaluate NetworkEventBase<TPayload> or static NetworkEventJson<TPayload> delegation helper, then verify byte-for-byte ByteBufferWriter output equivalence.
  Re-trigger: after wire-format regression coverage exists.

- Finding: 32700-byte JSON cap constant and check duplicated in ModTransferChunkNetworkEvent and ModConfigSyncNetworkEvent.
  Why deferred: wire-format-adjacent and should be paired with the NetworkEvent dedup decision.
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

- Finding: GodotObject lifetime / IsInstanceValid sweep.
  Why deferred: safety bug-surface, not readability work; tracked separately as Task #15.
  Fix sketch: audit cached GodotObject/Node/UI refs crossing QueueFree, callbacks, timers, deferred calls, static fields, dictionaries, tree teardown. Use GodotObject.IsInstanceValid on offending sites.
  Highest-suspicion targets: ModManager._voteUI, Show*Panel existing.QueueFree race window, ModButtonPromptHelper, Timer Timeout captures, WorkshopHook subscribers.
  Note: MainMenuIntegration._dialogToggles is ALREADY protected (SyncDialogToggle uses IsInstanceValid).
  Re-trigger: Task #15.

- Finding: No format-contract regression coverage.
  Why deferred: not readability work; infrastructure needed before touching persistence, wire, report-output, or filename-sanitization behavior.
  Fix sketch: add to ModFrameworkSelfTest — (a) config persistence roundtrip (save/reload/corrupt-fallback/type-mismatch/schema), (b) NetworkEvent wire-format roundtrip + golden cross-version payloads for all 7 wrappers; transfer-specific subtests covering in-order chunks, out-of-order chunks, duplicate chunks, hash mismatch, size cap, write failure, and scheduler fairness; network-lifecycle subtests covering transport mode transitions, hook/unhook, debug-peer mode guard (Offline-session-only), peer-auth rejection (non-lobby sender), targeted transfer rejection (wrong TargetUserId), and OnTransportReset firing exactly once per transition, (c) crash-report golden sample with timestamp normalization, (d) filename Sanitize golden inputs/outputs, (e) ModLogger log-line format + file output (timestamp format, padded level tags, exception join format, UTF-8 file append, Environment.NewLine terminator, ring buffer order/capacity).
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

- Finding: No helper-cluster subscription/disposal regression coverage.
  Why deferred: not readability work; needed before changing mod-author helper subscription/disposal behavior.
  Fix sketch: add tests for subscribe→fire, dispose→unsubscribe, double-dispose idempotence, handler exception isolation, tag filtering (string and GameplayTag.Equals), duplicate subscriptions (no dedup expected), argument-validation throw types (ArgumentNullException + ArgumentException for empty strings).
  Re-trigger: before any helper-cluster subscription/refactor work (gates the deferred ModGameEventHelper Subscribe* dedup + future shared ModSubscription helper).

- Finding: No ModAssemblyLoader load/unload regression coverage.
  Why deferred: not readability work; AssemblyLoadContext unload behavior is fragile and needs explicit tests before any loader refactor.
  Fix sketch: add tests for Harmony apply/remove across load/unload cycle, OnUnload fires exactly once after OnLoad throw, reload-same-id leaves no stale state, shared assembly refs use host context not ALC, SnapshotLoadedAssemblies doesn't prevent unload, ALC.Unload + GC actually frees the assembly (use WeakReference + force GC to verify).
  Re-trigger: before any ModAssemblyLoader refactor (gates the deferred Harmony-per-patch try/catch item).

- Finding: No vote-tally regression coverage.
  Why deferred: not readability work; needed before vote behavior refactors.
  Fix sketch: add ModVoteSession tests for strict-majority pass, ties fail, duplicate voter dedup, case-insensitive voter dedup, resolution threshold, minimum-player clamp, ClearAllVotes mid-tally, late votes after resolution, and duplicate StartVote no-op behavior.
  Re-trigger: before any vote-session tally/quorum/timeout refactor (gates the dead-Manifest-field cleanup + future VoteCoordinator split).

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

Track cross-file patterns across the mod-author helper cluster:
- ModGameEventHelper.cs (audited)
- ModButtonPromptHelper.cs (un-audited)
- ModDropPoolHelper.cs (un-audited)
- ModSaveDataHelper.cs (un-audited)

Initial observations (from ModGameEventHelper.cs):
- IDisposable subscription wrapper pattern.
- Idempotent Dispose nulls private callback field.
- Argument validation uses ArgumentNullException.ThrowIfNull and ArgumentException for empty strings.
- User callback invocation wrapped in try/catch.
- Handler exceptions log via GD.PrintErr and do NOT route to ModCrashReporter (intentional; see frequency-based policy below).
- **Handler exception policy**: use frequency-based handling. Rare lifecycle callbacks like OnLoad / OnUnload (called ~twice per mod lifetime) route to ModCrashReporter for full debug context. High-frequency callbacks like game-event handlers (hundreds-to-thousands per session) stay log-only to avoid crash-report flooding from a single repeating bug. Future helpers should follow this rule based on expected call frequency.
- Filter-then-invoke pattern implemented inside event lambdas.

Extraction candidates (decisions deferred until helper cluster fully audited):
- Shared subscription/registration handle IF IDisposable wrappers recur in 2+ helpers.
- Shared callback exception wrapper IF the try/catch/log pattern is identical across helpers.
- Shared argument-validation helper ONLY if it stays simpler than inline validation.

Re-trigger: after ModButtonPromptHelper, ModDropPoolHelper, and ModSaveDataHelper audits complete.

**Documentation policy for helper-cluster files:**
- High-frequency callback helpers should document their exception policy before v1.0.
- Current policy: high-frequency mod callbacks catch and log handler exceptions only; they do not route each occurrence through ModCrashReporter to avoid report flooding. Rare lifecycle callbacks (OnLoad/OnUnload, ~twice per mod lifetime) DO route through ModCrashReporter.
- Decision deferred: where exception-policy doc lives (per-method XML doc for IntelliSense, file-level comment for grep, or both).
- Re-trigger: after ModButtonPromptHelper, ModDropPoolHelper, and ModSaveDataHelper are audited; pick consistent style across the cluster.

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
