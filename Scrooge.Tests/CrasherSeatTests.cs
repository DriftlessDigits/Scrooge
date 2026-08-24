using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// THE GAP TEST, POINTED AT OURSELVES (ruled 2026-08-23). The founding receipt is
/// the Grade 2 Gemsap of Intelligence: our 200 held as 1st under a 480-500
/// cluster - a seat any other seller's walk classifies as the crasher to step
/// over, so it pressures nobody and sells first at the discount. The rule reuses
/// the house company margin (LanePricing.ClusterNearPct): a held ask reads as
/// the crasher when the cheapest REACHABLE competitor sits more than 25% above it.
/// </summary>
public class CrasherSeatTests
{
  private static LaneCensus Census(int competitors, long? floor)
    => new(Sellers: competitors, Seat: 1, PriorSeat: 1,
           Competitors: competitors, CompetitorFloor: floor, CompetitorCeiling: floor,
           AboveCeiling: 0, BandLow: null, BandHigh: null, SaleCount: 0, PerDay: null);

  [Fact]
  public void TheGemsapSeat_FlagsWhenTheClusterSitsFarAbove()
  {
    // The founding case: 200 held, six real sellers from 480.
    Assert.True(LanePricing.HeldAskReadsAsCrasher(200, Census(6, 480)));
  }

  [Fact]
  public void CompanyKillsIt_ANeighborWithinTheMarginIsALine()
  {
    // 240 is within 25% of 200 - we lead a line, we are not a crasher.
    Assert.False(LanePricing.HeldAskReadsAsCrasher(200, Census(6, 240)));
  }

  [Fact]
  public void TheBoundaryBelongsToCompany()
  {
    // Exactly 25% above is company by the house margin (strict inequality) -
    // the same edge the cluster test draws everywhere else.
    Assert.False(LanePricing.HeldAskReadsAsCrasher(200, Census(3, 250)));
    Assert.True(LanePricing.HeldAskReadsAsCrasher(200, Census(3, 251)));
  }

  [Fact]
  public void DreamersRaiseNothing_AGapToAWishIsNotAGap()
  {
    // No reachable competitors: everything on the board sits above the 3x rail.
    Assert.False(LanePricing.HeldAskReadsAsCrasher(200, Census(0, null)));
  }

  [Fact]
  public void SilenceOnDegenerateOperands()
  {
    Assert.False(LanePricing.HeldAskReadsAsCrasher(0, Census(6, 480)));
    Assert.False(LanePricing.HeldAskReadsAsCrasher(-5, Census(6, 480)));
    Assert.False(LanePricing.HeldAskReadsAsCrasher(200, Census(6, null)));
  }
}
