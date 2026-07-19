using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// Tests for the Listed-item pure core (M6 session 3): per-retainer grouping,
/// honest age labels, outlier own-listing detection (the Highland Fence smell),
/// vendor-floor pull-forward, Watch categorization, and the Contradicted-row
/// objection note. All render from captured data - no game reads anywhere.
/// </summary>
public class LedgerListingsTests
{
  private static ListedLine Line(string retainer = "Karen", long price = 1000,
    int qty = 1, long firstSeen = 0, string name = "Thing", bool hq = false, uint id = 1)
    => new(retainer, id, name, hq, price, qty, firstSeen);

  private static LaneModel Lane(double median = 1000, int n = 5)
    => new() { Median = median, SampleCount = n, WeightedAgeDays = 1, Source = LaneSource.Local };

  // ---- Item 1: per-retainer grouping + gil at ask ----

  [Fact]
  public void GroupByRetainer_SumsCountAndGilAtAsk()
  {
    var groups = LedgerListings.GroupByRetainer(new[]
    {
      Line("Karen", price: 100, qty: 3),   // 300
      Line("Karen", price: 50, qty: 1),    // 50
      Line("Dave", price: 1000, qty: 2),   // 2000
    });

    Assert.Equal(2, groups.Count);
    // Richest group first.
    Assert.Equal("Dave", groups[0].Retainer);
    Assert.Equal(1, groups[0].Count);
    Assert.Equal(2000, groups[0].GilAtAsk);
    Assert.Equal("Karen", groups[1].Retainer);
    Assert.Equal(2, groups[1].Count);
    Assert.Equal(350, groups[1].GilAtAsk);
  }

  [Fact]
  public void GroupByRetainer_ZeroQuantity_CountsAsOneStack()
  {
    // A synthetic/held row can carry qty 0; gil-at-ask never multiplies to zero.
    var groups = LedgerListings.GroupByRetainer(new[] { Line(price: 500, qty: 0) });
    Assert.Equal(500, groups[0].GilAtAsk);
  }

  // ---- Item 2: honest age labels ----

  [Fact]
  public void AgeDays_WholeDaysSinceFirstSeen()
  {
    Assert.Equal(3, LedgerListings.AgeDays(firstSeen: 0, now: 3 * 86400 + 500));
    Assert.Equal(0, LedgerListings.AgeDays(firstSeen: 100, now: 50)); // never negative
  }

  [Fact]
  public void Tier_BandsByConfig()
  {
    var cfg = new ListedAgeConfig { AgingDays = 7, StaleDays = 30 };
    Assert.Equal(ListedAgeTier.Fresh, LedgerListings.Tier(6, cfg));
    Assert.Equal(ListedAgeTier.Aging, LedgerListings.Tier(7, cfg));
    Assert.Equal(ListedAgeTier.Aging, LedgerListings.Tier(29, cfg));
    Assert.Equal(ListedAgeTier.Stale, LedgerListings.Tier(30, cfg));
  }

  [Fact]
  public void AgeLabel_ExactVsLowerBound()
  {
    Assert.Equal("3d listed", LedgerListings.AgeLabel(3, exact: true));
    Assert.Equal(">=3d listed", LedgerListings.AgeLabel(3, exact: false));
  }

  [Fact]
  public void AgeIsExact_LowerBoundWhenPresentAtFirstObservation()
  {
    // first_seen at or before our first-ever scan = we can't know the true start.
    Assert.False(LedgerListings.AgeIsExact(firstSeen: 100, firstObservationEver: 100));
    Assert.False(LedgerListings.AgeIsExact(firstSeen: 90, firstObservationEver: 100));
    // Seen strictly after we started observing = an exact age.
    Assert.True(LedgerListings.AgeIsExact(firstSeen: 200, firstObservationEver: 100));
    // No observation history known = treat as exact (nothing to bound against).
    Assert.True(LedgerListings.AgeIsExact(firstSeen: 200, firstObservationEver: 0));
  }

  // ---- Item 3: outlier own-listing detection ----

  [Fact]
  public void IsOutlierListing_AtOrAboveWallBoundary_IsOutlier()
  {
    var cfg = new LaneConfig(); // CeilingMult 3.0, MinHistorySamples 3
    // Lane median 1000, wall boundary 3000.
    Assert.True(LedgerListings.IsOutlierListing(3000, Lane(1000), cfg));
    Assert.True(LedgerListings.IsOutlierListing(55_000_000, Lane(1000), cfg));
    Assert.False(LedgerListings.IsOutlierListing(2999, Lane(1000), cfg));
  }

  [Fact]
  public void IsOutlierListing_ThinLane_NeverOutlier()
  {
    // A thin lane can't say a price is an outlier - no invented certainty.
    var cfg = new LaneConfig();
    Assert.False(LedgerListings.IsOutlierListing(999_999, Lane(1000, n: 2), cfg));
  }

  [Fact]
  public void OutlierReason_NamesGoingRateAndMultiple()
  {
    var r = LedgerListings.OutlierReason(55_000_000, Lane(1000));
    Assert.Contains("55,000,000", r);
    Assert.Contains("1,000", r);
  }

  // ---- Item 4: vendor-floor pull-forward ----

  [Fact]
  public void VendorBeatsBoard_LaneAtOrBelowVendor()
  {
    Assert.True(LedgerListings.VendorBeatsBoard(lanePrice: 100, vendorPrice: 100));
    Assert.True(LedgerListings.VendorBeatsBoard(lanePrice: 80, vendorPrice: 100));
    Assert.False(LedgerListings.VendorBeatsBoard(lanePrice: 101, vendorPrice: 100));
    // No vendor price = nothing to pull forward to.
    Assert.False(LedgerListings.VendorBeatsBoard(lanePrice: 5, vendorPrice: 0));
  }

  [Fact]
  public void VendorFloorReason_StatesBothPrices()
  {
    var r = LedgerListings.VendorFloorReason(lanePrice: 80, vendorPrice: 100);
    Assert.Contains("100", r);
    Assert.Contains("80", r);
  }

  // ---- Item 5: Watch categorization ----

  [Fact]
  public void CategorizeWatch_FromLaneOutcome()
  {
    Assert.Equal(WatchCategory.Race, LedgerListings.CategorizeWatch(LaneOutcome.RaceDeclined));
    Assert.Equal(WatchCategory.Bait, LedgerListings.CategorizeWatch(LaneOutcome.WallIgnored));
    Assert.Equal(WatchCategory.Thin, LedgerListings.CategorizeWatch(LaneOutcome.HeldThinHistory));
    Assert.Equal(WatchCategory.Other, LedgerListings.CategorizeWatch(LaneOutcome.InLane));
    Assert.Equal(WatchCategory.Other, LedgerListings.CategorizeWatch(LaneOutcome.LaneOwned));
  }

  [Fact]
  public void CategorizeWatchReason_FromFlagReason()
  {
    Assert.Equal(WatchCategory.Thin, LedgerListings.CategorizeWatchReason("lane_held"));
    Assert.Equal(WatchCategory.Race, LedgerListings.CategorizeWatchReason("race_declined"));
    Assert.Equal(WatchCategory.Bait, LedgerListings.CategorizeWatchReason("wall_ignored"));
    Assert.Equal(WatchCategory.Bait, LedgerListings.CategorizeWatchReason("outlier_warn"));
    Assert.Equal(WatchCategory.Other, LedgerListings.CategorizeWatchReason("banned"));
  }

  [Fact]
  public void WatchSummary_ComposesFromCategorizedCounts()
  {
    // The categorizer feeds the existing WatchSummary format end to end.
    var counts = new Dictionary<WatchCategory, int>();
    foreach (var o in new[] { LaneOutcome.RaceDeclined, LaneOutcome.WallIgnored,
      LaneOutcome.WallIgnored, LaneOutcome.HeldThinHistory })
    {
      var c = LedgerListings.CategorizeWatch(o);
      counts[c] = counts.GetValueOrDefault(c) + 1;
    }
    Assert.Equal("4 watching: 1 races, 1 thin, 2 bait", LedgerConfidence.WatchSummary(counts));
  }

  // ---- Item 8 addendum: the Contradicted row states its objection ----

  [Fact]
  public void ContradictionNote_StatesDcPriceSalesAndVelocity()
  {
    var note = LedgerListings.ContradictionNote(communityMedian: 98, communitySales: 13, velocityPerDay: 0.9);
    Assert.Contains("the DC pays ~98 on 13 sales", note);
    Assert.Contains("moves ~0.9/day", note);
    Assert.StartsWith("...but", note);
  }

  [Fact]
  public void ContradictionNote_SingularSale()
    => Assert.Contains("on 1 sale.", LedgerListings.ContradictionNote(98, 1, null));

  [Fact]
  public void ContradictionNote_NoMarketEvidence_IsEmpty()
  {
    Assert.Equal("", LedgerListings.ContradictionNote(null, 0, null));
    Assert.Equal("", LedgerListings.ContradictionNote(98, 0, 0)); // no sales, no velocity
  }
}
