using System.Collections.Generic;
using System.Linq;
using Xunit;
using static Scrooge.MarketEvents;

namespace Scrooge.Tests;

/// <summary>
/// Market-memory append-diff core: correct event kinds against a stored snapshot,
/// soft-identity twin ambiguity, own-vs-foreign certainty tiers, and the
/// observation-window (never a foreign point timestamp) discipline.
/// </summary>
public class MarketEventsTests
{
  private static BoardListing L(string retainer, long price, int qty = 1, bool hq = false, bool own = false)
    => new(retainer, qty, hq, price, own);

  private static List<BoardListing> Board(params BoardListing[] listings) => listings.ToList();

  // --- Event kinds -------------------------------------------------------

  [Fact]
  public void Appeared_WhenListingIsNewThisScan()
  {
    var prior = Board(L("Alice", 100));
    var current = Board(L("Alice", 100), L("Bob", 90));
    var events = Diff(prior, current);

    var appeared = Assert.Single(events);
    Assert.Equal(EventKind.Appeared, appeared.Kind);
    Assert.Equal("Bob", appeared.Retainer);
    Assert.Null(appeared.OldPrice);
    Assert.Equal(90, appeared.NewPrice);
  }

  [Fact]
  public void Disappeared_WhenPriorListingIsGone()
  {
    var prior = Board(L("Alice", 100), L("Bob", 90));
    var current = Board(L("Alice", 100));
    var events = Diff(prior, current);

    var gone = Assert.Single(events);
    Assert.Equal(EventKind.Disappeared, gone.Kind);
    Assert.Equal("Bob", gone.Retainer);
    Assert.Equal(90, gone.OldPrice);
    Assert.Null(gone.NewPrice);
  }

  [Fact]
  public void PriceMoved_WhenSameIdentityChangesPrice()
  {
    var prior = Board(L("Alice", 100));
    var current = Board(L("Alice", 85));
    var events = Diff(prior, current);

    var moved = Assert.Single(events);
    Assert.Equal(EventKind.PriceMoved, moved.Kind);
    Assert.Equal(100, moved.OldPrice);
    Assert.Equal(85, moved.NewPrice);
    Assert.False(moved.Ambiguous);
  }

  [Fact]
  public void NoEvent_WhenBoardIsUnchanged()
  {
    var board = Board(L("Alice", 100), L("Bob", 90));
    Assert.Empty(Diff(board, board));
  }

  [Fact]
  public void FirstEverScan_EmitsAppearedForEveryListing()
  {
    var events = Diff(new List<BoardListing>(), Board(L("Alice", 100), L("Bob", 90)));
    Assert.Equal(2, events.Count);
    Assert.All(events, e => Assert.Equal(EventKind.Appeared, e.Kind));
  }

  [Fact]
  public void QualityIsPartOfIdentity_NqAndHqDiffIndependently()
  {
    // Same retainer/qty, different quality: the HQ price move must not read as an
    // NQ disappear+appear. Two separate identity keys.
    var prior = Board(L("Alice", 100, hq: false), L("Alice", 500, hq: true));
    var current = Board(L("Alice", 100, hq: false), L("Alice", 450, hq: true));
    var events = Diff(prior, current);

    var moved = Assert.Single(events);
    Assert.Equal(EventKind.PriceMoved, moved.Kind);
    Assert.True(moved.IsHq);
    Assert.Equal(500, moved.OldPrice);
    Assert.Equal(450, moved.NewPrice);
  }

  // --- Soft-identity twin ambiguity -------------------------------------

  [Fact]
  public void TwinListings_PriceMove_FlagsAmbiguous()
  {
    // Two listings from the same retainer at the same qty/HQ (a twin collision):
    // one price changed, but soft identity can't say which. Every emitted event
    // for the key wears ambiguous_match rather than a silent guess.
    var prior = Board(L("Alice", 100), L("Alice", 120));
    var current = Board(L("Alice", 100), L("Alice", 110));
    var events = Diff(prior, current);

    Assert.NotEmpty(events);
    Assert.All(events, e => Assert.True(e.Ambiguous));
    // The 120 leaves, the 110 arrives; the 100 persisted silently.
    Assert.Contains(events, e => e.Kind == EventKind.Disappeared && e.OldPrice == 120);
    Assert.Contains(events, e => e.Kind == EventKind.Appeared && e.NewPrice == 110);
    Assert.DoesNotContain(events, e => e.OldPrice == 100 || e.NewPrice == 100);
  }

  [Fact]
  public void TwinListings_Unchanged_EmitNothing()
  {
    var board = Board(L("Alice", 100), L("Alice", 120));
    Assert.Empty(Diff(board, board));
  }

  [Fact]
  public void NonTwin_DifferentRetainersSamePrice_NotAmbiguous()
  {
    var prior = Board(L("Alice", 100), L("Bob", 100));
    var current = Board(L("Alice", 100), L("Bob", 90));
    var moved = Assert.Single(Diff(prior, current));
    Assert.False(moved.Ambiguous);
    Assert.Equal("Bob", moved.Retainer);
  }

  // --- Own vs foreign certainty tiers -----------------------------------

  [Fact]
  public void ForeignDisappearance_ResolvesGone_StaysObserved()
  {
    var prior = Board(L("Rival", 90, own: false));
    var events = Diff(prior, new List<BoardListing>());
    var gone = Assert.Single(events);

    Assert.Equal(DisappearResolution.Gone, gone.Resolution);
    var row = ToRow(gone, new ObservationWindow(1000, 2000), Observer.OwnScan);
    Assert.Equal(Certainty.Observed, row.Certainty); // never upgraded for foreign
  }

  [Fact]
  public void OwnDisappearance_DefaultsPulled_ObservedUntilConfirmed()
  {
    var prior = Board(L("MyRetainer", 90, own: true));
    var events = Diff(prior, new List<BoardListing>());
    var gone = Assert.Single(events);

    Assert.True(gone.IsOwn);
    Assert.Equal(DisappearResolution.Pulled, gone.Resolution); // no guess of sold
    var row = ToRow(gone, new ObservationWindow(1000, 2000), Observer.OwnScan);
    Assert.Equal(Certainty.Observed, row.Certainty);
  }

  [Fact]
  public void OwnDisappearance_ConfirmedSold_IsConfirmedTier()
  {
    // GilTrack confirm upgrades the resolution to Sold; ToRow then reads Confirmed.
    var sold = new MarketEvent(EventKind.Disappeared, "MyRetainer", 1, false, 90, null,
      IsOwn: true, Ambiguous: false, DisappearResolution.Sold);
    var row = ToRow(sold, new ObservationWindow(1000, 2000), Observer.OwnScan);
    Assert.Equal(Certainty.Confirmed, row.Certainty);
  }

  // --- Observation window is bounded, not point-stamped -----------------

  [Fact]
  public void EventRow_CarriesWindow_NotAPointTimestamp()
  {
    // The persisted row exposes only (seen_after, seen_by) - there is no single
    // observed-at field a caller could fill with a fake point for foreign activity.
    var appeared = Assert.Single(Diff(new List<BoardListing>(), Board(L("Bob", 90))));
    var row = ToRow(appeared, new ObservationWindow(1_700_000_000, 1_700_007_200), Observer.OwnScan);

    Assert.Equal(1_700_000_000, row.Window.SeenAfter);
    Assert.Equal(1_700_007_200, row.Window.SeenBy);
    // Structural guard: the row type's members are the event, the window, the
    // observer, and the certainty - a point timestamp is not among them.
    var members = typeof(MarketEventRow).GetProperties().Select(p => p.Name).ToArray();
    Assert.DoesNotContain("Timestamp", members);
    Assert.DoesNotContain("ObservedAt", members);
    Assert.Contains("Window", members);
  }

  [Fact]
  public void Observer_DefaultsOwnScan_CommunityIsTheSeam()
  {
    var appeared = Assert.Single(Diff(new List<BoardListing>(), Board(L("Bob", 90))));
    var own = ToRow(appeared, new ObservationWindow(0, 100), Observer.OwnScan);
    var community = ToRow(appeared, new ObservationWindow(0, 100), Observer.Community);
    Assert.Equal(Observer.OwnScan, own.Observer);
    Assert.Equal(Observer.Community, community.Observer);
  }
}
