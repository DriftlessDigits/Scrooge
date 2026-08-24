using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// Regime segmentation - who is still voting. The 07-25 case-study boards are
/// the fixtures: Blue Zircon (the cliff) and Ceremonial Culottes of Scouting
/// (the glide) are the two live convictions that produced A2, and the sparse /
/// tiny / stable boards are the shapes that must NOT be convicted of anything.
/// </summary>
internal static class R
{
  public const long Now = 1_800_000_000;

  public static long DaysAgo(double days) => Now - (long)(days * 86400);

  /// <summary>Sales newest-first: the first price is the newest sale, one day apart.</summary>
  public static List<LaneSale> NewestFirst(params long[] prices)
  {
    var sales = new List<LaneSale>();
    for (var i = 0; i < prices.Length; i++)
      sales.Add(new LaneSale(prices[i], DaysAgo(1 + i), false));
    return sales;
  }

  /// <summary>Sales newest-first with explicit ages in days (index-matched).</summary>
  public static List<LaneSale> Aged(long[] prices, double[] ageDays)
  {
    var sales = new List<LaneSale>();
    for (var i = 0; i < prices.Length; i++)
      sales.Add(new LaneSale(prices[i], DaysAgo(ageDays[i]), false));
    return sales;
  }

  public static LaneConfig Cfg() => new();
}

public class RegimeSegmentCliffTests
{
  [Fact]
  public void Zircon_SevenFreshClearsDemoteTheOldCluster()
  {
    // 07-25 receipt: a supply dump on 7/15 cleared seven times at 223-439 while
    // the lane's anchor sat at ~975 off ~20 older sales. The old cluster carried
    // the vote and we parked at 488 - above every sale in the current regime.
    var prices = new List<long> { 439, 223, 380, 256, 411, 298, 344 };   // the fresh dump, jumbled
    var ages = new List<double> { 1, 2, 3, 4, 5, 6, 7 };
    for (var i = 0; i < 13; i++)                                          // the old regime, ~10d back
    {
      prices.Add(900 + (i % 5) * 25);
      ages.Add(10 + i);
    }

    var segment = RegimeSegment.Resolve(R.Aged([.. prices], [.. ages]), R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.Cliff, segment.Cut);
    Assert.Equal(7, segment.SegmentCount);
    Assert.All(segment.Sales, s => Assert.True(s.UnitPrice <= 439));
  }

  [Fact]
  public void Cliff_ExtendsGreedilyToTheWholeFreshRun()
  {
    // Five fresh sales, not three: the cut belongs where the old cluster starts.
    var sales = R.NewestFirst(200, 260, 210, 250, 230, 900, 950, 920, 980, 930);

    var segment = RegimeSegment.Resolve(sales, R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.Cliff, segment.Cut);
    Assert.Equal(5, segment.SegmentCount);
  }

  [Fact]
  public void Cliff_NeedsMoreThanTwoFreshSales()
  {
    // Two cheap Tuesdays are not a regime (A2's first-law counterweight).
    var sales = R.NewestFirst(200, 260, 900, 950, 920, 980, 930, 910);

    var segment = RegimeSegment.Resolve(sales, R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.None, segment.Cut);
    Assert.Equal(8, segment.SegmentCount);
  }

  [Fact]
  public void Cliff_NeedsAnOldClusterToFallOffOf()
  {
    // Three fresh lows against two older sales: nothing here is a cluster.
    var sales = R.NewestFirst(200, 260, 210, 900, 950);

    Assert.Equal(SegmentCut.None, RegimeSegment.Resolve(sales, R.Cfg(), R.Now).Cut);
  }

  [Fact]
  public void Cliff_ReadsTheOldClusterByPriceNotByTime()
  {
    // WeightedQuantiles demands ascending-by-price; the older remainder arrives
    // newest-first. Unsorted, the walk crosses p25 at whatever price happened to
    // sell most recently - here a 1,000 - and convicts a 590-620 run that the
    // old market (sales at 500) still clears BELOW. No regime broke: the fresh
    // run sits inside the old market's own range.
    var prices = new long[] { 600, 620, 590, 1000, 500, 500, 1000 };
    var ages = new double[] { 1, 2, 3, 10, 11, 12, 13 };

    var segment = RegimeSegment.Resolve(R.Aged(prices, ages), R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.None, segment.Cut);
    Assert.Equal(7, segment.SegmentCount);
  }

  [Fact]
  public void Cliff_DemotesRatherThanDecays()
  {
    // The demoted sales are GONE from the electorate, not down-weighted (A2/A6):
    // nothing above the break survives into the segment at any weight.
    var sales = R.NewestFirst(200, 260, 210, 250, 900, 950, 920, 980);

    var segment = RegimeSegment.Resolve(sales, R.Cfg(), R.Now);

    Assert.DoesNotContain(segment.Sales, s => s.UnitPrice >= 900);
  }
}

public class RegimeSegmentGlideTests
{
  [Fact]
  public void Culottes_TheTwoMonthSlideCutsAtTheStartOfTheDecline()
  {
    // 07-25 receipt: 120-137k (mid-May) -> 90-99k (June) -> 78.4k (7/1) -> 48k
    // (7/14) -> 36k (7/20), board racing 34.3k x19. The June cluster outvoted
    // three July sales and we asked 44,999 - above the last clear AND the board.
    var prices = new long[] { 36_000, 48_000, 78_400, 90_500, 99_000, 93_000, 137_000, 120_000, 128_000, 122_000 };
    var ages = new double[] { 5, 11, 24, 33, 40, 47, 62, 68, 71, 75 };

    var segment = RegimeSegment.Resolve(R.Aged(prices, ages), R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.Glide, segment.Cut);
    // The run stops inside the jumbled June cluster - the mid-May cluster that
    // outvoted three July sales is out of the electorate entirely.
    Assert.Equal(5, segment.SegmentCount);
    Assert.DoesNotContain(segment.Sales, s => s.UnitPrice >= 120_000);
  }

  [Fact]
  public void Glide_NeedsFiveSteps()
  {
    // Four falling sales are a run of luck; five are a slide.
    var sales = R.NewestFirst(70, 80, 90, 100, 250, 255, 245, 260);

    Assert.NotEqual(SegmentCut.Glide, RegimeSegment.Resolve(sales, R.Cfg(), R.Now).Cut);
  }

  [Fact]
  public void Glide_ToleratesFivePercentOfJitter()
  {
    // Real tape hiccups - a round number, a buyer who did not check. A strict
    // monotonic test would break the slide on its first bump.
    var sales = R.NewestFirst(62, 60, 75, 88, 100, 104);

    var segment = RegimeSegment.Resolve(sales, R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.Glide, segment.Cut);
    Assert.Equal(6, segment.SegmentCount);
  }

  [Fact]
  public void Glide_IgnoresADriftThatGoesNowhere()
  {
    // Six sales easing down 8% total: a market holding still, not a regime.
    var sales = R.NewestFirst(92, 94, 96, 97, 99, 100);

    Assert.Equal(SegmentCut.None, RegimeSegment.Resolve(sales, R.Cfg(), R.Now).Cut);
  }

  [Fact]
  public void Glide_StopsAtABreakInsteadOfSwallowingTheOldCluster()
  {
    // A 66% single step is a cliff face, not a slide. A cliff is technically
    // monotonic, so without the step ceiling the glide's run would walk straight
    // through the break and re-elect the very cluster segmentation exists to
    // demote - the Zircon failure with a different name on it.
    var sales = R.NewestFirst(100, 150, 200, 250, 300, 880, 900, 890, 910, 895);

    var segment = RegimeSegment.Resolve(sales, R.Cfg(), R.Now);

    Assert.Equal(5, segment.SegmentCount);
    Assert.DoesNotContain(segment.Sales, s => s.UnitPrice >= 880);
  }
}

public class RegimeSegmentQuietBoardTests
{
  [Fact]
  public void StableMarket_TheWholeElectorateVotes()
  {
    var sales = R.NewestFirst(1000, 980, 1020, 995, 1010, 990, 1005, 1015);

    var segment = RegimeSegment.Resolve(sales, R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.None, segment.Cut);
    Assert.Equal(8, segment.SegmentCount);
    Assert.Equal(8, segment.ElectorateCount);
  }

  [Fact]
  public void SparseSlowMover_FourSalesOverThreeMonthsConvictNobody()
  {
    // The starvation case A2 was rewritten for: a count window sees all four,
    // and four scattered sales are still just four scattered sales.
    var segment = RegimeSegment.Resolve(
      R.Aged([700, 620, 780, 660], [4, 31, 58, 88]), R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.None, segment.Cut);
    Assert.Equal(4, segment.SegmentCount);
  }

  [Fact]
  public void TinyHistory_OneAndTwoSalesSurviveIntact()
  {
    var one = RegimeSegment.Resolve(R.NewestFirst(500), R.Cfg(), R.Now);
    Assert.Equal(SegmentCut.None, one.Cut);
    Assert.Equal(1, one.SegmentCount);

    var two = RegimeSegment.Resolve(R.NewestFirst(300, 900), R.Cfg(), R.Now);
    Assert.Equal(SegmentCut.None, two.Cut);
    Assert.Equal(2, two.SegmentCount);
  }

  [Fact]
  public void EmptyTape_ResolvesToNothingWithoutThrowing()
  {
    var segment = RegimeSegment.Resolve([], R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.None, segment.Cut);
    Assert.Equal(0, segment.SegmentCount);
    Assert.Empty(segment.Sales);
  }

  [Fact]
  public void Electorate_StopsAtTheWindow()
  {
    // Count-based, self-scaling: older sales are out entirely, not down-weighted.
    var prices = new long[SegmentWindowPlusFive];
    for (var i = 0; i < prices.Length; i++)
      prices[i] = 1000 + i;

    var segment = RegimeSegment.Resolve(R.NewestFirst(prices), R.Cfg(), R.Now);

    Assert.Equal(RegimeSegment.SegmentWindowK, segment.ElectorateCount);
    Assert.Equal(RegimeSegment.SegmentWindowK, segment.SegmentCount);
  }

  private const int SegmentWindowPlusFive = RegimeSegment.SegmentWindowK + 5;
}

public class RegimeSegmentVelocityTests
{
  [Fact]
  public void Velocity_ReadsTheSegmentsOwnSpanNotTheWholeTape()
  {
    // Zircon: seven fresh clears across seven days = 1/day. The full 20-sale
    // history spans three weeks and would report a third of that - the same
    // blend that let Sarcenet's "9.55/day" justify a 2,500 ask (A5).
    var segment = RegimeSegment.Resolve(
      R.Aged(
        [439, 223, 380, 256, 411, 298, 344, 900, 925, 950, 975, 1000, 910, 930],
        [1, 2, 3, 4, 5, 6, 7, 10, 12, 14, 16, 18, 20, 22]),
      R.Cfg(), R.Now);

    Assert.Equal(SegmentCut.Cliff, segment.Cut);
    Assert.Equal(7, segment.SegmentCount);
    Assert.Equal(7 / 6.0, RegimeSegment.VelocityPerDay(segment.Sales)!.Value, 3);
  }

  [Fact]
  public void Velocity_SameMinuteRoundDoesNotReportAnInfiniteRate()
  {
    // A bulk sweeper fills four listings in one second; the half-day floor keeps
    // the burst from claiming a rate no market can sustain.
    var burst = new List<LaneSale>
    {
      new(400, R.Now - 10, false),
      new(410, R.Now - 20, false),
      new(405, R.Now - 30, false),
      new(415, R.Now - 40, false),
    };

    Assert.Equal(8.0, RegimeSegment.VelocityPerDay(burst)!.Value, 3);
  }

  [Fact]
  public void Velocity_IsNullWithoutASegment()
  {
    Assert.Null(RegimeSegment.VelocityPerDay([]));
  }
}
