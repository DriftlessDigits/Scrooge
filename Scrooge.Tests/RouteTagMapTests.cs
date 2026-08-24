using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The Route column's tag, which is a MAPPING and nothing else since the door
/// gates retired: one item, two surfaces, one answer.
/// </summary>
public class RouteTagMapTests
{
  [Fact]
  public void NonEquipment_NoOpinion()
    => Assert.Equal(RouteTagMap.Verdict.None,
      RouteTagMap.Evaluate(T.Gear(equipment: false, sale: (20_000, 0, 5)), T.Batch()).Verdict);

  [Fact]
  public void Banned_NoOpinion_HawkOwnsIt()
    => Assert.Equal(RouteTagMap.Verdict.None,
      RouteTagMap.Evaluate(T.Gear(banned: true), T.Batch()).Verdict);

  [Fact]
  public void AlwaysVendor_NoOpinion_HawkOwnsIt()
    => Assert.Equal(RouteTagMap.Verdict.None,
      RouteTagMap.Evaluate(T.Gear(alwaysVendor: true), T.Batch()).Verdict);

  [Fact]
  public void SaleOnRecord_Passes()
    => Assert.Equal(RouteTagMap.Verdict.Pass,
      RouteTagMap.Evaluate(T.Gear(sale: (20_000, 0, 5)), T.Batch()).Verdict);

  [Fact]
  public void SmallSale_NoBetterExit_StillPasses()
  {
    // No worth floor left to fail: a 3,000 sale outscores a 100 gil counter, so
    // the router lists it and the tag says so.
    var r = RouteTagMap.Evaluate(T.Gear(sale: (3_000, 0, 5), vendor: 100), T.Batch());
    Assert.Equal(RouteTagMap.Verdict.Pass, r.Verdict);
  }

  [Fact]
  public void MeltBeatsTheSale_TagsDesynth()
    => Assert.Equal(RouteTagMap.Verdict.GateDesynth,
      RouteTagMap.Evaluate(T.Gear(sale: (3_000, 0, 5), melt: 5_000, vendor: 1_000), T.Batch()).Verdict);

  [Fact]
  public void SealsBeatEveryGilExit_TagsTurnIn()
    => Assert.Equal(RouteTagMap.Verdict.GateGc,
      RouteTagMap.Evaluate(T.Gear(seals: 200, vendor: 100), T.Batch()).Verdict);

  [Fact]
  public void VendorVerdict_TagsLow()
  {
    var r = RouteTagMap.Evaluate(T.Gear(melt: 900, vendor: 1_000), T.Batch());
    Assert.Equal(RouteTagMap.Verdict.GateVendor, r.Verdict);
  }

  [Fact]
  public void NeverSold_HealthyMarket_MapsToUnknown_NeverAutoPass()
  {
    // Locked Universalis design: the price axis stays unknown without own
    // history, so a healthy market upgrades the REASON, never the verdict.
    var r = RouteTagMap.Evaluate(T.Gear(velocity: 0.2), T.Batch());
    Assert.Equal(RouteTagMap.Verdict.Unknown, r.Verdict);
    Assert.Contains("Moves here", r.Reason);
  }

  [Fact]
  public void NeverSold_NoAlmanac_Unknown()
    => Assert.Equal(RouteTagMap.Verdict.Unknown,
      RouteTagMap.Evaluate(T.Gear(), T.Batch()).Verdict);

  [Fact]
  public void ReviewVerdict_MapsToUnknown()
  {
    // Thin melt lead lands in Review; the tag must not report ambiguity as a call.
    var r = RouteTagMap.Evaluate(T.Gear(melt: 1_400, vendor: 1_000), T.Batch());
    Assert.Equal(RouteTagMap.Verdict.Unknown, r.Verdict);
  }

  [Fact]
  public void TagAndRouter_SameItem_SameCall()
  {
    // The era review's yellow 6: one item, two surfaces, ONE answer.
    var item = T.Gear(sale: (3_000, 0, 5), melt: 12_000, seals: 800, vendor: 1_000);
    var batch = T.Batch();
    var pile = RoutingRules.Evaluate(item, batch);
    var tag = RouteTagMap.Evaluate(item, batch);
    var expected = pile.Exit switch
    {
      RoutingExit.Desynth => RouteTagMap.Verdict.GateDesynth,
      RoutingExit.Gc => RouteTagMap.Verdict.GateGc,
      _ => RouteTagMap.Verdict.GateVendor,
    };
    Assert.Equal(expected, tag.Verdict);
    Assert.Equal(pile.Reason, tag.Reason);
  }
}
