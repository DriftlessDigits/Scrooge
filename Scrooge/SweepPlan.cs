using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>Where a sweep stage physically happens - no button teleports the player.</summary>
internal enum SweepPlace
{
  Bell,
  Anywhere,
  ExpertDelivery,
}

/// <summary>The full sweep in workflow order (Sam's endgame sentence, 2026-07-19).</summary>
internal enum SweepStage
{
  /// <summary>Read the board and reprice standing listings (the pinch).</summary>
  Pinch,
  /// <summary>List and vendor the routed bag gear - the bell run.</summary>
  BellRun,
  /// <summary>Fix standing listings the guards held (cap / undercut / upward).</summary>
  Reprice,
  /// <summary>Melt the skillup/yield pile at the desynthesis window.</summary>
  Desynth,
  /// <summary>Churn the rest to seals at the GC's Expert Delivery.</summary>
  TurnIn,
}

/// <summary>
/// The one-button sweep's cursor, v0: pinch -&gt; bell run -&gt; reprice -&gt;
/// desynth -&gt; turn in, one press per stage, walking between stops. ZERO
/// intelligence by design: every stage fires only on a player press, a stage
/// with no work is skipped silently, and a stage is marked done AT FIRE TIME -
/// the button simply refuses to fire while any run is busy, so the cursor can
/// never race an orchestrator. "Gets smarter over time" has a precise meaning
/// here: when receipts ripen (4.0), stages justify skipping or auto-continuing
/// themselves; the cursor's seam does not move.
/// Pure and Dalamud-free (linked into the test project): the window feeds it
/// work counts and location, it answers "what's next and where."
/// </summary>
internal sealed class SweepPlan
{
  internal static readonly SweepStage[] Order =
  {
    SweepStage.Pinch,
    SweepStage.BellRun,
    SweepStage.Reprice,
    SweepStage.Desynth,
    SweepStage.TurnIn,
  };

  private readonly HashSet<SweepStage> _done = new();

  /// <summary>A sweep is underway - the deck shows the cursor.</summary>
  internal bool Active { get; private set; }

  internal static SweepPlace PlaceOf(SweepStage stage) => stage switch
  {
    SweepStage.Desynth => SweepPlace.Anywhere,
    SweepStage.TurnIn => SweepPlace.ExpertDelivery,
    _ => SweepPlace.Bell, // pinch, bell run, and reprice all live at a bell
  };

  internal void Start()
  {
    Active = true;
    _done.Clear();
  }

  internal void Cancel()
  {
    Active = false;
    _done.Clear();
  }

  internal void MarkDone(SweepStage stage) => _done.Add(stage);

  /// <summary>
  /// Reverts a fire-time MarkDone after the fired run ABORTS: "done at fire
  /// time" is a promise the orchestrator finished the errand, and a run that
  /// died (timeout, occupied refusal, user abort) must put the stage back on
  /// the cursor instead of letting the deck lie about completed work.
  /// </summary>
  internal void Unmark(SweepStage stage) => _done.Remove(stage);

  internal bool IsDone(SweepStage stage) => _done.Contains(stage);

  /// <summary>
  /// The first unfinished stage that has work, in sweep order; null when the
  /// sweep is complete (everything left is done or empty). Skipped-empty
  /// stages are NOT marked done - if work appears (a pinch flags reprices),
  /// the cursor picks the stage up on its way through.
  /// </summary>
  internal SweepStage? Next(Func<SweepStage, bool> hasWork)
  {
    foreach (var stage in Order)
      if (!_done.Contains(stage) && hasWork(stage))
        return stage;
    return null;
  }
}
