using Xunit;

namespace Scrooge.Tests;

public class GateMappingTests
{
  [Fact]
  public void NonEquipment_NoOpinion()
    => Assert.Equal(ListingGate.Verdict.None,
      ListingGate.Evaluate(T.Gear(equipment: false, sale: (20_000, 0, 5)), T.Batch()).Verdict);

  [Fact]
  public void Banned_NoOpinion_HawkOwnsIt()
    => Assert.Equal(ListingGate.Verdict.None,
      ListingGate.Evaluate(T.Gear(banned: true), T.Batch()).Verdict);

  [Fact]
  public void AlwaysVendor_NoOpinion_HawkOwnsIt()
    => Assert.Equal(ListingGate.Verdict.None,
      ListingGate.Evaluate(T.Gear(alwaysVendor: true), T.Batch()).Verdict);

  [Fact]
  public void SaleClearsFloor_Passes()
    => Assert.Equal(ListingGate.Verdict.Pass,
      ListingGate.Evaluate(T.Gear(sale: (20_000, 0, 5)), T.Batch()).Verdict);

  [Fact]
  public void BelowFloor_MeltBeatsVendor_GatesToDesynth()
    => Assert.Equal(ListingGate.Verdict.GateDesynth,
      ListingGate.Evaluate(T.Gear(sale: (3_000, 0, 5), melt: 5_000, vendor: 1_000), T.Batch()).Verdict);

  [Fact]
  public void BelowFloor_SealsBeatGil_GatesToGc()
    => Assert.Equal(ListingGate.Verdict.GateGc,
      ListingGate.Evaluate(T.Gear(sale: (3_000, 0, 5), seals: 200, vendor: 100), T.Batch()).Verdict);

  [Fact]
  public void BelowFloor_NoBetterExit_Informational()
  {
    var r = ListingGate.Evaluate(T.Gear(sale: (3_000, 0, 5), vendor: 100), T.Batch());
    Assert.Equal(ListingGate.Verdict.BelowFloor, r.Verdict);
    Assert.False(r.IsGated);
  }

  [Fact]
  public void NeverSold_HealthyMarket_MapsToUnknown_NeverAutoPass()
  {
    // Locked Universalis design: the price axis stays unknown without own
    // history, so a healthy market upgrades the REASON, never the verdict.
    var r = ListingGate.Evaluate(T.Gear(velocity: 0.2), T.Batch());
    Assert.Equal(ListingGate.Verdict.Unknown, r.Verdict);
    // Finding 9: velocity alone leans List into Review (no price witness), which
    // the gate maps to Unknown — never an auto-Pass. Reason upgraded, verdict not.
    Assert.Contains("Moves here", r.Reason);
  }

  [Fact]
  public void NeverSold_NoAlmanac_Unknown()
    => Assert.Equal(ListingGate.Verdict.Unknown,
      ListingGate.Evaluate(T.Gear(), T.Batch()).Verdict);

  [Fact]
  public void ReviewVerdict_MapsToUnknown_NotGated()
  {
    // Thin melt lead lands in Review; the gate must not act on ambiguity.
    var r = ListingGate.Evaluate(T.Gear(melt: 1_400, vendor: 1_000), T.Batch());
    Assert.Equal(ListingGate.Verdict.Unknown, r.Verdict);
    Assert.False(r.IsGated);
  }

  [Fact]
  public void GateAndRouter_SameItem_SameCall()
  {
    // The era review's yellow 6: one item, two surfaces, ONE answer.
    var item = T.Gear(sale: (3_000, 0, 5), melt: 12_000, seals: 800, vendor: 1_000);
    var batch = T.Batch();
    var pile = RoutingRules.Evaluate(item, batch);
    var gate = ListingGate.Evaluate(item, batch);
    var expected = pile.Exit switch
    {
      RoutingExit.Desynth => ListingGate.Verdict.GateDesynth,
      RoutingExit.Gc => ListingGate.Verdict.GateGc,
      _ => ListingGate.Verdict.BelowFloor,
    };
    Assert.Equal(expected, gate.Verdict);
    Assert.Equal(pile.Reason, gate.Reason);
  }
}

public class EquipmentFloorTests
{
  [Theory]
  [InlineData(15_000, 5, null, true)]    // clears both axes
  [InlineData(14_999, 5, null, false)]   // price boundary
  [InlineData(15_000, 10, null, true)]   // velocity boundary (inclusive)
  [InlineData(15_000, 11, null, false)]  // too slow
  [InlineData(15_000, null, 0.15, true)] // almanac fills the axis, healthy
  [InlineData(15_000, null, 0.05, false)]// almanac fills the axis, dead
  [InlineData(15_000, null, null, true)] // no data at all: benefit of the doubt
  public void PriceTimesVelocity(int price, int? soldAfterDays, double? velocity, bool clears)
    => Assert.Equal(clears,
      ListingGate.ClearsEquipmentFloor(price, soldAfterDays, velocity, T.Cfg()));

  [Fact]
  public void VelocityFloor_IsInverseOfWindow()
    => Assert.Equal(0.1, ListingGate.MarketVelocityFloor(T.Cfg()), 5);

  [Fact]
  public void VelocityFloor_GuardsZeroDayWindow()
    => Assert.Equal(1.0,
      ListingGate.MarketVelocityFloor(new RoutingConfig { ListingVelocityDays = 0 }), 5);
}
