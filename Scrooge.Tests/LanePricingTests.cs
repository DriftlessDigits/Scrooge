using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// Fixture factories for lane pricing. The calibration tables in
/// [[Scrooge - Lane Pricing - Design]] are the fixture data; named receipts
/// (Highland Fence, Gemsap of Mind, Megalania Leather) appear as named cases.
/// </summary>
internal static class L
{
  public const long Now = 1_800_000_000; // fixed clock, seconds

  public static long DaysAgo(double days) => Now - (long)(days * 86400);

  /// <summary>Sales all at the given age (default fresh) - weighted median == flat median.</summary>
  public static List<LaneSale> Sales(double ageDays = 1, bool hq = false, params long[] prices)
    => prices.Select(p => new LaneSale(p, DaysAgo(ageDays), hq)).ToList();

  public static List<LaneListing> Board(params long[] prices)
    => prices.Select(p => new LaneListing(p, IsOwn: false)).ToList();

  /// <summary>One own listing at the given price (the thing being repriced).</summary>
  public static LaneListing Own(long price) => new(price, IsOwn: true);

  public static LaneConfig Cfg(double? halfLife = null, double? hqPremium = null, int? seatBudget = null) => new()
  {
    HalfLifeDays = halfLife ?? 30.0,
    HqPremiumPct = hqPremium ?? 0.25,
    SeatBudget = seatBudget ?? 4,
  };

  /// <summary>
  /// A lane with a going rate and a WIDE band - half the median to double it.
  /// Every lane carries a band (BuildLane draws it off the median's own walk), so
  /// a band-less fixture would describe a lane the plugin cannot build; the wide
  /// edges keep the memory floor and the empty-board line where the old
  /// half/double stand-ins put them.
  /// </summary>
  public static LaneModel Lane(double median, int n, LaneSource source = LaneSource.Local) => new()
  {
    Median = median,
    BandLow = median * 0.5,
    BandHigh = median * 2.0,
    SampleCount = n,
    WeightedAgeDays = 1,
    Source = source,
  };

  /// <summary>A lane with an explicit band - the normal path.</summary>
  public static LaneModel Banded(double low, double median, double high, int n, LaneSource source = LaneSource.Local) => new()
  {
    Median = median,
    BandLow = low,
    BandHigh = high,
    SampleCount = n,
    WeightedAgeDays = 1,
    Source = source,
  };
}

public class LaneMathTests
{
  [Fact]
  public void WeightedMedian_RecentSalesDominateOldOnes()
  {
    // Two stale 500s (60d) vs one fresh 1000 (1d), half-life 30d: the fresh
    // sale outweighs both stale ones combined. A flat median would say 500.
    var sales = new List<LaneSale>
    {
      new(500, L.DaysAgo(60), false),
      new(500, L.DaysAgo(60), false),
      new(1000, L.DaysAgo(1), false),
    };

    var lane = LanePricing.BuildLane(sales, isHq: false, L.Cfg(), L.Now);

    Assert.NotNull(lane);
    Assert.Equal(1000, lane!.Median);
    Assert.Equal(3, lane.SampleCount);
  }

  [Fact]
  public void BuildLane_HqLaneFromHqSalesOnly()
  {
    // HQ finally gets protection: cheap NQ sales must not drag the HQ lane.
    var sales = new List<LaneSale>
    {
      new(100, L.DaysAgo(1), false),
      new(100, L.DaysAgo(1), false),
      new(900, L.DaysAgo(1), true),
      new(1000, L.DaysAgo(1), true),
      new(1100, L.DaysAgo(1), true),
    };

    var lane = LanePricing.BuildLane(sales, isHq: true, L.Cfg(), L.Now);

    Assert.NotNull(lane);
    Assert.Equal(1000, lane!.Median);
    Assert.Equal(3, lane.SampleCount);
  }

  [Fact]
  public void BuildLane_NoQualityMatch_ReturnsNull()
  {
    var sales = L.Sales(1, hq: false, 100, 100, 100);

    Assert.Null(LanePricing.BuildLane(sales, isHq: true, L.Cfg(), L.Now));
  }

  [Fact]
  public void BuildLane_OldSalesStillBuildALane()
  {
    // The long-window receipt: Blue Daisies had 20 sales "all older" than the
    // inherited 14d filter and was blinded. Age discounts, never discards.
    var sales = L.Sales(45, hq: false, 900, 1000, 1000, 1000, 1100);

    var lane = LanePricing.BuildLane(sales, isHq: false, L.Cfg(), L.Now);

    Assert.NotNull(lane);
    Assert.Equal(1000, lane!.Median);
    Assert.Equal(5, lane.SampleCount);
    Assert.InRange(lane.WeightedAgeDays, 40, 50);
  }
}

/// <summary>
/// The band (A4c): weighted p25-p75 beside the median, off the same walk. NOT a
/// confidence interval - sales are not iid and regimes shift - just an honest
/// report of how much the evidence disagrees with itself. It rides every
/// narration and receipt long before anything prices off it.
/// </summary>
public class LaneBandTests
{
  [Fact]
  public void Band_LandsOnTheQuartersOfAFlatDistribution()
  {
    // Four sales, one age, equal weight: the quarter crossings are exact.
    var lane = LanePricing.BuildLane(L.Sales(1, false, 100, 200, 300, 400), isHq: false, L.Cfg(), L.Now);

    Assert.NotNull(lane);
    Assert.Equal(100, lane!.BandLow);
    Assert.Equal(200, lane.Median);
    Assert.Equal(300, lane.BandHigh);
  }

  [Fact]
  public void Band_IsWeightedLikeTheMedianIs()
  {
    // Two stale 500s (60d) against one fresh 1000 (1d), half-life 30d: the fresh
    // sale carries the median AND the top of the band, exactly as it carries the
    // median alone. One measurement, read at three depths.
    var sales = new List<LaneSale>
    {
      new(500, L.DaysAgo(60), false),
      new(500, L.DaysAgo(60), false),
      new(1000, L.DaysAgo(1), false),
    };

    var lane = LanePricing.BuildLane(sales, isHq: false, L.Cfg(), L.Now);

    Assert.NotNull(lane);
    Assert.Equal(500, lane!.BandLow);
    Assert.Equal(1000, lane.Median);
    Assert.Equal(1000, lane.BandHigh);
  }

  [Fact]
  public void Band_CollapsesToThePriceOnASingleSale()
  {
    // A lone sale disagrees with nothing - the band is a point, and the width
    // reads as "we have one data point," which is the truth.
    var lane = LanePricing.BuildLane(L.Sales(1, false, 7_500), isHq: false, L.Cfg(), L.Now);

    Assert.NotNull(lane);
    Assert.Equal(7_500, lane!.BandLow);
    Assert.Equal(7_500, lane.Median);
    Assert.Equal(7_500, lane.BandHigh);
  }

  [Fact]
  public void Band_IsWideWhenTheEvidenceDisagrees()
  {
    // "sells 100-550, 9 sales, they disagree" is a different sentence from
    // "sells 9,800-13,500, 14 sales, they agree", and the width is what says so.
    var tight = LanePricing.BuildLane(
      L.Sales(1, false, 9_800, 10_200, 10_500, 11_000, 12_000, 13_500), isHq: false, L.Cfg(), L.Now)!;
    var scattered = LanePricing.BuildLane(
      L.Sales(1, false, 100, 120, 180, 260, 400, 550), isHq: false, L.Cfg(), L.Now)!;

    Assert.True((tight.BandHigh - tight.BandLow) / tight.Median
              < (scattered.BandHigh - scattered.BandLow) / scattered.Median);
  }

  [Fact]
  public void Band_RespectsTheQualitySplitLikeTheRestOfTheLane()
  {
    var sales = new List<LaneSale>
    {
      new(100, L.DaysAgo(1), false), new(200, L.DaysAgo(1), false),
      new(300, L.DaysAgo(1), false), new(400, L.DaysAgo(1), false),
      new(9_000, L.DaysAgo(1), true),
    };

    var lane = LanePricing.BuildLane(sales, isHq: false, L.Cfg(), L.Now);

    Assert.NotNull(lane);
    Assert.Equal(4, lane!.SampleCount);
    Assert.Equal(300, lane.BandHigh); // the HQ sale never touches the NQ band
  }

  [Fact]
  public void Band_OrdersItselfAroundTheMedian()
  {
    var lane = LanePricing.BuildLane(
      L.Sales(1, false, 5, 900, 40, 1_200, 75, 300, 610), isHq: false, L.Cfg(), L.Now);

    Assert.NotNull(lane);
    Assert.True(lane!.BandLow <= lane.Median);
    Assert.True(lane.Median <= lane.BandHigh);
  }
}


/// <summary>
/// A10, back to basics: undercut the cheapest CLUSTER on the board, step over
/// LONE CRAZIES, and let the tape price only an empty board. Every case below
/// that changed answer says so and cites the ruling - the A8 classifier's
/// bait/competition/dreamer grading is gone, not adjusted.
/// </summary>
public class LaneDecisionTests
{
  // ---- The acceptance fixture: the Highland Fence, both doors -------------
  // Lane median 117,603; board = one honest 248k plus the two 55M dreams
  // (467x). The old geometry dismissed the honest listing and anchored 55M
  // every run; A10 cuts in front of the 248k and cannot reach the 55M rows at
  // all, because writing 54,999,899 would break the hard ceiling (rule 4).

  private static readonly LaneModel FenceLane = L.Lane(117_603, 5);
  private static readonly List<LaneListing> FenceBoard = L.Board(248_000, 54_999_900, 55_000_000);

  [Fact]
  public void Fence_BirthDoor_FreshListingRefusesTheDream()
  {
    var d = LanePricing.Decide(FenceBoard, FenceLane, velocityPerDay: null, L.Cfg(), currentPrice: null);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(248_000, d.Anchor);
    Assert.True(d.AnchorIsListing);
    // Plain market language: names the anchor, the rail, and the going rate -
    // the going rate speaks as the band (what actually cleared), not a median.
    Assert.Contains("undercut the cheapest listing (248,000)", d.Evidence);
    Assert.Contains("2 listings at 54,999,900+ sit above the 3x ceiling", d.Evidence);
    Assert.Contains("235,206", d.Evidence);
    Assert.DoesNotContain("median", d.Evidence);
  }

  [Fact]
  public void Fence_CureDoor_RepriceWalksIntoTheLane()
  {
    // Same function, same fixture, reprice context: the standing 54,999,900
    // listing must walk into the lane. Safety references are lane-relative -
    // the current price changes nothing.
    var d = LanePricing.Decide(FenceBoard, FenceLane, velocityPerDay: null, L.Cfg(), currentPrice: 54_999_900);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(248_000, d.Anchor);
  }

  // ---- Calibration receipts ----------------------------------------------

  [Fact]
  public void GemsapOfMind_WrongfulSkipOverturned()
  {
    // skip/median 1.04 at n=18 - the certain wrongful skip. 132 is the front of
    // the queue, it is nowhere near half the going rate, and there is nobody
    // cheaper: cut in front of it.
    var d = LanePricing.Decide(L.Board(132, 300), L.Lane(127, 18), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(132, d.Anchor);
    Assert.Equal(0, d.CraziesSkipped);
  }

  [Fact]
  public void MegalaniaLeather_RepricesIntoTheLaneInsteadOfFreezing()
  {
    // Anchor 15,997 vs ~5k lane: the old upward-hold froze it ("guard
    // defends; band decides"). 4,000 and 4,800 agree on a neighborhood - a
    // cluster of two - so the answer is to cut in front of the cheaper.
    var d = LanePricing.Decide(L.Board(4_000, 4_800), L.Lane(4_999, 20), null, L.Cfg(), currentPrice: 15_997);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(4_000, d.Anchor);
    Assert.Equal(2, d.ClusterSize);
  }

  [Fact]
  public void DeepgoldControl_HonestBoardIsJustAnUndercut()
  {
    // The control: everything agrees when the board is honest. Three rows
    // inside a quarter of each other are one cluster.
    var d = LanePricing.Decide(L.Board(1_100, 1_150, 1_300), L.Lane(1_200, 20), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(1_100, d.Anchor);
    Assert.Equal(3, d.ClusterSize);
  }

  // ---- Lone crazies, clusters, and the rail -------------------------------

  [Fact]
  public void LoneLowball_IsSteppedOver_AndTheNextRowIsTheAnchor()
  {
    // 110 under a ~1,000 going rate with nobody within a quarter of it, and a
    // board row eight times its price behind it: nonsense, alone. Step over it
    // and cut in front of the 900.
    var d = LanePricing.Decide(L.Board(110, 900), L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(900, d.Anchor);
    Assert.Equal(1, d.CraziesSkipped);
    Assert.Contains("stepped over 1 lone lowball at 110", d.Evidence);
  }

  [Fact]
  public void TheWalkBanksItsSpans_WhatTheCrashersWereAndWhatTheLineAsked()
  {
    // V35 (Drift, 08-02: "I do want to know what the crashers were... I find it
    // hard to believe that there were 15 crashers on a single item" - he was
    // right, the count was a board bug, and only the prices could say so at a
    // glance). The skipped prefix's span and the anchor cluster's span ride the
    // decision so the receipt can be read without the run log.
    var d = LanePricing.Decide(L.Board(110, 900, 950), L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(1, d.CraziesSkipped);
    Assert.Equal(110, d.CrasherFloor);
    Assert.Equal(110, d.CrasherCeiling);
    Assert.Equal(900, d.ClusterFloor);
    Assert.Equal(950, d.ClusterCeiling);

    // No rows stepped = no crasher span, never a zero that reads as gil.
    var front = LanePricing.Decide(L.Board(1_100, 1_150, 1_300), L.Lane(1_200, 20), null, L.Cfg());
    Assert.Null(front.CrasherFloor);
    Assert.Null(front.CrasherCeiling);
    Assert.Equal(1_100, front.ClusterFloor);
    Assert.Equal(1_300, front.ClusterCeiling);
  }

  [Fact]
  public void ACheapCrowd_IsTheMarket_NotBait()
  {
    // A10 rule 1, the Almasty conviction in miniature: the same 110 with one
    // neighbor beside it is a crowd, and nobody engineers a crowd. The going
    // rate says 1,000 and the queue says 110 - the queue is in front of us.
    var d = LanePricing.Decide(L.Board(110, 120, 900), L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(110, d.Anchor);
    Assert.Equal(0, d.CraziesSkipped);
    Assert.Equal(2, d.ClusterSize);
  }

  [Fact]
  public void RowsAboveTheCeiling_CannotBeCutInFrontOf()
  {
    // REWRITTEN to A10 (was: "all walls -> own the lane at 2x"). Nothing here is
    // graded as a dreamer any more - the 400,000 and the 55M are simply above
    // the rail on what we may write (3x the going rate), so the queue holds
    // nobody we can get in front of and the tape prices the item instead.
    var d = LanePricing.Decide(L.Board(400_000, 55_000_000), L.Lane(117_603, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(235_206, d.Anchor); // the band-less line, 2.0x, still under the 3x rail
    Assert.False(d.AnchorIsListing);
    Assert.Contains("above the 3x ceiling", d.Evidence);
  }

  [Fact]
  public void TheCeilingIsARailOnWhatWeWrite_NotAVerdictAboutTheSeller()
  {
    // REWRITTEN to A10 (was: "just inside is competition, just outside is a
    // wall"). 2,900 against a 1,000 going rate is a perfectly cuttable row -
    // A8 called it competition and A10 does not call it anything. One gil the
    // other side of the rail is not a different kind of seller; it is a price
    // we are not allowed to write.
    var inside = LanePricing.Decide(L.Board(2_900), L.Lane(1_000, 5), null, L.Cfg());
    var outside = LanePricing.Decide(L.Board(3_100), L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, inside.Outcome);
    Assert.Equal(2_900, inside.Anchor);
    Assert.Equal(LaneOutcome.EmptyBoard, outside.Outcome);
    Assert.Equal(2_000, outside.Anchor);
  }

  [Fact]
  public void EmptyBoard_ListsAtTheTopOfDemonstratedClearing()
  {
    var d = LanePricing.Decide(new List<LaneListing>(), L.Lane(117_603, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(235_206, d.Anchor); // band-less fallback: 2.0x the going rate
    Assert.Contains("an empty board", d.Evidence);
  }

  // ---- Own listings are never the queue -----------------------------------

  [Fact]
  public void OwnStaleLowball_WalksUp_NeverAnchorsAtItsOwnBaitPrice()
  {
    // The upward cure: our only listing is a stale lowball. Own listings aren't
    // the market, so the queue reads empty and the tape prices it - the stale
    // lowball walks UP instead of anchoring on itself.
    var board = new List<LaneListing> { L.Own(50) };
    var d = LanePricing.Decide(board, L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(2_000, d.Anchor);
    Assert.False(d.AnchorIsListing);
  }

  [Fact]
  public void OwnListingPlusOneLoneLowball_LeavesNobodyRealInTheQueue()
  {
    // Our own lowball plus one foreign crazy: the first is not the market and
    // the second is nonsense with nobody behind it, so the tape prices the item
    // - and our own price is still never the anchor.
    var board = new List<LaneListing> { L.Own(60), new(110, IsOwn: false) };
    var d = LanePricing.Decide(board, L.Lane(1_000, 5), velocityPerDay: 0.02, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(2_000, d.Anchor);
    Assert.Equal(1, d.CraziesSkipped);
    Assert.Contains("1 lone lowball at 110", d.Evidence);
  }

  [Fact]
  public void OwnListingInsideTheBand_DoesNotUndercutOurselves()
  {
    var board = new List<LaneListing> { L.Own(2_900) };
    var d = LanePricing.Decide(board, L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(2_000, d.Anchor);
    Assert.False(d.AnchorIsListing);
  }

  [Fact]
  public void OwnListingAmongForeignDreams_StillPricesOffTheTape()
  {
    var board = new List<LaneListing> { L.Own(1_500), new(55_000_000, IsOwn: false) };
    var d = LanePricing.Decide(board, L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(2_000, d.Anchor);
    Assert.Contains("above the 3x ceiling", d.Evidence);
  }

  // ---- The thin gate: it gates the TAPE, not us (A10) ---------------------

  [Fact]
  public void ThinHistory_WithAQueueInFront_PricesOffTheQueue()
  {
    // REWRITTEN to A10 (was: "4.1x at n=1 - hold"). A queue is evidence. The
    // tape is too thin to say what this is worth, and it says so in the
    // narration - but there is a live seller in front of us, so we cut in front
    // of him rather than sending Drift a homework item.
    var d = LanePricing.Decide(L.Board(4_990), L.Lane(1_214, 1), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(4_990, d.Anchor);
    Assert.Contains("Only 1 sale on record", d.Evidence);
  }

  [Fact]
  public void ThinHistory_AndAnEmptyBoard_IsGenuineSilence_AndHolds()
  {
    // Genuine silence is BOTH silences at once: no queue to read, and no tape
    // to read it with. Never act on a guess wearing numbers.
    var d = LanePricing.Decide(new List<LaneListing>(), L.Lane(1_214, 1), null, L.Cfg());

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Null(d.Anchor);
  }

  [Fact]
  public void NoLaneAtAll_StillPricesOffTheQueue()
  {
    var d = LanePricing.Decide(L.Board(4_990), lane: null, null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(4_990, d.Anchor);
    Assert.Contains("No settled sales on record", d.Evidence);
  }

  [Fact]
  public void NoLaneAndNoQueue_Holds()
  {
    var d = LanePricing.Decide(new List<LaneListing>(), lane: null, null, L.Cfg());

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Null(d.Anchor);
  }

  [Fact]
  public void WithNoTapeThereIsNoRailAndNoMemoryArm()
  {
    // With no going rate there is nothing to be "far below" and no 3x to be
    // above: the queue is the only evidence in the room. A row with nobody
    // behind it is therefore never skippable, however odd it looks.
    var d = LanePricing.Decide(L.Board(3), lane: null, null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(3, d.Anchor);
  }

  [Fact]
  public void MinHistorySamples_BoundaryLetsTheTapeSpeak()
  {
    // n == MinHistorySamples (3) is enough for the tape to report a going rate;
    // one sale short and it says so instead. Either way the queue prices it.
    var speaks = LanePricing.Decide(L.Board(1_100), L.Lane(1_200, 3), null, L.Cfg());
    var mute = LanePricing.Decide(L.Board(1_100), L.Lane(1_200, 2), null, L.Cfg());

    Assert.Equal(1_100, speaks.Anchor);
    Assert.Contains("Sells 600-2,400, 3 sales, they disagree.", speaks.Evidence);
    Assert.Equal(1_100, mute.Anchor);
    Assert.Contains("too thin to check a price against", mute.Evidence);
  }

  [Fact]
  public void CommunityLane_ActsAndIsLabeled()
  {
    // The confidence-gated safety net: a community lane witnesses like a local
    // one - it prices empty boards and arms the crazy test - but its evidence
    // is always labeled.
    var d = LanePricing.Decide(L.Board(1_100), L.Lane(1_200, 10, LaneSource.Community), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(1_100, d.Anchor);
    Assert.Contains("community", d.Evidence, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void CommunityLane_BuiltFromSales_IsLabeledCommunitySource()
  {
    // BuildLane is the pure seam the DC fallback rides: hand it community sales
    // and LaneSource.Community, and the built lane carries the label through.
    var sales = L.Sales(2, hq: false, 900, 1_000, 1_100);
    var lane = LanePricing.BuildLane(sales, isHq: false, L.Cfg(), L.Now, LaneSource.Community);

    Assert.NotNull(lane);
    Assert.Equal(LaneSource.Community, lane!.Source);
    Assert.Equal(1_000, lane.Median);
  }

  [Fact]
  public void CommunityLane_TooThin_HoldsOnlyWhenTheBoardIsAlsoEmpty()
  {
    // A community lane below MinHistorySamples is no rescue for the tape's job.
    // It still cannot invent a queue - and it no longer stops one from pricing.
    var silent = LanePricing.Decide(new List<LaneListing>(), L.Lane(1_200, 2, LaneSource.Community), null, L.Cfg());
    var queued = LanePricing.Decide(L.Board(1_100), L.Lane(1_200, 2, LaneSource.Community), null, L.Cfg());

    Assert.Equal(LaneOutcome.HeldThinHistory, silent.Outcome);
    Assert.Null(silent.Anchor);
    Assert.Equal(LaneOutcome.Undercut, queued.Outcome);
    Assert.Equal(1_100, queued.Anchor);
  }
}

/// <summary>
/// The A10 seeds under load: what counts as "near" for cluster membership
/// (<see cref="LanePricing.ClusterNearPct"/>) and "far below" for the lone-crazy
/// test (<see cref="LanePricing.FarBelowPct"/>). Both are dumb on purpose and
/// re-cut by A9's grades, never by a slider - these fixtures pin where they sit
/// so a re-cut is visible rather than silent.
/// </summary>
public class A10SeedTests
{
  private static readonly LaneModel Lane = L.Banded(900, 1_000, 1_100, 12); // far-below line = 450

  [Fact]
  public void Near_DecidesWhoCountsAsACrowd_AndACrowdIsNeverSkipped()
  {
    // 100 and 125 are one neighborhood; 100 and 126 are two lonely rows. Either
    // way the answer is 100 - and that is worth knowing about the seeds. "Far
    // below" measures against the very NEXT row, at half, so any row with a
    // neighbour within 2x is already unskippable before the company clause is
    // consulted: the two seeds overlap, and the strict one is doing the work.
    // The company clause stays because it states rule 1 out loud rather than
    // leaving it as an accident of the other threshold - and it is what carries
    // the crowd's size into the narration.
    var together = LanePricing.Decide(L.Board(100, 125, 8_000), Lane, null, L.Cfg());
    var apart = LanePricing.Decide(L.Board(100, 126, 8_000), Lane, null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, together.Outcome);
    Assert.Equal(100, together.Anchor);
    Assert.Equal(2, together.ClusterSize);
    Assert.Equal(LaneOutcome.Undercut, apart.Outcome);
    Assert.Equal(100, apart.Anchor);
    Assert.Equal(1, apart.ClusterSize);
  }

  [Fact]
  public void OneNeighbourAnywhereWithinTheFarBelowLine_SavesARowFromTheSkip()
  {
    // 440 alone under a 2,500 queue is nonsense and goes. Put one more seller
    // at 800 beside it - not "near" by the quarter seed, but well inside the
    // half - and the 440 is no longer far below the rest of the board. Rule 1
    // in its strongest form: company of any kind is enough.
    var alone = LanePricing.Decide(L.Board(440, 2_500), Lane, null, L.Cfg());
    var withCompany = LanePricing.Decide(L.Board(440, 800, 2_500), Lane, null, L.Cfg());

    Assert.Equal(LaneOutcome.CrazySkipped, alone.Outcome);
    Assert.Equal(2_500, alone.Anchor);
    Assert.Equal(LaneOutcome.Undercut, withCompany.Outcome);
    Assert.Equal(440, withCompany.Anchor);
  }

  [Fact]
  public void FarBelow_NeedsBothArms_TheBoardAndTheTape()
  {
    // 449 is under half the band's 900 floor but the row behind it is only 700 -
    // not far below the board, so it stands and we cut in front of it.
    var tapeOnly = LanePricing.Decide(L.Board(449, 700), Lane, null, L.Cfg());
    Assert.Equal(LaneOutcome.Undercut, tapeOnly.Outcome);
    Assert.Equal(449, tapeOnly.Anchor);

    // 460 is far below the 2,500 behind it but not far below the tape's floor -
    // a market cheaper today than the tape remembers is not nonsense.
    var boardOnly = LanePricing.Decide(L.Board(460, 2_500), Lane, null, L.Cfg());
    Assert.Equal(LaneOutcome.Undercut, boardOnly.Outcome);
    Assert.Equal(460, boardOnly.Anchor);

    // Both arms: alone, under half the floor, under half the row behind it.
    var both = LanePricing.Decide(L.Board(440, 2_500), Lane, null, L.Cfg());
    Assert.Equal(LaneOutcome.CrazySkipped, both.Outcome);
    Assert.Equal(2_500, both.Anchor);
  }

  [Fact]
  public void ACrowdIsNeverBait_HoweverFarBelowTheTapeItSits()
  {
    // The Almasty shape: six sellers a long way under the tape's memory. Each
    // arm of the crazy test would convict them; the company clause is what
    // saves them, and it is the whole ruling.
    var d = LanePricing.Decide(L.Board(50, 52, 54, 55, 56, 60), Lane, null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(50, d.Anchor);
    Assert.Equal(6, d.ClusterSize);
    Assert.Equal(0, d.CraziesSkipped);
  }

  [Fact]
  public void OneLonelyRowOnAnOtherwiseEmptyBoard_IsJudgedByTheTapeAlone()
  {
    // Nothing behind it means the board arm has nothing to say. The 3-gil Tiger
    // Leather row is the receipt: skipped, and the tape prices the item.
    var d = LanePricing.Decide(L.Board(3), Lane, null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(1, d.CraziesSkipped);
    Assert.Equal(1_100, d.Anchor); // the band's top
  }

  [Fact]
  public void ALoneButReasonableRow_IsStillUndercut()
  {
    // Alone is not a crime. 700 against a 900-1,100 band is a seller pricing
    // under the tape's memory, which is what a moving market looks like.
    var d = LanePricing.Decide(L.Board(700, 1_500), Lane, null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(700, d.Anchor);
    Assert.Equal(1, d.ClusterSize);
    Assert.Contains("no company on the board", d.Evidence);
  }

  [Fact]
  public void RowCount_IsLogic_StackSizeIsNot()
  {
    // REWRITTEN to A10 (was: "deep bait gets the same answer as thin bait").
    // Drift's "buyers don't consider quantity" is about STACK SIZE, which the
    // decision never reads - LaneListing carries no quantity at all. How many
    // SELLERS agree is the opposite kind of fact, and A10 makes it the whole
    // first rule: one lone 50 is nonsense, six of them are the market.
    var alone = LanePricing.Decide(L.Board(50, 1_000), Lane, null, L.Cfg());
    var crowd = LanePricing.Decide(L.Board(50, 50, 50, 50, 50, 50, 1_000), Lane, null, L.Cfg());

    Assert.Equal(LaneOutcome.CrazySkipped, alone.Outcome);
    Assert.Equal(1_000, alone.Anchor);
    Assert.Equal(LaneOutcome.Undercut, crowd.Outcome);
    Assert.Equal(50, crowd.Anchor);
  }

  // ---- The thin gate that can finally ratify (A4, CS7/CS9) ---------------

  [Fact]
  public void TwoAgreeingSales_Speak()
  {
    // True Griffin Leather HQ: 1,000 and 1,005, six days old, in half-a-percent
    // agreement - held at 290 under a count-only gate. Two witnesses agreeing
    // are not silence, and with the tape speaking the lone 295 is convictable
    // nonsense: the item lists at the top of what actually cleared.
    var d = LanePricing.Decide(L.Board(295), L.Banded(1_000, 1_000, 1_005, 2), null, L.Cfg(), currentPrice: 290);

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(1_005, d.Anchor);
    Assert.Equal(1, d.CraziesSkipped);
  }

  [Fact]
  public void TwoScatteredSales_StillCannotSpeak_ButTheQueueCan()
  {
    // REWRITTEN to A10 (was: "two strangers, hold"). The gate still refuses to
    // let two strangers report a going rate - so there is no memory arm and no
    // rail - but refusing to speak is not a reason to refuse to sell. The 295
    // in front of us prices the item.
    var d = LanePricing.Decide(L.Board(295), L.Banded(700, 700, 1_005, 2), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(295, d.Anchor);
    Assert.Contains("too thin to check a price against", d.Evidence);
  }

  [Fact]
  public void OneSale_NeverRatifiesItself_HoweverTightItLooks()
  {
    // A lone sale agrees with nothing - its band is a point by absence of
    // disagreement, and a point band must never read as ratification. With an
    // empty board that is genuine silence.
    var d = LanePricing.Decide(new List<LaneListing>(), L.Banded(1_000, 1_000, 1_000, 1), null, L.Cfg());

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
  }
}

/// <summary>
/// Narration: A10 keeps the band context line on every verdict and keeps the one
/// rule the 07-25 findings paid for - always name the anchor the price came from.
/// </summary>
public class LaneNarrationTests
{
  [Fact]
  public void EveryVerdictCarriesTheBandTheCountAndTheSpread()
  {
    var tight = LanePricing.Decide(L.Board(11_000), L.Banded(9_800, 11_000, 13_500, 14), null, L.Cfg());
    var scattered = LanePricing.Decide(L.Board(300), L.Banded(100, 200, 550, 9), null, L.Cfg());

    Assert.Contains("Sells 9,800-13,500, 14 sales, they agree.", tight.Evidence);
    Assert.Contains("Sells 100-550, 9 sales, they disagree.", scattered.Evidence);
  }

  [Fact]
  public void EmptyBoard_ListsAtTheBandTop_AndTheBandComesFromTheTape()
  {
    // Drift's probe: "if the board is empty, where is the band coming from" - the
    // tape. Board and evidence are independent silences, and rule 3 is the one
    // job the tape still has.
    var d = LanePricing.Decide(new List<LaneListing>(), L.Banded(9_800, 11_000, 13_500, 14), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(13_500, d.Anchor);
    Assert.Contains("an empty board", d.Evidence);
  }

  [Fact]
  public void TheLine_StaysUnderTheCeiling()
  {
    // The rail is the hard cap in every direction: a band whose top runs past
    // 3x the going rate cannot push the listing past it.
    var d = LanePricing.Decide(new List<LaneListing>(), L.Banded(500, 1_000, 5_000, 9), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(3_000, d.Anchor);
  }

  [Fact]
  public void NameTheAnchorThePriceCameFrom()
  {
    // 07-25 finding #6: "ignored 10 listings at 6,001+" while listing at 200
    // hid the ~201 the number actually came from. Never hide the anchor again.
    var d = LanePricing.Decide(L.Board(201, 6_001, 6_500), L.Banded(180, 195, 210, 14), null, L.Cfg());

    Assert.Equal(201, d.Anchor);
    Assert.Contains("undercut the cheapest listing (201)", d.Evidence);
    Assert.Contains("2 listings at 6,001+ sit above the 3x ceiling", d.Evidence);
  }

  [Fact]
  public void ClusterNarration_NamesTheCrowdAndItsRange()
  {
    // A11 re-labels this Almasty shape: three sellers under the far-below line
    // with one row behind them are a wall that outnumbers its crowd - the
    // market, joined, same anchor as A10. The narration names the range either way.
    var d = LanePricing.Decide(L.Board(1_500, 1_670, 1_750, 2_900), L.Banded(3_800, 3_900, 4_000, 6), null, L.Cfg());

    Assert.Contains("the wall IS the market", d.Evidence);
    Assert.Contains("3 sellers from 1,500 to 1,750", d.Evidence);
    Assert.Equal(1_500, d.Anchor);
  }

  [Fact]
  public void AStepOverIsNeverNarratedAsAnEmptyQueue()
  {
    // The Ink row (Drift, 08-03): the report called the board empty while the row
    // said we had stepped over a lone lowball. Both clauses were about the same
    // board and they disagreed about whether anyone was standing on it.
    var d = LanePricing.Decide(L.Board(3), L.Banded(900, 1_000, 1_100, 12), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Contains("stepped over 1 lone lowball at 3 - nobody real behind it", d.Evidence);
    // The empty-board claim belongs to an empty board and nothing else.
    Assert.DoesNotContain("nothing real in the queue", d.Evidence);
    Assert.DoesNotContain("an empty board", d.Evidence);
  }

  [Fact]
  public void AGenuinelyEmptyQueue_StillSaysSo()
  {
    var d = LanePricing.Decide(L.Board(), L.Banded(900, 1_000, 1_100, 12), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Contains("an empty board — nobody in the queue", d.Evidence);
    Assert.DoesNotContain("stepped over", d.Evidence);
  }

  [Fact]
  public void EmptyBoardReason_PrintsTheComputedAnchor_NotAMultiplier()
  {
    // Band-less line = 117,603 x 2.0 = 235,206. The reason must name the actual
    // price, never make the reader multiply - and it must name what it could not
    // cut in front of, with its cheapest price (07-25 finding #6).
    var d = LanePricing.Decide(L.Board(400_000, 55_000_000), L.Lane(117_603, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Contains("235,206", d.Evidence);
    Assert.Contains("400,000", d.Evidence);
    Assert.DoesNotContain("2.0x", d.Evidence);
    Assert.DoesNotContain("2x", d.Evidence);
  }

  [Fact]
  public void VelocityColorsTheBandLine_AndBranchesNothing()
  {
    var d = LanePricing.Decide(L.Board(1_100), L.Lane(1_200, 10), velocityPerDay: 1.0, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(1_100, d.Anchor);
    Assert.Contains("Selling about 1/day here.", d.Evidence);
  }

  [Fact]
  public void HeldThinHistory_CountsTheTapeAndNamesTheBarToJudgeTheBoard()
  {
    var d = LanePricing.Decide(new List<LaneListing>(), L.Lane(1_214, 1), null, L.Cfg());

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    // The specimen, verbatim: the silent tape says the window came back with one
    // sale and names the bar. "know what it's worth" is retired - a tape this thin
    // cannot price the item, and it was never being asked to; it is what the queue
    // gets judged against.
    Assert.Equal("nothing on the board and only 1 sale on record, "
               + "need 3 to judge the board. Flagged for a ruling.", d.Evidence);
    Assert.DoesNotContain("worth", d.Evidence);
    Assert.DoesNotContain("lane has", d.Evidence);
  }

  [Fact]
  public void Name_MarksHqLanes_SoQualitySplitsSelfExplain()
  {
    Assert.Equal("Tea Brick HQ", RunLogVoice.Name("Tea Brick", isHq: true));
    Assert.Equal("Tea Brick", RunLogVoice.Name("Tea Brick", isHq: false));
  }
}

/// <summary>
/// A12: cross-quality rows walk the board with everyone else. "Walking the
/// board, making a decision, and then retconning that because of an HQ item
/// seems wrong" (Drift, 2026-08-05) - so there is no post-walk cap anymore. An HQ
/// row beside an NQ lane is a row in the ONE physical queue: unconvictable
/// without its own tape (fail-closed = we price under it, the old cap's truth
/// in its honest place), steppable WITH receipts (its own quality's clears).
/// </summary>
public class CrossQualityWalkTests
{
  // ---- The convicting fixture: Grade 2 Gemsap of Strength ------------------
  // The NQ row sat at 175 while fifty HQ walls stood at 123 and every clear in
  // the window was HQ at 123-124. No buyer takes the worse item for more money.
  // Under A12 the wall is IN the queue: the cheapest competitor, cross-flagged.

  private static readonly LaneModel GemsapNq = L.Banded(40, 46, 175, 9);
  /// <summary>HQ's own tape: clears 123-124. Any HQ ask near it is a real price.</summary>
  private static readonly LaneModel GemsapHq = L.Banded(123, 123, 124, 9);

  private static List<LaneListing> WithHq(List<LaneListing> board, params long[] hqAsks)
  {
    var b = new List<LaneListing>(board);
    foreach (var ask in hqAsks)
      b.Add(new LaneListing(ask, IsOwn: false, IsHq: true));
    return b;
  }

  [Fact]
  public void Gemsap_TheHqWall_IsTheCrossQualityAnchor()
  {
    // Nothing else real in the queue and the NQ band top says 175 - but the HQ
    // wall at 123 stands IN the line, priced exactly where HQ clears, so it is
    // a competitor no walk may step. Anchor 123, cross-flagged: the undercut
    // goes strictly below, because equal money still loses to the better item.
    var d = LanePricing.Decide(WithHq(new List<LaneListing>(), 123), GemsapNq, null, L.Cfg(),
      currentPrice: 175, hqLane: GemsapHq);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(123, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.True(d.CrossQualityCapped);
    Assert.Contains("nobody pays more for the worse item", d.Evidence);
  }

  [Fact]
  public void Gemsap_TheAnswerItActuallyGave_PassesUntouched()
  {
    // A10's own answer for this item was 46, cutting in front of the NQ cluster.
    // That row is ahead of the HQ wall in the one queue, so the wall never
    // becomes the anchor and the verdict says nothing about it binding.
    var d = LanePricing.Decide(WithHq(L.Board(46, 50), 123), GemsapNq, null, L.Cfg(), hqLane: GemsapHq);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(46, d.Anchor);
    Assert.False(d.CrossQualityCapped);
    Assert.DoesNotContain("worse item", d.Evidence);
  }

  [Fact]
  public void ANqClusterBehindTheHqWall_AnchorsOnTheWall()
  {
    // A whole NQ cluster asking more than the HQ beside it: every one of those
    // rows is dead, and in the one queue the wall simply IS the cheapest row -
    // the walk anchors there without any retcon. Agreement between NQ sellers
    // about a price no buyer will pay is not evidence.
    var d = LanePricing.Decide(WithHq(L.Board(175, 180, 200), 123),
      L.Banded(150, 170, 190, 9), null, L.Cfg(), hqLane: GemsapHq);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(123, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.True(d.CrossQualityCapped);
    Assert.Contains("nobody pays more for the worse item", d.Evidence);
  }

  [Fact]
  public void AnExpensiveHqRow_NeverRaisesOrBinds()
  {
    // An HQ standing far behind the NQ line changes nothing - byte for byte the
    // same verdict as no HQ at all, because a row behind the anchor was never
    // consulted. One-directional means one direction, exactly as the cap was.
    var railed = LanePricing.Decide(WithHq(L.Board(1_100, 1_150), 9_000), L.Lane(1_200, 20), null, L.Cfg(), hqLane: GemsapHq);
    var plain = LanePricing.Decide(L.Board(1_100, 1_150), L.Lane(1_200, 20), null, L.Cfg());

    Assert.Equal(plain.Outcome, railed.Outcome);
    Assert.Equal(plain.Anchor, railed.Anchor);
    Assert.Equal(plain.AnchorIsListing, railed.AnchorIsListing);
    Assert.False(railed.CrossQualityCapped);
  }

  [Fact]
  public void AtEqualMoney_TheBetterItemStandsInFront()
  {
    // NQ 123 and HQ 123: every buyer takes the HQ first, so the tie breaks
    // toward the better quality and the anchor is the HQ - cross-flagged, so
    // the write goes strictly under rather than matching a corpse.
    var d = LanePricing.Decide(WithHq(L.Board(123, 130), 123), L.Banded(100, 120, 140, 9), null, L.Cfg(), hqLane: GemsapHq);

    Assert.Equal(123, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.True(d.CrossQualityCapped);
  }

  [Fact]
  public void HeldStaysHeld_WithAnHqTapeInHand()
  {
    // An HQ lane is not evidence about the NQ item. Genuine silence - empty
    // board, thin NQ tape - has no price to find, and a conviction lane must
    // never invent one.
    var d = LanePricing.Decide(new List<LaneListing>(), L.Lane(1_214, 1), null, L.Cfg(), hqLane: GemsapHq);

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Null(d.Anchor);
  }

  [Fact]
  public void NoCrossRows_IsTodaysBehaviourExactly()
  {
    // The regression guard: an all-NQ board with an HQ lane in hand must be
    // byte-identical to the same board without one - the conviction lane only
    // ever speaks about rows that exist. The Fence, both rails in play.
    var board = L.Board(248_000, 54_999_900, 55_000_000);
    var lane = L.Lane(117_603, 5);

    var withLane = LanePricing.Decide(board, lane, null, L.Cfg(), currentPrice: null, hqLane: GemsapHq);
    var without = LanePricing.Decide(board, lane, null, L.Cfg(), currentPrice: null);

    Assert.Equal(without.Outcome, withLane.Outcome);
    Assert.Equal(without.Anchor, withLane.Anchor);
    Assert.Equal(without.AnchorIsListing, withLane.AnchorIsListing);
    Assert.Equal(without.CraziesSkipped, withLane.CraziesSkipped);
    Assert.Equal(without.ClusterSize, withLane.ClusterSize);
    Assert.Equal(without.Evidence, withLane.Evidence);
  }

  [Fact]
  public void AnHqItem_NeverSeesCrossQualityRows()
  {
    // The asymmetry is game-true and one-directional: HQ-seeking buyers skip
    // cheap NQ, so the HQ walk's rows are never cross - even when the caller
    // hands rows flagged HQ (its own board IS HQ). itemIsHq is the discriminator.
    var board = new List<LaneListing> { new(1_100, false, true), new(1_150, false, true) };
    var d = LanePricing.Decide(board, L.Lane(1_200, 20), null, L.Cfg(), itemIsHq: true);

    Assert.Equal(1_100, d.Anchor);
    Assert.False(d.CrossQualityCapped);
  }

  // ---- Swagger and receipts: stepping an HQ row -----------------------------

  [Fact]
  public void AnHqCrasher_WithReceipts_IsStepped()
  {
    // The Golden Silk Linere shape, shallow: one HQ at 195 under HQ clears of
    // 1,280-3,000. Convicted by its OWN tape, alone, far below the queue behind
    // it - stepped, and the NQ line at 995 is the anchor. Seat 1: well inside
    // the budget.
    var hqTape = L.Banded(1_280, 1_300, 3_000, 8);
    var d = LanePricing.Decide(WithHq(L.Board(995, 1_200, 1_222), 195),
      L.Banded(980, 1_050, 1_294, 10), null, L.Cfg(), hqLane: hqTape);

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(995, d.Anchor);
    Assert.Equal(1, d.CraziesSkipped);
    Assert.False(d.CrossQualityCapped);
  }

  [Fact]
  public void AnHqCrasher_WithoutReceipts_IsPricedUnder()
  {
    // The same board with a silent HQ tape: no conviction, no step. The row
    // stays a competitor and we duck under it - the old cap's behavior
    // surviving as the fail-closed default instead of an absolute law.
    var d = LanePricing.Decide(WithHq(L.Board(995, 1_200, 1_222), 195),
      L.Banded(980, 1_050, 1_294, 10), null, L.Cfg(), hqLane: null);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(195, d.Anchor);
    Assert.True(d.CrossQualityCapped);
    Assert.Contains("nobody pays more for the worse item", d.Evidence);
  }

  [Fact]
  public void TheBoardArmAlone_NeverConvictsAnHqRow()
  {
    // "Far below the NQ queue" is exactly where an honestly cheap HQ would sit,
    // so the queue-only conviction (no NQ tape either) that can step an NQ row
    // must never step an HQ one.
    var board = WithHq(L.Board(995, 1_200, 1_222), 195);
    var d = LanePricing.Decide(board, null, null, L.Cfg(), hqLane: null);

    Assert.Equal(195, d.Anchor);
    Assert.True(d.CrossQualityCapped);
  }
}

/// <summary>
/// THE SEAT RAIL (A12): "use good judgement, but don't be wrong" (Drift,
/// 2026-08-05). Certainty must scale with seat depth - spots 1-4 are the
/// judgment zone, 6+ is wrong by definition - and no one rounds the discount
/// bin: buyers eat the queue in strict price order, so a seat behind a deep
/// pile is a wait, not a position. A walk that wants one takes the front of
/// the line instead, and says so, annoyed.
/// </summary>
public class SeatRailTests
{
  /// <summary>
  /// Tonight's convicting board (Golden Silk, receipt 5943): 14 NQ crashers at
  /// 190-400, the Linere HQ at 195 (its own tape clears 1,280+), 16 real sellers
  /// from 995. The A11 count said "outnumbered by one - step all 15"; the seat
  /// rail says a 15-deep seat is not a judgment call anyone gets to make.
  /// </summary>
  private static List<LaneListing> GoldenSilk()
  {
    var b = L.Board(190, 190, 195, 299, 300, 300, 345, 346, 347, 387, 387, 390, 399, 400,
      995, 1_050, 1_100, 1_150, 1_180, 1_200, 1_210, 1_215, 1_220, 1_222, 1_222, 1_230, 1_235, 1_240, 1_245, 1_250);
    b.Add(new LaneListing(195, IsOwn: false, IsHq: true));
    return b;
  }

  private static readonly LaneModel GoldenSilkNq = L.Banded(980, 1_050, 1_294, 10);
  private static readonly LaneModel GoldenSilkHq = L.Banded(1_280, 1_300, 3_000, 8);

  [Fact]
  public void ADeepStep_CollapsesToTheFrontOfTheLine()
  {
    var d = LanePricing.Decide(GoldenSilk(), GoldenSilkNq, null, L.Cfg(),
      hqLane: GoldenSilkHq);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(190, d.Anchor);
    Assert.Equal(0, d.CraziesSkipped); // front of the queue - the pile joined, not stepped
    Assert.False(d.CrossQualityCapped); // the cheapest row is NQ 190; the HQ 195 is just in the pile
    Assert.Contains("the crashers have friends", d.Evidence);
    Assert.Contains("the pile is the line now", d.Evidence);
    Assert.Contains("the real line (995+) sits 15 rows back", d.Evidence);
  }

  [Fact]
  public void TheSameBoard_WithBudgetToSpare_StepsWithReceipts()
  {
    // The knob is the difference, nothing else: with a 20-row budget the A11
    // step stands, and the HQ in the pack is named with its receipts.
    var d = LanePricing.Decide(GoldenSilk(), GoldenSilkNq, null, L.Cfg(seatBudget: 20),
      hqLane: GoldenSilkHq);

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(995, d.Anchor);
    Assert.Equal(15, d.CraziesSkipped);
    Assert.Contains("stepped over 15 crashers from 190", d.Evidence);
    Assert.Contains("1 of them HQ — HQ itself clears 1,280+", d.Evidence);
    Assert.Equal(190, d.CrasherFloor);
    Assert.Equal(400, d.CrasherCeiling);
  }

  [Fact]
  public void AnUnconvictedHqInThePile_TerminatesTheStep_AndTheGapTestRefusesIt()
  {
    // Silent HQ tape on the same board: the HQ at 195 cannot be convicted, so
    // the pack still ends at the two 190s (A12's membership rule is untouched).
    // But the GAP TEST (F1, 08-22) now asks the question the step never did:
    // 195 is 2.6% over 190 - no daylight - so stepping would have written 194
    // BEHIND two cheaper rows, the Caligae seat-contradiction in miniature.
    // One continuous queue: join the line at the front.
    var d = LanePricing.Decide(GoldenSilk(), GoldenSilkNq, null, L.Cfg(), hqLane: null);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(190, d.Anchor);
    Assert.False(d.CrossQualityCapped);
    Assert.Equal(0, d.CraziesSkipped);
    Assert.Contains("no gap to step over", d.Evidence);
  }

  [Fact]
  public void SamsContrivedBoard_TheRailNeverEvenFires()
  {
    // 10 / 10 / 50 (Drift, 2026-08-05): "obviously, taking spot 4 and listing at
    // 50 gil is wrong. but it is still within the first 4 slots." A11's own
    // crowd-vs-crowd already answers it - the two 10s outnumber the lone 50,
    // the wall IS the market, front of the line - which is the point: the rail
    // is a backstop for the deep-pile case, not a rewrite of the classifier.
    var d = LanePricing.Decide(L.Board(10, 10, 50), L.Banded(40, 50, 60, 10), null, L.Cfg());

    Assert.Equal(10, d.Anchor);
    Assert.Equal(0, d.CraziesSkipped);
    Assert.Contains("the wall IS the market", d.Evidence);
  }

  [Fact]
  public void AShallowStep_IsUntouched()
  {
    // The Stuffed Alpha shape: two crashers under a real crowd, seat 2. Inside
    // the judgment zone, the step stands exactly as A11 built it.
    var d = LanePricing.Decide(L.Board(50, 60, 854, 860, 870, 887), L.Banded(800, 870, 895, 10), null, L.Cfg());

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(854, d.Anchor);
    Assert.Equal(2, d.CraziesSkipped);
    Assert.Contains("stepped over 2 crashers from 50", d.Evidence);
  }

  [Fact]
  public void ThePackStep_BanksTheCrowdThatWonTheTest_NotTheClusterItLandedIn()
  {
    // The Golden Silk misread (Drift, 08-03): the cells could only read back
    // cluster_size, so "stepped over 4 - the line: 3" hid the crowd the step
    // was actually decided by. Same board, same verdict, one more number.
    var d = LanePricing.Decide(
      L.Board(50, 60, 854, 860, 870, 887), L.Banded(800, 870, 895, 10), null, L.Cfg());

    Assert.Equal(2, d.CraziesSkipped);
    Assert.Equal(4, d.CrowdBehind);
    Assert.Contains("outnumbered by the 4 sellers behind them", d.Evidence);
  }

  [Fact]
  public void NoPackStep_BanksNoCrowd()
  {
    // The lone-crazy walk convicts on ALONENESS, not on a headcount - there is
    // no outnumbering test, so there is no crowd that won one.
    var lone = LanePricing.Decide(L.Board(110, 1000, 1100), L.Banded(900, 1000, 1100, 10), null, L.Cfg());
    Assert.Equal(1, lone.CraziesSkipped);
    Assert.Equal(0, lone.CrowdBehind);

    // And the seat rail's collapse un-steps the pack entirely: nothing was
    // stepped over, so nothing was outnumbered.
    var railed = LanePricing.Decide(GoldenSilk(), GoldenSilkNq, null, L.Cfg(), hqLane: GoldenSilkHq);
    Assert.Equal(0, railed.CraziesSkipped);
    Assert.Equal(0, railed.CrowdBehind);
  }
}

/// <summary>
/// The written-price half of the cross-quality rail. Re-anchoring to the HQ row
/// is only half a guarantee - the undercut step is quality-blind and will happily
/// copy a price exactly, so Gentleman's Match against a strictly better HQ writes
/// the very corpse the rail exists to prevent. StrictlyUnder is the invariant on
/// the ANSWER, applied once over every mode rather than mode by mode.
/// </summary>
public class StrictlyUnderTests
{
  [Theory]
  [InlineData(123)]  // GentlemansMatch — copy price exactly. The own-listing early
                     // return emits this too.
  [InlineData(124)]  // defensive: nothing may ever land above the anchor either
  public void AMatchedPrice_BecomesARealCut(long emitted)
  {
    Assert.Equal(122, LanePricing.StrictlyUnder(emitted, 123));
  }

  [Fact]
  public void HumanizedAgainstACappedAnchor_NeverLandsAtTheCap()
  {
    // The Humanized roll is 1/3 random pinch, 1/3 Gentleman's Match, 1/3 clean
    // numbers - so a third of the time it emits the anchor itself. Walk every
    // outcome the mode can produce against a 123 HQ anchor at the default max
    // pinch and pin that not one of them survives at or above the better item.
    var emitted = new List<long> { 123 }; // the match branch — the one that leaked
    for (var pinch = 1; pinch <= 10; pinch++)
      emitted.Add(123 - pinch); // the random-pinch branch
    emitted.Add(120); // the clean-numbers branch: 122 rounded down to a 5

    foreach (var price in emitted)
      Assert.True(LanePricing.StrictlyUnder(price, 123) < 123,
        $"a written price of {price} against an HQ anchor of 123 must land strictly under it");
  }

  [Fact]
  public void ARealCut_IsByteIdentical_CappedOrNot()
  {
    // The guard is a no-op on every price that already beat the anchor, so an
    // uncapped anchor and a capped one that was genuinely undercut agree exactly.
    for (var pinch = 1; pinch <= 10; pinch++)
      Assert.Equal(123 - pinch, LanePricing.StrictlyUnder(123 - pinch, 123));

    Assert.Equal(1, LanePricing.StrictlyUnder(1, 123));
    Assert.Equal(46, LanePricing.StrictlyUnder(46, 123));
  }

  [Fact]
  public void TheCutIsFloorGuarded_AndNeverReachesZero()
  {
    // A 1-gil HQ row leaves nowhere to go. Land on 1 rather than 0 and let the
    // vendor floor and minimum-listing guards downstream have their say.
    Assert.Equal(1, LanePricing.StrictlyUnder(1, 1));
    Assert.Equal(1, LanePricing.StrictlyUnder(5, 1));
    Assert.Equal(1, LanePricing.StrictlyUnder(2, 2));
  }
}

/// <summary>
/// Rung 2 of the premium ladder (Phase 3b): the HQ side of an item that has no
/// HQ tape of its own. An HQ item is worth more than its NQ twin - the one thing
/// about HQ that is true without measuring anything - so where the HQ side can
/// say nothing and the NQ side can, the NQ answer plus Drift's premium beats
/// holding. ONE multiply, and it is the LAST rung: every real seller and every
/// speaking HQ tape outranks it.
/// </summary>
public class HqPremiumLadderTests
{
  // ---- The fixture: Ground Sloth Leather ---------------------------------
  // Nobody listing either quality, no HQ clears on record at all, and an NQ tape
  // that clears up to 700. Today that item holds forever: the HQ side can never
  // grow a tape it is never listed long enough to write.

  private static readonly List<LaneListing> NoBoard = new();

  [Fact]
  public void GroundSloth_NoHqTapeAndNoQueue_PricesOffItsOwnNqTape()
  {
    var d = LanePricing.Decide(NoBoard, null, null, L.Cfg(), nqBandTop: 700);

    Assert.Equal(LaneOutcome.PremiumFromNq, d.Outcome);
    Assert.Equal(875, d.Anchor);
    Assert.False(d.AnchorIsListing); // an absolute price, nothing to cut in front of
    Assert.Contains("no HQ sales on record", d.Evidence);
    Assert.Contains("700", d.Evidence);
    Assert.Contains("25% premium", d.Evidence);
    Assert.Contains("875", d.Evidence); // never make the reader multiply
  }

  [Fact]
  public void AThinHqTapeIsTheSameHole_AsNoTapeAtAll()
  {
    // One HQ sale cannot speak, so the hole is identical and so is the answer.
    var d = LanePricing.Decide(NoBoard, L.Lane(1_214, 1), null, L.Cfg(), nqBandTop: 700);

    Assert.Equal(LaneOutcome.PremiumFromNq, d.Outcome);
    Assert.Equal(875, d.Anchor);
  }

  [Fact]
  public void ALiveCluster_AlwaysWins_TheLadderNeverFires()
  {
    // Real sellers in a real queue are money on the board. The tape is a witness,
    // and a witness borrowed from the other quality outranks nothing.
    var d = LanePricing.Decide(L.Board(500, 550), null, null, L.Cfg(), nqBandTop: 700);

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(500, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.DoesNotContain("NQ tape", d.Evidence);
  }

  [Fact]
  public void ASteppedOverLowball_StillReachesTheClusterBehindIt_NotTheLadder()
  {
    // The walk finding an anchor is the walk finding an anchor, however it got
    // there. A skipped crazy does not empty the board.
    var d = LanePricing.Decide(L.Board(3, 900, 950), L.Banded(800, 1_000, 1_200, 9), null,
      L.Cfg(), nqBandTop: 700);

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(900, d.Anchor);
  }

  [Fact]
  public void ASpeakingHqTape_AlwaysWins_EmptyBoardListsAtItsOwnBandTop()
  {
    // The item's OWN evidence beats a number borrowed from its twin, every time.
    var d = LanePricing.Decide(NoBoard, L.Banded(900, 1_000, 1_200, 9), null, L.Cfg(), nqBandTop: 700);

    Assert.Equal(LaneOutcome.EmptyBoard, d.Outcome);
    Assert.Equal(1_200, d.Anchor);
    Assert.DoesNotContain("NQ tape", d.Evidence);
  }

  [Fact]
  public void NqAlsoSilent_HoldsExactlyAsBefore()
  {
    // Both tapes silent and no queue is still genuine silence. A dark rung must
    // be byte-identical to the rung not existing.
    var withNull = LanePricing.Decide(NoBoard, L.Lane(1_214, 1), null, L.Cfg(), nqBandTop: null);
    var without = LanePricing.Decide(NoBoard, L.Lane(1_214, 1), null, L.Cfg());

    Assert.Equal(LaneOutcome.HeldThinHistory, withNull.Outcome);
    Assert.Null(withNull.Anchor);
    Assert.Equal(without.Outcome, withNull.Outcome);
    Assert.Equal(without.Anchor, withNull.Anchor);
    Assert.Equal(without.Evidence, withNull.Evidence);
  }

  [Fact]
  public void AZeroBandTop_IsNoEvidence_AndDoesNotArmTheRung()
  {
    // A band top of zero is an absent number wearing a value. Multiplying it
    // would write a 0-gil listing off nothing at all.
    var d = LanePricing.Decide(NoBoard, null, null, L.Cfg(), nqBandTop: 0);

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Null(d.Anchor);
  }

  [Fact]
  public void TheSliderIsTheNumber_ZeroPremiumIsTheNqBandTopExactly()
  {
    var d = LanePricing.Decide(NoBoard, null, null, L.Cfg(hqPremium: 0.0), nqBandTop: 700);

    Assert.Equal(LaneOutcome.PremiumFromNq, d.Outcome);
    Assert.Equal(700, d.Anchor);
    Assert.Contains("700", d.Evidence);
  }

  [Theory]
  [InlineData(0.0, 700)]
  [InlineData(0.25, 875)]
  [InlineData(0.5, 1_050)]
  [InlineData(1.0, 1_400)]
  public void TheSliderIsTheNumber_AndNothingElseTouchesIt(double premium, long expected)
  {
    // No resolver, no measured ratio, no readout - one multiply, and the setting
    // is the whole of it.
    var d = LanePricing.Decide(NoBoard, null, null, L.Cfg(hqPremium: premium), nqBandTop: 700);

    Assert.Equal(expected, d.Anchor);
  }

  // ---- Interplay with the cross-quality walk (A12) ------------------------

  [Fact]
  public void TheLadderIsNeverCrossFlagged_CrossIsNqSideOnly()
  {
    // The premium points one way and so does the cross flag: the NQ side prices
    // under the HQ beside it, and the HQ side asks a premium over its NQ twin.
    // The two must never meet on one verdict - an HQ walk with cross rows would
    // be the item standing in its own way.
    var d = LanePricing.Decide(NoBoard, null, null, L.Cfg(), nqBandTop: 700, itemIsHq: true);

    Assert.Equal(LaneOutcome.PremiumFromNq, d.Outcome);
    Assert.Equal(875, d.Anchor);
    Assert.False(d.CrossQualityCapped);
    Assert.DoesNotContain("worse item", d.Evidence);
  }

  [Fact]
  public void TheNqSideAnchorsOnTheHqRow_AndNeverGetsAPremium()
  {
    // The NQ-side call, A12 grammar: the HQ row stands IN the queue, becomes
    // the cross-flagged anchor, and rung 2 rides null there - no premium ever
    // points down.
    var board = new List<LaneListing> { new(123, IsOwn: false, IsHq: true) };
    var d = LanePricing.Decide(board, L.Banded(40, 46, 175, 9), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(123, d.Anchor);
    Assert.True(d.CrossQualityCapped);
    Assert.DoesNotContain("premium", d.Evidence);
  }
}

/// <summary>
/// A11: crasher / competitor / dreamer - the queue classifier (Drift, 08-02).
/// "The question is - is the item a crasher, a competitor, or a dreamer. We are
/// optimizing the spot in line where we can." A pack of two or more rows under
/// the far-below margin is judged crowd vs crowd: outnumbered = crashers (step
/// over the pack), outnumbering = the wall IS the market (join its line), a dead
/// heat = undercut everything and let a sale settle it. Aloneness stops
/// immunizing company; the lone-crazy test survives for the single row only.
/// </summary>
public class A11ClassifierTests
{
  [Fact]
  public void StuffedAlphas_TwoCrashersHuddling_AreSteppedOverNotJoined()
  {
    // THE receipt (08-02 shake): 2 crashers at 50/60 under a real 854+ crowd,
    // clears 870-895. Under A10 their company immunized them - the undercut
    // chased 49, the floor refused it downstream, and the row skipped forever.
    // Under A11 the 4 sellers behind them outnumber them: crashers, stepped.
    var d = LanePricing.Decide(L.Board(50, 60, 854, 860, 870, 887),
      L.Banded(870, 880, 895, 47), null, L.Cfg());

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(854, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.Equal(2, d.CraziesSkipped);
    Assert.Contains("stepped over 2 crashers from 50", d.Evidence);
    Assert.Contains("outnumbered by the 4 sellers behind them", d.Evidence);
  }

  [Fact]
  public void KudzuShape_CrashersUnderAnHonestCluster_RelistWithoutEyes()
  {
    // The other named receipt: crashers 45/50 under a 102 cluster that settles
    // ~137. The router prices past the crash instead of refusing at the floor -
    // the below-min Review contest mostly stops needing eyes.
    var d = LanePricing.Decide(L.Board(45, 50, 102, 105, 110),
      L.Banded(102, 120, 137, 12), null, L.Cfg());

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(102, d.Anchor);
    Assert.Equal(2, d.CraziesSkipped);
  }

  [Fact]
  public void ADeepWall_OutnumberingTheBand_IsTheMarketNotACrash()
  {
    // Drift's challenge that amended the rule: "I would argue that 500 is the new
    // market." Five sellers far below the band with two behind them - the wall
    // outnumbers the crowd, so the wall IS the market and we take our spot in
    // that line, no tape lag.
    var d = LanePricing.Decide(L.Board(300, 310, 320, 330, 340, 870, 890),
      L.Banded(870, 880, 895, 20), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(300, d.Anchor);
    Assert.Equal(0, d.CraziesSkipped);
    Assert.Contains("the wall IS the market", d.Evidence);
  }

  [Fact]
  public void ADeadHeat_UndercutsEverything_AndLetsASaleSettleIt()
  {
    // Drift, verbatim: "crowd vs crowd works, but if there is a tie, then
    // undercut everything." Front of the whole queue - first law: sold beats
    // museum piece. Floors still outrank downstream, exactly as today.
    var d = LanePricing.Decide(L.Board(200, 210, 870, 890),
      L.Banded(870, 880, 895, 20), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(200, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.Equal(0, d.CraziesSkipped);
    Assert.Contains("a sale settles the argument", d.Evidence);
  }

  [Fact]
  public void APackWithNothingBehindIt_IsAPlainUndercut_NobodyEngineersACrowd()
  {
    // The Almasty conviction stands: a pack that IS the whole queue has nothing
    // to be compared against, and joining it is what A10 already did.
    var d = LanePricing.Decide(L.Board(50, 52, 54, 55, 56, 60),
      L.Banded(900, 1_000, 1_100, 12), null, L.Cfg());

    Assert.Equal(LaneOutcome.Undercut, d.Outcome);
    Assert.Equal(50, d.Anchor);
    Assert.Equal(0, d.CraziesSkipped);
    Assert.DoesNotContain("wall", d.Evidence);
    Assert.DoesNotContain("crashers", d.Evidence);
  }

  [Fact]
  public void ASingleBelowBandRow_StillGetsTheFullLoneCrazyTest()
  {
    // One row is not a crowd. The three-part test (alone + far below board +
    // far below tape) survives for the singleton, where aloneness is exactly
    // the evidence - the Longpole and the 3-gil Tiger row are unchanged.
    var stepped = LanePricing.Decide(L.Board(110, 900), L.Lane(1_000, 5), null, L.Cfg());
    var saved = LanePricing.Decide(L.Board(440, 800, 2_500), L.Banded(900, 1_000, 1_100, 12), null, L.Cfg());

    Assert.Equal(LaneOutcome.CrazySkipped, stepped.Outcome);
    Assert.Equal(900, stepped.Anchor);
    Assert.Equal(LaneOutcome.Undercut, saved.Outcome);
    Assert.Equal(440, saved.Anchor);
  }

  [Fact]
  public void DreamersStayDreamers_TheRailAndTheClassifierCompose()
  {
    // Above the 3x ceiling is still not in line. Crashers stepped, competitors
    // undercut, dreamers unreachable - all three labels on one board.
    var d = LanePricing.Decide(L.Board(50, 60, 854, 860, 870, 887, 5_000),
      L.Banded(870, 880, 895, 47), null, L.Cfg());

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(854, d.Anchor);
    Assert.Equal(2, d.CraziesSkipped);
    Assert.Contains("above the 3x ceiling", d.Evidence);
  }

  [Fact]
  public void RawQueuePositionSurvivesAsAMeasure_CraziesSkippedIsTheGap()
  {
    // Drift, verbatim: "I still want to know absolute position in the queue. It
    // is a useful measure, just not a target." CraziesSkipped is what the
    // pipeline banks as queue_position; the competitor position it banks beside
    // it is zero by construction - raw 2, competitor 0 = standing behind bait.
    var d = LanePricing.Decide(L.Board(50, 60, 854, 860, 870, 887),
      L.Banded(870, 880, 895, 47), null, L.Cfg());

    Assert.Equal(2, d.CraziesSkipped); // the raw insertion index
    Assert.Equal(4, d.ClusterSize);    // the company at the competitor anchor
  }
}

/// <summary>
/// SF-P8 (Drift, live shake 2026-08-15): "hard to call 2 prices a low ball and then
/// be 1 gil behind them." Our own standing ask vouches for rows it is near - the
/// conviction line may not slice between prices the board reads as one neighbourhood
/// with us standing in it. Both operands are house words already: near is
/// ClusterNearPct, credibility is the same FarBelowPct line the conviction uses.
/// </summary>
public class AskVouchTests
{
  [Fact]
  public void GargantuaLeather_TwoCrashersOneGilAhead_AreCompetitorsWhenWeStandBesideThem()
  {
    // THE receipt (08-15 morning run): clears 989-1,005 so the conviction line
    // sits at 494.5 - and the board's front was 493, 494, us at 495. The absolute
    // test convicted the two and narrated a step our own ask was standing inside.
    // Vouched, they are competitors: the anchor is 493 and the manual play - front
    // of the line - is the derived one.
    var board = L.Board(493, 494, 500, 504, 550, 600, 700);
    var lane = L.Banded(989, 1_000, 1_005, 20);

    var vouched = LanePricing.Decide(board, lane, 7.32, L.Cfg(), currentPrice: 495);
    Assert.Equal(LaneOutcome.Undercut, vouched.Outcome);
    Assert.Equal(493, vouched.Anchor);
    Assert.Equal(0, vouched.CraziesSkipped);

    // The control: the same board with no standing ask. The conviction still
    // lands (the tape's word is unchanged), but the GAP TEST (F1, 08-22) now
    // refuses the step for the hawk too: 500 is one price-step over 494, not
    // daylight, so the board is one continuous queue and the walk joins its
    // front - the same "hard to call 2 prices a lowball and then be 1 gil
    // behind them" SF-P8 named, fixed structurally rather than only where our
    // own ask happened to stand.
    var unvouched = LanePricing.Decide(board, lane, 7.32, L.Cfg());
    Assert.Equal(LaneOutcome.Undercut, unvouched.Outcome);
    Assert.Equal(493, unvouched.Anchor);
    Assert.Equal(0, unvouched.CraziesSkipped);
    Assert.Contains("no gap to step over", unvouched.Evidence);
  }

  [Fact]
  public void StuffedAlphas_AnAskUpInTheCrowd_VouchesForNobody()
  {
    // The A11 receipt survives untouched: 50/60 under the 854+ crowd with our
    // own ask standing in that crowd. 850 is nowhere near 50 by the cluster's
    // own word, so the step happens exactly as it did on 08-02.
    var d = LanePricing.Decide(L.Board(50, 60, 854, 860, 870, 887),
      L.Banded(870, 880, 895, 47), null, L.Cfg(), currentPrice: 850);

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(854, d.Anchor);
    Assert.Equal(2, d.CraziesSkipped);
  }

  [Fact]
  public void ACrushedAsk_BelowTheConvictionLineItself_VouchesForNobody()
  {
    // The Multifaceted-Cotton shape: our own ask crushed to nonsense by the old
    // race logic. A 75 ask under a 230 floor is not a credible witness - the 60
    // row stays convicted and clamp-and-climb keeps healing the lane upward
    // instead of following bait down.
    var d = LanePricing.Decide(L.Board(60, 300, 320, 340),
      L.Banded(230, 240, 250, 20), null, L.Cfg(), currentPrice: 75);

    Assert.Equal(LaneOutcome.CrazySkipped, d.Outcome);
    Assert.Equal(300, d.Anchor);
    Assert.Equal(1, d.CraziesSkipped);
  }

  [Fact]
  public void ALoneLowball_OneGilUnderOurAsk_IsVouchedToo()
  {
    // The singleton arm of the same claim: our rows never enter the queue, so
    // the lone-crazy company test cannot see us standing right there. The vouch
    // is the company.
    var board = L.Board(493, 1_000, 1_050);
    var lane = L.Banded(989, 1_000, 1_005, 20);

    var vouched = LanePricing.Decide(board, lane, null, L.Cfg(), currentPrice: 495);
    Assert.Equal(LaneOutcome.Undercut, vouched.Outcome);
    Assert.Equal(493, vouched.Anchor);
    Assert.Equal(0, vouched.CraziesSkipped);

    var unvouched = LanePricing.Decide(board, lane, null, L.Cfg());
    Assert.Equal(LaneOutcome.CrazySkipped, unvouched.Outcome);
    Assert.Equal(1_000, unvouched.Anchor);
    Assert.Equal(1, unvouched.CraziesSkipped);
  }
}

/// <summary>
/// The Sarcenet clamp (Phase 3b): a borrowed answer may never price above
/// everything that has actually cleared HERE. Community history is the DC's tape,
/// consulted only because ours was too thin to price - so it arrives exactly when
/// we can least sanity-check it. Sarcenet is the receipt: the DC's number
/// justified a 2,500 ask on an item whose local clears sat at 600-1,500.
/// </summary>
public class SarcenetClampTests
{
  private static LaneDecision Priced(long anchor, bool isListing = false) => new()
  {
    Outcome = LaneOutcome.EmptyBoard,
    Anchor = anchor,
    AnchorIsListing = isListing,
    Evidence = "Listed at the top of what it sells for.",
  };

  [Fact]
  public void Sarcenet_ACommunityAnswerAboveEveryLocalClear_IsClampedAndSaysSo()
  {
    var clamped = LanePricing.ClampToLocalClearing(Priced(2_500), LaneSource.Community, 1_500);

    Assert.Equal(1_500, clamped.Anchor);
    Assert.Contains("Community history says 2,500", clamped.Evidence);
    Assert.Contains("never locally cleared above 1,500", clamped.Evidence);
    Assert.Contains("clamped", clamped.Evidence);
  }

  [Fact]
  public void ACommunityAnswerInsideLocalClearing_PassesUntouched()
  {
    var d = Priced(1_400);
    var clamped = LanePricing.ClampToLocalClearing(d, LaneSource.Community, 1_500);

    Assert.Equal(1_400, clamped.Anchor);
    Assert.Equal(d.Evidence, clamped.Evidence);
  }

  [Fact]
  public void AnAnswerExactlyAtTheHighestLocalClear_IsNotAboveIt()
  {
    // 1,500 HAS cleared here. The rail is "never above", not "always under".
    var clamped = LanePricing.ClampToLocalClearing(Priced(1_500), LaneSource.Community, 1_500);

    Assert.Equal(1_500, clamped.Anchor);
    Assert.DoesNotContain("clamped", clamped.Evidence);
  }

  [Fact]
  public void NoLocalClearsAtAll_LeavesTheCommunityAnswerStanding()
  {
    // Nothing better exists. A clamp against zero would hold every item whose
    // only evidence is the DC's, which is the fallback's entire purpose.
    var clamped = LanePricing.ClampToLocalClearing(Priced(2_500), LaneSource.Community, 0);

    Assert.Equal(2_500, clamped.Anchor);
    Assert.DoesNotContain("clamped", clamped.Evidence);
  }

  [Fact]
  public void ALocalSourcedDecision_IsNeverClamped()
  {
    // There is nothing borrowed about it. Clamping a local answer with local
    // sales would just be the lane arguing with itself.
    var clamped = LanePricing.ClampToLocalClearing(Priced(2_500), LaneSource.Local, 1_500);

    Assert.Equal(2_500, clamped.Anchor);
    Assert.DoesNotContain("clamped", clamped.Evidence);
  }

  [Fact]
  public void NoLaneAtAll_IsNothingToClamp()
  {
    var clamped = LanePricing.ClampToLocalClearing(Priced(2_500), null, 1_500);

    Assert.Equal(2_500, clamped.Anchor);
  }

  [Fact]
  public void ABoardAnchor_IsLiveLocalMoney_AndTheClampLeavesItAlone()
  {
    // A listing anchor is a row a local buyer can hit right now; it never came
    // from the community lane, and clamping the board with the tape is the model
    // A10 threw out.
    var clamped = LanePricing.ClampToLocalClearing(Priced(2_500, isListing: true), LaneSource.Community, 1_500);

    Assert.Equal(2_500, clamped.Anchor);
    Assert.True(clamped.AnchorIsListing);
    Assert.DoesNotContain("clamped", clamped.Evidence);
  }

  [Fact]
  public void AHold_HasNoPriceToClamp()
  {
    var held = new LaneDecision { Outcome = LaneOutcome.HeldThinHistory, Anchor = null, Evidence = "silence." };
    var clamped = LanePricing.ClampToLocalClearing(held, LaneSource.Community, 1_500);

    Assert.Null(clamped.Anchor);
    Assert.Equal(held.Evidence, clamped.Evidence);
  }
}

/// <summary>
/// The borrowed tape gets the same discipline as the item's own (Phase 3b): the
/// NQ operand runs the full electorate pipeline - segment first, arithmetic
/// second (A2) - so the number the HQ side borrows is the CURRENT NQ regime's,
/// not June's. A stale regime's band top wearing a premium is the theory-of-value
/// ghost this whole arc exists to kill.
///
/// <para>These pin the COMPOSITION the pipeline performs (segment -> BuildLane ->
/// band top). If anyone ever drops RegimeSegment out of that chain, the blended
/// contrast below is what fails.</para>
/// </summary>
public class NqOperandSegmentationTests
{
  /// <summary>The pipeline's operand build, verbatim: filter to NQ, newest first,
  /// segment, build, band top.</summary>
  private static long? NqOperand(IReadOnlyList<LaneSale> sales, LaneConfig cfg)
  {
    var nqSales = sales.Where(s => !s.IsHq).OrderByDescending(s => s.Timestamp).ToList();
    var segment = RegimeSegment.Resolve(nqSales, cfg, L.Now);
    var lane = LanePricing.BuildLane(segment.Sales, isHq: false, cfg, L.Now);
    if (!LanePricing.CanSpeak(lane, cfg))
      return null;
    return (long)Math.Round(lane!.BandHigh);
  }

  /// <summary>The same build with segmentation removed - what the defect did.</summary>
  private static long BlendedOperand(IReadOnlyList<LaneSale> sales, LaneConfig cfg)
  {
    var lane = LanePricing.BuildLane(sales, isHq: false, cfg, L.Now)!;
    return (long)Math.Round(lane.BandHigh);
  }

  /// <summary>
  /// A Zircon-shaped NQ ring: a supply dump cleared seven times at 223-439 over
  /// the last week, behind thirteen older clears at 900-1,000. Plus two HQ sales
  /// that must not vote in an NQ lane at all.
  /// </summary>
  private static List<LaneSale> SlothNqRing()
  {
    var sales = new List<LaneSale>();
    long[] fresh = [439, 223, 380, 256, 411, 298, 344];
    for (var i = 0; i < fresh.Length; i++)
      sales.Add(new LaneSale(fresh[i], L.DaysAgo(1 + i), false));
    for (var i = 0; i < 13; i++)
      sales.Add(new LaneSale(900 + (i % 5) * 25, L.DaysAgo(10 + i), false));
    // The HQ side's own tape: two sales, far too little to speak. This is the
    // hole rung 2 exists for, and these must never reach the NQ lane.
    sales.Add(new LaneSale(5_000, L.DaysAgo(200), true));
    sales.Add(new LaneSale(9_000, L.DaysAgo(240), true));
    return sales;
  }

  [Fact]
  public void TheOperandComesFromTheFreshNqRegime_NotTheBlend()
  {
    var cfg = L.Cfg();
    var ring = SlothNqRing();

    var operand = NqOperand(ring, cfg);

    Assert.Equal(411, operand); // the fresh dump's band top
    Assert.True(operand <= 439, "nothing in the current NQ regime cleared above 439");
  }

  [Fact]
  public void TheBlendedOperand_IsTheDefect_AndItReadsFarHigher()
  {
    // The contrast that gives the pin teeth: without segmentation the demoted
    // old regime carries the vote and the HQ side borrows a price no NQ buyer
    // has paid in over a week - then multiplies it by the premium.
    var cfg = L.Cfg();
    var ring = SlothNqRing();

    var blended = BlendedOperand(ring, cfg);

    Assert.True(blended > 439,
      $"the un-segmented blend ({blended}) must sit above the whole fresh regime, or this fixture proves nothing");
    Assert.NotEqual(blended, NqOperand(ring, cfg));
  }

  [Fact]
  public void TheHqSideBorrowsTheFreshNumber_AndPricesOffThat()
  {
    // End to end at the seam: current-regime band top 411, plus the 25% premium.
    var cfg = L.Cfg();
    var operand = NqOperand(SlothNqRing(), cfg);

    var d = LanePricing.Decide(new List<LaneListing>(), L.Lane(1_214, 1), null, cfg, nqBandTop: operand);

    Assert.Equal(LaneOutcome.PremiumFromNq, d.Outcome);
    Assert.Equal(514, d.Anchor); // 411 x 1.25
    Assert.Contains("411", d.Evidence);
    Assert.Contains("514", d.Evidence);
  }

  [Fact]
  public void HqSalesNeverVoteInTheBorrowedNqLane()
  {
    // The 5,000 and 9,000 HQ clears in the ring are the exact prices the HQ side
    // cannot speak with. They must not sneak in through the back door either.
    var cfg = L.Cfg();

    var operand = NqOperand(SlothNqRing(), cfg);

    Assert.True(operand < 1_000, $"an HQ sale voted in the NQ lane (operand {operand})");
  }

  [Fact]
  public void ASilentNqTape_YieldsNoOperandAtAll()
  {
    // Two NQ sales that disagree cannot speak, so there is nothing to borrow and
    // the item holds - the rung never invents evidence.
    var cfg = L.Cfg();
    var ring = new List<LaneSale>
    {
      new(100, L.DaysAgo(1), false),
      new(900, L.DaysAgo(2), false),
    };

    Assert.Null(NqOperand(ring, cfg));
  }
}
