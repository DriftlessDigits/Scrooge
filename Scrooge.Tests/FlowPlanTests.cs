using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// RUN COMPLETION IS A FLOW EVENT (WALK unit 6). The contract this file defends:
/// a completion NEVER fires anything, it only ever reacts to a run that already
/// ended; a death halts exactly when the round is the thing that fired the dead
/// stage; and the coffer rider - which is not a stage - can never halt a round.
/// </summary>
public class FlowPlanTests
{
  private static Func<RoundStage, bool> DoneSet(params RoundStage[] done)
  {
    var set = new HashSet<RoundStage>(done);
    return s => set.Contains(s);
  }

  private static Func<RoundStage, bool> NothingDone => _ => false;

  // ========================================================================
  // The stage map
  // ========================================================================

  [Fact]
  public void StageOf_MapsEveryRunShapeToItsStage()
  {
    Assert.Equal(RoundStage.Pinch, FlowPlan.StageOf(RunKind.Pinch));
    Assert.Equal(RoundStage.BellRun, FlowPlan.StageOf(RunKind.Bell));
    Assert.Equal(RoundStage.BellRun, FlowPlan.StageOf(RunKind.Standing));
    Assert.Equal(RoundStage.Desynth, FlowPlan.StageOf(RunKind.Melt));
    Assert.Equal(RoundStage.TurnIn, FlowPlan.StageOf(RunKind.TurnIn));
    Assert.Equal(RoundStage.Recon, FlowPlan.StageOf(RunKind.Recon));
  }

  [Fact]
  public void StageOf_TheTwoTriagesAreNotTheSameThing()
  {
    // A doubled word with a wiring bug waiting inside it (Rounds, 08-10).
    // RunKind.Standing is the standing-listing EXECUTOR - reprices, pulls, vendors -
    // and it belongs to the bell. RoundStage.Triage is the HUMAN HINGE, and it has
    // no run kind at all: its executor is a press, and a press does not report a
    // completion. If these two ever meet, a finished reprice leg would mark the
    // hinge done and walk the round straight into the act half unruled.
    Assert.Equal(RoundStage.BellRun, FlowPlan.StageOf(RunKind.Standing));
    Assert.NotEqual(RoundStage.Triage, FlowPlan.StageOf(RunKind.Standing));
    Assert.DoesNotContain(
      Enum.GetValues<RunKind>(),
      k => FlowPlan.StageOf(k) == RoundStage.Triage);
  }

  [Fact]
  public void HaltFor_EveryStageInTheOrderNamesItselfAndItsClearingAction()
  {
    // "The stage halted" is the advisor shrugging at the one stage the player was
    // standing in front of. Every stage the cursor can offer owes a name and an
    // honest sentence about what Resume would actually do.
    foreach (var stage in RoundPlan.Order)
    {
      var halt = FlowPlan.HaltFor(stage, "it died");
      Assert.Equal(stage, halt.Stage);
      Assert.DoesNotContain("The stage halted", halt.Message);
      Assert.Contains("Resume", halt.Message);
      Assert.Contains("it died", halt.Message);
    }
  }

  [Fact]
  public void StageOf_CofferIsNotAStage()
  {
    // The rider fires at the FRONT of the melt and hands off whether it opened
    // many, none, or died - the melt's own run marks the stage done. A dead
    // coffer loop must never halt a round over a stage that then proceeds.
    Assert.Null(FlowPlan.StageOf(RunKind.Coffer));
  }

  [Fact]
  public void Reaction_ADeadCofferRiderNeverHalts()
  {
    var reaction = FlowPlan.Reaction(
      RunCompletion.Died(RunKind.Coffer, "watchdog: task queue died"),
      roundActive: true, roundHalted: false, DoneSet(RoundStage.BellRun));
    Assert.Equal(FlowReaction.None, reaction);
  }

  // ========================================================================
  // The halt rule
  // ========================================================================

  [Fact]
  public void Reaction_ADeadFiredStageHalts()
  {
    // The 07-24 melt case: the round fired the melt, the run died, so the round
    // holds its place over the corpse. (The predicate is the caller's - here it
    // answers via a done-mark; the deck's real one answers via the in-flight
    // latch since SF2 moved marks to completion.)
    var reaction = FlowPlan.Reaction(
      RunCompletion.Died(RunKind.Melt, "timeout waiting for SalvageItemSelector"),
      roundActive: true, roundHalted: false, DoneSet(RoundStage.Desynth));
    Assert.Equal(FlowReaction.Halt, reaction);
  }

  [Fact]
  public void Reaction_EveryStageHaltsNotJustTheMelt()
  {
    // The old draw-poll only ever watched the melt. The event watches them all.
    var cases = new (RunKind Kind, RoundStage Stage)[]
    {
      (RunKind.Pinch, RoundStage.Pinch),
      (RunKind.Bell, RoundStage.BellRun),
      (RunKind.Standing, RoundStage.BellRun),
      (RunKind.Melt, RoundStage.Desynth),
      (RunKind.TurnIn, RoundStage.TurnIn),
    };
    foreach (var (kind, stage) in cases)
    {
      var reaction = FlowPlan.Reaction(
        RunCompletion.Died(kind, "it died"),
        roundActive: true, roundHalted: false, DoneSet(stage));
      Assert.Equal(FlowReaction.Halt, reaction);
    }
  }

  [Fact]
  public void Reaction_AStagedMeltDyingHalts()
  {
    // Contract B (ruled 07-26): the melt stages at fire and marks done only at
    // completion, so a staged melt is fired-but-NOT-done. The caller's predicate
    // answers "fired by the round" for it anyway - a run dying under the player's
    // own Run Desynth press is still the round's stage dying, and a halt that
    // required the checkmark would let a staged melt die silently and wedge the
    // flow behind a latch nothing clears.
    var meltStaged = true;
    var reaction = FlowPlan.Reaction(
      RunCompletion.Died(RunKind.Melt, "desynth aborted mid-pile"),
      roundActive: true, roundHalted: false,
      s => NothingDone(s) || (s == RoundStage.Desynth && meltStaged));
    Assert.Equal(FlowReaction.Halt, reaction);
  }

  [Fact]
  public void Reaction_ACompletedRunNeverHalts()
  {
    var reaction = FlowPlan.Reaction(
      RunCompletion.Done(RunKind.Melt),
      roundActive: true, roundHalted: false, DoneSet(RoundStage.Desynth));
    Assert.Equal(FlowReaction.None, reaction);
  }

  [Fact]
  public void Reaction_AStrayManualRunIsIgnored()
  {
    // The round's claim on the run (the in-flight latch, a done mark, the staged
    // melt) answers false for every stage, so the ROUND did not fire this run -
    // the player ran a melt off the pile button. It dies on its own time.
    var reaction = FlowPlan.Reaction(
      RunCompletion.Died(RunKind.Melt, "user-initiated abort"),
      roundActive: true, roundHalted: false, NothingDone);
    Assert.Equal(FlowReaction.None, reaction);
  }

  [Fact]
  public void Reaction_NoRoundMeansNothingToHalt()
  {
    var reaction = FlowPlan.Reaction(
      RunCompletion.Died(RunKind.Melt, "user-initiated abort"),
      roundActive: false, roundHalted: false, DoneSet(RoundStage.Desynth));
    Assert.Equal(FlowReaction.None, reaction);
  }

  [Fact]
  public void Reaction_AnAlreadyHaltedRoundIsNotReHalted()
  {
    // The round is already frozen over a corpse and naming that gap. A second
    // death must not overwrite the first one's message - the player is looking
    // at the first thing that broke.
    var reaction = FlowPlan.Reaction(
      RunCompletion.Died(RunKind.Melt, "a second death"),
      roundActive: true, roundHalted: true, DoneSet(RoundStage.Desynth));
    Assert.Equal(FlowReaction.None, reaction);
  }

  // ========================================================================
  // The advance rule (WALK unit 9): one press, then the stages flow
  // ========================================================================

  [Fact]
  public void Advance_FiresTheOfferedStageWhenThePlayerIsStandingThere()
  {
    // The whole ruling in one assertion: the predecessor completed, the cursor
    // offers the next stage, the player is where it happens - it goes.
    Assert.Equal(FlowAdvance.Fire, FlowPlan.Advance(
      roundActive: true, roundHalted: false, runBusy: false,
      offered: RoundStage.BellRun, locationSatisfied: true));
  }

  [Fact]
  public void Advance_NamesTheWalkWhenThePlayerIsNotThereYet()
  {
    // Not a refusal - a waypoint. The deck names the walk (and the port, for the
    // turn-in) and ARRIVAL fires the stage: this same rule, asked again on the
    // frame the location precondition flips true.
    Assert.Equal(FlowAdvance.NameWalk, FlowPlan.Advance(
      roundActive: true, roundHalted: false, runBusy: false,
      offered: RoundStage.TurnIn, locationSatisfied: false));
  }

  [Fact]
  public void Advance_NothingWithoutAPressedRound()
  {
    // THE FIRST LAW. No operand combination fires on an idle deck - standing at a
    // bell all night with work in every pile does nothing at all until the human
    // presses Start. We EARN what we make.
    foreach (var here in new[] { true, false })
      foreach (var busy in new[] { true, false })
        Assert.Equal(FlowAdvance.Nothing, FlowPlan.Advance(
          roundActive: false, roundHalted: false, runBusy: busy,
          offered: RoundStage.Pinch, locationSatisfied: here));
  }

  [Fact]
  public void Advance_NothingWhileHalted()
  {
    // The round froze over a corpse. The human's Resume is the press that
    // unfreezes it - the flow must never walk past a dead stage on its own.
    Assert.Equal(FlowAdvance.Nothing, FlowPlan.Advance(
      roundActive: true, roundHalted: true, runBusy: false,
      offered: RoundStage.Desynth, locationSatisfied: true));
  }

  [Fact]
  public void Advance_NothingWhileARunIsInFlight()
  {
    // "Fires when the predecessor completes" - a predecessor still working has
    // not completed, whatever the cursor is pointing at mid-run.
    Assert.Equal(FlowAdvance.Nothing, FlowPlan.Advance(
      roundActive: true, roundHalted: false, runBusy: true,
      offered: RoundStage.BellRun, locationSatisfied: true));
  }

  [Fact]
  public void Advance_NothingWhenTheRoundHasNothingLeftToOffer()
  {
    // A complete round (or one holding no work) offers no stage, and a round that
    // offers no stage fires nothing - including on the frame the player wanders
    // back to a bell.
    Assert.Equal(FlowAdvance.Nothing, FlowPlan.Advance(
      roundActive: true, roundHalted: false, runBusy: false,
      offered: null, locationSatisfied: true));
  }

  // ========================================================================
  // The named gap (halt-name-resume vocabulary)
  // ========================================================================

  [Fact]
  public void HaltFor_CarriesTheExecutorsOwnReasonVerbatim()
  {
    var halt = FlowPlan.HaltFor(RoundStage.Desynth, "timeout waiting for SalvageItemSelector");
    Assert.Equal(RoundStage.Desynth, halt.Stage);
    Assert.Contains("timeout waiting for SalvageItemSelector", halt.Message);
  }

  [Fact]
  public void HaltFor_NamesWhatDiedAndWhatWouldClearIt()
  {
    var halt = FlowPlan.HaltFor(RoundStage.BellRun, "every retainer's sell list is full");
    Assert.StartsWith("Bell run halted", halt.Message);
    Assert.Contains("Resume", halt.Message);
  }

  [Fact]
  public void HaltFor_EveryStageNamesItsOwnRescan()
  {
    // "Resume" always means "look again from where the world is now" - each
    // executor rescans at fire time, and each halt says so in its own terms.
    Assert.Contains("re-reads the board", FlowPlan.HaltFor(RoundStage.Pinch, "x").Message);
    Assert.Contains("re-reads each retainer's standing listings", FlowPlan.HaltFor(RoundStage.BellRun, "x").Message);
    Assert.Contains("rescans the bags", FlowPlan.HaltFor(RoundStage.Desynth, "x").Message);
    Assert.Contains("re-reads the delivery list", FlowPlan.HaltFor(RoundStage.TurnIn, "x").Message);
  }

  [Fact]
  public void HaltFor_ASilentDeathStillNamesSomething()
  {
    // An executor that dies without words must not produce a blank banner.
    foreach (var reason in new string?[] { null, "", "   " })
    {
      var halt = FlowPlan.HaltFor(RoundStage.Desynth, reason);
      Assert.Contains("the run stopped", halt.Message);
    }
  }

  // ---- The round ends itself (2026-07-26, the zombie round) ----

  [Fact]
  public void ErrandOver_OnlyWhenActiveUnhaltedIdleAndDry()
  {
    Assert.True(FlowPlan.ErrandOver(true, false, false, false));
    Assert.False(FlowPlan.ErrandOver(false, false, false, false)); // no round, nothing to end
    Assert.False(FlowPlan.ErrandOver(true, true, false, false));   // a corpse is not a finish line
    Assert.False(FlowPlan.ErrandOver(true, false, true, false));   // the last run is still in flight
    Assert.False(FlowPlan.ErrandOver(true, false, false, true));   // there is still work to offer
  }

  // ========================================================================
  // The completion mark (shake finding SF2, 2026-08-13)
  // ========================================================================

  /// <summary>
  /// THE BUG THIS PINS. Stages used to mark done at FIRE time, and the mark lied for
  /// the length of the run: the 08-12 pinch died 3 items into 102 with "x pinch"
  /// already on the rail, and the round offered the hinge over an unpinched board.
  /// A stage is done when its run says Complete - contract B, generalized.
  /// </summary>
  [Fact]
  public void MarksStageDone_ACompletedInFlightStageMarks()
  {
    Assert.True(FlowPlan.MarksStageDone(
      RunCompletion.Done(RunKind.Pinch), RoundStage.Pinch));
    Assert.True(FlowPlan.MarksStageDone(
      RunCompletion.Done(RunKind.Recon), RoundStage.Recon));
    Assert.True(FlowPlan.MarksStageDone(
      RunCompletion.Done(RunKind.TurnIn), RoundStage.TurnIn));
    // The bell's LISTING leg ends the chain, so it ends the stage.
    Assert.True(FlowPlan.MarksStageDone(
      RunCompletion.Done(RunKind.Bell), RoundStage.BellRun));
  }

  [Fact]
  public void MarksStageDone_AnAbortNeverMarks()
  {
    // Deaths belong to the halt machinery; a mark here would be the round
    // checking off a stage over its own corpse.
    Assert.False(FlowPlan.MarksStageDone(
      RunCompletion.Died(RunKind.Pinch, "it died"), RoundStage.Pinch));
  }

  [Fact]
  public void MarksStageDone_ARunOutsideTheRoundsClaimNeverMarks()
  {
    // Nothing in flight (a manual run), or a different stage in flight - either
    // way this completion is not the round's stage finishing.
    Assert.False(FlowPlan.MarksStageDone(
      RunCompletion.Done(RunKind.Pinch), stageInFlight: null));
    Assert.False(FlowPlan.MarksStageDone(
      RunCompletion.Done(RunKind.Pinch), RoundStage.Recon));
  }

  [Fact]
  public void MarksStageDone_TheStandingLegNeverMarksTheBell()
  {
    // The bell is one stage with two legs, and the standing leg is the FIRST: its
    // completion hands the chain to the listing leg (or settles it when there is
    // nothing to list) - the chain, not the leg, decides when the stage is over.
    // A mark here would check the bell off with the listing run still to come.
    Assert.False(FlowPlan.MarksStageDone(
      RunCompletion.Done(RunKind.Standing), RoundStage.BellRun));
  }

  [Fact]
  public void MarksStageDone_TheCofferRiderMarksNothing()
  {
    // Not a stage (see StageOf_CofferIsNotAStage) - and it must not mark one even
    // while a stage is in flight around it.
    Assert.False(FlowPlan.MarksStageDone(
      RunCompletion.Done(RunKind.Coffer), RoundStage.Desynth));
  }

  // ========================================================================
  // The completion carries its facts (review ruling S1/S2, 2026-08-10)
  // ========================================================================

  /// <summary>
  /// THE BUG THIS PINS. The pump is a tick late, and every executor nulls
  /// Plugin.CurrentRun as it ends - so a subscriber that asked the live run what it
  /// processed was asking nobody, every time. Two of them did, and both were dead
  /// code the day they shipped (the rail's tallies, the melt ids the bell admits
  /// yields from). The facts travel WITH the completion now, and these receipts are
  /// what a handler is entitled to read.
  /// </summary>
  [Fact]
  public void Done_CarriesWhatTheRunDid()
  {
    var completion = RunCompletion.Done(RunKind.Melt, new RunFacts(4242, 7));

    Assert.Equal(RunOutcome.Complete, completion.Outcome);
    Assert.Equal(4242, completion.Facts.DesynthRunId);
    Assert.Equal(7, completion.Facts.ItemsProcessed);
  }

  /// <summary>
  /// A melt that died halfway still put real materials in the bags, so its id has to
  /// reach the bell exactly as a completed one does (invariant B's one exception).
  /// The reason survives beside the facts - they answer different questions.
  /// </summary>
  [Fact]
  public void Died_CarriesTheFactsToo_AndKeepsItsOwnWords()
  {
    var completion = RunCompletion.Died(RunKind.Melt, "the salvage window closed", new RunFacts(9, 3));

    Assert.Equal(RunOutcome.Aborted, completion.Outcome);
    Assert.Equal("the salvage window closed", completion.Reason);
    Assert.Equal(9, completion.Facts.DesynthRunId);
    Assert.Equal(3, completion.Facts.ItemsProcessed);
  }

  /// <summary>
  /// A refusal on the road has no run behind it, and None says exactly that: zero
  /// processed, no melt. Not "unknown" - a tally that rendered a guess here would be
  /// crediting one stage with another stage's work.
  /// </summary>
  [Fact]
  public void ARefusalCarriesNone_WhichIsZeroRatherThanUnknown()
  {
    var refused = RunCompletion.Died(RunKind.Recon, "another run was still working");

    Assert.Equal(RunFacts.None, refused.Facts);
    Assert.Null(refused.Facts.DesynthRunId);
    Assert.Equal(0, refused.Facts.ItemsProcessed);
  }

  /// <summary>
  /// The tally's own guard, at the seam the handler applies it: a stage only claims a
  /// number when the run reported one. An empty run writes no tally rather than a "0".
  /// </summary>
  [Fact]
  public void AFactlessCompletionHasNoTallyToWrite()
  {
    Assert.False(RunCompletion.Done(RunKind.Bell).Facts.ItemsProcessed > 0);
    Assert.True(RunCompletion.Done(RunKind.Bell, new RunFacts(null, 12)).Facts.ItemsProcessed > 0);
  }
}
