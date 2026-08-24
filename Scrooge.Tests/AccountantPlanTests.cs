using System;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE ACCOUNTANT'S PURE HALF (Rounds unit 5). The wizard is five thousand lines of
/// Dalamud; these are the three sentences inside it that are about STATE rather than
/// about pixels - what the Round's window is showing, what the pane is showing, and
/// whether the one irreversible press is allowed - plus the rail's wording.
/// </summary>
public class AccountantPlanTests
{
  // ---- What the Round's window is showing (Movement 3, 2026-08-13) ----------
  //
  // This section replaces unit 5's routing tests. That ruling ("the door routes to
  // the dashboard when idle") was honest routing to the only idle surface there was:
  // the launch preview lived on the gil dashboard. The preview IS the idle screen
  // now, so the door opens one window and this decides what is inside it.

  [Fact]
  public void ScreenFor_NoRoundIsTheLaunchPreview()
  {
    // Drift, 08-13: "I won't want to need to open the Gil Dashboard to start a round.
    // That preview step should just be the initial Rounds screen." One press at the
    // bell lands on Make the Rounds, with no detour through the dashboard.
    Assert.Equal(RoundScreen.Preview, AccountantPlan.ScreenFor(roundLive: false, reportOwed: false));
  }

  [Fact]
  public void ScreenFor_ARoundIsTheWizard()
  {
    // Flowing or HELD - both are a Round to be in, and both draw the wizard, which is
    // where the resume line and its button live. A held round that showed the preview
    // would offer a second errand over the top of the one it is holding.
    Assert.Equal(RoundScreen.Wizard, AccountantPlan.ScreenFor(roundLive: true, reportOwed: false));
    Assert.Equal(RoundScreen.Wizard, AccountantPlan.ScreenFor(roundLive: true, reportOwed: true));
  }

  [Fact]
  public void ScreenFor_AnOwedReportOutranksThePreview()
  {
    // The report is what the player came back for; the preview is what he does next.
    // The screen carries both - report over preview - so an ended round is never a
    // dead end, and a dismissed one falls straight back to the preview.
    Assert.Equal(RoundScreen.Report, AccountantPlan.ScreenFor(roundLive: false, reportOwed: true));
  }

  [Fact]
  public void DoorLabel_TheDashboardDoorOpensAndNeverFires()
  {
    // Both labels are about opening a WINDOW. The dashboard reads the world; the
    // press that starts an errand is on the Round's own screen, and no wording here
    // may suggest otherwise.
    Assert.Equal("Open the Rounds", AccountantPlan.DoorLabel(roundLive: false));
    Assert.Equal("Open the Round", AccountantPlan.DoorLabel(roundLive: true));
  }

  // ---- What the pane is showing --------------------------------------------

  [Fact]
  public void PaneFor_AHaltOutranksEverything()
  {
    // A frozen round has one live verb and the pane has one job: say what died. Even
    // over the hinge, and even with a stage still offered - which cannot happen while
    // halted (Next answers null), but the precedence is asserted rather than assumed.
    Assert.Equal(StepPane.Halt, AccountantPlan.PaneFor(halted: true, offered: null));
    Assert.Equal(StepPane.Halt, AccountantPlan.PaneFor(halted: true, RoundStage.Triage));
    Assert.Equal(StepPane.Halt, AccountantPlan.PaneFor(halted: true, RoundStage.BellRun));
  }

  [Fact]
  public void PaneFor_NoOfferedStageIsTheCompletionPage()
  {
    // The cursor ran dry: nothing left to offer and no corpse to name. That is the
    // final page, and it is where the tally lives (spec section 5).
    Assert.Equal(StepPane.Completion, AccountantPlan.PaneFor(halted: false, offered: null));
  }

  [Fact]
  public void PaneFor_TheHingeIsTheBoardsOneHost()
  {
    // Ruled Q4: the board renders in exactly one place, and this is the predicate
    // that decides it. If this ever answers Board for a second stage, the board has a
    // second host and the ruling is quietly gone.
    Assert.Equal(StepPane.Board, AccountantPlan.PaneFor(halted: false, RoundStage.Triage));

    foreach (var stage in RoundPlan.Order)
    {
      if (stage == RoundStage.Triage) continue;
      Assert.Equal(StepPane.Run, AccountantPlan.PaneFor(halted: false, stage));
    }
  }

  // ---- The hinge's Continue (ruled Q1) -------------------------------------

  [Fact]
  public void Continue_RefusesOverOpenRulings_TheLaunchGatesArithmeticInItsNewSeat()
  {
    var gate = AccountantPlan.Continue(rulingsNeeded: 3, actHasWork: true);

    Assert.False(gate.CanContinue);
    // The same sentence the launch control said, because it is the same count -
    // GatePlan's queue, already narrowed to tonight's selected steps.
    Assert.Equal("3 rulings needed", gate.Companion);
  }

  [Fact]
  public void Continue_SaysOneRulingInTheSingular()
  {
    Assert.Equal("1 ruling needed", AccountantPlan.Continue(1, actHasWork: true).Companion);
  }

  [Fact]
  public void Continue_WithNothingOpen_Passes()
  {
    var gate = AccountantPlan.Continue(rulingsNeeded: 0, actHasWork: true);
    Assert.True(gate.CanContinue);
    Assert.Equal("", gate.Companion);
  }

  [Fact]
  public void Continue_WithNoActWorkTonight_PassesEvenWithRulingsOpen()
  {
    // The hinge guards the act half. With nothing about to be spent there is nothing
    // to guard, and refusing would be the gate arguing with the cursor about a stage
    // neither of them is going to run - the cursor skips an empty hinge silently.
    var gate = AccountantPlan.Continue(rulingsNeeded: 7, actHasWork: false);

    Assert.True(gate.CanContinue);
    Assert.Equal("", gate.Companion);
  }

  [Fact]
  public void Continue_ANegativeOrZeroCount_IsNeverARefusal()
  {
    // Fail-open only on the count, never on the work: a count that arrived wrong must
    // not wedge the Round at its own hinge with no way past.
    Assert.True(AccountantPlan.Continue(0, true).CanContinue);
    Assert.True(AccountantPlan.Continue(-1, true).CanContinue);
  }

  [Fact]
  public void Continue_RefusesOverAnUnfinishedWalk_TheForcedWalkIsTheGatesRule()
  {
    // Ruled 08-15: "Continue itself should refuse until you've walked every page."
    // A settled board the player never looked at is not a board he ruled on.
    var gate = AccountantPlan.Continue(rulingsNeeded: 0, actHasWork: true, walkFinished: false);

    Assert.False(gate.CanContinue);
    Assert.Equal("pages left to walk", gate.Companion);
  }

  [Fact]
  public void Continue_OpenRulingsOutrankTheUnfinishedWalk_TheCountIsTheSpecificRefusal()
  {
    // Both refusals hold at once; the companion names the rulings, because "what am I
    // owed" is the reader's question and the page count is just where he answers it.
    Assert.Equal("2 rulings needed",
      AccountantPlan.Continue(2, actHasWork: true, walkFinished: false).Companion);
  }

  [Fact]
  public void Continue_NoActWork_PassesEvenMidWalk()
  {
    // Nothing to guard = nothing to gate, walked or not - the same rule the ruling
    // count already follows.
    Assert.True(AccountantPlan.Continue(0, actHasWork: false, walkFinished: false).CanContinue);
  }

  [Fact]
  public void Continue_AFinishedWalk_PassesExactlyAsBefore()
  {
    Assert.True(AccountantPlan.Continue(0, actHasWork: true, walkFinished: true).CanContinue);
  }

  // ---- The narrowed gate, end to end at the hinge seam ---------------------

  /// <summary>
  /// The window's own narrowing rule, modelled: a candidate feeding a step this Round
  /// will not visit is dropped before the queue ever sees it (AccountantWindow's
  /// JudgmentQueue does exactly this with <c>_skipped</c>). Reproduced here so the
  /// SEAM - narrowing, then GatePlan, then the hinge's gate - is asserted as one
  /// arithmetic rather than three that happen to agree today.
  /// </summary>
  private static int NarrowedRulings(
    (RoundStage Stage, ConfidenceTier Tier, bool PlayerResolved)[] candidates,
    params RoundStage[] skipped)
    => GatePlan.Queue(candidates
        .Where(c => Array.IndexOf(skipped, c.Stage) < 0)
        .Select(c => ((object)c, c.Stage, c.Tier, c.PlayerResolved,
          InReview: false, RidesWholePile: false, Deferred: false)))
      .Count;

  [Fact]
  public void HingeGate_RefusesOverRowsFeedingTonightsSteps()
  {
    // Two Mixed rows nobody ruled, both feeding steps this Round will run. That is
    // what the launch button used to refuse over, and it is what Continue refuses
    // over now - the count did not change hands, its seat did.
    var open = NarrowedRulings(new[]
    {
      (RoundStage.BellRun, ConfidenceTier.Mixed, false),
      (RoundStage.TurnIn, ConfidenceTier.Mixed, false),
    });

    Assert.Equal(2, open);
    Assert.False(AccountantPlan.Continue(open, actHasWork: true).CanContinue);
  }

  [Fact]
  public void HingeGate_ARulingFeedingADeferredStepIsNotADecisionAboutTonight()
  {
    // THE NARROWING, and why it matters at this seat: the player unchecked GC
    // turn-in, so the rows that would have ridden it are not part of this errand. If
    // the hinge still refused over them the skip would be a TRAP - unchecking a step
    // would lock the Round at the one gate that cannot be skipped.
    var open = NarrowedRulings(new[]
    {
      (RoundStage.TurnIn, ConfidenceTier.Mixed, false),
      (RoundStage.TurnIn, ConfidenceTier.Mixed, false),
    }, RoundStage.TurnIn);

    Assert.Equal(0, open);
    Assert.True(AccountantPlan.Continue(open, actHasWork: true).CanContinue);
  }

  [Fact]
  public void HingeGate_ARuledRowStopsBlocking_WhateverItsEvidenceSaid()
  {
    // The click IS the ruling (the ruled board, stage 1). A Mixed row the player
    // resolved rides, so the queue drops it and the hinge opens - which is the whole
    // shape of the loop: rule the rows in front of you, then Continue.
    var before = NarrowedRulings(new[] { (RoundStage.BellRun, ConfidenceTier.Mixed, false) });
    var after = NarrowedRulings(new[] { (RoundStage.BellRun, ConfidenceTier.Mixed, true) });

    Assert.False(AccountantPlan.Continue(before, actHasWork: true).CanContinue);
    Assert.True(AccountantPlan.Continue(after, actHasWork: true).CanContinue);
  }

  [Fact]
  public void HingeGate_UnanimousRowsWereNeverWaitingOnAnybody()
  {
    // The maturation vector (GatePlan's own claim): as the confidence machinery
    // improves, fewer rows fail the bar and the hinge stops having anything to say -
    // with no change to the gate. A Round whose board is all Unanimous continues on
    // the first press.
    var open = NarrowedRulings(new[]
    {
      (RoundStage.BellRun, ConfidenceTier.Unanimous, false),
      (RoundStage.Desynth, ConfidenceTier.Unanimous, false),
    });

    Assert.Equal(0, open);
    Assert.True(AccountantPlan.Continue(open, actHasWork: true).CanContinue);
  }

  // ---- The rail's wording ---------------------------------------------------

  [Fact]
  public void RailLine_ACurrentStepCarriesItsCountAndItsMeasuredEta()
  {
    var row = new RailRow(RoundStage.Recon, RailState.Current, 12, 48_000);

    var line = AccountantPlan.RailLine(row, "recon", tally: "");

    Assert.Equal(" > recon (12) - ~<1m", line);
  }

  [Fact]
  public void RailLine_AnUnmeasuredStepSaysSoRatherThanBorrowingANumber()
  {
    var row = new RailRow(RoundStage.TurnIn, RailState.Pending, 4, null);

    Assert.Equal(" . turn-in (4) - no timing yet",
      AccountantPlan.RailLine(row, "turn-in", tally: ""));
  }

  [Fact]
  public void RailLine_TheHingeAndTheLiveRunSpeakTheRailsOwnWords()
  {
    // SF-P2: the vertical rail says exactly what the run log's horizontal one says,
    // because both now ask StageRail for the ETA slot instead of each hand-rolling it.
    var hinge = new RailRow(StageRail.Hinge, RailState.Current, 3, null, EtaBasis.WaitsOnYou);
    Assert.Equal(" > triage (3) - waits on you",
      AccountantPlan.RailLine(hinge, "triage", tally: ""));

    var walking = new RailRow(RoundStage.Pinch, RailState.Current, 0, 527_000, EtaBasis.LiveRun);
    Assert.Equal(" > pinch - ~9m", AccountantPlan.RailLine(walking, "pinch", tally: ""));
  }

  [Fact]
  public void RailLine_AnEmptyStepWearsTheEmptyNoteAndNoEta()
  {
    // Done and Empty have no time left to spend whatever they count, so neither
    // quotes one - and the empty note is StageRail's single string.
    var row = new RailRow(RoundStage.Desynth, RailState.Empty, 0, null);

    Assert.Equal(" . melt  (nothing to do)",
      AccountantPlan.RailLine(row, "melt", tally: ""));
  }

  [Fact]
  public void RailLine_ATallyReplacesTheEmptyNote()
  {
    // A finished step says what it DID. The empty note is about a pile that was
    // empty; a tally is about work that happened, and saying both would be the rail
    // claiming a stage both did something and had nothing to do.
    var row = new RailRow(RoundStage.Recon, RailState.Done, 0, null);

    Assert.Equal(" x recon  14 lanes banked",
      AccountantPlan.RailLine(row, "recon", tally: "14 lanes banked"));
  }

  [Fact]
  public void RailLine_AHaltedStepWearsTheBang()
  {
    var row = new RailRow(RoundStage.BellRun, RailState.Halted, 7, null);

    Assert.StartsWith(" ! bell", AccountantPlan.RailLine(row, "bell", tally: ""));
  }

  // ---- The done tally -------------------------------------------------------

  [Fact]
  public void DoneTally_OnlyFinishedStepsGetOne()
  {
    // A pending step's count is a FORECAST, already rendered as one. Dressing it in
    // the past tense - "14 lanes banked" over a recon that has not run - would be the
    // rail claiming work nobody did.
    foreach (var state in new[]
      { RailState.Pending, RailState.Current, RailState.Empty, RailState.Halted })
      Assert.Equal("", AccountantPlan.DoneTally(state, RoundStage.Recon, 14));

    Assert.Equal("14 lanes banked",
      AccountantPlan.DoneTally(RailState.Done, RoundStage.Recon, 14));
  }

  [Fact]
  public void DoneTally_AStepThatProcessedNothingSaysNothing()
  {
    // A silently-skipped stage marks nothing and a completed run that touched no
    // items has no story. "0 melted" is a sentence about nothing.
    Assert.Equal("", AccountantPlan.DoneTally(RailState.Done, RoundStage.Desynth, 0));
  }

  [Fact]
  public void DoneTally_TheHingeHasNoTally_BecauseItsExecutorProcessesNothing()
  {
    // Its executor is a human press. There is no run, no processed count, and
    // inventing one would be the rail measuring a decision.
    Assert.Equal("", AccountantPlan.DoneTally(RailState.Done, RoundStage.Triage, 9));
  }

  [Fact]
  public void DoneTally_EachRunStageSpeaksInItsOwnUnits()
  {
    Assert.Equal("87 lanes read", AccountantPlan.DoneTally(RailState.Done, RoundStage.Pinch, 87));
    Assert.Equal("1 lane banked", AccountantPlan.DoneTally(RailState.Done, RoundStage.Recon, 1));
    Assert.Equal("6 worked at the bell", AccountantPlan.DoneTally(RailState.Done, RoundStage.BellRun, 6));
    Assert.Equal("18 melted", AccountantPlan.DoneTally(RailState.Done, RoundStage.Desynth, 18));
    Assert.Equal("4 turned in", AccountantPlan.DoneTally(RailState.Done, RoundStage.TurnIn, 4));
  }
}
