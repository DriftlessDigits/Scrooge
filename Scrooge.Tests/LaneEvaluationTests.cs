using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The extracted pricing spine (08-02): one composition, two callers. These pin
/// the seams the extraction must not bend - the lazy providers fire exactly when
/// the pipeline's inline code did, and the relist preview's number is the
/// decision's number with the floors' teeth.
/// </summary>
public class LaneEvaluationTests
{
  private static List<LaneSale> Tape(bool hq, params (long Price, double DaysAgo)[] rows)
    => rows.Select(r => new LaneSale(r.Price, L.Now - (long)(r.DaysAgo * 86400), hq)).ToList();

  [Fact]
  public void TheSpine_ReachesTheSameDecisionAsItsPieces()
  {
    // The extraction guard: Evaluate must be the composition, not a cousin of it.
    var tape = Tape(false, (1_000, 1), (1_050, 2), (980, 3));
    var board = L.Board(900, 950);

    var answer = LaneEvaluation.Evaluate(tape, board, itemIsHq: false, hqPricing: false,
      L.Cfg(), L.Now, null, null, currentPrice: null);
    var (seg, lane, d) = Scrooge.Tests.Tape.Price(tape, false, board);

    Assert.Equal(d.Outcome, answer.Decision.Outcome);
    Assert.Equal(d.Anchor, answer.Decision.Anchor);
    Assert.Equal(seg.SegmentCount, answer.Segment.SegmentCount);
    Assert.Equal(lane.Median, answer.Lane!.Median);
  }

  [Fact]
  public void CommunityProvider_FiresOnlyWhenTheLocalLaneIsThin()
  {
    var calls = 0;
    IReadOnlyList<LaneSale>? Probe() { calls++; return null; }

    // A speaking local lane: the provider must never fire (the pipeline only
    // consulted Universalis on need, and its miss queues a fetch - eager
    // evaluation here would be fetch spam wearing a refactor).
    LaneEvaluation.Evaluate(Tape(false, (1_000, 1), (1_050, 2), (980, 3)),
      L.Board(900), false, false, L.Cfg(), L.Now, Probe, null, null);
    Assert.Equal(0, calls);

    // A thin one: it fires once.
    LaneEvaluation.Evaluate(Tape(false, (1_000, 1)),
      L.Board(900), false, false, L.Cfg(), L.Now, Probe, null, null);
    Assert.Equal(1, calls);
  }

  [Fact]
  public void CommunityLane_SubstitutesWhenItSpeaks_AndStaysLabeled()
  {
    var community = Tape(false, (500, 2), (520, 3), (510, 4));
    var answer = LaneEvaluation.Evaluate(Tape(false, (1_000, 1)), new List<LaneListing>(),
      false, false, L.Cfg(), L.Now, () => community, null, null);

    Assert.Equal(LaneSource.Community, answer.Lane!.Source);
    Assert.Contains("community", answer.Decision.Evidence, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void FallbackVelocity_FiresOnlyWithoutALocalSegment()
  {
    var calls = 0;
    double? Probe() { calls++; return 9.55; }

    // Local lane: the segment reports the pace; the fallback stays dark
    // (Sarcenet: a dump spikes the rate while cratering the price).
    var local = LaneEvaluation.Evaluate(Tape(false, (1_000, 1), (1_050, 2), (980, 3)),
      L.Board(900), false, false, L.Cfg(), L.Now, null, Probe, null);
    Assert.Equal(0, calls);
    Assert.NotNull(local.Velocity);

    // No tape at all: the fallback speaks.
    var silent = LaneEvaluation.Evaluate(new List<LaneSale>(),
      L.Board(900), false, false, L.Cfg(), L.Now, null, Probe, null);
    Assert.Equal(1, calls);
    Assert.Equal(9.55, silent.Velocity);
  }

  [Fact]
  public void AnHqRowInTheLine_WithNoHqTape_BecomesTheCrossQualityAnchor()
  {
    // The Gemsap shape through the extracted seam, A12 grammar: the HQ row
    // stands IN the one queue now, and with no HQ sales to convict it the walk
    // cannot call it nonsense - it is the cheapest competitor, cross-flagged so
    // the undercut goes strictly under.
    var board = new List<LaneListing>(L.Board(175, 180)) { new(123, IsOwn: false, IsHq: true) };
    var answer = LaneEvaluation.Evaluate(Tape(false, (170, 1), (170, 2), (170, 3)),
      board, itemIsHq: false, hqPricing: false, L.Cfg(), L.Now, null, null, null);

    Assert.True(answer.Decision.CrossQualityCapped);
    Assert.Equal(123, answer.Decision.Anchor);
  }

  [Fact]
  public void AnHqRow_ConvictedByItsOwnTape_IsSteppedWithReceipts()
  {
    // "If you are stepping over an HQ item, you better have the swagger and
    // receipts to back that decision" (Drift, 2026-08-05). The receipts: HQ's own
    // clears. The HQ crasher at 195 under HQ clears of 1,280-3,000 is stepped;
    // the NQ cluster behind it is the line.
    var tape = Tape(false, (1_000, 1), (1_050, 2), (980, 3));
    tape.AddRange(Tape(true, (1_280, 1), (1_300, 2), (3_000, 3)));
    var board = new List<LaneListing>(L.Board(995, 1_200, 1_222)) { new(195, IsOwn: false, IsHq: true) };

    var answer = LaneEvaluation.Evaluate(tape, board, itemIsHq: false, hqPricing: false,
      L.Cfg(), L.Now, null, null, null);

    Assert.Equal(LaneOutcome.CrazySkipped, answer.Decision.Outcome);
    Assert.Equal(995, answer.Decision.Anchor);
    Assert.False(answer.Decision.CrossQualityCapped);
  }

  // ---- The relist preview's number (walk ruling 3) ------------------------

  [Fact]
  public void HonestRelist_UndercutsAListingAnchor_ByOneGil()
  {
    var d = LanePricing.Decide(L.Board(850, 860), L.Banded(800, 880, 950, 12), null, L.Cfg());
    Assert.Equal(850, d.Anchor);
    Assert.Equal(849, LaneEvaluation.HonestRelist(d, Floor(75)));
  }

  [Fact]
  public void HonestRelist_TakesAnAbsoluteAnchorAsIs()
  {
    var d = LanePricing.Decide(new List<LaneListing>(), L.Banded(800, 880, 950, 12), null, L.Cfg());
    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(d.Anchor, LaneEvaluation.HonestRelist(d, Floor(75)));
  }

  [Fact]
  public void HonestRelist_DrawsADash_WhenTheFloorsRefuse()
  {
    // The Kudzu shape verbatim: 45/50 straddle the far-below seed, the 50
    // gives the 45 company, the undercut chases 44 - and the minimum is 75.
    // Refusing to price crasher-chasing is A10's personality - the cell dashes.
    var d = LanePricing.Decide(L.Board(45, 50), L.Banded(100, 120, 140, 12), null, L.Cfg());
    Assert.Equal(45, d.Anchor);
    Assert.Null(LaneEvaluation.HonestRelist(d, Floor(75)));
  }

  [Fact]
  public void HonestRelist_AHold_HasNoNumber()
  {
    var d = LanePricing.Decide(new List<LaneListing>(), L.Lane(1_214, 1), null, L.Cfg());
    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Null(LaneEvaluation.HonestRelist(d, Floor(75)));
  }

  /// <summary>The effective floor a preview asks with - the player's own minimum,
  /// which is what these cases pin (the one floor law, 2026-08-21).</summary>
  private static EffectiveFloor Floor(int minimum)
    => PriceFloor.Effective(PriceFloorMode.None, vendorPrice: 0, minimumListingPrice: minimum);
}
