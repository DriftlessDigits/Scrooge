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
public class BoardPilesTests
{
  // Evidence factory: a Unanimous-by-default OFF-market vendor verdict with a
  // dead market. Each test overrides only the axis it exercises.
  private static BoardConfidence.Evidence Ev(
    VerdictLean lean = VerdictLean.OffMarket,
    int laneN = 5,
    double spread = 0.1,
    double? velocity = 0.0,
    int recentSales = 0,
    int ageDays = 1,
    Accord community = Accord.Unknown,
    int minSamples = 3,
    int staleDays = 14,
    long? verdictWorth = null,
    long? marketBid = null,
    bool bookIsNew = false) => new(
      Lean: lean,
      LaneSampleCount: laneN,
      LaneSpread: spread,
      VelocityPerDay: velocity,
      RecentSalesCount: recentSales,
      EvidenceAgeDays: ageDays,
      LocalCommunityAccord: community,
      MinSamples: minSamples,
      StaleDays: staleDays,
      VerdictWorth: verdictWorth,
      MarketBid: marketBid,
      BookIsNew: bookIsNew);

  // ---- Tier assignment across the evidence axes ----

  // A live-market ON-market verdict with a full evidence base is the ON-market
  // Unanimous shape; each ON-market test perturbs one axis off it.
  private static BoardConfidence.Evidence LiveList(int laneN = 5, double spread = 0.1,
    int ageDays = 1) => Ev(lean: VerdictLean.OnMarket, velocity: 0.8, recentSales: 6,
      laneN: laneN, spread: spread, ageDays: ageDays);

  [Fact]
  public void OffMarket_DeadMarket_IsUnanimous()
  {
    // Vendor/churn/melt over a market that is not buying = the no-brainer disposal
    // pile: Unanimous by construction, no lane samples required.
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.BaseTier(Ev()));
  }

  [Fact]
  public void OffMarket_WithNoMarketEvidenceAtAll_IsUnanimous()
  {
    // Untradable vendor-trash gear: no market to measure. Absence IS the evidence -
    // this is exactly the bulk-vendor case ruling 7 exists to serve.
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.BaseTier(Ev(velocity: null, recentSales: 0)));
  }

  // ---- The book's maturity (ruled 08-22): absence testifies only from an
  // ---- experienced book ----

  [Fact]
  public void NewBook_TheAbsenceDoorGradesMixed_TheSameSilenceExperiencedGradesUnanimous()
  {
    // The cold-start hazard, pinned: identical all-silent OffMarket evidence.
    // On a new book the silence means "haven't looked yet" - the verdict still
    // gets made but arrives as a case. On an experienced book the same silence
    // is a real observation and rides.
    var silent = Ev(velocity: null, recentSales: 0, bookIsNew: true);
    Assert.Equal(ConfidenceTier.Mixed, BoardConfidence.BaseTier(silent));
    Assert.Equal(ConfidenceTier.Unanimous,
      BoardConfidence.BaseTier(silent with { BookIsNew = false }));
  }

  [Fact]
  public void NewBook_AMeasuredMarketTheVerdictOutbid_StaysConfident()
  {
    // Positive evidence is exempt: a young book may be confident about what it
    // HAS seen. A strong measured market (6 recent sales) that the verdict's own
    // worth outbids is a real comparison, not an absence claim.
    var measured = Ev(recentSales: 6, velocity: 0.8, verdictWorth: 100_000, marketBid: 11_000,
      bookIsNew: true);
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.BaseTier(measured));
  }

  [Fact]
  public void NewBook_ThePegDoorIsUntouched()
  {
    // The maturity gate never reaches PegOrGraded - a ruled constant is certain
    // on any book, day one included.
    var silent = Ev(velocity: null, recentSales: 0, bookIsNew: true);
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.PegOrGraded(true, silent, 0));
  }

  [Fact]
  public void IsNewBook_EitherWitnessStreamMatures()
  {
    // New = BOTH streams cold. Either one accumulating = the book has been
    // listening, and its silences start to mean something.
    Assert.True(BoardConfidence.IsNewBook(0, 0));
    Assert.True(BoardConfidence.IsNewBook(99, 24));
    Assert.False(BoardConfidence.IsNewBook(100, 0));   // the tape listened
    Assert.False(BoardConfidence.IsNewBook(0, 25));    // the almanac answered
  }

  [Fact]
  public void OnMarket_ThinSamples_IsMixed()
  {
    Assert.Equal(ConfidenceTier.Mixed, BoardConfidence.BaseTier(LiveList(laneN: 1)));
  }

  [Fact]
  public void OnMarket_StaleEvidence_IsMixed()
  {
    Assert.Equal(ConfidenceTier.Mixed, BoardConfidence.BaseTier(LiveList(ageDays: 90)));
  }

  [Fact]
  public void OnMarket_WideSpread_IsMixed()
  {
    Assert.Equal(ConfidenceTier.Mixed, BoardConfidence.BaseTier(LiveList(spread: 1.2)));
  }

  [Fact]
  public void OnMarket_Unmeasured_IsMixed()
  {
    // Listing into a market never measured (no velocity, no sales) is not confident.
    Assert.Equal(ConfidenceTier.Mixed,
      BoardConfidence.BaseTier(Ev(lean: VerdictLean.OnMarket, velocity: null, recentSales: 0)));
  }

  [Fact]
  public void OnMarketVerdict_OverLiveMarket_IsUnanimous()
  {
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.BaseTier(LiveList()));
  }

  [Fact]
  public void OnMarketVerdict_OverDeadMarket_IsContradicted()
  {
    // Listing a genuinely dead market contradicts a List/Reprice verdict.
    var e = Ev(lean: VerdictLean.OnMarket, velocity: 0.0, recentSales: 0);
    Assert.Equal(ConfidenceTier.Contradicted, BoardConfidence.BaseTier(e));
  }

  [Fact]
  public void LocalCommunityDisagreement_IsContradicted()
  {
    Assert.Equal(ConfidenceTier.Contradicted,
      BoardConfidence.BaseTier(Ev(community: Accord.Disagree)));
  }

  [Fact]
  public void NeutralVerdict_MakesNoMarketClaim()
  {
    // A Neutral lean's sales axis is Unknown regardless of the market.
    Assert.Equal(Accord.Unknown,
      BoardConfidence.SalesVerdictAccord(Ev(lean: VerdictLean.Neutral, recentSales: 20)));
  }

  // ---- The Alexander Miniature case (verbatim) ----

  [Fact]
  public void AlexanderMiniature_BelowMinVendorVerdict_ContradictedByStrongSales_IsContradicted()
  {
    // A below-minimum / vendor verdict CONTRADICTED by 13 sales in 14 days.
    var e = Ev(lean: VerdictLean.OffMarket, recentSales: 13, velocity: null);
    Assert.Equal(Accord.Disagree, BoardConfidence.SalesVerdictAccord(e));
    Assert.Equal(ConfidenceTier.Contradicted, BoardConfidence.BaseTier(e));
  }

  [Fact]
  public void AlexanderMiniature_DemotedToReview_AndImmuneToBulk()
  {
    var tier = BoardConfidence.Tier(Ev(lean: VerdictLean.OffMarket, recentSales: 13, velocity: null));
    // The natural pile was Pull-and-Vendor (a below-floor disposal row)...
    var natural = BoardPiles.ForStanding(PricingResult.BelowFloor);
    Assert.Equal(BoardPile.PullAndVendor, natural);
    // ...but a Contradicted verdict is demoted to Review, and cannot be bulked.
    Assert.Equal(BoardPile.Review, BoardPiles.Effective(natural, tier));
    Assert.False(BoardConfidence.IsBulkEligible(tier));
  }

  [Fact]
  public void StrongSalesThreshold_IsExactlyThree()
  {
    // 2 sales/14d does NOT contradict an off-market verdict; 3 does (the bar).
    Assert.NotEqual(Accord.Disagree,
      BoardConfidence.SalesVerdictAccord(Ev(recentSales: 2, velocity: null)));
    Assert.Equal(Accord.Disagree,
      BoardConfidence.SalesVerdictAccord(Ev(recentSales: 3, velocity: null)));
  }

  [Fact]
  public void VelocityAlone_CanTripTheStrongMarketBar()
  {
    // 0.3/day * 14 = 4.2 >= 3 => strong market, contradicts an off-market verdict.
    Assert.Equal(Accord.Disagree,
      BoardConfidence.SalesVerdictAccord(Ev(recentSales: 0, velocity: 0.3)));
  }

  // ---- The market must OUTBID a priced worth, not just exist (Drift's 07-22
  // ruling: "I already priced a red skillup at 100k... what else would that
  // number be for"). The Archeo Kingdom Codex case, verbatim. ----

  [Fact]
  public void ArcheoKingdomCodex_StrongMarketLosesToPricedWorth_Agrees()
  {
    // Melt verdict worth ~100k (priced red skillup); DC pays ~11,499 on 9 sales.
    // The market's bid is already in the score and loses 9x - old news, not a
    // contradiction. The verdict stands Unanimous.
    var e = Ev(recentSales: 9, velocity: null, verdictWorth: 100_000, marketBid: 11_499);
    Assert.Equal(Accord.Agree, BoardConfidence.SalesVerdictAccord(e));
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.BaseTier(e));
  }

  [Fact]
  public void StrongMarket_OutbidsPricedWorth_StillContradicts()
  {
    // The same skillup against a 150k market: a REAL Alexander - the market
    // outbids the priced worth, so the contradiction fires loudly.
    var e = Ev(recentSales: 9, velocity: null, verdictWorth: 100_000, marketBid: 150_000);
    Assert.Equal(Accord.Disagree, BoardConfidence.SalesVerdictAccord(e));
    Assert.Equal(ConfidenceTier.Contradicted, BoardConfidence.BaseTier(e));
  }

  [Fact]
  public void StrongMarket_NoPricedWorth_ContradictsAsBefore()
  {
    // Alexander stays alive: a verdict with no scored worth on record (triage
    // rows, pre-value exits) is still contradicted by existence alone.
    var e = Ev(recentSales: 13, velocity: null, marketBid: 999);
    Assert.Equal(Accord.Disagree, BoardConfidence.SalesVerdictAccord(e));
  }

  [Fact]
  public void StrongMarket_WorthKnownButNoBidNumber_Agrees()
  {
    // Sales volume with no price witness cannot outbid a known worth - the
    // market has to bring a NUMBER to the argument, not just foot traffic.
    var e = Ev(recentSales: 9, velocity: null, verdictWorth: 100_000);
    Assert.Equal(Accord.Agree, BoardConfidence.SalesVerdictAccord(e));
  }

  [Fact]
  public void StrongMarket_BidEqualToWorth_DoesNotOutbid()
  {
    // A tie is not an outbid; the player's priced decision holds the lane.
    var e = Ev(recentSales: 9, velocity: null, verdictWorth: 100_000, marketBid: 100_000);
    Assert.Equal(Accord.Agree, BoardConfidence.SalesVerdictAccord(e));
  }

  // ---- Override-count refinement shifts a tier ----

  [Fact]
  public void OverrideCount_DemotesUnanimousToMixed()
  {
    var e = Ev(); // Unanimous by construction
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.Tier(e, overrideCount: 0));
    Assert.Equal(ConfidenceTier.Unanimous, BoardConfidence.Tier(e, overrideCount: 1));
    Assert.Equal(ConfidenceTier.Mixed, BoardConfidence.Tier(e, overrideCount: 2));
  }

  [Fact]
  public void OverrideCount_DoesNotRescueAContradictedVerdict()
  {
    // Overrides only ever LOWER confidence - a contradicted verdict stays contradicted.
    var e = Ev(lean: VerdictLean.OffMarket, recentSales: 13, velocity: null);
    Assert.Equal(ConfidenceTier.Contradicted, BoardConfidence.Tier(e, overrideCount: 99));
  }

  [Fact]
  public void OverrideCount_DoesNotTouchMixed()
  {
    var e = Ev(lean: VerdictLean.OnMarket, velocity: null, recentSales: 0); // Mixed (unmeasured)
    Assert.Equal(ConfidenceTier.Mixed, BoardConfidence.BaseTier(e));
    Assert.Equal(ConfidenceTier.Mixed, BoardConfidence.Tier(e, overrideCount: 99));
  }

  // ---- Which overrides are doctrine evidence (Drift's 07-19 ruling) ----

  [Fact]
  public void CountsTowardDemotion_MarketBoundaryCrossingsCount()
  {
    // "The router undervalues things" / "overvalues them" - disagreements no
    // standing rule explains. Both directions count.
    Assert.True(BoardConfidence.CountsTowardDemotion("Gc", "List"));
    Assert.True(BoardConfidence.CountsTowardDemotion("Desynth", "List"));
    Assert.True(BoardConfidence.CountsTowardDemotion("Vendor", "Reprice"));
    Assert.True(BoardConfidence.CountsTowardDemotion("List", "Gc"));
    Assert.True(BoardConfidence.CountsTowardDemotion("List", "Vendor"));
  }

  [Fact]
  public void CountsTowardDemotion_OffMarketReshufflesAreStandingRuleApplications()
  {
    // The Choker+Cesti case: Gc->Melt for the skillup ladder is the value
    // hierarchy the router already encodes - a higher bidder, not an
    // indictment. Must not demote the class.
    Assert.False(BoardConfidence.CountsTowardDemotion("Gc", "Desynth"));
    Assert.False(BoardConfidence.CountsTowardDemotion("Desynth", "Gc"));
    Assert.False(BoardConfidence.CountsTowardDemotion("Gc", "Vendor"));
    Assert.False(BoardConfidence.CountsTowardDemotion("Vendor", "Desynth"));
  }

  [Fact]
  public void CountsTowardDemotion_ConfirmationsNeverCount()
  {
    Assert.False(BoardConfidence.CountsTowardDemotion("List", "List"));
    Assert.False(BoardConfidence.CountsTowardDemotion("Gc", "Gc"));
  }

  [Fact]
  public void CountsTowardDemotion_OnMarketReshufflesAreAlsoExempt()
  {
    // List<->Reprice keeps the item on the board - a pricing mechanics call,
    // not a market-boundary disagreement.
    Assert.False(BoardConfidence.CountsTowardDemotion("List", "Reprice"));
    Assert.False(BoardConfidence.CountsTowardDemotion("Reprice", "List"));
  }

  // ---- Assent clears dissent (Drift's 07-19 ruling, evening session) ----

  [Fact]
  public void AssentAction_MapsEachExitToItsOwnExecutor()
  {
    Assert.Equal("TurnedIn", BoardConfidence.AssentAction("Gc"));
    Assert.Equal("Desynthed", BoardConfidence.AssentAction("Desynth"));
    Assert.Equal("Listed", BoardConfidence.AssentAction("List"));
    Assert.Equal("Vendored", BoardConfidence.AssentAction("Vendor"));
    Assert.Null(BoardConfidence.AssentAction("Review"));
  }

  [Fact]
  public void LastAssentByClass_ExecutingTheRoutersOwnCallVouchesForTheClassAndItsGate()
  {
    var last = BoardConfidence.LastAssentByClass(new[]
    {
      ("Gc", "TurnedIn", 1000L),
    });
    Assert.Equal(1000L, last["Gc"]);
    Assert.Equal(1000L, last["GateGc"]); // trusting the turn-in IS trusting the gate
    Assert.False(last.ContainsKey("List"));
  }

  [Fact]
  public void LastAssentByClass_ExecutingADifferentExitVouchesForNothing()
  {
    // A router-Gc item the player melted: a reshuffle, not a vote of trust.
    var last = BoardConfidence.LastAssentByClass(new[]
    {
      ("Gc", "Desynthed", 1000L),
    });
    Assert.Empty(last);
  }

  [Fact]
  public void LastAssentByClass_NewestAssentWins()
  {
    var last = BoardConfidence.LastAssentByClass(new[]
    {
      ("Gc", "TurnedIn", 1000L),
      ("Gc", "TurnedIn", 2000L),
    });
    Assert.Equal(2000L, last["Gc"]);
  }

  [Fact]
  public void CrossingStands_AssentForgivesEverythingBeforeIt()
  {
    // The 78-click case: two boundary crossings, then a turn-in run executed
    // without overrides. Both crossings are cleared; the class re-earns
    // Unanimous by the player's own hands on the bell.
    var last = BoardConfidence.LastAssentByClass(new[] { ("Gc", "TurnedIn", 5000L) });
    Assert.False(BoardConfidence.CrossingStands(4000L, last, "Gc"));
    Assert.False(BoardConfidence.CrossingStands(4999L, last, "Gc"));
    Assert.True(BoardConfidence.CrossingStands(5001L, last, "Gc")); // dissent AFTER the assent stands
  }

  [Fact]
  public void CrossingStands_ClassWithNoAssentKeepsItsCrossings()
  {
    var empty = new Dictionary<string, long>(StringComparer.Ordinal);
    Assert.True(BoardConfidence.CrossingStands(1L, empty, "Gc"));
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
    var bulk = BoardConfidence.BulkSet(rows);
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
    Assert.Empty(BoardConfidence.BulkSet(rows));
  }

  [Fact]
  public void BulkSet_PlayerResolution_ConfirmsAnyTier()
  {
    // A player ruling makes the row confirmable regardless of tier (the Green
    // Beret fix); untouched rows still gate on Unanimous.
    var rows = new (string, ConfidenceTier, bool)[]
    {
      ("unanimous", ConfidenceTier.Unanimous, false),
      ("mixed-untouched", ConfidenceTier.Mixed, false),
      ("contra-ruled", ConfidenceTier.Contradicted, true),
      ("mixed-ruled", ConfidenceTier.Mixed, true),
    };
    Assert.Equal(new[] { "unanimous", "contra-ruled", "mixed-ruled" },
      BoardConfidence.BulkSet(rows));
  }

  [Fact]
  public void Effective_PlayerResolution_BeatsContradictedDemotion()
  {
    // Untouched Contradicted demotes to Review; once the player rules, the row
    // goes where they put it - otherwise a Contradicted row is stuck forever.
    Assert.Equal(BoardPile.Review,
      BoardPiles.Effective(BoardPile.PullAndVendor, ConfidenceTier.Contradicted));
    Assert.Equal(BoardPile.PullAndVendor,
      BoardPiles.Effective(BoardPile.PullAndVendor, ConfidenceTier.Contradicted, playerResolved: true));
    // Resolution is a no-op on tiers that never demoted.
    Assert.Equal(BoardPile.List,
      BoardPiles.Effective(BoardPile.List, ConfidenceTier.Unanimous, playerResolved: true));
  }

  // ---- Pile assignment: verdict -> pile ----

  [Fact]
  public void RoutingExit_MapsToPile()
  {
    Assert.Equal(BoardPile.List, BoardPiles.ForRoutingExit(RoutingExit.List, false));
    Assert.Equal(BoardPile.PullAndVendor, BoardPiles.ForRoutingExit(RoutingExit.Vendor, false));
    Assert.Equal(BoardPile.Melt, BoardPiles.ForRoutingExit(RoutingExit.Desynth, false));
    Assert.Equal(BoardPile.Churn, BoardPiles.ForRoutingExit(RoutingExit.Gc, false));
    // The SILENT board (08-06): a protected hold and an observed ban are
    // confident verdicts not to engage, and no pile draws them.
    Assert.Equal(BoardPile.Silent, BoardPiles.ForRoutingExit(RoutingExit.Hold, false));
    Assert.Equal(BoardPile.Silent, BoardPiles.ForRoutingExit(RoutingExit.Ban, false));
    // IsReview always wins, whatever the exit.
    Assert.Equal(BoardPile.Review, BoardPiles.ForRoutingExit(RoutingExit.List, true));
    Assert.Equal(BoardPile.Review, BoardPiles.ForRoutingExit(RoutingExit.Vendor, true));
  }

  [Fact]
  public void StandingResult_MapsToPile()
  {
    Assert.Equal(BoardPile.Reprice, BoardPiles.ForStanding(PricingResult.CapBlocked));
    // The pinch-side lane_held rehome: keeping the ask IS the act, so it
    // defers (labeling only) rather than sitting in a settled roll-up.
    Assert.Equal(BoardPile.Defer, BoardPiles.ForStanding(PricingResult.LaneHeld));
    Assert.Equal(BoardPile.PullAndVendor, BoardPiles.ForStanding(PricingResult.BelowFloor));
    Assert.Equal(BoardPile.Review, BoardPiles.ForStanding(PricingResult.NoData));
  }

  // ---- Merged two-reason WorkItems ----

  [Fact]
  public void Merge_TwoReasons_ReviewWins()
  {
    // An item flagged both reprice-worthy AND needs-eyes is ONE Review row.
    var merged = BoardPiles.Merge(new[] { BoardPile.Reprice, BoardPile.Review });
    Assert.Equal(BoardPile.Review, merged);
  }

  [Fact]
  public void Merge_RepriceVsPullAndVendor_PullWins()
  {
    // Below-floor (pull/vendor) beats a cap-block (reprice): if it is worthless,
    // fixing its price is moot.
    var merged = BoardPiles.Merge(new[] { BoardPile.Reprice, BoardPile.PullAndVendor });
    Assert.Equal(BoardPile.PullAndVendor, merged);
  }

  [Fact]
  public void Merge_SingleReason_IsThatReason()
  {
    Assert.Equal(BoardPile.Defer, BoardPiles.Merge(new[] { BoardPile.Defer }));
  }

  [Fact]
  public void Merge_Empty_DefaultsToReview()
  {
    Assert.Equal(BoardPile.Review, BoardPiles.Merge(System.Array.Empty<BoardPile>()));
  }

  // The Watch pile's count-summary tests are DELETED, not adapted: they
  // covered a roll-up ("14 watching: 3 races, 8 slow sellers, 3 bait") for a
  // pile where nothing ever happened. Defer draws rows in the board's own
  // table and its header does the same arithmetic every other pile's does, so
  // there is no second format left to test. See DeferPileTests for what
  // replaced the behavior.

  // ---- The bulk gate's third door (Defer, 08-06) ----

  [Fact]
  public void Rides_DeferredRow_RidesWithoutBeingUnanimous()
  {
    // The headliner's structural claim, in one assertion: a Mixed row that
    // defers is spent by the round exactly as if it were Unanimous. Without
    // this, Defer would name its doubt and then quietly do nothing.
    Assert.True(BoardConfidence.Rides(ConfidenceTier.Mixed, playerResolved: false, deferred: true));
    Assert.False(BoardConfidence.Rides(ConfidenceTier.Mixed, playerResolved: false, deferred: false));
  }

  [Fact]
  public void BulkSet_TakesDeferredRows_AlongsideUnanimousAndRuled()
  {
    var rows = new[]
    {
      ("unanimous", ConfidenceTier.Unanimous, false, false),
      ("ruled", ConfidenceTier.Contradicted, true, false),
      ("deferred", ConfidenceTier.Mixed, false, true),
      ("waiting", ConfidenceTier.Mixed, false, false),
    };
    var bulk = BoardConfidence.BulkSet(rows);
    Assert.Equal(new[] { "unanimous", "ruled", "deferred" }, bulk);
  }
}
