# CLAUDE.md - Scrooge

Guidance for Claude Code in this repo. This file is THE MACHINE doc: how the code works, workflows, key settings. Vision, roadmap, and boundaries live in Drift's vault (`Projects/Scrooge/Scrooge - Soul.md`). **Reviewed at every merge to main - the commit-msg gate requires this file touched or a "docs: no impact" stamp.**

## What Scrooge Is (mechanically)

A Dalamud plugin (net10.0-windows7.0, Dalamud.NET.Sdk, AGPL-3.0) that runs one player's FFXIV market enterprise as an ADVISOR: it reads the live market board, routes every item in the bags across four exits (List / Melt / GC turn-in / Vendor), prices honestly against the real lane, and executes only what the player confirms. First law: we EARN what we make - no automation past one confirm, no price prediction, ever.

Commands: `/scrooge` (dashboard; `round` = the Round, `log` = the Ledger transcript, `config` = settings, `sitrep` = diagnostics to clipboard), `/giltrack`. Every word door answers to singular AND plural (`setting`/`settings`, `round`/`rounds`, ...) - plurality is not an error (ruled 2026-08-23).

## Build & Test (the traps)

- **Build**: `dotnet build Scrooge\Scrooge.csproj -c Debug -p:Platform=x64` - BOTH flags or you get a stale-DLL mystery. Verify: `(Get-Item ...\bin\x64\Debug\Scrooge.dll).VersionInfo.FileVersion`.
- **Tests**: `dotnet test Scrooge.Tests\Scrooge.Tests.csproj` - solution-level `dotnet test` silently no-ops (restore only, exit 0).
- **Tests are LINKED SOURCES** (no project reference, no Dalamud refs). A game/config static leaking into pure core breaks the test compile ON PURPOSE. New pure logic goes in Dalamud-free files, linked into the test csproj.
- **COMMIT FIRST, THEN BUILD** - BuildStamp bakes the commit; a dirty build stamps "+dirty".
- **GREEN TESTS ARE NOT A BUILD.** The test project links sources and carries no Dalamud references, so it never compiles the Windows/orchestrator layer at all - a plugin that cannot load can sit behind a full green suite indefinitely. **Receipt (2026-08-22)**: the one-floor-law commit shipped `PrintBelowPriceFloorError` as `public` taking the `internal` `EffectiveFloor`; CS0051, the plugin had not compiled for a day, and 1,744 green said nothing. **A phase is not done until it compiles.** When the no-build gate is up (the player may be in-game), that debt is NAMED IN THE HANDOFF, never left silent - and the first move next session is the build. A symbol sweep over changed Windows-layer identifiers is the stopgap, not a substitute.
- **NEVER build while Drift may be in-game or testing** - the DLL on disk is what the next reload loads. Agents never build, period (hot-load = live deploy).
- **Worktrees don't auto-init the ECommons submodule**: `git submodule update --init --depth 1 ECommons` or 85 phantom errors.
- Dalamud libs expected at `%AppData%\XIVLauncher\addon\Hooks\dev\`.

## Git & Release

- **Feature branches only.** `master` and `era/advisor` NEVER take direct commits; merges to master are release save-points, `--no-ff` with a named message, version bump per merge (`Scrooge.csproj <Version>`).
- **Versioning**: minor = the player can see it, patch = they can't. Pick the slot honestly.
- **NO AI attribution on commits.** Plain messages in the repo's voice.
- **This repo is quasi-public.** No personal names anywhere in it - code, comments, docs, commits. Use "Drift" (the repo's GitHub identity) or "the player".
- **THE PUBLICATION SEAM (ruled 2026-08-22): local history is private; only squashed save-points reach GitHub.** Local `master` is NEVER pushed. Each release publishes ONE `git commit-tree` squash of master's tree onto `origin/master`, with a public-facing changelog-voice message, and the `v*` tag points at the PUBLICATION commit - never at local master, which would drag the private history up with it. A pre-publish name scan (term list lives in the vault runbook, deliberately not here) gates the squash. GitHub Actions delegates the tag to the upstream plugin-repo reusable workflow. Full runbook: vault `Projects/Scrooge/Release Process.md`.

## Architecture

Four layers; the Orchestrator Rule and Tabbed-Window Rule govern the shape (any stateful multi-step process gets an orchestrator - lifecycle latch, wedge watchdog, logged failures; decisions stay pure functions; UI reads state, forwards presses, owns nothing; window shells own the frame, each tab/surface owns itself, shared grammar in named helpers).

1. **Pure decision core** (Dalamud-free, test-linked): `LanePricing` (the pricing spine: listings judged as crashers / competition / dreamers against a recency-weighted sale lane - "A11" in doc comments; regime segments, bands), `RoutingRules` (four-exit router), `TriageCase` (case voice + verdicts), `Spine`/`FlowPlan`/`GatePlan`/`BellPlan` (round planning), `ReceiptGrading`, `Durations` (four duration grammars - Span/Elapsed/Ago/DayAge - deliberately not unified; a pin test records their disagreements), `PriceFloor`, `PricingVoice`, `PaceBank`, the `*Schema` classes.
2. **Orchestrators/executors** (every one carries the RunLifecycle terminal latch + QueueWedgeWatchdog): `PinchRunExecutor` + `VendorRiderExecutor` (hosted by `PinchHost`), `HawkRunOrchestrator` (bell/listing), `ReconRunOrchestrator`, `StandingOrchestrator`, `DesynthOrchestrator`, `CofferOrchestrator`, `GcTurnInOrchestrator`, `PortOrchestrator`. Game UI automation sequences through ECommons TaskManager (tasks return `bool?`: null=retry, true=done, false=failed); addon access via FFXIVClientStructs pointers and AddonLifecycle listeners.
3. **The Round engine** (`Rounds/` + `Board/`): `RoundConductor` (the round state machine; talks to the window through the `IRoundBoard` seam), `HingeCommit` (triage-hinge commit sets + AdmitGate), `DeckState` (per-frame banked queue), `LedgerCache` (board read cache; `Rescanned`/`ListingsRefreshed`/`HeldFlagsRefreshed` events, one owner per fact). This layer carries open audit items (naming, the IRoundBoard testability claim, spread instrument ownership) - check the vault's drift-audit doc before reshaping it.
4. **Windows** (paint + press-forwarding only): `AccountantWindow` (shell + surface partials incl. `ScoreCell`), `GilWindow` (shell + per-tab partials), `ConfigWindow` (+ `ConfigWidgets`), `HawkWindow`, `LedgerWindow` (the run-log transcript - unrelated to `Board/LedgerCache`), `DesynthPreviewWindow`. Shared grammar: `TableSort`, `TabCache`, `Format`, `ScroogeColors`, `StageRail`. Config tabs were fully reconciled against the knob registry 2026-08-23 (all ten; the Pricing tab is the style bar - gold `SectionHeader`, one plain subheader line, ALL mechanics behind `(?)` hovers); the tab once called Ledger is **Rounds** (`DrawRoundsTab`) and Hawk Settings is **Item Rules**.

## Storage

- SQLite at `%AppData%\XIVLauncher\pluginConfigs\Scrooge\scrooge.db`. **Spot-checks copy the TRIO** (.db + -wal + -shm) and query the COPY, never the live file. Timestamps are unix epoch: `datetime(x,'unixepoch','localtime')`.
- `GilStorage` = facade + family partials. Migrations: `GilStorageBootstrap` ordered ladder, one transaction per rung, fail-closed Initialize (`StorageAvailable` degrades windows in words). Migrations must be diffable; carry-overs that duplicate rather than partition are fossil dual-state.
- The receipts corpus (`routing_receipts`, `decision_receipts`, `contest_receipts`, `routing_overrides`) is deliberately write-only - food for the scoreboard era. Stats over receipts must cluster by DECISION-SESSION, not row (one "clear the queue" ruling stamps across ~70 rows), and weight overrides above compliant executions.

## Config & Pricing Mechanics

- **The two-register constitution** (vault: `Projects/Scrooge/Scrooge - Learning From Rulings.md`): estimates and confidence self-regulate with evidence; config values the player set (pegs) HOLD until the player changes them. Never silently override a peg; a seed that gates behavior needs a VISIBLE ConfigWindow knob.
- **Undercut write styles** (`UndercutMode.cs`): FixedAmount / GentlemansMatch / CleanNumbers / Humanized, applied inside the seat the lane already picked; ordinals frozen (0,2,3,4), retired `Percentage`(1) folds to FixedAmount via `LegacyUndercutMode.Fold` on deserialization. All clamp to min 1 gil.
- **THE ONE FLOOR LAW** (ruled 2026-08-21): `PriceFloor.Effective(mode, vendorPrice, MinimumListingPrice)` is the ONE calculation - `max(the player minimum, the mode floor)` - and it returns the binding floor NAMED (`EffectiveFloor.Binding`), so every sentence can say which one bit. `PriceFloor.For` (None / Vendor / DomanEnclave = 2x vendor) is the mode half underneath it, not a second answer. An honest ask that cannot clear the floor means NO LEGAL LISTING EXISTS: List sits out and the other exits compete on score; nothing is ever clamped up to a floor. One verdict species (`BelowFloor`; `BelowMinimum` was folded into it).
- **The Doman destiny**: under the Enclave floor, a refused item never auto-vendors - it is worth 2x at the Enclave and 1x at the counter, so melt and turn-in compete and an item nothing wins holds in the bags with its reason spoken. Player-staged vendor rows still ride the rider: a human answering is not the machine answering.
- **PRICING-TAB edits invalidate the pinch price cache** via `SavePricing()` (`Save()` + `PinchHost.ClearCachedPrices()`); every other tab saves plain. The doctrine is one sentence: if it is on the Pricing tab, it re-prices. (Corrected 2026-08-23 - the old line here claimed "config edits" generally.)
- **The deep-cut guard is DELISTED** (ruled 2026-08-23): `MaxUndercutPercentage` sits inert at its 100 default - it measured the drop from our OWN standing ask, pinch-only, and at 100 the question never fires (write modes floor at 1 gil). The lane owns crasher defense. Field + its four "under the anchor" echo strings die in 3.1.
- **The Look rung + the witness ladder**: a fresh banked recon Look (window = `ReconFreshHours`, ONE number at both doors - recon's work set and posting from cache) scores List for the router when own-sale and tape are silent. DC community history (Universalis) is the ladder's LAST rung: it CAN price, but only when own-sale, tape, and Look are all silent - own evidence always outranks it.
- **Recon marks its receipts** (`arm_id='recon'`, cleared at adoption; V48 back-marked the standing phantoms): an un-posted Look is never a standing ask - the grading walk, On Market, both reconcilers, and the sale confirm all sit marked rows out.
- **Two vendor classes at the hawk**: `RoutedVendor` (evidence verdict - "No better exit in evidence.") vs `IsAlwaysVendor` (config ruling - "You always vendor this one."). Recon and Re-Look skip ONLY the config class - a standing ruling no board read can move. Always Vendor is BAGS ONLY (listed copies reprice normally); ban outranks it when an item is somehow on both lists, and the ban holds in the salvage window too (a banned item cannot be melted or Select-All'd).
- **Item rules are bag right-clicks, no window required** (2026-08-23): Always Vendor / Ban / their removals appear on any inventory context menu; only Select for Sale is Hawk-gated (selection is meaningless without the surface it selects on).
- **The crasher-seat flag** (2026-08-23): a pinch that HOLDS an ask runs the gap test in reverse - `LanePricing.HeldAskReadsAsCrasher` (the house `ClusterNearPct` margin, REACHABLE competitors only; dreamers past the 3x rail raise nothing) - and raises `crasher_seat` to triage when our own seat reads as the crasher the walk would step over on anyone else's board.
- **`BandTie` is the verdict's own word**: only a genuine dead heat inside the review band carries it; the case page's "a dead heat" and the walk's RouterDeclined read the flag, never re-derive the heat.
- **Triage verbs**: a bag case takes an exit or HOLD (a `BoardPile.Silent` verdict - answers the docket, stages nothing, the round withholds the row per Review's contract, next round re-asks); a standing ask takes its exits or Dismiss.
- **The board freshness gate depends on Gil Tracking**: `RepinchFloorHours` reads the last COMPLETED FULL pinch's stamp, written only by `GilTracker.FinalizeRun` behind `EnableGilTracking` - tracking off means every round re-pinches forever (disclosed in both tabs' hovers; ripping out core-gating toggles is the ruled 3.1 posture: "you installed Scrooge, you get Scrooge").
- **Ages FLOOR** (`Durations`, ruled 2026-08-23): the decimal rungs floor to the tenth - rounding an age up claims evidence is staler than it is.
- **Trust loop**: boundary-crossing overrides demote a class Unanimous->Mixed (2 distinct items); assent-clears-dissent pardons on the next unoverridden execution of the class's own action.

## Diagnosis (start here, always)

1. `/scrooge sitrep` - full diagnostic block to clipboard; paste it before describing a problem.
2. The log: `%AppData%\XIVLauncher\dalamud.log`. Task lines: `[DBG] [Scrooge] Starting to execute task: <Name>` / `Task <Name> completed successfully` - a run's death point is the last Starting without its completed.
3. The DB (copy the trio, see Storage).

Known failure signatures (tri-state close, salvage timeout ceiling `ServerRoundTripCeilingMs`, AgentSalvage ~100-row truncation, demotion-by-design, evidence-phase oscillation) are catalogued with fixes in the vault's Stewardship doc - check it before re-diagnosing a known class.

## Working Style

- Drift's one-liners are rulings - ship them same-hour. Mid-decision asides carry the structural insight. Plain questions are load-bearing. Read the log and the DB BEFORE asking.
- **Naming and boundary-merges are rulings** - agents queue them for Drift, never make them mid-refactor.
- **THE DARK-MODE RULE** (ruled 2026-08-21, governs every player-facing string): a surface states the DECISION and its live operands, never the configuration that produced the policy. "Websites don't have an icon saying 'these colors are dark because you picked dark mode'." Verdict operands stay at the verdict ("under your 75 floor" keeps its 75); curve anchors, thresholds, window sizes and knob recitals belong on that knob's tooltip and nowhere else. A tooltip explaining how a mechanism works is the icon - delete it, do not reword it.
- Fail loud beats fail silent, everywhere, even on success. Zero TODO/HACK/FIXME markers in the codebase; keep it that way. XML doc comments on public/internal methods; 2-space indent (`.editorconfig`); readability over cleverness.
- Multi-language: context-menu text checks must handle EN/DE/JA/FR.

## Code-to-Docs Map

| Topic | Where |
|---|---|
| Vision / roadmap / boundaries | vault `Projects/Scrooge/Scrooge - Soul.md` |
| How Scrooge learns from rulings | vault `Projects/Scrooge/Scrooge - Learning From Rulings.md` |
| Current pricing law, in one read | vault `Projects/Scrooge/Scrooge - Pricing Strategy.md` |
| Pricing spec (the lane model, regimes, premium ladder) | vault `Projects/Scrooge/Scrooge - Lane Pricing - Design.md` |
| The Round: order, seams, durability | vault `Projects/Scrooge/Scrooge - The Round.md` |
| Every knob's desired truth (peg, visibility, consumers, fate) | vault `Projects/Scrooge/Scrooge - Knob Registry.md` - **updates ride the same commit as any knob change** |
| Outstanding list + carried backlog | vault `Scrooge - Road to v3.0 - Build Plan.md` (Legacy) |
| Code-health record + drift audit | vault `Scrooge - Code Quality Pass Scratchpad.md` + `Scrooge - Code Shine Drift Audit.md` |
| Release runbook | vault `Projects/Scrooge/Release Process.md` |
| Live state | newest Scrooge session log's C section in the vault |
