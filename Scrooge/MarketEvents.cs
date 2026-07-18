using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Market memory - the append-diff core (LanePricing/TriageMemory mold: no game
/// reads, no storage, no statics, linked into Scrooge.Tests). Diffs an incoming
/// board scan against the stored snapshot and emits one row per change.
///
/// The memory is STROBOSCOPIC (design Section 2): foreign activity is only ever
/// observed as a window between two scans, never at a point. So this core never
/// stamps a point timestamp on a foreign event - it works in identity + price
/// space only, and the wiring wraps the result in an <see cref="ObservationWindow"/>
/// (seen_after, seen_by) before persisting.
///
/// Identity is SOFT (Sam's ruling, design Section 3): a foreign listing is matched
/// across scans on (retainer, qty, HQ); price is the mutable field that can move
/// without breaking the match. Two listings from the same retainer at the same
/// qty/HQ are a "twin" collision - unresolvable by soft identity, so every event
/// touching such a key is flagged <c>ambiguous_match</c> rather than guessed
/// (the fence-lesson tripwire: the ambiguity becomes a queryable count, not a vibe).
/// </summary>
internal static class MarketEvents
{
  /// <summary>One live board listing at scan time. Identity = (Retainer, Quantity, IsHq); UnitPrice is mutable.</summary>
  internal readonly record struct BoardListing(string Retainer, int Quantity, bool IsHq, long UnitPrice, bool IsOwn);

  /// <summary>The diff kinds (design Section 3, append-diff shape).</summary>
  internal enum EventKind { Appeared, Disappeared, PriceMoved }

  /// <summary>Where the observation came from. Every row is <c>own_scan</c> until the 4.0 community eye lands.</summary>
  internal enum Observer { OwnScan, Community }

  /// <summary>Certainty tier (design Section 3). Foreign disappearances stay <c>Observed</c> forever; own can be <c>Confirmed</c> by GilTrack.</summary>
  internal enum Certainty { Observed, Confirmed }

  /// <summary>
  /// What a disappearance resolved to. Foreign is always <see cref="Gone"/> (sold-or-pulled
  /// is unknowable for someone else's listing). Own defaults to <see cref="Pulled"/> at diff
  /// time and only upgrades to <see cref="Sold"/> when a GilTrack confirm arrives - never guessed.
  /// </summary>
  internal enum DisappearResolution { Gone, Pulled, Sold }

  /// <summary>
  /// One diffed change. No point timestamp: foreign timing is a window the wiring
  /// supplies. <see cref="Ambiguous"/> is the soft-identity self-report flag.
  /// </summary>
  internal readonly record struct MarketEvent(
    EventKind Kind,
    string Retainer,
    int Quantity,
    bool IsHq,
    long? OldPrice,        // null for Appeared
    long? NewPrice,        // null for Disappeared
    bool IsOwn,
    bool Ambiguous,
    DisappearResolution? Resolution); // set only for Disappeared

  /// <summary>
  /// The bounded observation window for a scan gap (design Section 2). SeenAfter is
  /// the previous scan; SeenBy is this scan. A 2h gap is tight evidence, a 4d gap is
  /// mush - the width itself is the honesty metric downstream weights undercut
  /// attribution by. A first-ever scan for an item has SeenAfter == 0 (unbounded start).
  /// </summary>
  internal readonly record struct ObservationWindow(long SeenAfter, long SeenBy);

  /// <summary>
  /// A persist-ready event: the pure diff plus the window and observer the wiring
  /// stamps on. Deliberately carries NO single point timestamp - only the window -
  /// so "foreign events never get a fake point-in-time" is structural, not a
  /// convention a caller could forget (design Section 2).
  /// </summary>
  internal readonly record struct MarketEventRow(
    MarketEvent Event,
    ObservationWindow Window,
    Observer Observer,
    Certainty Certainty);

  private readonly record struct Key(string Retainer, int Quantity, bool IsHq);

  /// <summary>
  /// Diffs an incoming board against the stored snapshot for ONE item, emitting an
  /// event per change. Both lists are the listings for the same item; quality lives
  /// on the identity key, so an item's NQ and HQ listings diff independently in one
  /// pass. The current list IS the new snapshot (the caller persists it as the
  /// current-board read model). Pure: no timestamps, no I/O.
  /// </summary>
  internal static List<MarketEvent> Diff(
    IReadOnlyList<BoardListing> priorSnapshot,
    IReadOnlyList<BoardListing> currentBoard)
  {
    var events = new List<MarketEvent>();

    var prior = priorSnapshot.GroupBy(l => new Key(l.Retainer, l.Quantity, l.IsHq))
      .ToDictionary(g => g.Key, g => g.ToList());
    var current = currentBoard.GroupBy(l => new Key(l.Retainer, l.Quantity, l.IsHq))
      .ToDictionary(g => g.Key, g => g.ToList());

    foreach (var key in prior.Keys.Union(current.Keys))
    {
      var p = prior.TryGetValue(key, out var pl) ? pl : new List<BoardListing>();
      var c = current.TryGetValue(key, out var cl) ? cl : new List<BoardListing>();

      // Twin collision: more than one listing shares this soft-identity key on
      // either side. Soft identity cannot say which moved, so every event we emit
      // for this key wears the ambiguous flag.
      var ambiguous = p.Count > 1 || c.Count > 1;

      if (!ambiguous)
      {
        DiffCleanKey(key, p, c, events);
        continue;
      }

      DiffTwinKey(key, p, c, events);
    }

    return events;
  }

  /// <summary>The clean 1:1 path: at most one listing per side for the key.</summary>
  private static void DiffCleanKey(Key key, List<BoardListing> p, List<BoardListing> c, List<MarketEvent> events)
  {
    if (p.Count == 0 && c.Count == 1)
    {
      events.Add(Appeared(c[0], ambiguous: false));
    }
    else if (p.Count == 1 && c.Count == 0)
    {
      events.Add(Disappeared(p[0], ambiguous: false));
    }
    else if (p.Count == 1 && c.Count == 1)
    {
      if (p[0].UnitPrice != c[0].UnitPrice)
        events.Add(PriceMoved(c[0], p[0].UnitPrice, ambiguous: false));
      // else: persisted unchanged - snapshot refreshes its window, no event.
    }
  }

  /// <summary>
  /// The twin path: multiset-diff prices under one soft-identity key. Prices present
  /// now but not before appeared; prices present before but not now disappeared; a
  /// same-multiset pair is a silent persist. Every emitted event is flagged ambiguous
  /// (a genuine price_moved can surface here as an appeared+disappeared pair - accepted
  /// as rare and unbiased, because nothing downstream reads a single row raw).
  /// </summary>
  private static void DiffTwinKey(Key key, List<BoardListing> p, List<BoardListing> c, List<MarketEvent> events)
  {
    // Multiset subtraction: matched prices persisted unchanged, the rest are events.
    var priorBag = p.ToList();
    var appearedList = new List<BoardListing>();
    foreach (var cur in c)
    {
      var idx = priorBag.FindIndex(l => l.UnitPrice == cur.UnitPrice);
      if (idx >= 0) priorBag.RemoveAt(idx);   // matched - persisted unchanged
      else appearedList.Add(cur);             // no prior at this price - appeared
    }
    // Whatever remains in priorBag was present before and is not now - disappeared.

    foreach (var cur in appearedList)
      events.Add(Appeared(cur, ambiguous: true));
    foreach (var gone in priorBag)
      events.Add(Disappeared(gone, ambiguous: true));
  }

  private static MarketEvent Appeared(BoardListing l, bool ambiguous)
    => new(EventKind.Appeared, l.Retainer, l.Quantity, l.IsHq, null, l.UnitPrice, l.IsOwn, ambiguous, null);

  private static MarketEvent Disappeared(BoardListing l, bool ambiguous)
    => new(EventKind.Disappeared, l.Retainer, l.Quantity, l.IsHq, l.UnitPrice, null, l.IsOwn, ambiguous,
        // Own disappearances default to Pulled (upgradeable to Sold by a GilTrack
        // confirm); foreign stays Gone forever - sold-or-pulled is unknowable.
        l.IsOwn ? DisappearResolution.Pulled : DisappearResolution.Gone);

  private static MarketEvent PriceMoved(BoardListing now, long oldPrice, bool ambiguous)
    => new(EventKind.PriceMoved, now.Retainer, now.Quantity, now.IsHq, oldPrice, now.UnitPrice, now.IsOwn, ambiguous, null);

  /// <summary>
  /// Wraps a diffed event in the window + observer it is persisted with. The
  /// certainty tier falls out of ownership: a foreign row is always Observed; an own
  /// row is Observed at diff time and only reaches Confirmed once GilTrack confirms
  /// the sale (design Section 3). Foreign events NEVER carry a point timestamp - the
  /// window is the only timing this method will attach.
  /// </summary>
  internal static MarketEventRow ToRow(MarketEvent ev, ObservationWindow window, Observer observer)
  {
    var certainty = ev.IsOwn && ev.Resolution == DisappearResolution.Sold
      ? Certainty.Confirmed
      : Certainty.Observed;
    return new MarketEventRow(ev, window, observer, certainty);
  }
}
