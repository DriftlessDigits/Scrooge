using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// Tests for the Ledger pure core (M6 session 2): pile assignment, the
/// evidence-refined confidence score, and bulk eligibility. The Alexander
/// Miniature case is the load-bearing receipt - it proves the guard gates on
/// evidence disagreement, not on pile membership.
/// </summary>
public class LedgerTests
{
  // Evidence factory: a Unanimous-by-default OFF-market vendor verdict with a
  // dead market. Each test overrides only the axis it exercises.
  private static LedgerConfidence.Evidence Ev(
    VerdictLean lean = VerdictLean.OffMarket,
    int laneN = 5,
    double spread = 0.1,
    double? velocity = 0.0,
    int recentSales = 0,
    int ageDays = 1,
    Accord community = Accord.Unknown,
    int minSamples = 3,
    int staleDays = 14) => new(
      Lean: lean,
      LaneSampleCount: laneN,
      LaneSpread: spread,
      VelocityPerDay: velocity,
      RecentSalesCount: recentSales,
      EvidenceAgeDays: ageDays,
      LocalCommunityAccord: community,
      MinSamples: minSamples,
      StaleDays: staleDays);

  // ---- Tier assignment across the evidence axes ----

  [Fact]
  public void FullAgreement_IsUnanimous()
  {
    // Off-market vendor verdict, dead market, plenty of fresh tight samples.
    Assert.Equal(ConfidenceTier.Unanimous, LedgerConfidence.BaseTier(Ev()));
  }

  [Fact]
  public void ThinSamples_IsMixed()
  {
    Assert.Equal(ConfidenceTier.Mixed, LedgerConfidence.BaseTier(Ev(laneN: 1)));
  }

  [Fact]
  public void StaleEvidence_IsMixed()
  {
    Assert.Equal(ConfidenceTier.Mixed, LedgerConfidence.BaseTier(Ev(ageDays: 90)));
  }

  [Fact]
  public void WideSpread_IsMixed()
  {
    Assert.Equal(ConfidenceTier.Mixed, LedgerConfidence.BaseTier(Ev(spread: 1.2)));
  }

  [Fact]
  public void OffMarketVerdict_WithNoMarketEvidence_IsMixed_NotUnanimous()
  {
    // No sales and no velocity = the sales axis is Unknown, not Agree, so the
    // verdict is not actively backed - honest Mixed, never a silent Unanimous.
    Assert.Equal(ConfidenceTier.Mixed, LedgerConfidence.BaseTier(Ev(velocity: null, recentSales: 0)));
  }

  [Fact]
  public void OnMarketVerdict_OverLiveMarket_IsUnanimous()
  {
    var e = Ev(lean: VerdictLean.OnMarket, velocity: 0.8, recentSales: 6);
    Assert.Equal(ConfidenceTier.Unanimous, LedgerConfidence.BaseTier(e));
  }

  [Fact]
  public void OnMarketVerdict_OverDeadMarket_IsContradicted()
  {
    // Listing a genuinely dead market contradicts a List/Reprice verdict.
    var e = Ev(lean: VerdictLean.OnMarket, velocity: 0.0, recentSales: 0);
    Assert.Equal(ConfidenceTier.Contradicted, LedgerConfidence.BaseTier(e));
  }

  [Fact]
  public void LocalCommunityDisagreement_IsContradicted()
  {
    Assert.Equal(ConfidenceTier.Contradicted,
      LedgerConfidence.BaseTier(Ev(community: Accord.Disagree)));
  }

  [Fact]
  public void NeutralVerdict_MakesNoMarketClaim()
  {
    // A Neutral lean's sales axis is Unknown regardless of the market.
    Assert.Equal(Accord.Unknown,
      LedgerConfidence.SalesVerdictAccord(Ev(lean: VerdictLean.Neutral, recentSales: 20)));
  }

  // ---- The Alexander Miniature case (verbatim) ----

  [Fact]
  public void AlexanderMiniature_BelowMinVendorVerdict_ContradictedByStrongSales_IsContradicted()
  {
    // A below-minimum / vendor verdict CONTRADICTED by 13 sales in 14 days.
    var e = Ev(lean: VerdictLean.OffMarket, recentSales: 13, velocity: null);
    Assert.Equal(Accord.Disagree, LedgerConfidence.SalesVerdictAccord(e));
    Assert.Equal(ConfidenceTier.Contradicted, LedgerConfidence.BaseTier(e));
  }

  [Fact]
  public void AlexanderMiniature_DemotedToReview_AndImmuneToBulk()
  {
    var tier = LedgerConfidence.Tier(Ev(lean: VerdictLean.OffMarket, recentSales: 13, velocity: null));
    // The natural pile was Pull-and-Vendor (a below-min disposal row)...
    var natural = LedgerPiles.ForTriage(PricingResult.BelowMinimum);
    Assert.Equal(LedgerPile.PullAndVendor, natural);
    // ...but a Contradicted verdict is demoted to Review, and cannot be bulked.
    Assert.Equal(LedgerPile.Review, LedgerPiles.Effective(natural, tier));
    Assert.False(LedgerConfidence.IsBulkEligible(tier));
  }

  [Fact]
  public void StrongSalesThreshold_IsExactlyThree()
  {
    // 2 sales/14d does NOT contradict an off-market verdict; 3 does (the bar).
    Assert.NotEqual(Accord.Disagree,
      LedgerConfidence.SalesVerdictAccord(Ev(recentSales: 2, velocity: null)));
    Assert.Equal(Accord.Disagree,
      LedgerConfidence.SalesVerdictAccord(Ev(recentSales: 3, velocity: null)));
  }

  [Fact]
  public void VelocityAlone_CanTripTheStrongMarketBar()
  {
    // 0.3/day * 14 = 4.2 >= 3 => strong market, contradicts an off-market verdict.
    Assert.Equal(Accord.Disagree,
      LedgerConfidence.SalesVerdictAccord(Ev(recentSales: 0, velocity: 0.3)));
  }

  // ---- Override-count refinement shifts a tier ----

  [Fact]
  public void OverrideCount_DemotesUnanimousToMixed()
  {
    var e = Ev(); // Unanimous by construction
    Assert.Equal(ConfidenceTier.Unanimous, LedgerConfidence.Tier(e, overrideCount: 0));
    Assert.Equal(ConfidenceTier.Unanimous, LedgerConfidence.Tier(e, overrideCount: 1));
    Assert.Equal(ConfidenceTier.Mixed, LedgerConfidence.Tier(e, overrideCount: 2));
  }

  [Fact]
  public void OverrideCount_DoesNotRescueAContradictedVerdict()
  {
    // Overrides only ever LOWER confidence - a contradicted verdict stays contradicted.
    var e = Ev(lean: VerdictLean.OffMarket, recentSales: 13, velocity: null);
    Assert.Equal(ConfidenceTier.Contradicted, LedgerConfidence.Tier(e, overrideCount: 99));
  }

  [Fact]
  public void OverrideCount_DoesNotTouchMixed()
  {
    var e = Ev(laneN: 1); // Mixed (thin)
    Assert.Equal(ConfidenceTier.Mixed, LedgerConfidence.Tier(e, overrideCount: 99));
  }

  // ---- Bulk eligibility ----

  [Fact]
  public void BulkSet_EnumeratesOnlyUnanimousRows()
  {
    var rows = new (string, ConfidenceTier)[]
    {
      ("a", ConfidenceTier.Unanimous),
      ("b", ConfidenceTier.Mixed),
      ("c", ConfidenceTier.Contradicted),
      ("d", ConfidenceTier.Unanimous),
    };
    var bulk = LedgerConfidence.BulkSet(rows);
    Assert.Equal(new[] { "a", "d" }, bulk);
  }

  [Fact]
  public void BulkSet_ContradictedNever_MixedNever()
  {
    var rows = new (string, ConfidenceTier)[]
    {
      ("mixed", ConfidenceTier.Mixed),
      ("contra", ConfidenceTier.Contradicted),
    };
    Assert.Empty(LedgerConfidence.BulkSet(rows));
  }

  // ---- Pile assignment: verdict -> pile ----

  [Fact]
  public void RoutingExit_MapsToPile()
  {
    Assert.Equal(LedgerPile.List, LedgerPiles.ForRoutingExit(RoutingExit.List, false));
    Assert.Equal(LedgerPile.PullAndVendor, LedgerPiles.ForRoutingExit(RoutingExit.Vendor, false));
    Assert.Equal(LedgerPile.Melt, LedgerPiles.ForRoutingExit(RoutingExit.Desynth, false));
    Assert.Equal(LedgerPile.Churn, LedgerPiles.ForRoutingExit(RoutingExit.Gc, false));
    Assert.Equal(LedgerPile.Watch, LedgerPiles.ForRoutingExit(RoutingExit.Hold, false));
    Assert.Equal(LedgerPile.Watch, LedgerPiles.ForRoutingExit(RoutingExit.Ban, false));
    // IsReview always wins, whatever the exit.
    Assert.Equal(LedgerPile.Review, LedgerPiles.ForRoutingExit(RoutingExit.List, true));
    Assert.Equal(LedgerPile.Review, LedgerPiles.ForRoutingExit(RoutingExit.Vendor, true));
  }

  [Fact]
  public void TriageResult_MapsToPile()
  {
    Assert.Equal(LedgerPile.Reprice, LedgerPiles.ForTriage(PricingResult.CapBlocked));
    Assert.Equal(LedgerPile.Reprice, LedgerPiles.ForTriage(PricingResult.UndercutTooDeep));
    Assert.Equal(LedgerPile.Reprice, LedgerPiles.ForTriage(PricingResult.UpwardHeld));
    Assert.Equal(LedgerPile.Watch, LedgerPiles.ForTriage(PricingResult.LaneHeld));
    Assert.Equal(LedgerPile.PullAndVendor, LedgerPiles.ForTriage(PricingResult.BelowFloor));
    Assert.Equal(LedgerPile.PullAndVendor, LedgerPiles.ForTriage(PricingResult.BelowMinimum));
    Assert.Equal(LedgerPile.Review, LedgerPiles.ForTriage(PricingResult.NoData));
  }

  // ---- Merged two-reason WorkItems ----

  [Fact]
  public void Merge_TwoReasons_ReviewWins()
  {
    // An item flagged both reprice-worthy AND needs-eyes is ONE Review row.
    var merged = LedgerPiles.Merge(new[] { LedgerPile.Reprice, LedgerPile.Review });
    Assert.Equal(LedgerPile.Review, merged);
  }

  [Fact]
  public void Merge_RepriceVsPullAndVendor_PullWins()
  {
    // Below-floor (pull/vendor) beats a cap-block (reprice): if it is worthless,
    // fixing its price is moot.
    var merged = LedgerPiles.Merge(new[] { LedgerPile.Reprice, LedgerPile.PullAndVendor });
    Assert.Equal(LedgerPile.PullAndVendor, merged);
  }

  [Fact]
  public void Merge_SingleReason_IsThatReason()
  {
    Assert.Equal(LedgerPile.Watch, LedgerPiles.Merge(new[] { LedgerPile.Watch }));
  }

  [Fact]
  public void Merge_Empty_DefaultsToReview()
  {
    Assert.Equal(LedgerPile.Review, LedgerPiles.Merge(System.Array.Empty<LedgerPile>()));
  }

  // ---- Watch pile summary counts ----

  [Fact]
  public void WatchSummary_FormatsCountsOmittingZeros()
  {
    var counts = new Dictionary<WatchCategory, int>
    {
      [WatchCategory.Race] = 3,
      [WatchCategory.Thin] = 8,
      [WatchCategory.Bait] = 3,
    };
    Assert.Equal("14 watching: 3 races, 8 thin, 3 bait", LedgerConfidence.WatchSummary(counts));
  }

  [Fact]
  public void WatchSummary_OmitsEmptyCategories()
  {
    var counts = new Dictionary<WatchCategory, int> { [WatchCategory.Thin] = 5 };
    Assert.Equal("5 watching: 5 thin", LedgerConfidence.WatchSummary(counts));
  }

  [Fact]
  public void WatchSummary_EmptyIsZero()
  {
    Assert.Equal("0 watching", LedgerConfidence.WatchSummary(new Dictionary<WatchCategory, int>()));
  }
}
