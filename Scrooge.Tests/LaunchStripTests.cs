using System;
using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The launch strip's three pure pieces (ruled ledger, stage 2a): the per-run stage
/// skips, the one launch control's refusal, and the completion banner's tally.
///
/// What is worth pinning here is not the wording - it is the three invariants the
/// design rests on: a skipped stage answers "no work" (so the cursor walks past it
/// without the flow learning a new word), the button's refusal comes from the SAME
/// count the front-load gate derives (so they cannot disagree), and the banner may
/// only say "the board is worked" when the board actually is.
/// </summary>
public class LaunchStripTests
{
  private static IReadOnlySet<RoundStage> Skip(params RoundStage[] stages)
    => new HashSet<RoundStage>(stages);

  // ========================================================================
  // The skips: deferral is a plan-level answer, never a flow-level branch
  // ========================================================================

  [Fact]
  public void HasWork_UnskippedStageKeepsTheDecksOwnAnswer()
  {
    Assert.True(RoundSkips.HasWork(RoundStage.BellRun, true, Skip()));
    Assert.False(RoundSkips.HasWork(RoundStage.BellRun, false, Skip()));
  }

  [Fact]
  public void HasWork_SkippedStageHasNoWorkHoweverFullItsPileIs()
  {
    // The whole mechanism, in one assertion: the deck says yes, the run says not
    // tonight. RoundPlan.Next already knows how to walk past a stage with no work
    // WITHOUT marking it done, which is exactly what "deferred" has to mean.
    Assert.False(RoundSkips.HasWork(RoundStage.TurnIn, true, Skip(RoundStage.TurnIn)));
  }

  [Fact]
  public void HasWork_SkipsAreScopedToTheirOwnStage()
  {
    var skipped = Skip(RoundStage.Desynth);
    Assert.False(RoundSkips.HasWork(RoundStage.Desynth, true, skipped));
    Assert.True(RoundSkips.HasWork(RoundStage.BellRun, true, skipped));
    Assert.True(RoundSkips.HasWork(RoundStage.TurnIn, true, skipped));
    Assert.True(RoundSkips.HasWork(RoundStage.Pinch, true, skipped));
  }

  [Fact]
  public void Deferred_ReportsSkippedStagesInRoundOrderWithTheirCounts()
  {
    var deferred = RoundSkips.Deferred(
      Skip(RoundStage.TurnIn, RoundStage.Desynth),
      s => s == RoundStage.Desynth ? 6 : 4);

    Assert.Equal(new[] { RoundStage.Desynth, RoundStage.TurnIn }, deferred.Select(d => d.Stage));
    Assert.Equal(new[] { 6, 4 }, deferred.Select(d => d.Count));
  }

  [Fact]
  public void Deferred_AnEmptyStageSkippedIsNotSomethingLeftBehind()
  {
    // Skipping a stage that had nothing to do leaves nothing behind. A banner that
    // reported it would be counting the absence of work as work.
    Assert.Empty(RoundSkips.Deferred(Skip(RoundStage.Pinch), _ => 0));
  }

  [Fact]
  public void Chip_EveryStageHasABox()
  {
    foreach (var stage in RoundPlan.Order)
    {
      Assert.NotEqual("?", RoundSkips.Chip(stage));
      Assert.NotEqual("", RoundSkips.Hint(stage));
    }
  }

  [Fact]
  public void Chip_TheBellWearsBothOfItsWords()
  {
    // "List" and "Vendor" are legs of ONE stop (WALK unit 4's one-door bell), so
    // they defer together and the box has to say both - a box labelled "List" that
    // silently also deferred the vendoring would be the side doors back again.
    Assert.Contains("List", RoundSkips.Chip(RoundStage.BellRun));
    Assert.Contains("Vendor", RoundSkips.Chip(RoundStage.BellRun));
  }

  // ========================================================================
  // The launch control: one button, and a reason when it refuses
  // ========================================================================

  // The three rulings-arm tests that used to live here were RETIRED 2026-08-12 with
  // the arm itself (the minors batch): the launch stopped asking about rulings when
  // the board moved to the hinge, so they were certifying a refusal no press could
  // reach. The live contract - the count is GatePlan's own derived queue, not a
  // second opinion - moved to the hinge with the gate and is pinned there
  // (Hinge_RulingsCountIsTheGatesOwnQueue).

  [Fact]
  public void Assess_FiresWhenThereIsWorkAndNothingOwed()
  {
    var state = LaunchControl.Assess(armedStages: 3, turnInIsTheOnlyWork: false,
      sealWalletFull: false);
    Assert.True(state.CanFire);
    Assert.Equal(LaunchBlock.None, state.Block);
    Assert.Equal("", state.Companion);
  }

  [Fact]
  public void Assess_NothingStagedRefusesWithItsOwnWords()
  {
    var state = LaunchControl.Assess(0, false, false);
    Assert.False(state.CanFire);
    Assert.Equal(LaunchBlock.NothingStaged, state.Block);
    Assert.Equal("nothing staged", state.Companion);
  }

  [Fact]
  public void Assess_FullWalletRefusesOnlyWhenTheTurnInIsTheWholeErrand()
  {
    // Narrow by construction: the wallet stops the turn-in, not the round. A night
    // with a bell to run still has an errand worth walking.
    Assert.True(LaunchControl.Assess(3, turnInIsTheOnlyWork: false, sealWalletFull: true).CanFire);

    var only = LaunchControl.Assess(1, turnInIsTheOnlyWork: true, sealWalletFull: true);
    Assert.False(only.CanFire);
    Assert.Equal(LaunchBlock.SealWalletFull, only.Block);
    Assert.Equal("seal wallet full", only.Companion);
  }

  [Fact]
  public void Assess_ASoleTurnInWithRoomStillFires()
    => Assert.True(LaunchControl.Assess(1, turnInIsTheOnlyWork: true, sealWalletFull: false).CanFire);

  [Fact]
  public void Hinge_RulingsCountIsTheGatesOwnQueue()
  {
    // The contract that keeps the refusal and the gate from disagreeing, at its new
    // seat: the number the HINGE refuses over is GatePlan's derived queue, not a
    // second opinion. Carried over from the retired launch-side pin.
    var candidates = new[]
    {
      ("rides", RoundStage.BellRun, ConfidenceTier.Unanimous, false, false, false, false),
      ("asks", RoundStage.BellRun, ConfidenceTier.Mixed, false, false, false, false),
      ("demoted", RoundStage.TurnIn, ConfidenceTier.Contradicted, false, false, false, false),
      // A deferring row is not a ruling the hinge may refuse over (08-06):
      // the round spends it whatever the player does.
      ("defers", RoundStage.BellRun, ConfidenceTier.Mixed, false, false, false, true),
    };
    var queue = GatePlan.Queue(candidates);

    var gate = AccountantPlan.Continue(queue.Count, actHasWork: true);
    Assert.False(gate.CanContinue);
    Assert.Equal("2 rulings needed", gate.Companion);
  }

  // ========================================================================
  // The completion banner: "worked" only when the board is
  // ========================================================================

  [Fact]
  public void Banner_CleanBookIsTheOnlyBoardIsWorked()
  {
    var clean = WaitingTally.Clean;
    Assert.True(clean.BookIsClean);
    Assert.Equal("Round complete - the board is worked.", RoundBanner.Lead(clean));
    Assert.Equal("", RoundBanner.Count(clean));
    Assert.Equal("", RoundBanner.Breakdown(clean));
    // No jump target: unit 5 deleted it with the standalone desk (see RoundBanner).
  }

  [Fact]
  public void Banner_OneUnruledRowIsEnoughToStopSayingWorked()
  {
    var t = new WaitingTally(1, 0, Array.Empty<(RoundStage, int)>());
    Assert.False(t.BookIsClean);
    Assert.Equal("Round complete -", RoundBanner.Lead(t));
    Assert.Equal("1 row still waiting on you", RoundBanner.Count(t));
  }

  [Fact]
  public void Banner_TotalIsTheThreeSourcesAdded()
  {
    var t = new WaitingTally(3, 2, new[] { (RoundStage.TurnIn, 4) });
    Assert.Equal(4, t.DeferredRows);
    Assert.Equal(9, t.Total);
    Assert.Equal("9 rows still waiting on you", RoundBanner.Count(t));
  }

  [Fact]
  public void Banner_BreakdownNamesEachSourceAndWearsTheCheckboxWords()
  {
    var t = new WaitingTally(3, 2, new[] { (RoundStage.TurnIn, 4) });
    Assert.Equal("(3 unruled, 2 staged, 4 GC turn-in skipped)", RoundBanner.Breakdown(t));
  }

  [Fact]
  public void Banner_BreakdownOmitsTheSourcesWithNothingInThem()
  {
    var t = new WaitingTally(0, 0, new[] { (RoundStage.Desynth, 6) });
    Assert.Equal("(6 Desynth skipped)", RoundBanner.Breakdown(t));
  }

  // ---- Stage all: the zero case is a state, not an armed verb (Movement 4) ----

  // The five StageAll pins retired with the button (Task 4, 08-15): the in-list bulk
  // verb died with the pile groups that hosted it, so the module they pinned is gone.
  }
