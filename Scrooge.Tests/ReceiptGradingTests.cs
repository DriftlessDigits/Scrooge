using System.Collections.Generic;
using Xunit;
using static Scrooge.ReceiptGrading;

namespace Scrooge.Tests;

/// <summary>
/// A9 grading core, after the reframe that made it score DECISIONS instead of
/// prices (ruled 2026-08-15). Three things are under test:
///
/// <list type="bullet">
/// <item>the interim disambiguation - a settle below our ask is ambiguous alone,
/// and only the queue measurement separates "the line is advancing toward us"
/// from "cutters keep eating the demand in front of us";</item>
/// <item>the chain verdict - the direction of the NEXT decision is the verdict on
/// this one, with no tape involved at all;</item>
/// <item>the margin measurement - the tape fact that covers the survivorship hole
/// the verdicts structurally cannot, and its 0-vs-NULL contract.</item>
/// </list>
///
/// Every case here is synthetic operands in, verdict out - no storage, no board,
/// no tape gathering. Gathering is GilStorage's job and is deliberately not under
/// test here.
/// </summary>
public class ReceiptGradingTests
{
  private static List<Settle> Tape(params (long Price, long At)[] rows)
  {
    var list = new List<Settle>();
    foreach (var (p, a) in rows) list.Add(new Settle(p, a));
    return list;
  }

  private static InterimOperands Held(
    long? price = 1000, int? queuePosition = 3,
    List<Settle>? settles = null, int? belowNow = null)
    => new(price, queuePosition, settles ?? new List<Settle>(), belowNow);

  // =======================================================================
  // Interim: the disambiguation
  // =======================================================================

  [Fact]
  public void SettlesBelowUsAndTheQueueShrank_IsLineMoving()
  {
    // Three rows stood ahead of us; one is left, and the sales that took the
    // others landed under our ask. That is the line ADVANCING toward our seat -
    // exactly what we were waiting for when we picked the spot. The old grader
    // stamped this MISS, which convicted the case the doctrine is built on.
    var g = GradeInterim(Held(price: 1000, queuePosition: 3,
      settles: Tape((900, 50), (950, 60)), belowNow: 1));
    Assert.Equal(InterimGrade.LineMoving, g);
  }

  [Fact]
  public void SettlesBelowUsAndTheQueueHeldOrGrew_IsCutOff()
  {
    // Same sales, opposite story: rows in front of us cleared and the queue in
    // front of us did NOT shrink, so insertions are feeding in as fast as the
    // line drains. This is Drift's spot-4 case in flight - the in-flight forecast
    // of CHASED.
    var held = GradeInterim(Held(price: 1000, queuePosition: 3,
      settles: Tape((900, 50)), belowNow: 3));
    var grew = GradeInterim(Held(price: 1000, queuePosition: 3,
      settles: Tape((900, 50)), belowNow: 7));
    Assert.Equal(InterimGrade.CutOff, held);
    Assert.Equal(InterimGrade.CutOff, grew);
  }

  [Fact]
  public void SettlesBelowUsWithNoWayToCompareTheQueue_IsSilenceNotCutOff()
  {
    // ONLY A WITNESSED NON-SHRINKING QUEUE CONVICTS. With no recorded position
    // (held row, or pre-A10) or no board read this pass, both stories are still
    // alive - and a conviction we cannot see the evidence for is not one.
    var noPosition = GradeInterim(Held(price: 1000, queuePosition: null,
      settles: Tape((900, 50)), belowNow: 0));
    var noBoard = GradeInterim(Held(price: 1000, queuePosition: 3,
      settles: Tape((900, 50)), belowNow: null));
    Assert.Equal(InterimGrade.Silence, noPosition);
    Assert.Equal(InterimGrade.Silence, noBoard);
  }

  [Fact]
  public void TheDrainIsNeverOverruledByThePriceTest()
  {
    // The old rule was "MISS outranks drain" and it was exactly backwards: it let
    // the ambiguous fact (something sold cheap) overrule the disambiguating one
    // (the queue in front of us got shorter). Pinned so it cannot come back.
    var g = GradeInterim(Held(price: 1000, queuePosition: 5,
      settles: Tape((100, 50), (200, 55), (300, 60)), belowNow: 0));
    Assert.Equal(InterimGrade.LineMoving, g);
  }

  // --- Interim boundaries and the catch-all ------------------------------

  [Fact]
  public void NoSettlesAtAll_IsSilence_BecauseASlowMarketConvictsNobody()
    => Assert.Equal(InterimGrade.Silence, GradeInterim(Held(settles: Tape())));

  [Fact]
  public void SettlesOnlyAtOrAboveOurAsk_IsSilence()
  {
    // Nothing in front of us moved, so there is no question to disambiguate -
    // whatever the queue did.
    var g = GradeInterim(Held(price: 1000, queuePosition: 3,
      settles: Tape((1100, 50), (1200, 60)), belowNow: 0));
    Assert.Equal(InterimGrade.Silence, g);
  }

  [Fact]
  public void ASettleAtExactlyOurAsk_DoesNotOpenTheQuestion()
  {
    // Strictly below, always. A sale AT our number is the market clearing at our
    // price. If this ever loosens to <=, every correctly-priced listing on a busy
    // item starts getting its queue interrogated over a sale that proves us right.
    var g = GradeInterim(Held(price: 1000, queuePosition: 3,
      settles: Tape((1000, 50)), belowNow: 3));
    Assert.Equal(InterimGrade.Silence, g);
    Assert.NotEqual(InterimGrade.CutOff, g);
  }

  [Fact]
  public void APreV26ReceiptWithNoAbsoluteAsk_IsNotGradedAtAll()
  {
    // Null, not SILENCE: SILENCE is a finding about the market, and this receipt
    // has no ask for the market to be measured against. The row keeps its NULL.
    Assert.Null(GradeInterim(Held(price: null, settles: Tape((900, 50)))));
  }

  [Fact]
  public void GradingIsAPureFunctionOfItsOperands_SameInputsSameVerdict()
  {
    // The core has no memory - "stamp once" is enforced by the caller's IS NULL
    // guard, and this pins the half the core owns: re-running never drifts.
    var o = Held(price: 1000, queuePosition: 3, settles: Tape((900, 50)), belowNow: 1);
    Assert.Equal(GradeInterim(o), GradeInterim(o));
    Assert.Equal(InterimGrade.LineMoving, GradeInterim(o));
  }

  [Fact]
  public void VerdictNames_AreTheStorageSpellings()
  {
    // Verbatim specimens: these strings land in the DB and get drawn on the
    // receipt trail, so they are display copy as much as storage values.
    Assert.Equal("PASS", Name(InterimGrade.Pass));
    Assert.Equal("LINE_MOVING", Name(InterimGrade.LineMoving));
    Assert.Equal("CUT_OFF", Name(InterimGrade.CutOff));
    Assert.Equal("SILENCE", Name(InterimGrade.Silence));
    Assert.Equal("CLEARED", Name(FinalGrade.Cleared));
    Assert.Equal("CHASED", Name(FinalGrade.Chased));
    Assert.Equal("OUTGROWN", Name(FinalGrade.Outgrown));
    Assert.Equal("NEVER_TESTED", Name(FinalGrade.NeverTested));
  }

  // =======================================================================
  // The chain verdict: the direction of the next decision
  // =======================================================================

  private static ChainOperands Chain(
    long? price = 1000, bool hasSuccessor = false, long? successorPrice = null,
    Scrooge.DecisionReceipts.OutcomeState outcome = Scrooge.DecisionReceipts.OutcomeState.Open)
    => new(price, hasSuccessor, successorPrice, outcome);

  [Fact]
  public void ASuccessorAtALowerPrice_IsChased()
  {
    // Drift's scenario, landed: we were confident at spot 4, the market kept moving,
    // and the next decision re-priced for the front of the line. The verdict on
    // the spot-4 receipt is that the market walked down through it.
    Assert.Equal(FinalGrade.Chased,
      GradeChain(Chain(price: 1000, hasSuccessor: true, successorPrice: 800)));
  }

  [Fact]
  public void ASuccessorAtAHigherPrice_IsOutgrown()
  {
    // Wrong in the timid direction: the board healed above us.
    Assert.Equal(FinalGrade.Outgrown,
      GradeChain(Chain(price: 1000, hasSuccessor: true, successorPrice: 1400)));
  }

  [Fact]
  public void ASuccessorAtTheSamePrice_StampsNothing()
  {
    // A re-affirmed spot is not a supersession. A pinch that re-checked the
    // listing and confirmed the seat still fits has overturned nothing, and
    // stamping there would score the same un-reversed decision once per re-check.
    Assert.Null(GradeChain(Chain(price: 1000, hasSuccessor: true, successorPrice: 1000)));
  }

  [Fact]
  public void TheVerdictLandsOnTheLastReceiptOfASamePriceRun()
  {
    // The run 1000 -> 1000 -> 1000 -> 800: only the third receipt, the one whose
    // successor actually moved, carries the verdict. The earlier two keep their
    // NULL, which reads honestly as "re-affirmed, not overturned".
    var first = GradeChain(Chain(price: 1000, hasSuccessor: true, successorPrice: 1000));
    var second = GradeChain(Chain(price: 1000, hasSuccessor: true, successorPrice: 1000));
    var last = GradeChain(Chain(price: 1000, hasSuccessor: true, successorPrice: 800));
    Assert.Null(first);
    Assert.Null(second);
    Assert.Equal(FinalGrade.Chased, last);
  }

  [Fact]
  public void ASuccessorWithNoPriceCannotNameADirection()
  {
    // A held successor (or a pre-V26 one) names no spot, so there is no direction
    // of travel to read off it.
    Assert.Null(GradeChain(Chain(price: 1000, hasSuccessor: true, successorPrice: null)));
  }

  [Fact]
  public void NoSuccessorAndOurOwnSale_IsCleared()
  {
    // The seat call was right, whatever its depth. It says nothing about whether
    // it was the BEST seat - that is the margin measurement's job, below.
    Assert.Equal(FinalGrade.Cleared,
      GradeChain(Chain(outcome: Scrooge.DecisionReceipts.OutcomeState.Cleared)));
  }

  [Fact]
  public void NoSuccessorAndAPull_IsNeverTested()
  {
    // The listing left the board before it sold, so the spot never got its turn.
    // The absence IS the finding - it is not a gap to leave open.
    Assert.Equal(FinalGrade.NeverTested,
      GradeChain(Chain(outcome: Scrooge.DecisionReceipts.OutcomeState.NeverCleared)));
  }

  [Fact]
  public void AnAmbiguousEnd_StaysUnstamped()
  {
    // Still open: the backfill looks again next recurrence. Gone-unobserved: the
    // pinch reconciler's close is provisional by construction (a late sale confirm
    // upgrades it to cleared), and a final verdict is written once and never
    // moves, so stamping NEVER_TESTED here would freeze a possible lie.
    Assert.Null(GradeChain(Chain(outcome: Scrooge.DecisionReceipts.OutcomeState.Open)));
    Assert.Null(GradeChain(Chain(outcome: Scrooge.DecisionReceipts.OutcomeState.GoneUnobserved)));
  }

  [Fact]
  public void AReceiptWithNoAskHasNoChainVerdict()
    => Assert.Null(GradeChain(Chain(price: null, hasSuccessor: true, successorPrice: 800)));

  [Fact]
  public void TheChainVerdictReadsNoTapeAtAll()
  {
    // Structural, not incidental: the operand record has no settles and no ring
    // bounds, so there is no state where the evidence can scroll away before the
    // verdict is made. UNGRADEABLE died with the tape.
    Assert.Equal(FinalGrade.Chased,
      GradeChain(Chain(price: 1000, hasSuccessor: true, successorPrice: 999)));
  }

  // =======================================================================
  // The margin measurement
  // =======================================================================

  private static MarginOperands Closed(
    long? price = 1000, long? closedAt = 100,
    long? oldestBanked = 10, List<Settle>? after = null)
    => new(price, closedAt, oldestBanked, after ?? new List<Settle>());

  [Fact]
  public void TheMarginIsTheBiggestPremiumTheMarketPaidAfterWeLeft()
  {
    // Gil, not a grade. MAX over the quality-matched settles after our close,
    // because the question the Phase-4 trigger asks is how much was on the table,
    // not how many buyers walked past it.
    Assert.Equal(400L, MarginDonated(Closed(price: 1000, after: Tape((1200, 150), (1400, 160)))));
  }

  [Fact]
  public void MeasuredAndNothingDonated_IsZeroNotNull()
  {
    // THE CONTRACT: 0 means we looked and the market paid no premium. An analysis
    // that cannot tell that from "we never got to look" cannot compute a frequency
    // at all, which is the only thing this column exists to feed.
    Assert.Equal(0L, MarginDonated(Closed(price: 1000, after: Tape((900, 150), (500, 160)))));
  }

  [Fact]
  public void ASettleAtExactlyOurPrice_DonatesNothing()
  {
    // Mirrors the interim boundary: the next buyer paid exactly what we took.
    Assert.Equal(0L, MarginDonated(Closed(price: 1000, after: Tape((1000, 150)))));
  }

  [Fact]
  public void TapeThatStartsAfterOurClose_IsNeverMeasured()
  {
    // The ring's oldest sale is NEWER than our exit, so whatever settled in the
    // gap has already scrolled off. NULL, honestly - not a zero that would claim
    // the market paid no premium.
    Assert.Null(MarginDonated(Closed(price: 1000, closedAt: 100, oldestBanked: 120,
      after: Tape((1400, 150)))));
    Assert.Null(MarginDonated(Closed(price: 1000, closedAt: 100, oldestBanked: null,
      after: Tape((1400, 150)))));
  }

  [Fact]
  public void TapeReachingExactlyOurClose_StillCoversTheGap()
  {
    // oldest == closedAt is coverage, not overflow: the ring's earliest sale is
    // our own close instant, so nothing after it is missing.
    Assert.Equal(400L, MarginDonated(Closed(price: 1000, closedAt: 100, oldestBanked: 100,
      after: Tape((1400, 150)))));
  }

  [Fact]
  public void CoveredButNothingHasSettledYet_IsLeftUnmeasured()
  {
    // Null on purpose, and NOT the same as a measured zero. The gap is re-examined
    // every time the item comes back, so freezing a zero on a market that has not
    // spoken yet would bury a premium that arrives tomorrow.
    Assert.Null(MarginDonated(Closed(price: 1000, closedAt: 100, after: Tape())));
  }

  [Fact]
  public void AnOpenReceiptIsNeverMeasured()
    => Assert.Null(MarginDonated(Closed(price: 1000, closedAt: null, after: Tape((1400, 150)))));

  [Fact]
  public void AReceiptWithNoAskIsNeverMeasured()
    => Assert.Null(MarginDonated(Closed(price: null, closedAt: 100, after: Tape((1400, 150)))));

  [Fact]
  public void AClearedReceiptCanCarryAPositiveMargin()
  {
    // THE SURVIVORSHIP HOLE, finally visible. The ask sold - the chain verdict is
    // CLEARED, the seat call was right - AND the market paid 600 more right after
    // we left, so the seat was not the best one available. The old design graded a
    // sale PASS and stopped asking; this pairing is the whole reason the
    // measurement exists beside the verdict instead of inside it.
    var verdict = GradeChain(Chain(outcome: Scrooge.DecisionReceipts.OutcomeState.Cleared));
    var margin = MarginDonated(Closed(price: 1000, closedAt: 100, after: Tape((1600, 150))));
    Assert.Equal(FinalGrade.Cleared, verdict);
    Assert.Equal(600L, margin);
  }
}
