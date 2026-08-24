using System.Collections.Generic;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// Tests for the Listed-item pure core (M6 session 3): per-retainer grouping,
/// honest age labels, outlier own-listing detection (the Highland Fence smell),
/// vendor-floor pull-forward, doubt-branch classification, and the Contradicted-row
/// objection note. All render from captured data - no game reads anywhere.
/// </summary>
public class BoardListingsTests
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
    var groups = BoardListings.GroupByRetainer(new[]
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
    var groups = BoardListings.GroupByRetainer(new[] { Line(price: 500, qty: 0) });
    Assert.Equal(500, groups[0].GilAtAsk);
  }

  // ---- Item 2: honest age labels ----

  [Fact]
  public void AgeDays_WholeDaysSinceFirstSeen()
  {
    Assert.Equal(3, BoardListings.AgeDays(firstSeen: 0, now: 3 * 86400 + 500));
    Assert.Equal(0, BoardListings.AgeDays(firstSeen: 100, now: 50)); // never negative
  }

  [Fact]
  public void Tier_BandsByConfig()
  {
    var cfg = new ListedAgeConfig { AgingDays = 7, StaleDays = 30 };
    Assert.Equal(ListedAgeTier.Fresh, BoardListings.Tier(6, cfg));
    Assert.Equal(ListedAgeTier.Aging, BoardListings.Tier(7, cfg));
    Assert.Equal(ListedAgeTier.Aging, BoardListings.Tier(29, cfg));
    Assert.Equal(ListedAgeTier.Stale, BoardListings.Tier(30, cfg));
  }

  [Fact]
  public void AgeLabel_ExactVsLowerBound()
  {
    Assert.Equal("3d listed", BoardListings.AgeLabel(3, exact: true));
    Assert.Equal(">=3d listed", BoardListings.AgeLabel(3, exact: false));
  }

  [Fact]
  public void AgeIsExact_LowerBoundWhenPresentAtFirstObservation()
  {
    // first_seen at or before our first-ever scan = we can't know the true start.
    Assert.False(BoardListings.AgeIsExact(firstSeen: 100, firstObservationEver: 100));
    Assert.False(BoardListings.AgeIsExact(firstSeen: 90, firstObservationEver: 100));
    // Seen strictly after we started observing = an exact age.
    Assert.True(BoardListings.AgeIsExact(firstSeen: 200, firstObservationEver: 100));
    // No observation history known = treat as exact (nothing to bound against).
    Assert.True(BoardListings.AgeIsExact(firstSeen: 200, firstObservationEver: 0));
  }

  // ---- Item 3: outlier own-listing detection ----

  [Fact]
  public void IsOutlierListing_AtOrAboveWallBoundary_IsOutlier()
  {
    var cfg = new LaneConfig(); // CeilingMult 3.0, MinHistorySamples 3
    // Lane median 1000, wall boundary 3000.
    Assert.True(BoardListings.IsOutlierListing(3000, Lane(1000), cfg));
    Assert.True(BoardListings.IsOutlierListing(55_000_000, Lane(1000), cfg));
    Assert.False(BoardListings.IsOutlierListing(2999, Lane(1000), cfg));
  }

  [Fact]
  public void IsOutlierListing_ThinLane_NeverOutlier()
  {
    // A thin lane can't say a price is an outlier - no invented certainty.
    var cfg = new LaneConfig();
    Assert.False(BoardListings.IsOutlierListing(999_999, Lane(1000, n: 2), cfg));
  }

  [Fact]
  public void OutlierReason_NamesWhereTheTapeSettledAndHowFarAboveTheAskSits()
  {
    // The specimen, verbatim. "the going rate" is retired: the tape does not name
    // a price the item is owed, it names where sales settled. The multiple stays -
    // it is distance from that group, which is what makes 55M a self-inflicted wall.
    var r = BoardListings.OutlierReason(55_000_000, Lane(1000));
    Assert.Equal("Listed at 55,000,000; the tape settles around 1,000 - 55000x above it. "
               + "It has been sitting because nothing looked at it. Pull and reprice, or vendor.", r);
    Assert.DoesNotContain("going rate", r);
  }

  // ---- Item 4: vendor-floor pull-forward ----

  [Fact]
  public void VendorBeatsBoard_LaneAtOrBelowVendor()
  {
    Assert.True(BoardListings.VendorBeatsBoard(lanePrice: 100, vendorPrice: 100));
    Assert.True(BoardListings.VendorBeatsBoard(lanePrice: 80, vendorPrice: 100));
    Assert.False(BoardListings.VendorBeatsBoard(lanePrice: 101, vendorPrice: 100));
    // No vendor price = nothing to pull forward to.
    Assert.False(BoardListings.VendorBeatsBoard(lanePrice: 5, vendorPrice: 0));
  }

  [Fact]
  public void VendorFloorReason_StatesBothPrices()
  {
    // The specimen, verbatim. "the board only clears ~X" claimed a settlement the
    // operand cannot support - lanePrice is where the line stands, not a clearing
    // this listing was promised.
    var r = BoardListings.VendorFloorReason(lanePrice: 80, vendorPrice: 100);
    Assert.Equal("The vendor pays 100, the line stands at ~80 - "
               + "never keep listing what the vendor beats. Pull and vendor.", r);
    Assert.DoesNotContain("clears", r);
  }

  // ---- The doubt branch a HELD flag acted through ----
  // (Replaces the Watch categorizer. The old buckets - races / slow sellers /
  // bait - described why a row was being looked at; a doubt branch describes
  // what the walk could not settle, which is the thing worth counting.)

  [Fact]
  public void DoubtOfFlagReason_ReadsTheBranchOffTheFlagClass()
  {
    // A lane_held flag IS the spine's genuine-silence branch, persisted.
    Assert.Equal(DoubtBranch.NoTape, BoardListings.DoubtOfFlagReason("lane_held"));
    // The pre-A10 spelling of "the whole board sits below the band" - the shape
    // the dead-heat test replaced. Historical rows keep their strings forever.
    Assert.Equal(DoubtBranch.DeadHeat, BoardListings.DoubtOfFlagReason("race_declined"));
  }

  [Fact]
  public void DoubtOfFlagReason_RuleFiringsAreNotDoubts()
  {
    // A cap-block or a step-over is a rule working exactly as written. Folding
    // those into a branch would dilute every tally the tape ever reports - and
    // the tape is the whole reason the branch is stable vocabulary.
    Assert.Equal(DoubtBranch.None, BoardListings.DoubtOfFlagReason("wall_ignored"));
    Assert.Equal(DoubtBranch.None, BoardListings.DoubtOfFlagReason("outlier_warn"));
    Assert.Equal(DoubtBranch.None, BoardListings.DoubtOfFlagReason("banned"));
    Assert.Equal(DoubtBranch.None, BoardListings.DoubtOfFlagReason(""));
  }

  // ---- Item 8 addendum: the Contradicted row states its objection ----

  [Fact]
  public void ContradictionNote_StatesDcPriceSalesAndVelocity()
  {
    var note = BoardListings.ContradictionNote(median: 98, sales: 13, velocityPerDay: 0.9);
    Assert.Contains("the DC pays ~98 on 13 sales", note);
    Assert.Contains("moves ~0.9/day", note);
    Assert.StartsWith("...but", note);
  }

  [Fact]
  public void ContradictionNote_PayerNamesLocalProvenanceHonestly()
    => Assert.Contains("settled sales pay ~98 on 2 sales",
      BoardListings.ContradictionNote(98, 2, null, payer: "settled sales pay"));

  [Fact]
  public void ContradictionNote_SingularSale()
    => Assert.Contains("on 1 sale.", BoardListings.ContradictionNote(98, 1, null));

  [Fact]
  public void ContradictionNote_NoMarketEvidence_IsEmpty()
  {
    Assert.Equal("", BoardListings.ContradictionNote(null, 0, null));
    Assert.Equal("", BoardListings.ContradictionNote(98, 0, 0)); // no sales, no velocity
  }

  // ---- The next-round note: the slow mover's forward tense ----

  [Fact]
  public void NextRoundNote_CutStatesPriceAndSignedDelta()
    => Assert.Equal("2,254 (-8%)", BoardListings.NextRoundNote(2450, 2254));

  [Fact]
  public void NextRoundNote_RaiseCarriesAPlus()
    => Assert.Equal("1,100 (+10%)", BoardListings.NextRoundNote(1000, 1100));

  [Fact]
  public void NextRoundNote_NoPreview_IsEmpty()
  {
    Assert.Equal("", BoardListings.NextRoundNote(2450, null));
    Assert.Equal("", BoardListings.NextRoundNote(2450, 0));
    Assert.Equal("", BoardListings.NextRoundNote(2450, -5));
  }

  [Fact]
  public void NextRoundNote_SamePrice_HoldsInsteadOfZeroPercent()
  {
    Assert.Equal("holds", BoardListings.NextRoundNote(2450, 2450));
    Assert.DoesNotContain("0%", BoardListings.NextRoundNote(2450, 2450));
  }

  [Fact]
  public void NextRoundNote_SubPercentMoveKeepsItsDecimal()
    => Assert.Equal("99,700 (-0.3%)", BoardListings.NextRoundNote(100000, 99700));

  [Fact]
  public void NextRoundNote_MoveTooSmallToRoundStillSaysItMoved()
    => Assert.Equal("999,600 (-<0.1%)", BoardListings.NextRoundNote(1000000, 999600));

  [Fact]
  public void NextRoundNote_UnknownAsk_ShowsThePriceWithoutAFabricatedDelta()
    => Assert.Equal("2,254", BoardListings.NextRoundNote(0, 2254));

  // ---- Melt beats the ask (Drift's ruling, 2026-08-06) ----

  [Fact]
  public void MeltBeatsAsk_FiresOnMeasuredMeltOverTheStandingAsk()
  {
    // The Bread Rack: standing at 600, own desynths return ~1,064 an attempt,
    // and nothing on any surface said so.
    Assert.True(BoardListings.MeltBeatsAsk(600, 1_064, MeltGrade.Measured));
  }

  [Fact]
  public void MeltBeatsAsk_NeverFiresOnAPrior()
  {
    // "A prior is a rumor, not evidence" - the band average is a fact about
    // gear of this weight, and a flag on it would invite a pull against a
    // measurement nobody made. Same numbers, no flag.
    Assert.False(BoardListings.MeltBeatsAsk(600, 1_064, MeltGrade.Prior));
    Assert.False(BoardListings.MeltBeatsAsk(600, 1_064, MeltGrade.Skillup));
    Assert.False(BoardListings.MeltBeatsAsk(600, 1_064, MeltGrade.None));
  }

  [Fact]
  public void MeltBeatsAsk_NeedsBothNumbers_AndNeedsMeltToActuallyWin()
  {
    Assert.False(BoardListings.MeltBeatsAsk(600, null, MeltGrade.Measured));
    Assert.False(BoardListings.MeltBeatsAsk(0, 1_064, MeltGrade.Measured));   // no ask on record
    Assert.False(BoardListings.MeltBeatsAsk(1_064, 1_064, MeltGrade.Measured)); // a tie is not a beat
    Assert.False(BoardListings.MeltBeatsAsk(2_000, 1_064, MeltGrade.Measured));
  }

  [Fact]
  public void ADismissalIsAnAnswer_AndTheSameAskIsNotReAsked()
  {
    // Dismissing books a receipt and re-affirms the ask. Raising it again on
    // the next refresh - seconds later - would make the dismissal meaningless.
    Assert.False(BoardListings.ShouldRaiseMeltContest(600, 1_064, MeltGrade.Measured, dismissedAtAsk: 600));
    // A repriced ask is a different question about a different price.
    Assert.True(BoardListings.ShouldRaiseMeltContest(700, 1_064, MeltGrade.Measured, dismissedAtAsk: 600));
    // Never answered at all: ask it.
    Assert.True(BoardListings.ShouldRaiseMeltContest(600, 1_064, MeltGrade.Measured, dismissedAtAsk: null));
  }

  // ---- The raiser closes its own (V36 disease prevention) ----

  private static readonly (uint, bool, string) BreadRack = (100u, false, "Karen");

  private static Dictionary<(uint ItemId, bool IsHq, string Retainer), BoardListings.MeltContestLane>
    Standing(long ask, long? melt, MeltGrade grade = MeltGrade.Measured)
    => new() { [BreadRack] = new BoardListings.MeltContestLane(ask, melt, grade) };

  private static (long, (uint, bool, string))[] OpenFlag(long id = 7)
    => new[] { (id, BreadRack) };

  [Fact]
  public void AnOpenContest_StaysOpenWhileItsConditionHolds()
  {
    // Still standing at 600 under a measured 1,064: the question is live and
    // the flag has not been answered. Closing it would be losing the finding.
    Assert.Empty(BoardListings.MeltContestsToClose(OpenFlag(), Standing(600, 1_064)));
  }

  [Fact]
  public void ARepricedAsk_ClosesTheContestItAnswered()
  {
    // The ask moved above the melt - by a round, by hand, or because a new
    // desynth reading moved the melt. Nothing here is true anymore, and the
    // detail text still says "standing at 600". Self-heal cannot reach this
    // class, so if this did not close it, nothing ever would.
    Assert.Equal(new long[] { 7 },
      BoardListings.MeltContestsToClose(OpenFlag(), Standing(2_000, 1_064)));
  }

  [Fact]
  public void ALaneThatLeftTheBoard_ClosesItsContest()
  {
    // Sold, pulled, or expired: the listing this flag is about does not exist.
    // This is the V36 immortal Watch tenant exactly - a flag whose subject is
    // gone and which no round can prove anything about.
    var gone = new Dictionary<(uint ItemId, bool IsHq, string Retainer), BoardListings.MeltContestLane>();
    Assert.Equal(new long[] { 7 }, BoardListings.MeltContestsToClose(OpenFlag(), gone));
  }

  [Fact]
  public void AMeltThatStoppedBeingMeasured_ClosesTheContest()
  {
    // The grade is the whole predicate. A melt score that is now a band prior
    // (or gone) can no longer justify a flag only a measurement was allowed to
    // raise - the close is symmetric with the raise by construction.
    Assert.Equal(new long[] { 7 },
      BoardListings.MeltContestsToClose(OpenFlag(), Standing(600, 1_064, MeltGrade.Prior)));
    Assert.Equal(new long[] { 7 },
      BoardListings.MeltContestsToClose(OpenFlag(), Standing(600, null)));
  }

  [Fact]
  public void ClosingDoesNotTouchTheDismissalMemory()
  {
    // Two mechanisms, two questions. The close asks "does the condition still
    // hold"; the dismissal asks "has he already answered this ask". A closed
    // contest on a repriced lane must still re-arm at the new price.
    Assert.Equal(new long[] { 7 },
      BoardListings.MeltContestsToClose(OpenFlag(), Standing(2_000, 1_064)));
    Assert.True(BoardListings.ShouldRaiseMeltContest(700, 1_064, MeltGrade.Measured, dismissedAtAsk: 600));
    Assert.False(BoardListings.ShouldRaiseMeltContest(600, 1_064, MeltGrade.Measured, dismissedAtAsk: 600));
  }

  [Fact]
  public void EachFlagIsJudgedOnItsOwnLane()
  {
    // Two retainers holding the same item: one repriced, one not. A close pass
    // that keyed on the item would take both.
    var flags = new[] { (7L, BreadRack), (8L, (100u, false, "Dave")) };
    var standing = new Dictionary<(uint ItemId, bool IsHq, string Retainer), BoardListings.MeltContestLane>
    {
      [BreadRack] = new(600, 1_064, MeltGrade.Measured),
      [(100u, false, "Dave")] = new(2_000, 1_064, MeltGrade.Measured),
    };
    Assert.Equal(new long[] { 8 }, BoardListings.MeltContestsToClose(flags, standing));
  }

  [Fact]
  public void TheFlagStatesBothNumbers_AndActsOnNeither()
  {
    var detail = BoardListings.MeltBeatsAskDetail(600, 1_064);
    Assert.Contains("600", detail);
    Assert.Contains("1,064", detail);
    Assert.Contains("your call", detail);
    // The advisor advises. No sentence here decides anything for anyone.
    Assert.DoesNotContain("Pulling", detail);
    Assert.DoesNotContain("will ", detail);
  }
}
