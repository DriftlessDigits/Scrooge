using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE INVARIANT the band-less stand-ins died on (cleanup pass): a lane with a
/// going rate always carries a band.
///
/// <para>LanePricing.BuildLane reads all three quantiles - p25, the median, p75 -
/// off ONE sorted walk over the same weighted evidence, so the going rate and its
/// edges are produced together or not at all. At n=1 the band collapses onto the
/// price, which is the honest report: a single sale disagrees with nothing. That
/// left LaneFloorPct and LaneOwnedMultiplier feeding arms nothing could reach,
/// which is why they are gone.</para>
///
/// <para>These are pins, not decoration: if a future lane source ever hands back a
/// going rate with no edges, the arms have to come back - and this is what says
/// so, loudly, instead of the pricing spine quietly anchoring at zero.</para>
/// </summary>
public class LaneAlwaysBandedTests
{
  private static LaneModel Build(params long[] prices)
    => LanePricing.BuildLane(L.Sales(1, false, prices), isHq: false, L.Cfg(), L.Now)!;

  [Theory]
  [InlineData(new long[] { 1_000 })]                          // n=1: the band IS the price
  [InlineData(new long[] { 1_000, 1_000 })]                   // n=2, agreeing
  [InlineData(new long[] { 100, 200, 300 })]                  // n=3, spread
  [InlineData(new long[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })]  // a long tail
  [InlineData(new long[] { 50_000, 51_000, 4_000_000 })]      // one dreamer in the evidence
  public void AGoingRateAlwaysCarriesABand(long[] prices)
  {
    var lane = Build(prices);

    Assert.True(lane.Median > 0, "a lane built from positive sales has a going rate");
    Assert.True(lane.BandLow > 0, "the band's lower edge is a real price, never absent");
    Assert.True(lane.BandHigh >= lane.BandLow, "the edges arrive in order");
    Assert.True(lane.BandLow <= lane.Median && lane.Median <= lane.BandHigh,
      "the going rate sits inside its own band");
  }

  [Fact]
  public void SingleSale_BandCollapsesOntoThePrice()
  {
    var lane = Build(1_214);

    Assert.Equal(1_214, lane.Median);
    Assert.Equal(1_214, lane.BandLow);
    Assert.Equal(1_214, lane.BandHigh);
  }

  [Fact]
  public void AgedEvidence_StillBanded()
  {
    // Weighting changes WHERE the edges land, never WHETHER they exist - the
    // quantile walk is the same walk however old the sales are.
    var sales = new List<LaneSale>
    {
      new(900, L.DaysAgo(120), false),
      new(1_000, L.DaysAgo(60), false),
      new(1_100, L.DaysAgo(1), false),
    };
    var lane = LanePricing.BuildLane(sales, isHq: false, L.Cfg(), L.Now)!;

    Assert.True(lane.BandLow > 0);
    Assert.True(lane.BandHigh >= lane.BandLow);
  }

  [Fact]
  public void NoSalesForTheQuality_IsNoLaneAtAll_NotABandlessOne()
  {
    // The only "no band" case there has ever been: no lane. The spine reads that
    // as silence and holds - it never anchors on a rate with no edges.
    var hqOnly = new List<LaneSale> { new(1_000, L.DaysAgo(1), true) };

    Assert.Null(LanePricing.BuildLane(hqOnly, isHq: false, L.Cfg(), L.Now));
  }
}
