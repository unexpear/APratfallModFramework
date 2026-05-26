# Session mod resolver — V1 design (lobby mod matching)

Design note. **No code yet.** Scope: per-session, vote-gated mod compatibility
resolution. Decisions marked **[D]** are choices that can still be adjusted.

## 1. Product framing

When a lobby forms, players arrive with different installed/enabled mod sets.
Before the session starts, the framework compares the **host's desired enabled
mods** against **every player's installed mods + compatibility rules** and builds
a **temporary effective session mod set** that everyone runs for that session
only. This is *lobby mod matching*, not mod sharing: it never edits a player's
saved enabled state, and it never silently runs a mod some players can't.
Safe alignments apply automatically; contentious choices go to a normal vote;
unsafe compatibility overrides need everyone to agree. Peer download / Workshop
acquisition is out of V1 — V1 only **detects, decides, and applies**
session-scoped enable/disable.

## 2. Data model sketch (conceptual, not final types)

- **DesiredHostModSet** — host's enabled mod ids + versions (read-only snapshot; never mutated).
- **PlayerModInventory[playerId]** — installed mod ids + versions per player (from each peer's manifest snapshot).
- **CompatibilityWarning** — { Kind, ModId, AffectedPlayers, Detail }. Kind ∈ { MissingForPlayer, MissingDependency, DeclaredConflict, VersionMismatch }.
- **SessionDecision** — one resolvable item: { ModId, ProposedState (Enable/Disable), Safety (Safe/Unsafe), Warning? }.
- **PendingVoteDecision** — a SessionDecision awaiting a vote + its rule (Majority | Unanimous).
- **ApprovedOverrides** — unsafe decisions the lobby unanimously accepted.
- **RejectedOverrides** — unsafe decisions that failed their unanimous vote.
- **DisabledForSession** — mod ids turned off for this session only.
- **EffectiveSessionModSet** — final per-session enabled set = (host desired ∪ safe enables) − DisabledForSession, after votes resolve.
- **SessionResolutionPlan** — the computed bundle: { Warnings, Decisions, PendingVotes, EffectiveSet (preview), Resolved (bool) }.

## 3. Decision types

- **AutoEnable (Safe):** host wants it, every player has a compatible version, no conflict → enabled, no vote.
- **DisableForSession (Safe):** a mod can't satisfy everyone (e.g. missing for a player) → propose disabling for the session. Normal majority vote; **[D]** default outcome on a failed vote = disabled.
- **UnsafeOverride (Unsafe):** "enable anyway despite a warning" (force-enable a missing/mismatched mod, or run two declared-conflict mods together) → **unanimous** required.
- **HardConflict:** two would-be-effective mods declare mutual conflict → never auto-enable both; resolvable only by disabling one (Safe vote) or a unanimous UnsafeOverride to keep both.

## 4. Vote rules

- Fully-safe AutoEnable: **no vote**.
- Safe decisions (DisableForSession / which-to-keep): **majority** (reuse ModVoteSession). **[D]** failed vote → safe fallback = disable-for-session.
- Unsafe overrides: **unanimous** — every connected player must vote yes; any no / non-vote → RejectedOverride → mod stays disabled for the session.
- Invariant: **never auto-enable a mod that conflicts with an already-effective mod** — a conflict always forces a decision.
- **[D]** host has no unilateral override after a failed vote (the lobby decides session enables; host authority is proposing the set + starting).

## 5. State transition flow

1. Lobby gathers PlayerModInventory snapshots (host + peers) from existing manifest broadcasts.
2. Resolver computes Warnings + SessionDecisions from DesiredHostModSet vs inventories + compatibility rules.
3. Safe AutoEnables apply to EffectiveSessionModSet (preview).
4. Each contentious/unsafe decision becomes a PendingVoteDecision (majority or unanimous).
5. Votes resolve → ApprovedOverrides / RejectedOverrides / DisabledForSession update.
6. EffectiveSessionModSet finalized; SessionResolutionPlan.Resolved = true.
7. Session starts with the effective set (temporary). On session end **nothing persists** — host's saved enabled state is untouched.

## 6. Example: Infinite Flares mismatch

Host has `InfiniteFlares v2.0` enabled; player B has `v1.0` (version mismatch); player C doesn't have it (missing).
- Warnings: VersionMismatch(B), MissingForPlayer(C).
- Safe path → DisableForSession(InfiniteFlares), majority vote. Not-passed → disabled this session (safe).
- Unsafe path → UnsafeOverride "enable v2.0 anyway" → unanimous; even if approved, C still lacks it → desync warned, and (V1, no acquisition) discouraged. Realistic V1 outcome: disabled for session.

## 7. Example: two incompatible mods

Host has `RagdollPlus` and `PhysicsRewrite` enabled; they declare mutual conflict.
- Warning: DeclaredConflict(RagdollPlus, PhysicsRewrite).
- Never auto-enable both. Decision: keep one, disable the other for the session (Safe majority vote: which to keep). **[D]** unresolved declared conflict → default to disabling the lower-priority / later-added mod for this session, unless a unanimous override explicitly keeps both.
- Keeping BOTH despite the declared conflict = UnsafeOverride → unanimous; rejected → one stays disabled.

## Hard policy — P4 (session-scoped apply) + P5 (acquisition / download / install)

> **Core rule:** Lobby votes decide the desired session set. User consent, safety checks, permissions, and storage decide what runs on that user's machine.

This is the gating contract for implementing P4 (apply) and any P5 (acquisition / download / install). It is **not optional, not bypassable.** Record it here before any apply/download behavior is built.

### Hard rules (invariants — never broken)

1. A vote must never force a player to download, install, enable, or run code.
2. No majority vote can override local user consent.
3. No automatic download.
4. No automatic install.
5. No automatic enable of newly acquired code.
6. Saved enabled-mod state must not be mutated.
7. Session enables are temporary only.

### Voting policy (refines §4)

- **Safe / session-shaping decisions** (e.g., `DisableForSession`, which-of-two-to-keep) — **majority** via the existing `ModVoteSession` (default rule).
- **Unsafe compatibility overrides** — **unanimous** (early-fail on first No, per P3). One No fails the override.
- "Unsafe" includes:
  - missing dependency
  - declared conflict between two would-be-effective mods
  - major version mismatch
  - a player is missing a mod the resolved plan would require
  - force-keeping two mods that declare mutual conflict
- Failed vote **OR** timeout → safe default = **disable the mod for this session**.

### Personal-consent policy (per player, after the session plan is voted)

Even after the lobby votes the plan, each player is asked individually before anything is downloaded, installed, or enabled on their machine.

**If the voted plan requires a mod the player does not have:**
- Show that player two options:
  1. **Download / install** and stay.
  2. **Leave session.**
- If they decline → they leave cleanly.
- The session may continue only if the remaining lobby still satisfies the resolved plan; otherwise the session is renegotiated or aborted.

**If the player already has the mod but it is currently disabled (per their saved preference):**
- Show that player two options:
  1. **Enable for this session only.**
  2. **Leave session.**
- **Do not mutate their saved enabled-mod preference.** Session enable is in-memory / session-scoped only — restored on session end.

### Local-capability policy (every client, before any download / install / enable)

Before touching the file system or loading new code, the client MUST verify all of:

- enough free disk space (in staging AND final destination)
- write permission to the mod folder (or staging folder)
- the download can complete (source reachable, transfer doesn't fail mid-stream)
- extraction succeeds
- hash / fingerprint matches the expected value
- the mod passes the user-check / fingerprint gate (no auto-trust of newly-acquired code)
- the user has explicitly consented to install + enable

If any check fails (no disk space, permission denied, hash mismatch, etc.):
- **Do not** install.
- **Do not** enable.
- **Do not** write partial files into the live mods folder.
- Show: 1. Free space / choose another location / retry. 2. Leave session.
- Clean up temporary download files where possible.

### Staged install flow (the only acceptable path)

```
download  →  temp staging folder  →  verify  →  user confirms  →  move into mods folder  →  session-enable only
```

**Never stream directly into the active mods folder.** A failed, cancelled, or partial download must not leave the live mods folder in a broken or partially-installed state. Temp staging is cleaned on failure.

### Relationship to existing scope

- **P4 (apply):** must satisfy every rule above. Session-scoped enable/disable only; restore saved state on session end; no mutation of saved preference.
- **P5 (acquisition / download / install):** design-captured here; implementation remains deferred per [§9 / "What NOT to build yet"](#9-what-not-to-build-yet) until this policy is wired in. When P5 lands, it MUST follow the staged install flow above.

## 8. V1 implementation plan (phased; design only)

- **P1 — pure resolver:** `SessionModResolver.Resolve(DesiredHostModSet, inventories, rules) → SessionResolutionPlan`. No network, no UI. Unit-testable; extends the existing ModCompatibilityChecker. **Cheapest + highest-value first slice.**
- **P2 — inputs:** feed inventories from the manifest snapshots already broadcast; compute the plan host-side.
- **P3 — votes:** drive decisions through ModVoteSession — majority for Safe, add a Unanimous mode for Unsafe.
- **P4 — apply:** apply EffectiveSessionModSet session-scoped at session start (via SessionStartHooks), restore saved state after.
- Each phase gated by the relevant existing coverage; P1 needs only new unit tests (no infra gaps).

## 9. What NOT to build yet

- Peer download / mod transfer (acquisition).
- Workshop acquisition.
- Real-transport dispatch changes / ModNetworkLayer refactor.
- VoteUI redesign — reuse the current vote flow; add Unanimous as a *rule*, not a UI rebuild.
- Persisting any session decision to saved enabled state.
