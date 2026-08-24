using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE STAGE RAIL (WALK unit 6) - the round's face in the run log. The contract:
/// every stage says where it stands, and every ETA is MEASURED or absent. A stage
/// that has never run borrows nothing.
/// </summary>
public class StageRailTests
{
  private static List<RailRow> Build(
    RoundStage? current = null,
    RoundStage? halted = null,
    IEnumerable<RoundStage>? done = null,
    IEnumerable<RoundStage>? empty = null,
    Dictionary<RoundStage, float>? rates = null,
    int count = 10,
    long? liveRunEtaMs = null,
    int pinchListedAsks = 0)
  {
    var doneSet = new HashSet<RoundStage>(done ?? []);
    var emptySet = new HashSet<RoundStage>(empty ?? []);
    return StageRail.Build(
      countOf: s => emptySet.Contains(s) ? 0 : count,
      hasWork: s => !emptySet.Contains(s),
      isDone: doneSet.Contains,
      current: current,
      halted: halted,
      msPerItem: s => rates?.GetValueOrDefault(s) ?? 0f,
      liveRunEtaMs: liveRunEtaMs,
      pinchListedAsks: pinchListedAsks);
  }

  /// <summary>
  /// The pinch as the live deck actually presents it: holding NOTHING, always. It
  /// stages no rows - it re-reads a board - so its count is structurally zero whether
  /// it is pending, walking, or done (DeckState.CountOf has no Pinch arm). Every SF-P2
  /// pinch test builds through here rather than through the default count of 10,
  /// because a pinch row holding 10 is a shape the deck cannot produce.
  /// </summary>
  private static List<RailRow> BuildWithEmptyPinch(
    RoundStage? current = null,
    Dictionary<RoundStage, float>? rates = null,
    long? liveRunEtaMs = null,
    int pinchListedAsks = 0)
    => StageRail.Build(
      countOf: s => s == RoundStage.Pinch ? 0 : 10,
      // The pinch's work answer is the cadence gate's, not its count's - a board past
      // the re-pinch floor has work with nothing staged. That gap is the bug.
      hasWork: _ => true,
      isDone: _ => false,
      current: current,
      halted: null,
      msPerItem: s => rates?.GetValueOrDefault(s) ?? 0f,
      liveRunEtaMs: liveRunEtaMs,
      pinchListedAsks: pinchListedAsks);

  private static RailRow Row(List<RailRow> rows, RoundStage stage)
    => rows.First(r => r.Stage == stage);

  // ========================================================================
  // States
  // ========================================================================

  [Fact]
  public void Build_CoversEveryStageInRoundOrder()
  {
    var rows = Build();
    Assert.Equal(RoundPlan.Order, rows.Select(r => r.Stage).ToArray());
  }

  [Fact]
  public void Build_NamesEachState()
  {
    var rows = Build(
      current: RoundStage.Desynth,
      done: [RoundStage.Pinch],
      empty: [RoundStage.TurnIn]);

    Assert.Equal(RailState.Done, Row(rows, RoundStage.Pinch).State);
    Assert.Equal(RailState.Pending, Row(rows, RoundStage.BellRun).State);
    Assert.Equal(RailState.Current, Row(rows, RoundStage.Desynth).State);
    Assert.Equal(RailState.Empty, Row(rows, RoundStage.TurnIn).State);
  }

  [Fact]
  public void Build_AHaltOutranksEverything()
  {
    // The stage was marked done at fire time and then died. It must read as the
    // corpse it is, not as a finished stage.
    var rows = Build(current: RoundStage.TurnIn, halted: RoundStage.Desynth,
      done: [RoundStage.Desynth]);
    Assert.Equal(RailState.Halted, Row(rows, RoundStage.Desynth).State);
  }

  // ========================================================================
  // ETAs - measured or absent, never invented
  // ========================================================================

  [Fact]
  public void Eta_IsThePaceTimesTheLoad()
  {
    Assert.Equal(20_000, StageRail.Eta(10, 2000f));
  }

  [Fact]
  public void Eta_NoRateMeansNoEstimate()
  {
    // A stage that has never run has no measured pace. It says so.
    Assert.Null(StageRail.Eta(10, 0f));
  }

  [Fact]
  public void Eta_NothingToDoMeansNoEstimate()
  {
    Assert.Null(StageRail.Eta(0, 2000f));
  }

  [Fact]
  public void Build_AnUnmeasuredStageCarriesNoEta()
  {
    var rows = Build(rates: new() { [RoundStage.Pinch] = 1000f });
    Assert.Equal(10_000, Row(rows, RoundStage.Pinch).EtaMs);
    Assert.Null(Row(rows, RoundStage.BellRun).EtaMs); // never measured - no borrowing
  }

  [Fact]
  public void Build_FinishedAndEmptyStagesHaveNoTimeLeft()
  {
    var rates = RoundPlan.Order.ToDictionary(s => s, _ => 1000f);
    var rows = Build(done: [RoundStage.Pinch], empty: [RoundStage.TurnIn], rates: rates);
    Assert.Null(Row(rows, RoundStage.Pinch).EtaMs);
    Assert.Null(Row(rows, RoundStage.TurnIn).EtaMs);
  }

  [Fact]
  public void EtaText_SaysSoWhenThereIsNoTiming()
  {
    Assert.Equal("no timing yet", StageRail.EtaText(null));
    Assert.Equal("~5m", StageRail.EtaText(300_000));
  }

  // ========================================================================
  // SF-P2 (live shake 08-14/15): pace x HOLDING was the wrong operand
  // ========================================================================

  [Fact]
  public void Build_ARunningStageQuotesItsOwnCountdown_NotPaceTimesZero()
  {
    // THE SHAKE BUG, VERBATIM. The rail said "pinch - no timing yet" while the pinch
    // run on the same screen counted down "2/75 ~9m47s". The pinch freshens the board
    // retainer by retainer, so mid-run it is holding nothing and pace x 0 is null -
    // while the run itself knew its remaining time exactly.
    var rows = BuildWithEmptyPinch(
      current: RoundStage.Pinch,
      rates: new() { [RoundStage.Pinch] = 10_200f }, // the banked pace IS on record
      liveRunEtaMs: 527_000); // 8m47s, the run's own answer

    var pinch = Row(rows, RoundStage.Pinch);
    Assert.Equal(527_000, pinch.EtaMs);
    Assert.Equal(EtaBasis.LiveRun, pinch.Basis);
    Assert.Equal(" - ~9m", StageRail.EtaSuffix(pinch));
  }

  [Fact]
  public void Build_TheLiveCountdownOutranksAMeasurableBankedEstimate()
  {
    // Not just a fallback for a stage holding nothing: the run's countdown is
    // calibrated against the items THIS run has already finished, so it outranks the
    // banked estimate even where the banked one could be computed.
    var rates = RoundPlan.Order.ToDictionary(s => s, _ => 1000f);
    var rows = Build(current: RoundStage.BellRun, rates: rates, liveRunEtaMs: 42_000);

    Assert.Equal(42_000, Row(rows, RoundStage.BellRun).EtaMs);
    Assert.Equal(EtaBasis.LiveRun, Row(rows, RoundStage.BellRun).Basis);
    // And it reaches exactly one row - a pending stage has no run to quote.
    Assert.Equal(10_000, Row(rows, RoundStage.Desynth).EtaMs);
    Assert.Equal(EtaBasis.Measured, Row(rows, RoundStage.Desynth).Basis);
  }

  [Fact]
  public void Build_ARunWithNoHonestEstimateFallsBackToTheBankedAnswer()
  {
    // Before its first item a run may have no pace at all (RunLifecycle answers null -
    // "gathering data"). That arrives here as null and the row says exactly what it
    // said before this fix, rather than the rail inventing a countdown.
    var rates = RoundPlan.Order.ToDictionary(s => s, _ => 1000f);
    var rows = Build(current: RoundStage.BellRun, rates: rates, liveRunEtaMs: null);
    Assert.Equal(10_000, Row(rows, RoundStage.BellRun).EtaMs);
    Assert.Equal(EtaBasis.Measured, Row(rows, RoundStage.BellRun).Basis);
  }

  [Fact]
  public void Build_AnArmedPinchPricesTheAsksItWillWalk()
  {
    // A PENDING pinch has staged nothing either - there is no run yet to extinguish
    // anything, its holding is simply always zero. Its honest load is the listed asks
    // the pass will visit, at its own banked pace: 75 x 10.2s = ~12.75m.
    var rows = BuildWithEmptyPinch(
      rates: new() { [RoundStage.Pinch] = 10_200f },
      pinchListedAsks: 75);

    var pinch = Row(rows, RoundStage.Pinch);
    Assert.Equal(RailState.Pending, pinch.State);
    Assert.Equal(765_000, pinch.EtaMs);
    Assert.Equal(EtaBasis.Measured, pinch.Basis);
  }

  [Fact]
  public void Build_AnArmedPinchWithNoBankedPaceStillSaysNoTimingYet()
  {
    // The ask count is a load, not a rate. A pinch that has never run borrows nothing.
    var rows = BuildWithEmptyPinch(pinchListedAsks: 75);
    Assert.Null(Row(rows, RoundStage.Pinch).EtaMs);
    Assert.Equal(" - no timing yet", StageRail.EtaSuffix(Row(rows, RoundStage.Pinch)));
  }

  [Fact]
  public void Build_TheAskCountReachesOnlyThePinch()
  {
    // It is the pinch's operand specifically - the one stage whose holding is
    // structurally zero. No other stage inherits it.
    var rates = RoundPlan.Order.ToDictionary(s => s, _ => 1000f);
    var rows = StageRail.Build(
      countOf: _ => 0, hasWork: _ => true, isDone: _ => false,
      current: null, halted: null,
      msPerItem: s => rates[s], liveRunEtaMs: null, pinchListedAsks: 75);

    Assert.Equal(75_000, Row(rows, RoundStage.Pinch).EtaMs);
    Assert.Null(Row(rows, RoundStage.Recon).EtaMs); // holding nothing, and nothing to forecast
    Assert.Null(Row(rows, RoundStage.BellRun).EtaMs);
  }

  [Fact]
  public void Build_ARealHoldingOutranksTheAskForecast()
  {
    // If a caller ever does give the pinch a genuine load, the load wins: a count of
    // things staged outranks a forecast of things to visit.
    var rows = Build(rates: new() { [RoundStage.Pinch] = 1000f }, count: 10,
      pinchListedAsks: 75);
    Assert.Equal(10_000, Row(rows, RoundStage.Pinch).EtaMs);
  }

  [Fact]
  public void Build_TheHingeWaitsOnYou_ItNeverPromisesAMeasurement()
  {
    // The hinge's time is a human reading his own board, and nothing in this codebase
    // can ever write a pace for it (LedgerWindow.StageForRate banks a triage-flavoured
    // run into BellRun by design). "No timing yet" there promised a number that would
    // never arrive.
    var rows = Build(rates: RoundPlan.Order.ToDictionary(s => s, _ => 1000f));
    var hinge = Row(rows, StageRail.Hinge);

    Assert.Equal(EtaBasis.WaitsOnYou, hinge.Basis);
    Assert.Null(hinge.EtaMs);
    Assert.Equal(" - waits on you", StageRail.EtaSuffix(hinge));
  }

  [Fact]
  public void Build_TheHingeSaysItWhereverItStands()
  {
    // Armed is armed: pending, current, or frozen over a halt, the answer is the same
    // because the reason is the same. Only DONE and EMPTY silence the slot, exactly as
    // they silence every other stage's.
    foreach (var rows in new[]
    {
      Build(rates: RoundPlan.Order.ToDictionary(s => s, _ => 1000f)),
      Build(current: StageRail.Hinge, liveRunEtaMs: 42_000),
      Build(halted: StageRail.Hinge),
    })
      Assert.Equal(" - waits on you", StageRail.EtaSuffix(Row(rows, StageRail.Hinge)));

    Assert.Equal("", StageRail.EtaSuffix(Row(Build(done: [StageRail.Hinge]), StageRail.Hinge)));
    Assert.Equal("", StageRail.EtaSuffix(Row(Build(empty: [StageRail.Hinge]), StageRail.Hinge)));
  }

  [Fact]
  public void EtaSuffix_IsTheOneVocabularyEverySurfaceSpeaks()
  {
    // Three surfaces draw this rail - the run log, its copy-to-clipboard twin, and the
    // Accountant's vertical rail through AccountantPlan.RailLine - and each had
    // hand-rolled this conditional. One word, one place.
    Assert.Equal(" - ~5m",
      StageRail.EtaSuffix(new RailRow(RoundStage.BellRun, RailState.Current, 4, 300_000)));
    Assert.Equal(" - no timing yet",
      StageRail.EtaSuffix(new RailRow(RoundStage.BellRun, RailState.Pending, 4, null)));
    Assert.Equal("",
      StageRail.EtaSuffix(new RailRow(RoundStage.BellRun, RailState.Done, 0, null)));
    Assert.Equal("",
      StageRail.EtaSuffix(new RailRow(RoundStage.BellRun, RailState.Empty, 0, null)));
  }

  [Fact]
  public void Build_EveryOtherRowIsUntouched()
  {
    // The SF-P2 guard rail: with no live run and no ask count, every stage but the
    // hinge answers exactly what it answered before - measured pace times holding,
    // null when unmeasured, silent when done or empty.
    var rates = new Dictionary<RoundStage, float>
    {
      [RoundStage.Recon] = 1000f,
      [RoundStage.BellRun] = 2000f,
    };
    var rows = Build(current: RoundStage.Desynth, done: [RoundStage.Pinch],
      empty: [RoundStage.TurnIn], rates: rates);

    Assert.Equal(10_000, Row(rows, RoundStage.Recon).EtaMs);   // 10 x 1000
    Assert.Equal(20_000, Row(rows, RoundStage.BellRun).EtaMs); // 10 x 2000
    Assert.Null(Row(rows, RoundStage.Desynth).EtaMs);          // current, but unmeasured
    Assert.Null(Row(rows, RoundStage.Pinch).EtaMs);            // done
    Assert.Null(Row(rows, RoundStage.TurnIn).EtaMs);           // empty
    foreach (var stage in new[] { RoundStage.Recon, RoundStage.BellRun, RoundStage.Desynth })
      Assert.Equal(EtaBasis.Measured, Row(rows, stage).Basis);
  }

  // ========================================================================
  // The whole-round total
  // ========================================================================

  [Fact]
  public void RemainingMs_SumsWhatIsLeft()
  {
    var rates = RoundPlan.Order.ToDictionary(s => s, _ => 1000f);
    // Pinch done, hinge done, turn-in empty: every OTHER stage in the order is armed
    // at 10 x 1000ms. Derived from Order rather than hard-coded, so the sum stops
    // being a fossil the next time a stage joins the round. The hinge is marked done
    // because an OPEN one has no duration at all and takes the total with it - see
    // RemainingMs_AnOpenHingeHasNoTotal.
    var armed = RoundPlan.Order.Length - 3;
    var rows = Build(done: [RoundStage.Pinch, StageRail.Hinge], empty: [RoundStage.TurnIn],
      rates: rates);
    Assert.Equal(armed * 10_000, StageRail.RemainingMs(rows));
  }

  [Fact]
  public void RemainingMs_CountsTheLiveRunsOwnCountdown()
  {
    // SF-P2 RULING, HALF ONE: better evidence must not read as less evidence. The
    // running stage used to cost pace x 0 = null and take the whole total down with
    // it, so "Round - ~Xm of work left" went blank for the length of every run - the
    // one stretch where the plugin has the best number it will ever have.
    var rates = RoundPlan.Order.ToDictionary(s => s, _ => 1000f);
    var rows = Build(current: RoundStage.BellRun, done: [RoundStage.Pinch, StageRail.Hinge],
      empty: [RoundStage.TurnIn], rates: rates, liveRunEtaMs: 42_000);

    // Recon (10 x 1000) + Desynth (10 x 1000) + the bell's OWN countdown.
    var banked = (RoundPlan.Order.Length - 4) * 10_000;
    Assert.Equal(banked + 42_000, StageRail.RemainingMs(rows));
  }

  [Fact]
  public void RemainingMs_AWalkingPinchNoLongerErasesTheTotal()
  {
    // The shake's own shape, at the header. The pinch holds nothing mid-run, so before
    // SF-P2 the total was null for the whole Look half of every round.
    var rows = StageRail.Build(
      countOf: s => s == RoundStage.Pinch ? 0 : 10,
      hasWork: s => s is RoundStage.Pinch or RoundStage.BellRun,
      isDone: s => s == StageRail.Hinge,
      current: RoundStage.Pinch, halted: null,
      msPerItem: _ => 1000f, liveRunEtaMs: 527_000, pinchListedAsks: 75);

    Assert.Equal(527_000 + 10_000, StageRail.RemainingMs(rows)); // the run + the bell
  }

  [Fact]
  public void RemainingMs_AnOpenHingeHasNoTotal()
  {
    // SF-P2 RULING, HALF TWO - and it is a RULING, not the old accident it looks
    // identical to. The header's claim is a DURATION, and "~12m of work left" over a
    // round with an unbounded human pause sitting in the middle of it is a number the
    // plugin cannot keep. Excluding the hinge by design was the live alternative (its
    // own row says "waits on you", so nothing would be SILENTLY omitted) and it lost:
    // the player reading a total is not auditing which stages the sum walked. The
    // total returns the moment the hinge is behind him.
    var rates = RoundPlan.Order.ToDictionary(s => s, _ => 1000f);
    Assert.Null(StageRail.RemainingMs(Build(rates: rates)));
    Assert.Null(StageRail.RemainingMs(Build(current: StageRail.Hinge, rates: rates)));
    Assert.NotNull(StageRail.RemainingMs(Build(done: [StageRail.Hinge], rates: rates)));
    // An EMPTY hinge is not an open one - a night with nothing to act on has nothing
    // to check, the cursor skips it, and the total is owed as usual.
    Assert.NotNull(StageRail.RemainingMs(Build(empty: [StageRail.Hinge], rates: rates)));
  }

  [Fact]
  public void RemainingMs_IsNullWhenAnyArmedStageIsUnmeasured()
  {
    // A total that silently omits a stage reads as complete, which is worse than
    // no total at all.
    var rates = new Dictionary<RoundStage, float> { [RoundStage.Pinch] = 1000f };
    var rows = Build(rates: rates);
    Assert.Null(StageRail.RemainingMs(rows));
  }

  [Fact]
  public void RemainingMs_IsNullWhenNothingIsLeft()
  {
    var rows = Build(done: [.. RoundPlan.Order]);
    Assert.Null(StageRail.RemainingMs(rows));
  }

  [Fact]
  public void EmptyNote_IsOneClaimAboutThePile_ForEveryStage()
  {
    // "(nothing to do)" is a claim about the PILE - the stage exists, it scanned, it
    // was empty. Through the Rounds build there was a SECOND string, "(not built
    // yet)", because a stage with no executor never scanned and claiming an empty
    // pile for it would have been the rail inventing a look nobody took.
    //
    // Unit 5 collapsed it back to one, as StageRail said it would: recon got its
    // executor in unit 2 and the hinge got its presenter in unit 5, so there is no
    // longer a stage the rail has to hedge about. Asserted across the WHOLE order,
    // because the failure this replaces is a future stage arriving unbuilt and
    // quietly inheriting a sentence that lies about it.
    foreach (var stage in RoundPlan.Order)
      Assert.Equal("  (nothing to do)", StageRail.EmptyNote(RailState.Empty, stage));
  }

  [Fact]
  public void EmptyNote_SaysNothingAboutAStageThatIsNotEmpty()
  {
    // A pending, current, done or halted stage has its own words already; a
    // parenthetical about emptiness under any of them would be the rail talking
    // over itself.
    foreach (var state in new[] { RailState.Done, RailState.Current, RailState.Halted, RailState.Pending })
    {
      Assert.Equal("", StageRail.EmptyNote(state, RoundStage.Desynth));
      Assert.Equal("", StageRail.EmptyNote(state, RoundStage.Recon));
    }
  }

  [Fact]
  public void Glyph_SpeaksTheDecksVocabulary()
  {
    Assert.Equal("!", StageRail.Glyph(RailState.Halted));
    Assert.Equal("x", StageRail.Glyph(RailState.Done));
    Assert.Equal(">", StageRail.Glyph(RailState.Current));
    Assert.Equal(".", StageRail.Glyph(RailState.Pending));
    Assert.Equal(".", StageRail.Glyph(RailState.Empty));
  }

  // ---- The rail footer's two clocks (ruled 08-15 shake) ----------------------

  [Fact]
  public void ReturnClockLine_SaysBothClocksInOneSentence()
  {
    var rows = new[]
    {
      new RailRow(RoundStage.Pinch, RailState.Done, 0, null),
      new RailRow(RoundStage.Desynth, RailState.Pending, 16, 112_000),
      new RailRow(RoundStage.BellRun, RailState.Pending, 15, 107_000),
    };
    Assert.Equal("retainers return in ~5m, and the round has an estimated ~4m in it",
      StageRail.ReturnClockLine(300, rows));
  }

  [Fact]
  public void ReturnClockLine_AnOpenHingeSaysAfterYouRule_AndSumsPastIt()
  {
    // Unlike RemainingMs (the header's total, which an open hinge nulls), the footer
    // claims a COMPARISON: the machine work is summed around the hinge and the
    // sentence says whose time is missing.
    var rows = new[]
    {
      new RailRow(RoundStage.Triage, RailState.Current, 11, null, EtaBasis.WaitsOnYou),
      new RailRow(RoundStage.Desynth, RailState.Pending, 16, 112_000),
      new RailRow(RoundStage.BellRun, RailState.Pending, 15, 107_000),
    };
    Assert.Equal(
      "retainers return in ~5m, and the round has an estimated ~4m in it after you rule",
      StageRail.ReturnClockLine(300, rows));
  }

  [Fact]
  public void ReturnClockLine_AnUnmeasuredArmedStage_DropsTheRoundHalf()
  {
    // A sum with a hole is not an estimate - the deadline stands alone rather than
    // underclaiming the spend.
    var rows = new[]
    {
      new RailRow(RoundStage.Desynth, RailState.Pending, 16, 112_000),
      new RailRow(RoundStage.BellRun, RailState.Pending, 15, null),
    };
    Assert.Equal("retainers return in ~5m", StageRail.ReturnClockLine(300, rows));
  }

  [Fact]
  public void ReturnClockLine_TheHaulWaiting_OutranksEverything()
  {
    var rows = new[] { new RailRow(RoundStage.Desynth, RailState.Pending, 16, 112_000) };
    Assert.Equal("retainers are back - the haul is waiting",
      StageRail.ReturnClockLine(0, rows));
  }

  // ---- The plan's machine-time total (ruled 08-16 round walk) ----------------

  [Fact]
  public void PlanMachineMs_SumsEveryStageAtItsOwnPace()
  {
    // The fit check's estimate is the WHOLE round, one arithmetic with the step
    // list: pinch over the listed asks, every other stage over its holding.
    var est = StageRail.PlanMachineMs(
      s => s switch
      {
        RoundStage.Recon => 20,
        RoundStage.Desynth => 6,
        RoundStage.BellRun => 18,
        RoundStage.TurnIn => 11,
        _ => 0,
      },
      s => s switch
      {
        RoundStage.Pinch => 9_600f,
        RoundStage.Recon => 12_000f,
        RoundStage.Desynth => 7_000f,
        RoundStage.BellRun => 6_700f,
        RoundStage.TurnIn => 2_300f,
        _ => 0f,
      },
      pinchListedAsks: 145);
    // 145*9.6s + 20*12s + 6*7s + 18*6.7s + 11*2.3s = 1392 + 240 + 42 + 120.6 + 25.3
    Assert.Equal(1_392_000 + 240_000 + 42_000 + 120_600 + 25_300, est);
  }

  [Fact]
  public void PlanMachineMs_TheHingeCostsNothing_ItWaitsOnYou()
  {
    var est = StageRail.PlanMachineMs(
      s => s == RoundStage.Triage ? 7 : s == RoundStage.Desynth ? 10 : 0,
      _ => 1_000f);
    Assert.Equal(10_000L, est);
  }

  [Fact]
  public void PlanMachineMs_AnArmedUnmeasuredStage_NullsTheWholeAnswer()
  {
    // A sum with a hole is not an estimate (ReturnClockLine's own rule).
    var est = StageRail.PlanMachineMs(
      s => s == RoundStage.Desynth ? 10 : s == RoundStage.BellRun ? 5 : 0,
      s => s == RoundStage.Desynth ? 1_000f : 0f);
    Assert.Null(est);
  }

  [Fact]
  public void PlanMachineMs_NothingHeldAnywhere_IsNoEstimateNotAZeroOne()
  {
    Assert.Null(StageRail.PlanMachineMs(_ => 0, _ => 1_000f));
  }

  [Fact]
  public void ReturnClockLine_DoneAndEmptyStages_CostNothing()
  {
    var rows = new[]
    {
      new RailRow(RoundStage.Pinch, RailState.Done, 0, 999_000),
      new RailRow(RoundStage.Recon, RailState.Empty, 0, null),
      new RailRow(RoundStage.TurnIn, RailState.Pending, 10, 23_000),
    };
    Assert.Equal("retainers return in ~10m, and the round has an estimated ~<1m in it",
      StageRail.ReturnClockLine(600, rows));
  }

  // ---- The rendered line (the run log and its Copy All twin) ----

  [Fact]
  public void RowText_PendingStage_CarriesGlyphNounCountAndEta()
  {
    // The window and the clipboard read the same sentence, which is the whole
    // reason the composition moved off the two ImGui methods that had it twice.
    Assert.Equal(" . melt (4) - ~2m",
      StageRail.RowText(new RailRow(RoundStage.Desynth, RailState.Pending, 4, 120_000)));
  }

  [Fact]
  public void RowText_EmptyStage_DropsTheCountAndSaysNothingToDo()
  {
    Assert.Equal(" . melt  (nothing to do)",
      StageRail.RowText(new RailRow(RoundStage.Desynth, RailState.Empty, 0, null)));
  }

  [Fact]
  public void RowText_TheHingeWaitsOnYouAndTheHaltShouts()
  {
    Assert.Equal(" > triage (3) - waits on you",
      StageRail.RowText(new RailRow(RoundStage.Triage, RailState.Current, 3, null,
        EtaBasis.WaitsOnYou)));
    Assert.Equal(" ! bell (2) - no timing yet",
      StageRail.RowText(new RailRow(RoundStage.BellRun, RailState.Halted, 2, null)));
  }

  [Fact]
  public void HeaderText_NoTotal_IsJustTheWordRound()
  {
    // An open hinge has no clock, so the round has no total - and says so by
    // saying nothing rather than by quoting a number it cannot back.
    Assert.Equal("Round", StageRail.HeaderText(null));
    Assert.Equal("Round - ~2m of work left", StageRail.HeaderText(120_000));
  }
}
