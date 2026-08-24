using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// PINS ON THE VENDOR FLOOR (review pricing item 1, 2026-08-16).
///
/// <para>One rule, four call sites: the lane's guard, the cached-post re-check, the
/// first-pass sentinel conversion in <c>MarketBoardHandler</c>, and the Ledger's relist
/// preview. Before the fold, each spelled the Doman doubling itself - and a preview
/// that doubled where the pinch did not would show the player a relist target the pinch
/// then refuses. These pins are what the fold is worth.</para>
/// </summary>
public class PriceFloorTests
{
  [Fact]
  public void None_has_no_floor_at_all()
  {
    Assert.Equal(0, PriceFloor.For(PriceFloorMode.None, 0));
    Assert.Equal(0, PriceFloor.For(PriceFloorMode.None, 1_159));
  }

  [Fact]
  public void Vendor_is_the_vendor_price()
    => Assert.Equal(1_159, PriceFloor.For(PriceFloorMode.Vendor, 1_159));

  [Fact]
  public void DomanEnclave_pays_twice_vendor_so_its_floor_is_twice_vendor()
    => Assert.Equal(2_318, PriceFloor.For(PriceFloorMode.DomanEnclave, 1_159));

  [Fact]
  public void A_vendor_price_of_zero_is_no_floor_under_every_mode()
  {
    // Every caller reads the answer as `floor > 0 && price < floor`, so an item the
    // vendor will not buy has no floor rather than a floor of nothing.
    Assert.Equal(0, PriceFloor.For(PriceFloorMode.Vendor, 0));
    Assert.Equal(0, PriceFloor.For(PriceFloorMode.DomanEnclave, 0));
  }

  // ---- THE ONE FLOOR LAW (ruled 2026-08-21) -------------------------------

  [Fact]
  public void No_mode_and_no_minimum_is_no_floor_and_binds_nothing()
  {
    var floor = PriceFloor.Effective(PriceFloorMode.None, 1_159, 0);
    Assert.Equal(0, floor.Floor);
    Assert.Equal(FloorBinding.None, floor.Binding);
    Assert.False(floor.Refuses(1));
  }

  [Fact]
  public void The_higher_of_the_two_rules_is_the_floor()
  {
    // Minimum over vendor.
    var min = PriceFloor.Effective(PriceFloorMode.Vendor, 1_159, 5_000);
    Assert.Equal(5_000, min.Floor);
    Assert.Equal(FloorBinding.PlayerMinimum, min.Binding);

    // Vendor over minimum.
    var vendor = PriceFloor.Effective(PriceFloorMode.Vendor, 1_159, 75);
    Assert.Equal(1_159, vendor.Floor);
    Assert.Equal(FloorBinding.Vendor, vendor.Binding);

    // The Enclave doubles before the comparison, which is the whole point of
    // asking both rules in ONE place.
    var enclave = PriceFloor.Effective(PriceFloorMode.DomanEnclave, 1_159, 2_000);
    Assert.Equal(2_318, enclave.Floor);
    Assert.Equal(FloorBinding.DomanEnclave, enclave.Binding);
  }

  [Fact]
  public void A_tie_goes_to_the_mode_floor()
  {
    // Same number, two rules. The vendor is a fact about the world and the minimum
    // is a preference; the sentence that teaches names the fact.
    var floor = PriceFloor.Effective(PriceFloorMode.Vendor, 1_159, 1_159);
    Assert.Equal(1_159, floor.Floor);
    Assert.Equal(FloorBinding.Vendor, floor.Binding);
  }

  [Fact]
  public void An_unvendorable_item_still_answers_to_the_players_minimum()
  {
    var floor = PriceFloor.Effective(PriceFloorMode.Vendor, 0, 75);
    Assert.Equal(75, floor.Floor);
    Assert.Equal(FloorBinding.PlayerMinimum, floor.Binding);
  }

  [Fact]
  public void An_unknown_vendor_price_is_no_mode_floor()
  {
    var floor = PriceFloor.Effective(PriceFloorMode.DomanEnclave, null, 0);
    Assert.Equal(0, floor.Floor);
    Assert.Equal(FloorBinding.None, floor.Binding);
  }

  [Fact]
  public void The_verdict_is_strictly_below_never_clamped()
  {
    var floor = PriceFloor.Effective(PriceFloorMode.Vendor, 1_159, 0);
    Assert.True(floor.Refuses(1_158));
    // Exactly at the floor is legal - the pipeline's comparison is the same strict
    // one, and a law that disagreed by a gil would refuse listings the pinch posts.
    Assert.False(floor.Refuses(1_159));
    Assert.False(floor.Refuses(1_160));
  }
}
