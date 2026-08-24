using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE TRANSIENT-ABSENCE GRACE (2026-07-26). Two live halts in one evening - the bell
/// run refusing while Drift stood at the bell (AutoRetainer mid-summon), and the turn-in's
/// port refusing because the previous stage's UI was still closing - were the same bug:
/// a reading that a second would have flipped, believed on the first look.
///
/// These cover the PURE half: the persistence window (does an absence deserve to be
/// believed yet) and the gate over it (is this refusal worth waiting out at all). The
/// sensors and the framework-tick waiter are Dalamud and untested here by design.
/// </summary>
public class GraceTests
{
  // ---- The window: how long an absence has to persist ----------------------

  [Fact]
  public void PresentReadsMet_AndHoldsNoClock()
  {
    var g = new TransientGrace(2500);
    Assert.Equal(GraceVerdict.Met, g.Observe(met: true, nowMs: 1000));
    Assert.Null(g.FirstAbsentAt);
  }

  [Fact]
  public void OneAbsentRead_DoesNotHalt()
  {
    // THE INCIDENT, in one assertion. The spine read Place absent for a frame during a
    // retainer summon; before the window that single read halted the round.
    var g = new TransientGrace(2500);
    Assert.Equal(GraceVerdict.Waiting, g.Observe(met: false, nowMs: 1000));
  }

  [Fact]
  public void AbsentWithinTheWindow_KeepsWaiting()
  {
    var g = new TransientGrace(2500);
    g.Observe(false, 1000);
    Assert.Equal(GraceVerdict.Waiting, g.Observe(false, 1500));
    Assert.Equal(GraceVerdict.Waiting, g.Observe(false, 3499));
  }

  [Fact]
  public void AbsentPastTheWindow_Expires()
  {
    var g = new TransientGrace(2500);
    g.Observe(false, 1000);
    Assert.Equal(GraceVerdict.Expired, g.Observe(false, 3500));
  }

  [Fact]
  public void AbsentExactlyAtTheWindow_Expires()
  {
    // The boundary belongs to the halt: >=, not >. A window that needed one more
    // millisecond than it advertised would be a constant that means something else.
    var g = new TransientGrace(1000);
    g.Observe(false, 0);
    Assert.Equal(GraceVerdict.Expired, g.Observe(false, 1000));
  }

  [Fact]
  public void Flicker_ResetsTheClock()
  {
    // absent -> present -> absent is EXACTLY the shape a UI swap makes, and it must not
    // accumulate toward a halt. The second absence starts its own window.
    var g = new TransientGrace(2500);
    g.Observe(false, 0);
    Assert.Equal(GraceVerdict.Met, g.Observe(true, 1000));
    Assert.Null(g.FirstAbsentAt);

    Assert.Equal(GraceVerdict.Waiting, g.Observe(false, 1100));
    Assert.Equal(GraceVerdict.Waiting, g.Observe(false, 3500)); // 2400ms into the NEW absence
    Assert.Equal(GraceVerdict.Expired, g.Observe(false, 3600));
  }

  [Fact]
  public void ResetForgetsAnAbsenceInFlight()
  {
    var g = new TransientGrace(1000);
    g.Observe(false, 0);
    g.Reset();
    Assert.Equal(GraceVerdict.Waiting, g.Observe(false, 5000));
  }

  // ---- The gate: which refusals are worth waiting out ----------------------

  private static SpineEvaluation Eval(params (bool Met, Spine.Persistence Class)[] readings)
  {
    var expectations = new List<SpineExpectation>();
    var reads = new List<FacetReading>();
    for (var i = 0; i < readings.Length; i++)
    {
      expectations.Add(new SpineExpectation(
        Spine.Facet.Place, $"expectation {i}", Spine.Rung.WalkThere, readings[i].Class));
      reads.Add(new FacetReading(readings[i].Met, $"reading {i}"));
    }
    return SpineEvaluator.Evaluate(new ExpectedState("list", expectations), reads);
  }

  [Fact]
  public void ExpectationsDefaultToTransient()
  {
    // Every expectation this codebase declares is an addon-ready or occupancy read, so
    // the default is the honest one - a STABLE facet has to say so.
    var e = new SpineExpectation(Spine.Facet.Place, "to be at a retainer bell", Spine.Rung.WalkThere);
    Assert.Equal(Spine.Persistence.Transient, e.Class);
  }

  [Fact]
  public void AllTransientGaps_AreWaitable_WhenTheFlowFiredIt()
  {
    var eval = Eval((false, Spine.Persistence.Transient), (false, Spine.Persistence.Transient));
    Assert.True(eval.AllGapsTransient);
    Assert.True(GracePlan.ShouldWaitOut(eval, playerPressed: false));
  }

  [Fact]
  public void OneStableGap_PoisonsTheWholeWait()
  {
    // A gap seconds cannot fix makes the refusal un-waitable however waitable its
    // neighbours are - waiting on it is just a delayed refusal, which is worse.
    var eval = Eval((false, Spine.Persistence.Transient), (false, Spine.Persistence.Stable));
    Assert.False(eval.AllGapsTransient);
    Assert.False(GracePlan.ShouldWaitOut(eval, playerPressed: false));
  }

  [Fact]
  public void AStableExpectationThatIsMet_DoesNotPoisonAnything()
  {
    // Only UNMET expectations classify the refusal. A satisfied stable facet is not a gap.
    var eval = Eval((false, Spine.Persistence.Transient), (true, Spine.Persistence.Stable));
    Assert.True(eval.AllGapsTransient);
    Assert.True(GracePlan.ShouldWaitOut(eval, playerPressed: false));
  }

  [Fact]
  public void APlayerPress_StaysInstant()
  {
    // A human who pressed a button a frame ago is owed an answer NOW. The whole grace
    // exists for stages the FLOW fired between its own stops.
    var eval = Eval((false, Spine.Persistence.Transient));
    Assert.False(GracePlan.ShouldWaitOut(eval, playerPressed: true));
  }

  [Fact]
  public void AFiringEvaluation_IsNeverWaitedOn()
  {
    var eval = Eval((true, Spine.Persistence.Transient));
    Assert.True(eval.CanFire);
    Assert.False(GracePlan.ShouldWaitOut(eval, playerPressed: false));
  }

  [Fact]
  public void TheGraceWindowsAreDistinctAndOrdered()
  {
    // The stage-boundary window is deliberately the wider of the two: it has a whole
    // teardown to outlast, not one addon swap.
    Assert.True(GracePlan.AutoFireGraceMs > GracePlan.PlaceGraceMs);
  }

  // ========================================================================
  // THE SILENT SKIP (07-26): a refusal that reported nothing
  // ========================================================================

  [Fact]
  public void AnAutoFiredRefusal_ReportsSoTheRoundHalts()
  {
    // The hole: GcTurnInOrchestrator.StartRun and PinchRunExecutor.PinchAllRetainers could
    // refuse WITHOUT reporting. The round marks a stage done at fire time, so the
    // errand walked on with no run behind it and no halt - the one failure mode the
    // completion hub was built to make impossible.
    Assert.True(GracePlan.ShouldReport(playerPressed: false));
  }

  [Fact]
  public void APlayerPressedRefusal_ReportsNothing()
  {
    // He is looking at the button he pressed; the chat error IS the answer, and
    // there may be no round to halt at all.
    Assert.False(GracePlan.ShouldReport(playerPressed: true));
  }

  [Fact]
  public void ReportingAndWaiting_TurnOnTheSameFact()
  {
    // The twin rule: whatever earns a refusal patience earns it a report. If these
    // ever disagreed, a stage could be graced as auto-fired and then reported as
    // player-pressed (or the reverse) - one operand, read once, in both places.
    var transient = Eval((false, Spine.Persistence.Transient));
    Assert.Equal(GracePlan.ShouldWaitOut(transient, playerPressed: false),
                 GracePlan.ShouldReport(playerPressed: false));
    Assert.Equal(GracePlan.ShouldWaitOut(transient, playerPressed: true),
                 GracePlan.ShouldReport(playerPressed: true));
  }

  [Fact]
  public void AReportedTurnInRefusal_HaltsARoundThatFiredThatStage()
  {
    // The end-to-end shape of the fix: the refusal becomes a RunCompletion, and
    // FlowPlan turns it into the halt the rail names.
    var died = RunCompletion.Died(RunKind.TurnIn, "the Expert Delivery window isn't open");
    Assert.Equal(FlowReaction.Halt, FlowPlan.Reaction(died,
      roundActive: true, roundHalted: false, firedByRound: s => s == RoundStage.TurnIn));
  }

  [Fact]
  public void AReportedPinchRefusal_HaltsARoundThatFiredThatStage()
  {
    var died = RunCompletion.Died(RunKind.Pinch, "the retainer roster isn't open");
    Assert.Equal(FlowReaction.Halt, FlowPlan.Reaction(died,
      roundActive: true, roundHalted: false, firedByRound: s => s == RoundStage.Pinch));
  }

  [Fact]
  public void AReportedRefusalWithNoRound_ChangesNothing()
  {
    // The second guard: even if a report leaked out of a player press, an idle deck
    // has no stage to halt. Two independent defenses on the same mistake.
    var died = RunCompletion.Died(RunKind.TurnIn, "the Expert Delivery window isn't open");
    Assert.Equal(FlowReaction.None, FlowPlan.Reaction(died,
      roundActive: false, roundHalted: false, firedByRound: _ => true));
  }

  [Fact]
  public void AManualRefusalDuringALiveRound_DoesNotHaltAStageTheRoundNeverFired()
  {
    var died = RunCompletion.Died(RunKind.TurnIn, "the Expert Delivery window isn't open");
    Assert.Equal(FlowReaction.None, FlowPlan.Reaction(died,
      roundActive: true, roundHalted: false, firedByRound: _ => false));
  }
}
