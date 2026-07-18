using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The value a run accrues, for the progress readout and completion summary:
/// gil for pinch and Hawk listings, seals for a GC turn-in run.
/// </summary>
internal enum RunValueUnit { None, Gil, Seals }

/// <summary>
/// Where a run is in its life. <see cref="Running"/> is the only live state; the
/// three terminal outcomes are kept distinct so cleanup and the summary can name
/// what actually happened (finished its work / player cancelled / watchdog tripped).
/// </summary>
internal enum RunState { Idle, Running, Complete, Cancelled, Stalled }

/// <summary>An immutable snapshot of a run for the completion summary line.</summary>
internal readonly record struct RunSummary(
  RunState Outcome, int Done, int Total, long Value, RunValueUnit Unit, TimeSpan Duration);

/// <summary>
/// The run-host lifecycle core (LanePricing/TriageMemory mold: no game reads, no
/// storage, no statics, linked into Scrooge.Tests). Four executors - pinch, GC
/// churn, Hawk, triage - each hand-rolled this same lifecycle with a different
/// gap set (the scar receipts: triage wedge #14, GC timeout wedge, silent GC run,
/// LegacyTaskManager null-aborts-queue). The GC Progress tuple shipped in 0129f13
/// (done, total, seals, eta) was the accidental sketch of the interface; this is
/// that sketch generalized so the game-side host becomes a thin adapter and the
/// DECISION logic - state transitions, progress/value accounting, self-calibrating
/// ETA, stall detection - lives here where it is testable without the game.
///
/// Every rule fails CLOSED: transitions out of a terminal state are refused, the
/// stall watchdog only ever moves a live run to Stalled (never back), and each of
/// the three exit paths (Complete / Cancel / Stall) lands in a terminal state the
/// adapter can hang its listener-teardown and IsRunning-clear off of, on EVERY
/// exit. A run is restarted by calling <see cref="Start"/> again (fresh state);
/// adapters guard their own re-entry on <see cref="IsRunning"/> exactly as the
/// shipped orchestrators already do.
/// </summary>
internal sealed class RunLifecycle
{
  private readonly TimeSpan _stallBound;
  private DateTime _startedAt;
  private DateTime _lastProgressAt;
  private DateTime _endedAt;
  private double _seededMsPerItem; // 0 = self-calibrate only (no pre-run estimate)

  /// <summary>Current lifecycle state. Starts <see cref="RunState.Idle"/>.</summary>
  internal RunState State { get; private set; } = RunState.Idle;

  /// <summary>Items finished so far (success or per-item skip - a processed unit).</summary>
  internal int Done { get; private set; }

  /// <summary>Total units the run expects to process, as declared at start confirm.</summary>
  internal int Total { get; private set; }

  /// <summary>Value accrued so far, in <see cref="Unit"/> (gil or seals).</summary>
  internal long Value { get; private set; }

  /// <summary>What <see cref="Value"/> is denominated in.</summary>
  internal RunValueUnit Unit { get; private set; }

  /// <summary>What the run says it will do, for the start-confirm line.</summary>
  internal string Description { get; private set; } = "";

  /// <summary>
  /// The stall bound is how long a live run may go without recording progress
  /// before the watchdog surfaces it. Default 30s (a generous multiple of the
  /// executors' 10s per-task TaskManager timeout), overridable per adapter.
  /// </summary>
  internal RunLifecycle(TimeSpan? stallBound = null)
    => _stallBound = stallBound ?? TimeSpan.FromSeconds(30);

  internal bool IsRunning => State == RunState.Running;

  internal bool IsTerminal
    => State is RunState.Complete or RunState.Cancelled or RunState.Stalled;

  /// <summary>Units still expected. Never negative (over-count clamps to 0).</summary>
  internal int Remaining => Math.Max(0, Total - Done);

  /// <summary>
  /// Begin (or restart) a run. Resets all accounting. <paramref name="total"/> is
  /// clamped non-negative; <paramref name="seededMsPerItem"/> is an optional pre-run
  /// per-item estimate (pinch's persisted AvgMsPerItem style) used for the ETA only
  /// until the first item is observed, after which the ETA self-calibrates on the
  /// run's own pace.
  /// </summary>
  internal void Start(int total, RunValueUnit unit, DateTime now,
    string description = "", double seededMsPerItem = 0)
  {
    _startedAt = now;
    _lastProgressAt = now;
    _endedAt = now;
    _seededMsPerItem = seededMsPerItem > 0 ? seededMsPerItem : 0;
    Total = Math.Max(0, total);
    Unit = unit;
    Done = 0;
    Value = 0;
    Description = description;
    State = RunState.Running;
  }

  /// <summary>
  /// Record one beat of progress: <paramref name="doneDelta"/> more items finished
  /// and <paramref name="valueDelta"/> more value earned (either may be 0 - a value
  /// beat with no item, or a skip that advances done without value). Resets the stall
  /// watchdog. Returns false and does nothing if the run is not live (a queued
  /// follow-up firing after a terminal transition no-ops here, same as the shipped
  /// IsRunning guards).
  /// </summary>
  internal bool RecordProgress(int doneDelta, long valueDelta, DateTime now)
  {
    if (State != RunState.Running) return false;
    if (doneDelta > 0) Done += doneDelta;
    Value += valueDelta;
    _lastProgressAt = now;
    return true;
  }

  /// <summary>The run finished its work. No-op (false) unless live.</summary>
  internal bool Complete(DateTime now) => Finish(RunState.Complete, now);

  /// <summary>The player cancelled, or the plugin is disposing. No-op (false) unless live.</summary>
  internal bool Cancel(DateTime now) => Finish(RunState.Cancelled, now);

  private bool Finish(RunState outcome, DateTime now)
  {
    if (State != RunState.Running) return false; // fail closed: no exit from terminal
    State = outcome;
    _endedAt = now;
    return true;
  }

  /// <summary>
  /// The stall watchdog. If the run is live and no progress has landed within the
  /// stall bound, transition to <see cref="RunState.Stalled"/> and return true (the
  /// adapter then runs its fail-closed teardown). Returns false while progress is
  /// still fresh, and on any terminal state - it never resurrects or re-fires.
  /// </summary>
  internal bool CheckStall(DateTime now)
  {
    if (State != RunState.Running) return false;
    if (now - _lastProgressAt < _stallBound) return false;
    State = RunState.Stalled;
    _endedAt = now;
    return true;
  }

  /// <summary>Elapsed run time, frozen at the terminal moment once the run ends.</summary>
  internal TimeSpan Elapsed(DateTime now) => (IsTerminal ? _endedAt : now) - _startedAt;

  /// <summary>
  /// Self-calibrating ETA for the remaining items, or null when there is no honest
  /// basis for one (nothing left, or nothing observed yet and no seed). Once at
  /// least one item has finished the estimate rides the run's own observed pace
  /// (elapsed / done) - the same shape the GC sketch shipped, generalized - so it
  /// converges as the run proceeds. Before the first item it falls back to the
  /// seeded per-item estimate if the adapter supplied one.
  /// </summary>
  internal TimeSpan? Eta(DateTime now)
  {
    if (State != RunState.Running) return null;
    var remaining = Remaining;
    if (remaining <= 0) return null;

    double msPerItem;
    if (Done > 0)
      msPerItem = (now - _startedAt).TotalMilliseconds / Done; // observed pace
    else if (_seededMsPerItem > 0)
      msPerItem = _seededMsPerItem; // pre-run estimate, until the first item lands
    else
      return null; // no basis yet - "gathering data"

    return TimeSpan.FromMilliseconds(msPerItem * remaining);
  }

  /// <summary>A snapshot for the completion summary (outcome, counts, value, duration).</summary>
  internal RunSummary Summary(DateTime now)
    => new(State, Done, Total, Value, Unit, Elapsed(now));

  // ===========================================================================
  // Hawk completion boundary - the M4 inventory-scope zombie sweep gate
  // ===========================================================================

  /// <summary>
  /// The gate for the Hawk run's M4 inventory-scope zombie sweep. A Hawk run may
  /// close an open inventory-scope lane_held flag as item_gone only when it walked
  /// the FULL inventory container the flag points at - so this returns the observed
  /// item-id set ONLY when the run signals full observation (reached its natural end
  /// having enumerated the whole container). A partial run - aborted early because
  /// every retainer filled, cancelled, stalled, or an incomplete enumeration -
  /// returns null and sweeps nothing. Fail toward open flags, never toward a silent
  /// close. Pure sibling of the pinch board sweep wired in SnapshotListings.
  /// </summary>
  internal static IReadOnlySet<uint>? HawkSweepInputs(
    bool fullyObserved, IReadOnlySet<uint> observedItemIds)
    => fullyObserved ? observedItemIds : null;
}
