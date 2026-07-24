using System.Collections.Generic;
using System.Linq;
using Scrooge;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// The Venture Coffer rider's pure core (WALK, Sam's 2026-07-23 ruling). The three
/// things worth pinning: the coffer identity, the free-slot guard threshold, and the
/// set-diff attribution that turns a bag before/after into "here's what the coffer
/// unlocked" - the opening loop and item-use are the untested Dalamud plumbing.
/// </summary>
public class CofferLogicTests
{
  [Fact]
  public void IsVentureCoffer_MatchesTheVerifiedId()
  {
    Assert.True(CofferLogic.IsVentureCoffer(32161));
    Assert.False(CofferLogic.IsVentureCoffer(32160));
    Assert.False(CofferLogic.IsVentureCoffer(0));
  }

  [Theory]
  [InlineData(0, false)]
  [InlineData(4, false)]   // one below the floor
  [InlineData(5, true)]    // exactly the floor
  [InlineData(20, true)]
  public void CanOpen_GuardsBelowTheFloor(int freeSlots, bool expected)
    => Assert.Equal(expected, CofferLogic.CanOpen(freeSlots));

  private static Dictionary<(uint, bool), int> Bag(params ((uint id, bool hq) key, int qty)[] rows)
  {
    var d = new Dictionary<(uint, bool), int>();
    foreach (var r in rows) d[r.key] = r.qty;
    return d;
  }

  [Fact]
  public void NewItems_SingleYield_IsAttributed()
  {
    var before = Bag(((32161u, false), 3));                    // 3 coffers
    var after = Bag(((32161u, false), 2), ((5u, false), 1));   // 2 coffers + 1 new item

    var gained = CofferLogic.NewItems(before, after);

    var y = Assert.Single(gained);
    Assert.Equal(5u, y.ItemId);
    Assert.False(y.IsHq);
    Assert.Equal(1, y.Qty);
  }

  [Fact]
  public void NewItems_ExcludesTheCofferItself()
  {
    // The coffer stack shrinks (a negative delta) - it must never be narrated as a yield.
    var before = Bag(((32161u, false), 5));
    var after = Bag(((32161u, false), 4));

    Assert.Empty(CofferLogic.NewItems(before, after));
  }

  [Fact]
  public void NewItems_CountsIncreaseToExistingStack()
  {
    // A yield that stacks onto mats you already held is still a positive delta.
    var before = Bag(((32161u, false), 2), ((100u, false), 10));
    var after = Bag(((32161u, false), 1), ((100u, false), 15));

    var y = Assert.Single(CofferLogic.NewItems(before, after));
    Assert.Equal(100u, y.ItemId);
    Assert.Equal(5, y.Qty); // 15 - 10
  }

  [Fact]
  public void NewItems_HqAndNqAreDistinctKeys()
  {
    var before = Bag(((32161u, false), 1), ((200u, false), 1));
    var after = Bag(((200u, false), 1), ((200u, true), 1)); // an HQ copy appeared

    var y = Assert.Single(CofferLogic.NewItems(before, after));
    Assert.Equal(200u, y.ItemId);
    Assert.True(y.IsHq);
    Assert.Equal(1, y.Qty);
  }

  [Fact]
  public void NewItems_MultipleYields_AllReported()
  {
    var before = Bag(((32161u, false), 2));
    var after = Bag(((32161u, false), 1), ((5u, false), 1), ((6u, true), 2));

    var gained = CofferLogic.NewItems(before, after);

    Assert.Equal(2, gained.Count);
    Assert.Contains(gained, g => g.ItemId == 5 && !g.IsHq && g.Qty == 1);
    Assert.Contains(gained, g => g.ItemId == 6 && g.IsHq && g.Qty == 2);
  }

  [Fact]
  public void NewItems_NothingNew_IsEmpty()
  {
    var before = Bag(((32161u, false), 2), ((7u, false), 3));
    var after = Bag(((32161u, false), 2), ((7u, false), 3));

    Assert.Empty(CofferLogic.NewItems(before, after));
  }
}
