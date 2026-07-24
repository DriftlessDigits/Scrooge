using System;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The one-button sweep's cursor (v2.18). The contract: stages fire in
/// workflow order, empty stages are skipped but not buried (work appearing
/// late is picked up), done is done, and the sweep completes when nothing
/// with work remains.
/// </summary>
public class SweepPlanTests
{
  private static bool All(SweepStage _) => true;

  [Fact]
  public void Order_IsTheEndgameSentence()
  {
    // "kick off a full sweep - pinch, desynth, and then list items for sale"
    // resolved to workflow order: the board read first, everything at the
    // bell next, then the melt, then the churn.
    Assert.Equal(new[]
    {
      SweepStage.Pinch, SweepStage.BellRun, SweepStage.Reprice,
      SweepStage.Desynth, SweepStage.TurnIn,
    }, SweepPlan.Order);
  }

  [Fact]
  public void PlaceOf_ThreeStops()
  {
    Assert.Equal(SweepPlace.Bell, SweepPlan.PlaceOf(SweepStage.Pinch));
    Assert.Equal(SweepPlace.Bell, SweepPlan.PlaceOf(SweepStage.BellRun));
    Assert.Equal(SweepPlace.Bell, SweepPlan.PlaceOf(SweepStage.Reprice));
    Assert.Equal(SweepPlace.Anywhere, SweepPlan.PlaceOf(SweepStage.Desynth));
    Assert.Equal(SweepPlace.ExpertDelivery, SweepPlan.PlaceOf(SweepStage.TurnIn));
  }

  [Fact]
  public void Next_WalksTheOrderAsStagesComplete()
  {
    var plan = new SweepPlan();
    plan.Start();
    Assert.Equal(SweepStage.Pinch, plan.Next(All));
    plan.MarkDone(SweepStage.Pinch);
    Assert.Equal(SweepStage.BellRun, plan.Next(All));
    plan.MarkDone(SweepStage.BellRun);
    Assert.Equal(SweepStage.Reprice, plan.Next(All));
  }

  [Fact]
  public void Next_SkipsEmptyStages()
  {
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    // No bell work, no reprices - cursor lands on the melt.
    Assert.Equal(SweepStage.Desynth,
      plan.Next(s => s is SweepStage.Desynth or SweepStage.TurnIn));
  }

  [Fact]
  public void Next_PicksUpWorkThatAppearsLate()
  {
    // A pinch flags reprices AFTER the cursor skipped past an empty Reprice:
    // skipped-empty is not marked done, so the stage is picked up in order.
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    Assert.Equal(SweepStage.Desynth, plan.Next(s => s == SweepStage.Desynth));
    Assert.Equal(SweepStage.Reprice,
      plan.Next(s => s is SweepStage.Reprice or SweepStage.Desynth));
  }

  [Fact]
  public void Unmark_HandsAnAbortedStageBackToTheCursor()
  {
    // The 07-22 sweep lap: melt fired (marked done at fire time), the run died
    // over an open bell, and the deck kept claiming the melt was done. Unmark
    // reverts the fire-time promise so the cursor offers the stage again.
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.MarkDone(SweepStage.Desynth);
    Assert.Equal(SweepStage.TurnIn,
      plan.Next(s => s is SweepStage.Desynth or SweepStage.TurnIn));
    plan.Unmark(SweepStage.Desynth);
    Assert.Equal(SweepStage.Desynth,
      plan.Next(s => s is SweepStage.Desynth or SweepStage.TurnIn));
  }

  [Fact]
  public void Unmark_OfAnUnmarkedStage_IsANoOp()
  {
    var plan = new SweepPlan();
    plan.Start();
    plan.Unmark(SweepStage.Desynth);
    Assert.False(plan.IsDone(SweepStage.Desynth));
    Assert.Equal(SweepStage.Pinch, plan.Next(All));
  }

  [Fact]
  public void Next_NullWhenEverythingLeftIsDoneOrEmpty()
  {
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.MarkDone(SweepStage.Desynth);
    Assert.Null(plan.Next(s => s is SweepStage.Pinch or SweepStage.Desynth));
  }

  [Fact]
  public void StartAndCancel_ResetTheCursor()
  {
    var plan = new SweepPlan();
    Assert.False(plan.Active);
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.Cancel();
    Assert.False(plan.Active);
    plan.Start();
    // A fresh sweep forgets the last one's progress.
    Assert.Equal(SweepStage.Pinch, plan.Next(All));
  }

  // ---- HALT-NAME-RESUME (WALK unit 2) ----

  [Fact]
  public void Halt_HoldsThePlace_NeverOffersPastACorpse()
  {
    // The melt fires (marked done at fire time) and dies mid-run. Halt holds the
    // place: the completed pinch stays done, but the deck must NOT offer the next
    // stage past the corpse - Next returns null while halted.
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.MarkDone(SweepStage.Desynth);
    plan.Halt(SweepHalt.Plainly(SweepStage.Desynth, "Melt", "the run stopped", "Resume."));

    Assert.True(plan.Halted);
    Assert.Equal(SweepStage.Desynth, plan.HaltStage);
    Assert.True(plan.IsDone(SweepStage.Pinch)); // completion marks held
    Assert.False(plan.IsDone(SweepStage.Desynth)); // the corpse is not "done"
    Assert.Null(plan.Next(All)); // never past the corpse
  }

  [Fact]
  public void Resume_ReOffersTheHaltedStageOnly_NeverFromTheTop()
  {
    // Resume re-fires the dead stage from its own rescan: the halted stage becomes
    // current again, and the completed pinch is NEVER re-offered (a re-running
    // pinch is the market-pressure shape the cadence gate exists to prevent).
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.MarkDone(SweepStage.BellRun);
    plan.MarkDone(SweepStage.Reprice);
    plan.MarkDone(SweepStage.Desynth);
    plan.Halt(SweepHalt.Plainly(SweepStage.Desynth, "Melt", "the run stopped", "Resume."));

    plan.Resume();
    Assert.False(plan.Halted);
    // Only the halted stage is re-offered; the done stages stay buried.
    Assert.Equal(SweepStage.Desynth, plan.Next(All));
    Assert.True(plan.IsDone(SweepStage.Pinch));
    Assert.True(plan.IsDone(SweepStage.BellRun));
  }

  [Fact]
  public void Resume_SkipsTheHaltedStageWhenItsRescanFindsNoWork()
  {
    // The melt's own rescan (fire-time work list) is now empty - Resume must not
    // wedge on an empty stage; the cursor skips it forward like any empty stage.
    var plan = new SweepPlan();
    plan.Start();
    plan.MarkDone(SweepStage.Pinch);
    plan.MarkDone(SweepStage.Desynth);
    plan.Halt(SweepHalt.Plainly(SweepStage.Desynth, "Melt", "the run stopped", "Resume."));
    plan.Resume();

    // Desynth has no work now, TurnIn does.
    Assert.Equal(SweepStage.TurnIn, plan.Next(s => s == SweepStage.TurnIn));
  }

  [Fact]
  public void HaltVsUnmark_QuietRevertVersusLoudStop()
  {
    // Unmark: quiet revert - stage straight back onto the cursor, no halt.
    var quiet = new SweepPlan();
    quiet.Start();
    quiet.MarkDone(SweepStage.Pinch);
    quiet.MarkDone(SweepStage.BellRun);
    quiet.MarkDone(SweepStage.Reprice);
    quiet.MarkDone(SweepStage.Desynth);
    quiet.Unmark(SweepStage.Desynth);
    Assert.False(quiet.Halted);
    Assert.Equal(SweepStage.Desynth, quiet.Next(All)); // offered immediately

    // Halt: loud stop - same un-mark of the stage, but Next is blocked until Resume.
    var loud = new SweepPlan();
    loud.Start();
    loud.MarkDone(SweepStage.Pinch);
    loud.MarkDone(SweepStage.BellRun);
    loud.MarkDone(SweepStage.Reprice);
    loud.MarkDone(SweepStage.Desynth);
    loud.Halt(SweepHalt.Plainly(SweepStage.Desynth, "Melt", "the run stopped", "Resume."));
    Assert.True(loud.Halted);
    Assert.False(loud.IsDone(SweepStage.Desynth)); // both revert the stage mark
    Assert.Null(loud.Next(All)); // but Halt blocks the cursor
  }

  [Fact]
  public void Halt_NamesTheGap_PlainlyAndInSpineVocabulary()
  {
    // Plainly: a death that is not a spine facet (server timeout) - what died and
    // what would clear it.
    var plain = SweepHalt.Plainly(SweepStage.Desynth, "Melt",
      "timeout waiting for SalvageResult", "Clear it and Resume.");
    Assert.Equal(SweepStage.Desynth, plain.Stage);
    Assert.Contains("Melt halted", plain.Message);
    Assert.Contains("timeout waiting for SalvageResult", plain.Message);
    Assert.Contains("Resume", plain.Message);

    // FromSpine: a death whose reason maps to a declared expectation borrows the
    // evaluation's "expected X, but Y" message verbatim.
    var eval = SpineEvaluator.Evaluate(
      new ExpectedState("melt",
        new SpineExpectation(Spine.Facet.Occupancy, "an un-occupied player", Spine.Rung.Refuse)),
      new[] { new FacetReading(false, "the retainer bell is open") });
    var spun = SweepHalt.FromSpine(SweepStage.Desynth, eval);
    Assert.Equal(eval.Message, spun.Message);
    Assert.Equal(SweepStage.Desynth, spun.Stage);
  }

  // ---- Persistence: the held place survives a reload ----

  [Fact]
  public void ExportRestore_RoundTripsTheHeldPlace()
  {
    var started = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var plan = new SweepPlan();
    plan.Start(started);
    plan.MarkDone(SweepStage.Pinch);
    plan.MarkDone(SweepStage.BellRun);
    plan.MarkDone(SweepStage.Desynth);
    plan.Halt(SweepHalt.Plainly(SweepStage.Desynth, "Melt", "the run stopped", "Resume."));

    var state = plan.Export();

    var restored = new SweepPlan();
    restored.Restore(state);

    Assert.True(restored.Active);
    Assert.True(restored.IsDone(SweepStage.Pinch));
    Assert.True(restored.IsDone(SweepStage.BellRun));
    Assert.False(restored.IsDone(SweepStage.Desynth)); // halted, not done
    Assert.True(restored.Halted);
    Assert.Equal(SweepStage.Desynth, restored.HaltStage);
    Assert.Equal(plan.CurrentHalt!.Message, restored.CurrentHalt!.Message);
    Assert.Equal(started, restored.StartedAt);
    // The deck shows the same stages done/current/halted as before the reload.
    Assert.Null(restored.Next(All));
  }

  [Fact]
  public void ExportRestore_FlowingSweepRoundTripsWithoutAHalt()
  {
    var plan = new SweepPlan();
    plan.Start(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
    plan.MarkDone(SweepStage.Pinch);

    var restored = new SweepPlan();
    restored.Restore(plan.Export());

    Assert.False(restored.Halted);
    Assert.Null(restored.CurrentHalt);
    Assert.Equal(SweepStage.BellRun, restored.Next(All));
  }

  // ---- Staleness: a sweep too old to trust is history, not a sweep ----

  [Fact]
  public void IsStale_RetiresASweepPastTheCeiling()
  {
    var started = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    var ceiling = TimeSpan.FromHours(4);

    // Within the ceiling: still a sweep.
    Assert.False(SweepPlan.IsStale(started, started.AddHours(3), ceiling));
    Assert.False(SweepPlan.IsStale(started, started.AddHours(4), ceiling)); // exactly the ceiling holds
    // Past the ceiling: history.
    Assert.True(SweepPlan.IsStale(started, started.AddHours(4).AddSeconds(1), ceiling));
    Assert.True(SweepPlan.IsStale(started, started.AddDays(1), ceiling));
  }
}
