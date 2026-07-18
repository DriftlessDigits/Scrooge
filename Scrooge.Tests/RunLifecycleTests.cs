using System;
using System.Collections.Generic;
using Xunit;
using static Scrooge.RunLifecycle;

namespace Scrooge.Tests;

/// <summary>
/// The run-host lifecycle core: the state machine every executor now shares. Time
/// is injected (a fixed base moment plus offsets) so ETA and the stall watchdog are
/// deterministic. Every exit path is checked to land terminal - that terminal state
/// is the hook the game-side adapter hangs fail-closed teardown on.
/// </summary>
public class RunLifecycleTests
{
  private static readonly DateTime T0 = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
  private static DateTime At(double seconds) => T0.AddSeconds(seconds);

  private static Scrooge.RunLifecycle Started(int total = 10,
    Scrooge.RunValueUnit unit = Scrooge.RunValueUnit.Gil, double stallSeconds = 30)
  {
    var run = new Scrooge.RunLifecycle(TimeSpan.FromSeconds(stallSeconds));
    run.Start(total, unit, T0, "test run");
    return run;
  }

  // --- Lifecycle transitions -------------------------------------------------

  [Fact]
  public void Fresh_IsIdle_NotRunning()
  {
    var run = new Scrooge.RunLifecycle();
    Assert.Equal(Scrooge.RunState.Idle, run.State);
    Assert.False(run.IsRunning);
    Assert.False(run.IsTerminal);
  }

  [Fact]
  public void Start_EntersRunning_AndResetsAccounting()
  {
    var run = Started(total: 12, unit: Scrooge.RunValueUnit.Seals);
    Assert.Equal(Scrooge.RunState.Running, run.State);
    Assert.True(run.IsRunning);
    Assert.Equal(0, run.Done);
    Assert.Equal(12, run.Total);
    Assert.Equal(0, run.Value);
    Assert.Equal(Scrooge.RunValueUnit.Seals, run.Unit);
  }

  [Fact]
  public void Running_ToComplete()
  {
    var run = Started();
    Assert.True(run.Complete(At(5)));
    Assert.Equal(Scrooge.RunState.Complete, run.State);
    Assert.True(run.IsTerminal);
  }

  [Fact]
  public void Running_ToCancelled()
  {
    var run = Started();
    Assert.True(run.Cancel(At(5)));
    Assert.Equal(Scrooge.RunState.Cancelled, run.State);
    Assert.True(run.IsTerminal);
  }

  [Fact]
  public void Running_ToStalled_ViaWatchdog()
  {
    var run = Started(stallSeconds: 30);
    Assert.True(run.CheckStall(At(31)));
    Assert.Equal(Scrooge.RunState.Stalled, run.State);
    Assert.True(run.IsTerminal);
  }

  [Fact]
  public void NoTransition_OutOfComplete()
  {
    var run = Started();
    run.Complete(At(5));
    // Every exit and mutation refuses once terminal - fail closed.
    Assert.False(run.Cancel(At(6)));
    Assert.False(run.Complete(At(6)));
    Assert.False(run.CheckStall(At(999)));
    Assert.False(run.RecordProgress(1, 100, At(6)));
    Assert.Equal(Scrooge.RunState.Complete, run.State);
  }

  [Fact]
  public void Stall_ForcedByAdapterSignal_Independent_OfTimeBound()
  {
    // The GC idle-task-manager wedge: an adapter's own precise stall signal fires
    // well before the time bound would. It still lands Stalled, fail closed.
    var run = Started(stallSeconds: 30);
    Assert.True(run.Stall(At(3)));
    Assert.Equal(Scrooge.RunState.Stalled, run.State);
    Assert.False(run.Stall(At(4))); // no re-fire once terminal
  }

  [Fact]
  public void NoTransition_OutOfCancelled_OrStalled()
  {
    var cancelled = Started();
    cancelled.Cancel(At(5));
    Assert.False(cancelled.Complete(At(6)));
    Assert.Equal(Scrooge.RunState.Cancelled, cancelled.State);

    var stalled = Started();
    stalled.CheckStall(At(31));
    Assert.False(stalled.Complete(At(40)));
    Assert.False(stalled.Cancel(At(40)));
    Assert.Equal(Scrooge.RunState.Stalled, stalled.State);
  }

  [Fact]
  public void Start_FromTerminal_RestartsCleanly()
  {
    var run = Started(total: 5);
    run.RecordProgress(3, 300, At(2));
    run.Cancel(At(3));
    // A fresh run reuses the same object and wipes prior accounting.
    run.Start(8, Scrooge.RunValueUnit.Gil, At(10), "second run");
    Assert.Equal(Scrooge.RunState.Running, run.State);
    Assert.Equal(0, run.Done);
    Assert.Equal(8, run.Total);
    Assert.Equal(0, run.Value);
  }

  // --- Fail-closed: every exit path lands terminal ---------------------------

  [Theory]
  [InlineData("complete")]
  [InlineData("cancel")]
  [InlineData("stall")]
  public void EveryExitPath_LandsTerminal(string how)
  {
    var run = Started(stallSeconds: 10);
    run.RecordProgress(2, 200, At(1));
    switch (how)
    {
      case "complete": run.Complete(At(2)); break;
      case "cancel": run.Cancel(At(2)); break;
      case "stall": Assert.True(run.CheckStall(At(20))); break;
    }
    Assert.True(run.IsTerminal);
    Assert.False(run.IsRunning);
    var summary = run.Summary(At(50));
    Assert.Equal(run.State, summary.Outcome);
    Assert.Equal(2, summary.Done); // accounting survives into the summary
  }

  // --- Stall detection -------------------------------------------------------

  [Fact]
  public void Stall_NotTripped_BeforeBound()
  {
    var run = Started(stallSeconds: 30);
    Assert.False(run.CheckStall(At(29)));
    Assert.Equal(Scrooge.RunState.Running, run.State);
  }

  [Fact]
  public void Stall_ProgressResetsWatchdog()
  {
    var run = Started(stallSeconds: 30);
    // 25s in, a beat of progress - the watchdog clock restarts from there.
    run.RecordProgress(1, 100, At(25));
    Assert.False(run.CheckStall(At(50))); // only 25s since last progress
    Assert.True(run.CheckStall(At(56)));  // now 31s since last progress
    Assert.Equal(Scrooge.RunState.Stalled, run.State);
  }

  // --- ETA self-calibration --------------------------------------------------

  [Fact]
  public void Eta_Null_WhenNothingObservedAndNoSeed()
  {
    var run = Started(total: 10);
    Assert.Null(run.Eta(At(5))); // no items done, no seed - gathering data
  }

  [Fact]
  public void Eta_UsesSeed_BeforeFirstItem()
  {
    var run = new Scrooge.RunLifecycle();
    run.Start(10, Scrooge.RunValueUnit.Gil, T0, seededMsPerItem: 1000);
    // 0 done, 10 remaining, 1000ms/item seed -> ~10s.
    var eta = run.Eta(At(3));
    Assert.NotNull(eta);
    Assert.Equal(10_000, eta!.Value.TotalMilliseconds, 3);
  }

  [Fact]
  public void Eta_ConvergesOnObservedPace()
  {
    var run = Started(total: 10);
    // 4 items in 8s => 2s/item observed; 6 remaining => 12s.
    run.RecordProgress(4, 0, At(8));
    var eta = run.Eta(At(8));
    Assert.NotNull(eta);
    Assert.Equal(12_000, eta!.Value.TotalMilliseconds, 3);

    // Pace doubles (next 4 items take 16s more -> 8 items in 24s = 3s/item);
    // 2 remaining => 6s. The estimate rides the run's own measured pace.
    run.RecordProgress(4, 0, At(24));
    var eta2 = run.Eta(At(24));
    Assert.Equal(6_000, eta2!.Value.TotalMilliseconds, 3);
  }

  [Fact]
  public void Eta_Sane_WithZeroItemRun()
  {
    var run = Started(total: 0);
    Assert.Null(run.Eta(At(5))); // nothing remaining -> no ETA, never divide by zero
  }

  [Fact]
  public void Eta_Sane_WithSingleItemRun()
  {
    var run = Started(total: 1);
    Assert.Null(run.Eta(At(1))); // before the one item: no observed pace, no seed
    run.RecordProgress(1, 50, At(4));
    Assert.Null(run.Eta(At(4))); // the item done, nothing remaining -> null, not zero-div
  }

  [Fact]
  public void Eta_Null_OnceTerminal()
  {
    var run = Started(total: 10);
    run.RecordProgress(3, 0, At(6));
    run.Complete(At(7));
    Assert.Null(run.Eta(At(8)));
  }

  // --- Value accounting ------------------------------------------------------

  [Fact]
  public void Value_SumsAcrossBeats_Gil()
  {
    var run = Started(total: 3, unit: Scrooge.RunValueUnit.Gil);
    run.RecordProgress(1, 14_000, At(1));
    run.RecordProgress(1, 6_000, At(2));
    run.RecordProgress(1, 100_000, At(3));
    Assert.Equal(120_000, run.Value);
    Assert.Equal(3, run.Done);
    Assert.Equal(Scrooge.RunValueUnit.Gil, run.Unit);
  }

  [Fact]
  public void Value_SumsAcrossBeats_Seals()
  {
    var run = Started(total: 4, unit: Scrooge.RunValueUnit.Seals);
    run.RecordProgress(1, 500, At(1));
    run.RecordProgress(1, 500, At(2));
    run.RecordProgress(1, 1_200, At(3));
    Assert.Equal(2_200, run.Value);
    Assert.Equal(Scrooge.RunValueUnit.Seals, run.Unit);
  }

  [Fact]
  public void Value_BeatWithoutItem_AndItemWithoutValue_BothCounted()
  {
    var run = Started(total: 5);
    run.RecordProgress(0, 250, At(1)); // value with no item advance (e.g. partial credit)
    run.RecordProgress(1, 0, At(2));   // a skip: item advances, no value
    Assert.Equal(1, run.Done);
    Assert.Equal(250, run.Value);
  }

  // --- Hawk full-container-observed gate -------------------------------------

  [Fact]
  public void HawkSweepInputs_FullObservation_ReturnsObservedSet()
  {
    IReadOnlySet<uint> observed = new HashSet<uint> { 100u, 200u, 300u };
    var inputs = Scrooge.RunLifecycle.HawkSweepInputs(fullyObserved: true, observed);
    Assert.NotNull(inputs);
    Assert.Equal(observed, inputs);
  }

  [Fact]
  public void HawkSweepInputs_PartialObservation_ReturnsNull()
  {
    IReadOnlySet<uint> observed = new HashSet<uint> { 100u, 200u };
    // Aborted early / cancelled / stalled / incomplete walk -> no sweep, flags stay open.
    Assert.Null(Scrooge.RunLifecycle.HawkSweepInputs(fullyObserved: false, observed));
  }

  [Fact]
  public void HawkSweepInputs_FullButEmpty_StillReturnsSet()
  {
    // A genuinely-empty container fully observed is a real (if rare) state; the
    // gate honors the full-observation signal and lets the caller decide. The
    // partial-observation guard, not emptiness, is what protects the flags.
    IReadOnlySet<uint> empty = new HashSet<uint>();
    var inputs = Scrooge.RunLifecycle.HawkSweepInputs(fullyObserved: true, empty);
    Assert.NotNull(inputs);
    Assert.Empty(inputs!);
  }

  // =========================================================================
  // SetTotal / EnsureRunning - the RunData delegation surface (pinch adoption)
  // =========================================================================

  [Fact]
  public void SetTotal_FromIdle_StartsTheRun()
  {
    var run = new RunLifecycle();
    Assert.True(run.SetTotal(50, RunValueUnit.Gil, T0));
    Assert.Equal(RunState.Running, run.State);
    Assert.Equal(50, run.Total);
  }

  [Fact]
  public void SetTotal_WhileRunning_RevisesTotalWithoutResettingProgress()
  {
    // Pinch calls SetTotalItems twice (pre-scan estimate, then refined count) -
    // the second call must not zero Done or restart the clock.
    var run = new RunLifecycle();
    run.SetTotal(50, RunValueUnit.Gil, T0);
    run.RecordProgress(10, 5_000, T0.AddMinutes(1));
    Assert.True(run.SetTotal(60, RunValueUnit.Gil, T0.AddMinutes(1)));
    Assert.Equal(60, run.Total);
    Assert.Equal(10, run.Done);
    Assert.Equal(5_000, run.Value);
    Assert.Equal(TimeSpan.FromMinutes(1), run.Elapsed(T0.AddMinutes(1)));
  }

  [Fact]
  public void SetTotal_OnTerminal_RefusesFailClosed()
  {
    var run = new RunLifecycle();
    run.SetTotal(5, RunValueUnit.Gil, T0);
    run.Complete(T0.AddMinutes(1));
    Assert.False(run.SetTotal(10, RunValueUnit.Gil, T0.AddMinutes(2)));
    Assert.Equal(5, run.Total);
    Assert.Equal(RunState.Complete, run.State);
  }

  [Fact]
  public void SetTotal_SeedsEtaBeforeFirstItem()
  {
    // The persisted AvgMsPerItem seed gives an honest pre-first-item ETA.
    var run = new RunLifecycle();
    run.SetTotal(10, RunValueUnit.Gil, T0, seededMsPerItem: 6_000);
    Assert.Equal(TimeSpan.FromMilliseconds(60_000), run.Eta(T0));
  }

  [Fact]
  public void EnsureRunning_FromIdle_StartsZeroTotalRun()
  {
    // The desynth path records beats without ever declaring a total.
    var run = new RunLifecycle();
    Assert.True(run.EnsureRunning(RunValueUnit.Gil, T0));
    Assert.Equal(RunState.Running, run.State);
    Assert.True(run.RecordProgress(1, 0, T0.AddSeconds(5)));
    Assert.Equal(1, run.Done);
  }

  [Fact]
  public void EnsureRunning_OnTerminal_Refuses()
  {
    var run = new RunLifecycle();
    run.EnsureRunning(RunValueUnit.Gil, T0);
    run.Cancel(T0.AddSeconds(1));
    Assert.False(run.EnsureRunning(RunValueUnit.Gil, T0.AddSeconds(2)));
    Assert.Equal(RunState.Cancelled, run.State);
  }
}
