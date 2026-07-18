using Xunit;
using static Scrooge.TriageMemory;

namespace Scrooge.Tests;

/// <summary>
/// Decision-memory core: self-heal (a rule that stops firing closes its flag)
/// and the evidence key (unchanged evidence never re-asks). The four evidence
/// cases from the M2 spec appear as named cases; MinHistorySamples is 3
/// throughout, matching the lane default.
/// </summary>
public class TriageMemoryTests
{
  private const int MinSamples = 3;

  // A held (item, retainer) with a single open thin-history flag.
  private static (long Id, string Reason)[] Flags(params string[] reasons)
  {
    var arr = new (long, string)[reasons.Length];
    for (var i = 0; i < reasons.Length; i++)
      arr[i] = (i + 1, reasons[i]);
    return arr;
  }

  private static System.Collections.Generic.HashSet<string> Raised(params string[] reasons)
    => new(reasons);

  // --- Self-heal --------------------------------------------------------

  [Fact]
  public void SelfHeal_ClosesFlag_WhenRuleDoesNotFireAgain()
  {
    // lane_held was raised last pass; this pass the item priced fine (no
    // reason fired). The stale flag heals.
    var close = FlagsToClose(Flags("lane_held"), Raised(/* nothing fired */));
    Assert.Equal(new long[] { 1 }, close);
  }

  [Fact]
  public void SelfHeal_KeepsFlag_WhenSameRuleFiresAgain()
  {
    // Still thin this pass — lane_held fires again, so its flag stays open.
    var close = FlagsToClose(Flags("lane_held"), Raised("lane_held"));
    Assert.Empty(close);
  }

  [Fact]
  public void SelfHeal_ClosesOnlyTheReasonsThatStoppedFiring()
  {
    // Two open flags; only slow_evict re-fires. lane_held heals, slow_evict stays.
    var close = FlagsToClose(Flags("lane_held", "slow_evict"), Raised("slow_evict"));
    Assert.Equal(new long[] { 1 }, close);
  }

  [Fact]
  public void SelfHeal_ClosesLegacyDeadProducerFlags()
  {
    // upward_held / outlier_warn lost their producers in the lane rewrite —
    // nothing can re-raise them, so processing their item always sweeps them,
    // even when a live rule (lane_held) fires on the same pass.
    var close = FlagsToClose(Flags("upward_held", "outlier_warn", "lane_held"), Raised("lane_held"));
    Assert.Equal(new long[] { 1, 2 }, close);
  }

  [Fact]
  public void DeadReasons_AreTheLaneRewriteCasualties()
  {
    Assert.Contains("upward_held", DeadReasons);
    Assert.Contains("outlier_warn", DeadReasons);
    Assert.DoesNotContain("lane_held", DeadReasons);
  }

  // --- Evidence snapshot round-trip -------------------------------------

  [Fact]
  public void Evidence_RoundTripsThroughSerialization()
  {
    var snap = new EvidenceSnapshot(1234, 2, 1_800_000_000, 999);
    var parsed = EvidenceSnapshot.TryParse(snap.Serialize());
    Assert.Equal(snap, parsed);
  }

  [Theory]
  [InlineData("")]
  [InlineData(null)]
  [InlineData("garbage")]
  [InlineData("L1|n2|s3")] // too few fields
  public void Evidence_TryParse_ReturnsNullOnJunk(string? junk)
    => Assert.Null(EvidenceSnapshot.TryParse(junk));

  // --- The four evidence cases ------------------------------------------

  [Fact]
  public void Case4_NothingChanged_IsSilent()
  {
    // Same world, same question. Don't churn the row / reset the clock.
    var snap = new EvidenceSnapshot(500, 2, 1000, 0);
    Assert.Equal(EvidenceChange.Unchanged, Classify(snap, snap, MinSamples));
    Assert.Equal(FlagAction.Silent, DecideUpsert(snap.Serialize(), snap, MinSamples));
  }

  [Fact]
  public void Case1_ManualPrice_IsSilent()
  {
    // The player repriced by hand: only ListingPrice moved. The live listing
    // is the standing anchor now — the decision is recorded, don't re-ask.
    var prior = new EvidenceSnapshot(500, 2, 1000, 0);
    var now = prior with { ListingPrice = 750 };
    Assert.Equal(EvidenceChange.ManualPrice, Classify(prior, now, MinSamples));
    Assert.Equal(FlagAction.Silent, DecideUpsert(prior.Serialize(), now, MinSamples));
  }

  [Fact]
  public void Case2_UndercutSinceDecision_IsNoFlag()
  {
    // A competitor undercut since the hold. Ordinary lane pinch follows the
    // live competitor (guards cap extremes) — nothing new to ask the player.
    var prior = new EvidenceSnapshot(500, 2, 1000, 600);
    var now = prior with { CheapestCompetitor = 480 };
    Assert.Equal(EvidenceChange.Undercut, Classify(prior, now, MinSamples));
    Assert.Equal(FlagAction.Silent, DecideUpsert(prior.Serialize(), now, MinSamples));
  }

  [Fact]
  public void Case2_FirstCompetitorAppearing_IsUndercut()
  {
    // Empty board before, a listing now: still an undercut probe, lane follows.
    var prior = new EvidenceSnapshot(500, 2, 1000, 0);
    var now = prior with { CheapestCompetitor = 700 };
    Assert.Equal(EvidenceChange.Undercut, Classify(prior, now, MinSamples));
  }

  [Fact]
  public void Case3a_NewSalesCrossMinSamples_Resolves()
  {
    // History grew from 2 to 3 (== MinHistorySamples): the lane can price it
    // now. Resolved case — the flag will heal (item prices, no re-hold).
    var prior = new EvidenceSnapshot(500, 2, 1000, 0);
    var now = prior with { SaleCount = 3, LatestSaleUnix = 2000 };
    Assert.Equal(EvidenceChange.NewSalesResolved, Classify(prior, now, MinSamples));
  }

  [Fact]
  public void Case3b_NewSalesContradict_ReFlags()
  {
    // A new sale arrived but the lane is still thin (2 samples). The world
    // moved under an open question — legitimately re-ask.
    var prior = new EvidenceSnapshot(500, 1, 1000, 0);
    var now = prior with { SaleCount = 2, LatestSaleUnix = 2000 };
    Assert.Equal(EvidenceChange.NewSalesContradict, Classify(prior, now, MinSamples));
    Assert.Equal(FlagAction.Refresh, DecideUpsert(prior.Serialize(), now, MinSamples));
  }

  [Fact]
  public void NewSale_WithoutCountChange_StillCountsAsGrowth()
  {
    // The 14-day window can hold count flat while a fresh sale rolls in and an
    // old one rolls out — a newer LatestSaleUnix alone is history moving.
    var prior = new EvidenceSnapshot(500, 2, 1000, 0);
    var now = prior with { LatestSaleUnix = 5000 };
    Assert.Equal(EvidenceChange.NewSalesContradict, Classify(prior, now, MinSamples));
  }

  [Fact]
  public void DecideUpsert_InsertsOnlyWhenNoOpenRowExists()
  {
    // Null is the storage layer's "no open row" signal — the only Insert case.
    var snap = new EvidenceSnapshot(500, 2, 1000, 0);
    Assert.Equal(FlagAction.Insert, DecideUpsert(null, snap, MinSamples));
  }

  [Theory]
  [InlineData("")]          // pre-V17 legacy row (evidence column defaulted '')
  [InlineData("corrupt")]   // garbage
  [InlineData("L1|n2|s3")]  // too few fields
  public void DecideUpsert_AdoptsExistingRowWhenEvidenceUnparseable(string stored)
  {
    // A non-null stored value means an open row EXISTS, snapshot or not.
    // Refresh adopts it (stamping real evidence in); Insert here filed the
    // same question twice — the duplicate-flag bug the 07-16 soak caught.
    var snap = new EvidenceSnapshot(500, 2, 1000, 0);
    Assert.Equal(FlagAction.Refresh, DecideUpsert(stored, snap, MinSamples));
  }

  // --- Zombie flag heal (M4) --------------------------------------------
  // A lane_held flag whose item left the retainer never gets re-processed, so the
  // self-heal sweep never touches it. The zombie sweep closes it - but ONLY when
  // the run genuinely observed the flag's own container and the item was absent.

  private const uint Molybdenum = 19947; // the live inventory-flag trap
  private const uint LuncheonToad = 12345;

  private static System.Collections.Generic.HashSet<uint> Observed(params uint[] ids)
    => new(ids);

  [Fact]
  public void Zombie_ClosesBoardFlag_WhenPinchSawTheListingsAndItemAbsent()
  {
    // Pinch (board) run walked the retainer's listings; the flagged item wasn't
    // among them. Its container was observed, the item is gone -> close as item_gone.
    var flags = new[] { new ZombieFlagRow(91, LuncheonToad, FlagScope.Board) };
    var close = ZombieFlagsToClose(FlagScope.Board, Observed(111, 222), flags);
    Assert.Equal(new long[] { 91 }, close);
  }

  [Fact]
  public void Zombie_DoesNotClose_WhenItemStillPresent_MerelySkipped()
  {
    // The item was still in the sell list (mannequin/bound/banned -> skipped, not
    // evaluated). Present in the observed container -> leave the flag open.
    var flags = new[] { new ZombieFlagRow(91, LuncheonToad, FlagScope.Board) };
    var close = ZombieFlagsToClose(FlagScope.Board, Observed(LuncheonToad, 222), flags);
    Assert.Empty(close);
  }

  [Fact]
  public void Zombie_DoesNotClose_InventoryFlag_FromAPinchThatOnlySawTheBoard()
  {
    // The Molybdenum-in-inventory case: a Hawk-raised flag points at an unlisted
    // item in the sell inventory. A pinch run only walks the board, never that
    // container, so it must NOT close the flag even though it didn't see the item.
    var flags = new[] { new ZombieFlagRow(81, Molybdenum, FlagScope.Inventory) };
    var close = ZombieFlagsToClose(FlagScope.Board, Observed(111, 222), flags);
    Assert.Empty(close);
  }

  [Fact]
  public void Zombie_ClosesInventoryFlag_WhenHawkSawInventoryAndItemAbsent()
  {
    // The matching run type (Hawk observes inventory) DID walk that container and
    // the item is gone -> close is now provable.
    var flags = new[] { new ZombieFlagRow(81, Molybdenum, FlagScope.Inventory) };
    var close = ZombieFlagsToClose(FlagScope.Inventory, Observed(555), flags);
    Assert.Equal(new long[] { 81 }, close);
  }

  [Fact]
  public void Zombie_NeverClosesUnknownScope_LegacyFlagsUnprovable()
  {
    // A pre-M4 flag has no recorded container; absence is unprovable -> leave open.
    var flags = new[] { new ZombieFlagRow(70, LuncheonToad, FlagScope.Unknown) };
    Assert.Empty(ZombieFlagsToClose(FlagScope.Board, Observed(111), flags));
    Assert.Empty(ZombieFlagsToClose(FlagScope.Inventory, Observed(111), flags));
  }

  [Fact]
  public void Zombie_MixedBatch_ClosesOnlyTheProvableOnes()
  {
    var flags = new[]
    {
      new ZombieFlagRow(91, LuncheonToad, FlagScope.Board),      // gone from board -> close
      new ZombieFlagRow(81, Molybdenum, FlagScope.Inventory),    // wrong run type -> open
      new ZombieFlagRow(70, 999, FlagScope.Board),               // still present -> open
      new ZombieFlagRow(60, 888, FlagScope.Unknown),             // unprovable -> open
    };
    var close = ZombieFlagsToClose(FlagScope.Board, Observed(999), flags);
    Assert.Equal(new long[] { 91 }, close);
  }

  [Fact]
  public void Zombie_ScopeTag_RoundTrips()
  {
    Assert.Equal(FlagScope.Board, ParseScope(ScopeTag(FlagScope.Board)));
    Assert.Equal(FlagScope.Inventory, ParseScope(ScopeTag(FlagScope.Inventory)));
    Assert.Equal("", ScopeTag(FlagScope.Unknown));
    Assert.Equal(FlagScope.Unknown, ParseScope(""));
    Assert.Equal(FlagScope.Unknown, ParseScope(null));
  }
}
