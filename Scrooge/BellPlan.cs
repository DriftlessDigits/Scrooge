using System.Collections.Generic;

namespace Scrooge;

/// <summary>One bag variant - the identity the bell dedupes on (HQ is a slot flag).</summary>
internal readonly record struct BellVariant(uint ItemId, bool IsHq);

/// <summary>Why a gate-sourced candidate did or did not join the bell.</summary>
internal enum BellExclusion
{
  /// <summary>It joins the bell run.</summary>
  Joins,
  /// <summary>The routing brain evaluated this variant - its verdict governs, not the gate.</summary>
  RouterOwns,
  /// <summary>The listing gate advised a better exit (desynth / GC).</summary>
  Gated,
  /// <summary>A Venture Coffer - the rider's input, never a listing.</summary>
  RiderOwns,
}

/// <summary>
/// The bell's one count, broken out by where each row came from. The deck shows
/// the total; the breakdown is what the button says it will do.
///
/// <para>WIDENED 07-25 to the bell's new scope. <paramref name="Reprice"/> and
/// <paramref name="Pull"/> are STANDING LISTINGS - rows that already sit on the
/// board and need a retainer visit to fix or retrieve. They ride the triage
/// executor rather than the Hawk run, but they are the same errand at the same
/// stop, so they are counted here: "the bell" means everything that needs a
/// retainer, and a tally that omitted two of its three verbs would put a number
/// on the button that the button does not do.</para>
/// </summary>
internal readonly record struct BellTally(
  int RoutedList, int RoutedVendor, int GateList, int GateVendor,
  int Reprice = 0, int Pull = 0)
{
  /// <summary>New listings the Hawk run posts (routed gear + gate joiners).</summary>
  internal int ListCount => RoutedList + GateList;

  /// <summary>Bag rows the Hawk run vendors on the way past.</summary>
  internal int VendorCount => RoutedVendor + GateVendor;

  /// <summary>Standing listings the triage executor pulls back and vendors.</summary>
  internal int PullCount => Pull;

  /// <summary>Standing listings the triage executor reprices at their own retainer.</summary>
  internal int RepriceCount => Reprice;

  internal int Total => ListCount + VendorCount + Reprice + Pull;

  /// <summary>
  /// "4 list + 2 reprice + 1 vendor" - the sentence the confirm button ends with.
  /// Only the verbs that have rows are named: a bell that lists and nothing else
  /// should not read as though it is about to reprice zero things.
  /// </summary>
  internal string Breakdown
  {
    get
    {
      var parts = new List<string>();
      if (ListCount > 0) parts.Add($"{ListCount} list");
      if (Reprice > 0) parts.Add($"{Reprice} reprice");
      if (VendorCount + Pull > 0) parts.Add($"{VendorCount + Pull} vendor");
      return parts.Count > 0 ? string.Join(" + ", parts) : "nothing";
    }
  }
}

/// <summary>
/// The ONE-DOOR BELL (WALK unit 4, Drift 07-24). The bell stage's question is not
/// "what gear did the router stage?" - it is <b>"what would the Hawk list?"</b>,
/// across every bag. Before this, that question had three side doors: verdicted
/// gear rows rode the bell, fresh melt yields needed a separate claim click, and
/// gate-passing non-gear was only ever visible inside the Hawk window's own
/// checklist. The live 07-24 lap said "0 bell" over 48 yield types and 6 coffer
/// dyes that all wanted the bell.
///
/// The fix is COMPOSITION, not re-routing. The Ledger borrows the Hawk gate's
/// answer (bag scan, MB-listable, tradeable, Ban as flood control, plus the
/// listing gate's better-exit advice) as a ROW SOURCE; the routing brain stays
/// gear-scoped and keeps the last word on anything it evaluated. This file is the
/// pure seam between the two - Dalamud-free so it links into the test project.
///
/// The precedence rule is the whole idea, and it is one line: <b>if the router
/// evaluated the variant, the router owns it.</b> Not "if the router staged it" -
/// a Review row, a Mixed-confidence List row waiting on a click, and a zero-exit
/// exclusion are all rows the router has an opinion about, and the gate must not
/// smuggle any of them onto the market through the back door just because the
/// gate itself has no opinion on them (non-equipment reads as ungated). Staged
/// rows are added by the caller from the confidence-gated sets; everything else
/// in the router's jurisdiction is silence, deliberately.
/// </summary>
internal static class BellPlan
{
  /// <summary>
  /// One gate candidate's fate. <paramref name="routerJurisdiction"/> is every
  /// variant the routing brain evaluated this refresh - staged or not, excluded
  /// or not. Order matters: jurisdiction is checked FIRST, so a gear row the
  /// router is still asking about can never fall through to the gate's silence.
  /// </summary>
  internal static BellExclusion Judge(BellVariant key, bool gated,
    IReadOnlySet<BellVariant> routerJurisdiction)
  {
    // A coffer is the rider's input, not a row: the bell opens it and lists what
    // came out. Belt-and-braces (the game marks it untradeable, so the gate scan
    // should never offer it) - but the one case where this matters is the one
    // where it would be worst, a disarmed rider quietly listing sealed coffers.
    if (CofferLogic.IsVentureCoffer(key.ItemId)) return BellExclusion.RiderOwns;
    if (routerJurisdiction.Contains(key)) return BellExclusion.RouterOwns;
    if (gated) return BellExclusion.Gated;
    return BellExclusion.Joins;
  }

  /// <summary>
  /// Filters the gate's candidates down to the rows that join the bell. Keeps
  /// per-slot rows intact (two stacks of the same mat are two listings); the
  /// jurisdiction check is by VARIANT, so a gear variant the router owns takes
  /// all of its slots with it.
  /// </summary>
  internal static List<T> Join<T>(
    IEnumerable<(T Row, BellVariant Key, bool Gated)> candidates,
    IReadOnlySet<BellVariant> routerJurisdiction)
  {
    var joined = new List<T>();
    foreach (var c in candidates)
      if (Judge(c.Key, c.Gated, routerJurisdiction) == BellExclusion.Joins)
        joined.Add(c.Row);
    return joined;
  }

  /// <summary>
  /// Does the bell stage have work? Its rows - all three verbs, since the tally
  /// now carries the reprices and pulls too.
  ///
  /// <para>The coffers moved OUT of this question on 07-25. They used to arm the
  /// bell because the rider fired at its front; the ruled order now puts the melt
  /// immediately before the bell, so the rider rides the melt instead and its
  /// yields still land in the bags a stage before the bell composes its rows -
  /// which was the whole point of moving the rider forward in the first place.
  /// See <see cref="MeltHasWork"/>.</para>
  /// </summary>
  internal static bool HasWork(BellTally tally) => tally.Total > 0;

  /// <summary>
  /// Does the MELT stage have work? Its own rows - but ALSO coffers, because the
  /// coffer rider fires at the front of the melt (Drift 07-24, "scan for coffers and
  /// pop them, then include them in the listing"; 07-25 moved the stage the rider
  /// hangs off, not the ruling). Unopened coffers are hidden routable inventory,
  /// so they ARM the stage: a 0-row melt with coffers in the bags still has work
  /// and the cursor must not skip past it, or the coffers stay sealed all night.
  /// </summary>
  internal static bool MeltHasWork(int meltRows, int coffersInBags, bool riderArmed)
    => meltRows > 0 || (riderArmed && coffersInBags > 0);
}

/// <summary>
/// FLEET CAPACITY (gate 9b, ruled 2026-08-03): "hawk run tells us capacity -
/// that sounds like a thing to expose at round time" (Drift). Not a standing
/// display - the dead Listed pile was the standing display - but an advisory on
/// the launch surface, where capacity actually bites: a hawk staging more
/// listings than the fleet has slots is a fact worth knowing BEFORE the press,
/// not an error at the bell.
///
/// <para>Fed by observations the round already makes for free: both the pinch
/// and the hawk read every retainer's "Selling N items" count off the bell
/// roster on their way in. No new fetch machinery, ever - a launch drawn before
/// any bell visit simply has no advisory, honestly.</para>
/// </summary>
internal static class FleetCapacity
{
  /// <summary>Free sell slots across the fleet at the last roster read. Null = never observed.</summary>
  internal static int? FreeSlots { get; private set; }

  /// <summary>When the roster was last read (unix seconds).</summary>
  internal static long ObservedAtUnix { get; private set; }

  /// <summary>Bank a roster read. Called wherever the bell roster is already in hand.</summary>
  internal static void Observe(int freeSlots, long nowUnix)
  {
    FreeSlots = freeSlots;
    ObservedAtUnix = nowUnix;
  }

  /// <summary>Plugin teardown - a stale observation must not survive into a new session's launch strip.</summary>
  internal static void Reset()
  {
    FreeSlots = null;
    ObservedAtUnix = 0;
  }

  /// <summary>
  /// The launch strip's line, or null when there is nothing worth saying: no
  /// listings staged, or no roster ever read. <paramref name="Tight"/> = the
  /// hawk wants more slots than the fleet has free - an advisory, NEVER a
  /// launch block: the hawk already handles a full fleet honestly at the bell
  /// (it lists what fits and says so), so refusing the press over an aging
  /// observation would gate real work on stale data.
  /// </summary>
  internal static (string Text, bool Tight)? Advisory(int stagedListings, int? freeSlots, long? observedAgeSeconds)
  {
    if (stagedListings <= 0 || freeSlots is not int free) return null;

    var seen = observedAgeSeconds is long age
      ? $" (seen {Durations.Elapsed(age)} ago)"
      : "";
    return free < stagedListings
      ? ($"hawk wants to list {stagedListings} - fleet has {free} slot{(free == 1 ? "" : "s")} free{seen}", true)
      : ($"hawk wants to list {stagedListings} - {free} slots free across the fleet{seen}", false);
  }
}
