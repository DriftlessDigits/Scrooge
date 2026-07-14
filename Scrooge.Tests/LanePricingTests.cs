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

  public static LaneConfig Cfg(double? halfLife = null, double? raceJoin = null) => new()
  {
    HalfLifeDays = halfLife ?? 30.0,
    RaceJoinMinVelocityPerDay = raceJoin ?? 0.1,
  };

  public static LaneModel Lane(double median, int n, LaneSource source = LaneSource.Local) => new()
  {
    Median = median,
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

public class LaneDecisionTests
{
  // ---- The acceptance fixture: the Highland Fence, both doors -------------
  // Lane median 117,603; board = one honest 248k (2.11x, in-lane) plus the
  // two 55M dreams (467x). The old geometry dismissed the honest listing and
  // anchored 55M every run.

  private static readonly LaneModel FenceLane = L.Lane(117_603, 5);
  private static readonly List<LaneListing> FenceBoard = L.Board(248_000, 54_999_900, 55_000_000);

  [Fact]
  public void Fence_BirthDoor_FreshListingRefusesTheWall()
  {
    var d = LanePricing.Decide(FenceBoard, FenceLane, velocityPerDay: null, L.Cfg(), currentPrice: null);

    Assert.Equal(LaneOutcome.WallIgnored, d.Outcome);
    Assert.Equal(248_000, d.Anchor);
    Assert.True(d.AnchorIsListing);
    Assert.Equal(2, d.WallsIgnored);
    Assert.Contains("median 117,603", d.Evidence);
  }

  [Fact]
  public void Fence_CureDoor_RepriceWalksIntoTheLane()
  {
    // Same function, same fixture, reprice context: the standing 54,999,900
    // listing must walk into the lane. Safety references are lane-relative -
    // the current price changes nothing.
    var d = LanePricing.Decide(FenceBoard, FenceLane, velocityPerDay: null, L.Cfg(), currentPrice: 54_999_900);

    Assert.Equal(LaneOutcome.WallIgnored, d.Outcome);
    Assert.Equal(248_000, d.Anchor);
  }

  // ---- Calibration receipts ----------------------------------------------

  [Fact]
  public void GemsapOfMind_WrongfulSkipOverturned()
  {
    // skip/median 1.04 at n=18 - the certain wrongful skip. The honest 132
    // is in-lane; the 300 (2.36x) is also in-lane, not a wall. Plain undercut.
    var d = LanePricing.Decide(L.Board(132, 300), L.Lane(127, 18), null, L.Cfg());

    Assert.Equal(LaneOutcome.InLane, d.Outcome);
    Assert.Equal(132, d.Anchor);
    Assert.Equal(0, d.WallsIgnored);
  }

  [Fact]
  public void MegalaniaLeather_RepricesIntoTheLaneInsteadOfFreezing()
  {
    // Anchor 15,997 vs ~5k lane: the old upward-hold froze it ("guard
    // defends; band decides"). The lane joins the honest 4,000 instead.
    var d = LanePricing.Decide(L.Board(4_000, 4_800), L.Lane(4_999, 20), null, L.Cfg(), currentPrice: 15_997);

    Assert.Equal(LaneOutcome.InLane, d.Outcome);
    Assert.Equal(4_000, d.Anchor);
  }

  [Fact]
  public void DeepgoldControl_HonestBoardIsJustAnUndercut()
  {
    // The control: everything agrees when the board is honest.
    var d = LanePricing.Decide(L.Board(1_100, 1_150, 1_300), L.Lane(1_200, 20), null, L.Cfg());

    Assert.Equal(LaneOutcome.InLane, d.Outcome);
    Assert.Equal(1_100, d.Anchor);
  }

  // ---- Bait / walls / ownership ------------------------------------------

  [Fact]
  public void BaitIgnored_WhenInLaneCompetitionExists()
  {
    // Below-lane claims are ignored by construction; anchor the honest 900.
    var d = LanePricing.Decide(L.Board(110, 900), L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.BaitIgnored, d.Outcome);
    Assert.Equal(900, d.Anchor);
    Assert.Equal(1, d.BaitIgnored);
  }

  [Fact]
  public void AllWalls_OwnsTheLane()
  {
    // No in-lane competition, all dreams: list at the lane's upper edge.
    var d = LanePricing.Decide(L.Board(400_000, 55_000_000), L.Lane(117_603, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.LaneOwned, d.Outcome);
    Assert.Equal(352_809, d.Anchor); // 3.0x median
    Assert.False(d.AnchorIsListing);
  }

  [Fact]
  public void EmptyBoard_OwnsTheLane()
  {
    var d = LanePricing.Decide(new List<LaneListing>(), L.Lane(117_603, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.LaneOwned, d.Outcome);
    Assert.Equal(352_809, d.Anchor);
  }

  [Fact]
  public void CeilingBoundary_JustInsideIsCompetition_JustOutsideIsAWall()
  {
    var inside = LanePricing.Decide(L.Board(2_900), L.Lane(1_000, 5), null, L.Cfg());
    var outside = LanePricing.Decide(L.Board(3_100), L.Lane(1_000, 5), null, L.Cfg());

    Assert.Equal(LaneOutcome.InLane, inside.Outcome);
    Assert.Equal(2_900, inside.Anchor);
    Assert.Equal(LaneOutcome.LaneOwned, outside.Outcome);
  }

  // ---- The race (all listings below lane) --------------------------------

  [Fact]
  public void Race_SlowMarket_DeclinesAndWaitsAtTheLaneFloor()
  {
    // Astral Silk chased a below-lane race; the lane floor is the brake.
    var d = LanePricing.Decide(L.Board(110, 330), L.Lane(1_000, 5), velocityPerDay: 0.02, L.Cfg());

    Assert.Equal(LaneOutcome.RaceDeclined, d.Outcome);
    Assert.Equal(500, d.Anchor); // 0.5x median
    Assert.False(d.AnchorIsListing);
  }

  [Fact]
  public void Race_FastMarket_JoinsTheRace()
  {
    var d = LanePricing.Decide(L.Board(110, 330), L.Lane(1_000, 5), velocityPerDay: 1.0, L.Cfg());

    Assert.Equal(LaneOutcome.RaceJoined, d.Outcome);
    Assert.Equal(110, d.Anchor);
    Assert.True(d.AnchorIsListing);
  }

  [Fact]
  public void Race_UnknownVelocity_Declines()
  {
    // Fail toward holding value: no velocity evidence means no chase.
    var d = LanePricing.Decide(L.Board(110, 330), L.Lane(1_000, 5), velocityPerDay: null, L.Cfg());

    Assert.Equal(LaneOutcome.RaceDeclined, d.Outcome);
    Assert.Equal(500, d.Anchor);
  }

  // ---- Thin history --------------------------------------------------------

  [Fact]
  public void ThinHistory_Holds()
  {
    // Dwarven Mythril Ingot: 4.1x at n=1 - never act on a guess wearing
    // numbers. Anchor null = keep-price-and-flag / don't-auto-price.
    var d = LanePricing.Decide(L.Board(4_990), L.Lane(1_214, 1), null, L.Cfg());

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Null(d.Anchor);
  }

  [Fact]
  public void NoLaneAtAll_Holds()
  {
    var d = LanePricing.Decide(L.Board(4_990), lane: null, null, L.Cfg());

    Assert.Equal(LaneOutcome.HeldThinHistory, d.Outcome);
    Assert.Null(d.Anchor);
  }

  [Fact]
  public void MinHistorySamples_BoundaryActs()
  {
    // n == MinHistorySamples (3) is enough; the lane acts.
    var d = LanePricing.Decide(L.Board(1_100), L.Lane(1_200, 3), null, L.Cfg());

    Assert.Equal(LaneOutcome.InLane, d.Outcome);
    Assert.Equal(1_100, d.Anchor);
  }

  [Fact]
  public void CommunityLane_ActsAndIsLabeled()
  {
    // The confidence-gated safety net: a community lane decides like a local
    // one but its evidence is always labeled.
    var d = LanePricing.Decide(L.Board(1_100), L.Lane(1_200, 10, LaneSource.Community), null, L.Cfg());

    Assert.Equal(LaneOutcome.InLane, d.Outcome);
    Assert.Equal(1_100, d.Anchor);
    Assert.Contains("community", d.Evidence, StringComparison.OrdinalIgnoreCase);
  }
}
