using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The one-door bell's composition rule (WALK unit 4). Three things are worth
/// pinning: the router's jurisdiction beats the gate's silence (the whole reason
/// non-gear can join without gear leaking out), gated rows stay off the market,
/// and coffers ARM the stage so a 0-row bell with coffers in the bags is still
/// work. The bag scan and the Hawk run itself are the untested Dalamud plumbing.
/// </summary>
public class BellPlanTests
{
  private static IReadOnlySet<BellVariant> Jurisdiction(params (uint Id, bool Hq)[] variants)
    => variants.Select(v => new BellVariant(v.Id, v.Hq)).ToHashSet();

  [Fact]
  public void Judge_RouterOwnsEveryVariantItEvaluated()
  {
    var mine = Jurisdiction((100, false));
    // Ungated by the gate's own reckoning - and still not the gate's call.
    Assert.Equal(BellExclusion.RouterOwns,
      BellPlan.Judge(new BellVariant(100, false), gated: false, mine));
  }

  [Fact]
  public void Judge_HqIsPartOfTheIdentity()
  {
    var mine = Jurisdiction((100, false));
    // The router ruled on the NQ variant; the HQ one is a different question and
    // falls to the gate like any other bag item.
    Assert.Equal(BellExclusion.Joins,
      BellPlan.Judge(new BellVariant(100, true), gated: false, mine));
  }

  [Fact]
  public void Judge_GatedRowsStayOffTheMarket()
    => Assert.Equal(BellExclusion.Gated,
      BellPlan.Judge(new BellVariant(7, false), gated: true, Jurisdiction()));

  [Fact]
  public void Judge_ACofferIsNeverARow()
  {
    // The rider opens it; the bell lists what came out. Never the coffer itself,
    // even with the rider disarmed.
    Assert.Equal(BellExclusion.RiderOwns,
      BellPlan.Judge(new BellVariant(CofferLogic.VentureCofferItemId, false),
        gated: false, Jurisdiction()));
  }

  [Fact]
  public void Judge_UngatedOutsideJurisdictionJoins()
    => Assert.Equal(BellExclusion.Joins,
      BellPlan.Judge(new BellVariant(7, false), gated: false, Jurisdiction()));

  [Fact]
  public void Judge_JurisdictionIsCheckedBeforeTheGate()
  {
    // A gear row the router owns AND the gate gated: one answer, and it is the
    // router's - the ordering is what stops a Review row sneaking in as "ungated".
    Assert.Equal(BellExclusion.RouterOwns,
      BellPlan.Judge(new BellVariant(5, false), gated: true, Jurisdiction((5, false))));
  }

  [Fact]
  public void Join_KeepsEveryUnownedUngatedRowIncludingRepeatedVariants()
  {
    // Two stacks of the same mat are two listings - the dedupe is by variant
    // against the ROUTER, never against the gate's own rows.
    var candidates = new[]
    {
      ("mat slot 1", new BellVariant(20, false), false),
      ("mat slot 2", new BellVariant(20, false), false),
      ("gated gear", new BellVariant(30, false), true),
      ("routed gear", new BellVariant(40, false), false),
    };
    var joined = BellPlan.Join(candidates, Jurisdiction((40, false)));
    Assert.Equal(new[] { "mat slot 1", "mat slot 2" }, joined);
  }

  [Fact]
  public void Join_AVariantTheRouterOwnsTakesAllItsSlotsWithIt()
  {
    var candidates = new[]
    {
      ("gear slot 1", new BellVariant(40, false), false),
      ("gear slot 2", new BellVariant(40, false), false),
    };
    Assert.Empty(BellPlan.Join(candidates, Jurisdiction((40, false))));
  }

  [Fact]
  public void Tally_CountsBothHalvesOfTheOneDoor()
  {
    var tally = new BellTally(RoutedList: 3, RoutedVendor: 1, GateList: 48, GateVendor: 2);
    Assert.Equal(51, tally.ListCount);
    Assert.Equal(3, tally.VendorCount);
    Assert.Equal(54, tally.Total);
    Assert.Equal("51 list + 3 vendor", tally.Breakdown);
  }

  [Fact]
  public void Tally_CarriesTheStandingListingWork_TheBellIsTheRetainerErrand()
  {
    // Drift, 07-25: "'bell' should mean anything that needs a retainer." Reprices
    // and pulls are standing listings - they ride the triage executor rather than
    // the Hawk run, but they happen at the same stop on the same visit, so the
    // button's number has to include them or it is lying about its own errand.
    var tally = new BellTally(RoutedList: 3, RoutedVendor: 0, GateList: 1, GateVendor: 0,
      Reprice: 2, Pull: 1);
    Assert.Equal(4, tally.ListCount);
    Assert.Equal(2, tally.RepriceCount);
    Assert.Equal(1, tally.PullCount);
    Assert.Equal(7, tally.Total);
    Assert.Equal("4 list + 2 reprice + 1 vendor", tally.Breakdown);
  }

  [Fact]
  public void Tally_BreakdownNamesOnlyTheVerbsItHasRowsFor()
  {
    // A listing-only bell must not read as though it is about to reprice zero
    // things, and an empty one says so rather than rendering an empty string.
    Assert.Equal("3 list", new BellTally(3, 0, 0, 0).Breakdown);
    Assert.Equal("2 reprice", new BellTally(0, 0, 0, 0, Reprice: 2).Breakdown);
    Assert.Equal("1 vendor", new BellTally(0, 0, 0, 0, Pull: 1).Breakdown);
    Assert.Equal("nothing", default(BellTally).Breakdown);
  }

  [Fact]
  public void HasWork_RowsAreWork()
  {
    Assert.True(BellPlan.HasWork(new BellTally(1, 0, 0, 0)));
    // Any of the three verbs arms it - a bell with nothing to list but two
    // listings to reprice is still a trip to the bell.
    Assert.True(BellPlan.HasWork(new BellTally(0, 0, 0, 0, Reprice: 2)));
    Assert.True(BellPlan.HasWork(new BellTally(0, 0, 0, 0, Pull: 1)));
    Assert.False(BellPlan.HasWork(default));
  }

  [Fact]
  public void MeltHasWork_CoffersArmAnEmptyMelt()
  {
    // The hole this closes: the stage the coffer rider hangs off counted its own
    // rows only, so a bag full of coffers and nothing to melt never fired the
    // rider at all. (07-25 moved the rider from the bell to the melt with the
    // ruled reorder; the arming rule moved with it.)
    Assert.True(BellPlan.MeltHasWork(meltRows: 0, coffersInBags: 6, riderArmed: true));
    Assert.True(BellPlan.MeltHasWork(meltRows: 4, coffersInBags: 0, riderArmed: true));
  }

  [Fact]
  public void MeltHasWork_ADisarmedRiderDoesNotArmTheMelt()
  {
    // Config escape hatch off: coffers are not this round's business, so an empty
    // melt is genuinely empty and the cursor skips it.
    Assert.False(BellPlan.MeltHasWork(meltRows: 0, coffersInBags: 6, riderArmed: false));
  }

  [Fact]
  public void MeltHasWork_NothingIsNothing()
    => Assert.False(BellPlan.MeltHasWork(meltRows: 0, coffersInBags: 0, riderArmed: true));
}

/// <summary>
/// FLEET CAPACITY (gate 9b): the Listed pile's one live fact, moved to the
/// launch surface. An advisory, never a gate - and silent unless there are
/// staged listings AND a roster has actually been read.
/// </summary>
public class FleetCapacityTests
{
  [Fact]
  public void Advisory_SilentWithNothingStaged()
    => Assert.Null(FleetCapacity.Advisory(stagedListings: 0, freeSlots: 40, observedAgeSeconds: 60));

  [Fact]
  public void Advisory_SilentBeforeAnyRosterRead()
    => Assert.Null(FleetCapacity.Advisory(stagedListings: 12, freeSlots: null, observedAgeSeconds: null));

  [Fact]
  public void Advisory_RoomToSpare_IsCalmAndNamesBothNumbers()
  {
    var a = FleetCapacity.Advisory(stagedListings: 12, freeSlots: 40, observedAgeSeconds: null);
    Assert.NotNull(a);
    Assert.False(a!.Value.Tight);
    Assert.Contains("hawk wants to list 12", a.Value.Text);
    Assert.Contains("40 slots free", a.Value.Text);
  }

  [Fact]
  public void Advisory_TightFleet_SaysSoAndStaysAnAdvisory()
  {
    var a = FleetCapacity.Advisory(stagedListings: 12, freeSlots: 9, observedAgeSeconds: 300);
    Assert.NotNull(a);
    Assert.True(a!.Value.Tight);
    Assert.Contains("fleet has 9 slots free", a.Value.Text);
    Assert.Contains("seen", a.Value.Text);
  }

  [Fact]
  public void Advisory_OneSlot_SpeaksInTheSingular()
  {
    var a = FleetCapacity.Advisory(stagedListings: 5, freeSlots: 1, observedAgeSeconds: null);
    Assert.Contains("1 slot free", a!.Value.Text);
    Assert.DoesNotContain("1 slots", a.Value.Text);
  }
}
