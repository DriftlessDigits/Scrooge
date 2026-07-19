using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// The Ledger's action-named piles (design Section 3). One worklist grouped by
/// ACTION, not by data source: every WorkItem the advisor has an opinion about
/// lands in exactly one of these, and every pile header names where it executes.
/// Review is the safety pile - ambiguous calls and evidence-contradicted verdicts
/// live here, row-by-row only, never bulk-actionable.
/// </summary>
internal enum LedgerPile
{
  /// <summary>Too close to call, no evidence, or evidence CONTRADICTS the verdict - the player's eyes, row by row.</summary>
  Review,
  /// <summary>Earns real gil on the market board - the Hawk run lists it.</summary>
  List,
  /// <summary>A standing listing needs its price fixed (cap/undercut/upward held) - triage reprice.</summary>
  Reprice,
  /// <summary>Pull from the board and/or vendor-sell - no better exit in evidence.</summary>
  PullAndVendor,
  /// <summary>Skillup value or yields beat the alternatives - the desynth window.</summary>
  Melt,
  /// <summary>Seals beat gil (or venture stock demands it) - GC Expert Delivery.</summary>
  Churn,
  /// <summary>Correctly placed, leave it: thin lanes, floor-waiting races, ignored bait, protected holds. Always visible, collapsed.</summary>
  Watch,
}

/// <summary>
/// Which way a verdict leans relative to the market, for the confidence accord
/// checks. A verdict that keeps or puts the item ON the market is contradicted by
/// a DEAD market; a verdict that takes it OFF the market is contradicted by a LIVE
/// one (the Alexander Miniature case). Neutral verdicts (review/watch/hold) make no
/// market claim, so sales evidence neither backs nor contradicts them.
/// </summary>
internal enum VerdictLean { OnMarket, OffMarket, Neutral }

/// <summary>How one evidence axis relates to the verdict it is judged against.</summary>
internal enum Accord { Unknown, Agree, Disagree }

/// <summary>
/// The three-tier evidence-agreement confidence score (design Section 4, ruling 7).
/// v0 is the honest simple shape: Unanimous / Mixed / Contradicted computed from
/// evidence agreement, refined by the override-count history the player has already
/// written against a verdict class (V14 routing_overrides). The maturation path is
/// the SealToGilRate pattern - tiers become measured quantities as outcome joins
/// accrue - but that is NOT built here; do not overbuild v0.
///
/// Bulk-ability IS the confidence threshold: a pile is one-click-able only because
/// everything in it is Unanimous. The bulk button carries no safety logic of its
/// own - the gate is upstream, in what tier a verdict is allowed to land in.
/// </summary>
internal enum ConfidenceTier
{
  /// <summary>Every available evidence axis agrees, and there is enough fresh evidence. Bulk-confirmable.</summary>
  Unanimous,
  /// <summary>Evidence exists but is thin, stale, or partly silent - shows in its pile, needs the row click.</summary>
  Mixed,
  /// <summary>An evidence axis DISAGREES with the verdict - demoted to Review, immune to bulk actions.</summary>
  Contradicted,
}

/// <summary>
/// Pure pile-assignment and confidence-scoring core (LanePricing/TriageMemory mold:
/// no game reads, no storage, no statics-that-touch-Dalamud, linked into
/// Scrooge.Tests). The Ledger window builds an <see cref="LedgerConfidence.Evidence"/>
/// off each row's real facts and asks this core where the row goes and whether it
/// may be bulk-confirmed.
/// </summary>
internal static class LedgerPiles
{
  /// <summary>
  /// Bag-routing verdict -> pile. IsReview always wins (the router already flagged
  /// it too-close-to-call). Hold/Ban are observed-not-routed, so they Watch.
  /// </summary>
  internal static LedgerPile ForRoutingExit(RoutingExit exit, bool isReview) =>
    isReview ? LedgerPile.Review : exit switch
    {
      RoutingExit.List => LedgerPile.List,
      RoutingExit.Vendor => LedgerPile.PullAndVendor,
      RoutingExit.Desynth => LedgerPile.Melt,
      RoutingExit.Gc => LedgerPile.Churn,
      RoutingExit.Hold => LedgerPile.Watch,
      RoutingExit.Ban => LedgerPile.Watch,
      _ => LedgerPile.Review,
    };

  /// <summary>
  /// Triage flag / run-item result -> its NATURAL pile (before the confidence layer
  /// may demote a contradicted verdict to Review). Reprice-eligible holds go to
  /// Reprice; a thin lane_held is a Watch state; the below-floor / below-min disposal
  /// rows default to Pull-and-Vendor; genuinely-no-evidence NoData needs eyes.
  /// </summary>
  internal static LedgerPile ForTriage(PricingResult result) => result switch
  {
    PricingResult.CapBlocked or PricingResult.UndercutTooDeep or PricingResult.UpwardHeld => LedgerPile.Reprice,
    PricingResult.LaneHeld => LedgerPile.Watch,
    PricingResult.BelowFloor or PricingResult.BelowMinimum => LedgerPile.PullAndVendor,
    PricingResult.NoData => LedgerPile.Review,
    _ => LedgerPile.Review,
  };

  /// <summary>
  /// The pile a row is ACTUALLY drawn in: a Contradicted verdict is demoted to Review
  /// (the Alexander Miniature rule) no matter what its natural pile was; everything
  /// else stays where its verdict put it. A player resolution beats the demotion -
  /// Review exists to collect the player's ruling, so once it lands the row goes
  /// where the player put it (otherwise Contradicted rows are stuck forever).
  /// </summary>
  internal static LedgerPile Effective(LedgerPile natural, ConfidenceTier tier, bool playerResolved = false)
    => !playerResolved && tier == ConfidenceTier.Contradicted ? LedgerPile.Review : natural;

  /// <summary>
  /// Precedence for merging a two-reason WorkItem into ONE row (design Section 7 /
  /// brief: "flagged for two reasons = ONE WorkItem with a merged verdict, never two
  /// rows"). Lower index = wins. Review first (any reason that needs eyes makes the
  /// whole item need eyes); then the market-exit actions ordered take-off-market
  /// (Pull-and-Vendor) over adjust-in-place (Reprice) over keep-listing (List); then
  /// the disposal exits; Watch last (only wins when it is the sole reason).
  /// JUDGMENT CALL - flagged for Fable QA.
  /// </summary>
  private static readonly LedgerPile[] MergePrecedence =
  {
    LedgerPile.Review,
    LedgerPile.PullAndVendor,
    LedgerPile.Reprice,
    LedgerPile.List,
    LedgerPile.Churn,
    LedgerPile.Melt,
    LedgerPile.Watch,
  };

  /// <summary>
  /// Merges the piles of several reasons on the SAME (item, location) key into the
  /// single pile that WorkItem is drawn in. Empty input is a defensive Review.
  /// </summary>
  internal static LedgerPile Merge(IEnumerable<LedgerPile> piles)
  {
    var best = -1;
    foreach (var p in piles)
    {
      var idx = System.Array.IndexOf(MergePrecedence, p);
      if (idx >= 0 && (best < 0 || idx < best)) best = idx;
    }
    return best < 0 ? LedgerPile.Review : MergePrecedence[best];
  }
}

/// <summary>The Watch pile's count-summary categories (ruling 3): "14 watching: 3 races, 8 thin, 3 bait".</summary>
internal enum WatchCategory { Race, Thin, Bait, Other }

/// <summary>
/// The evidence-refined confidence score. See <see cref="ConfidenceTier"/> for the
/// v0 honesty contract.
/// </summary>
internal static class LedgerConfidence
{
  /// <summary>Strong-market witness threshold: this many settled sales in the lookback window backs (or contradicts) a verdict. The Alexander Miniature bar (design Section 4).</summary>
  internal const int StrongRecentSales = 3;

  /// <summary>Sales/day at or below this reads as a dead market - a List/Reprice verdict over it is contradicted.</summary>
  internal const double DeadVelocityPerDay = 0.01;

  /// <summary>Relative lane spread above which evidence is too scattered to call Unanimous.</summary>
  internal const double MaxTightSpread = 0.5;

  /// <summary>Override count against a verdict class at or above which a Unanimous tier demotes to Mixed (v0 teaching).</summary>
  internal const int DefaultDemoteThreshold = 2;

  /// <summary>
  /// One WorkItem verdict's evidence, normalized off the row's real facts. Every axis
  /// the design names lives here: lane n, spread, velocity, evidence age,
  /// local-vs-community accord, and (derived) sales-history-vs-verdict accord.
  /// </summary>
  internal readonly record struct Evidence(
    VerdictLean Lean,
    int LaneSampleCount,
    double LaneSpread,
    double? VelocityPerDay,
    int RecentSalesCount,
    int EvidenceAgeDays,
    Accord LocalCommunityAccord,
    int MinSamples,
    int StaleDays);

  /// <summary>
  /// Whether the recent-sales evidence is a STRONG market witness: enough settled
  /// sales in the window, or a velocity that projects to enough over 14 days.
  /// </summary>
  internal static bool StrongMarket(in Evidence e)
    => e.RecentSalesCount >= StrongRecentSales
       || (e.VelocityPerDay is double v && v * 14.0 >= StrongRecentSales);

  /// <summary>
  /// Sales-history-vs-verdict accord - the axis that carries the Alexander Miniature
  /// rule. The market evidence is ASYMMETRIC by verdict direction:
  ///
  /// - OFF-market (vendor/pull/melt/churn): a strong live market CONTRADICTS the
  ///   verdict (Alexander). A weak or ABSENT market AGREES with it - "nobody buys
  ///   this" is exactly why it should come off the board, so the vendor-trash pile
  ///   is confident by construction (this is what makes the bulk quick-pick useful).
  /// - ON-market (list/reprice): a live market AGREES; a demonstrably DEAD market
  ///   (known ~0 velocity, no recent sales) CONTRADICTS - a listing just sits. No
  ///   market evidence at all is Unknown, because you cannot confidently list into a
  ///   market you have never measured.
  /// - Neutral (review/watch): makes no market claim - Unknown.
  /// </summary>
  internal static Accord SalesVerdictAccord(in Evidence e)
  {
    switch (e.Lean)
    {
      case VerdictLean.OffMarket:
        return StrongMarket(e) ? Accord.Disagree : Accord.Agree;
      case VerdictLean.OnMarket:
        if (e.VelocityPerDay is double v)
          return v <= DeadVelocityPerDay && e.RecentSalesCount == 0 ? Accord.Disagree : Accord.Agree;
        return e.RecentSalesCount > 0 ? Accord.Agree : Accord.Unknown;
      default:
        return Accord.Unknown;
    }
  }

  /// <summary>
  /// The pre-refinement tier from market evidence alone. Any DISAGREE axis
  /// (sales-vs-verdict or local-vs-community) is Contradicted. Then the verdict
  /// direction decides what "enough evidence" means:
  ///
  /// - OFF-market: the absence of a live market IS the evidence, so a
  ///   non-contradicted off-market verdict is Unanimous without needing lane samples
  ///   (the vendor / churn / melt no-brainers).
  /// - ON-market: you need a real, fresh, tight lane to confidently keep something
  ///   listed - enough samples, within the stale window, spread inside the tight
  ///   band, over a market the sales history actively backs; short of that it is
  ///   Mixed (shows in the pile, needs the row click).
  /// - Neutral: never Unanimous - Review and Watch are not bulk actions.
  /// </summary>
  internal static ConfidenceTier BaseTier(in Evidence e)
  {
    var sales = SalesVerdictAccord(e);
    if (sales == Accord.Disagree || e.LocalCommunityAccord == Accord.Disagree)
      return ConfidenceTier.Contradicted;

    if (e.Lean == VerdictLean.Neutral)
      return ConfidenceTier.Mixed;

    if (e.Lean == VerdictLean.OffMarket)
      return ConfidenceTier.Unanimous; // sales agrees (not strong), community not against

    // ON-market: sales is Agree (live) or Unknown (unmeasured). Unmeasured -> Mixed.
    if (sales != Accord.Agree)
      return ConfidenceTier.Mixed;

    var enoughSamples = e.LaneSampleCount >= e.MinSamples;
    var fresh = e.EvidenceAgeDays <= e.StaleDays;
    var tight = e.LaneSpread <= MaxTightSpread;
    return enoughSamples && fresh && tight ? ConfidenceTier.Unanimous : ConfidenceTier.Mixed;
  }

  /// <summary>
  /// Override-count refinement (design Section 4: "manual decisions teach"). A verdict
  /// CLASS the player has overruled repeatedly loses its Unanimous standing and drops
  /// to Mixed - it must be re-earned by the eyes. v0 keeps this deliberately simple:
  /// a single one-step demotion, no elaborate model. Contradicted and Mixed are
  /// unchanged (already not bulk-eligible).
  /// </summary>
  internal static ConfidenceTier Refine(ConfidenceTier baseTier, int overrideCount,
    int demoteThreshold = DefaultDemoteThreshold)
    => baseTier == ConfidenceTier.Unanimous && overrideCount >= demoteThreshold
      ? ConfidenceTier.Mixed
      : baseTier;

  /// <summary>Verdict classes that keep or put the item ON the market board.</summary>
  private static readonly HashSet<string> OnMarketVerdicts =
    new(StringComparer.Ordinal) { "List", "Reprice" };

  /// <summary>
  /// Whether an override is doctrine evidence (Sam's 07-19 ruling: a standing
  /// rule the router already encodes never indicts a class). Only overrides
  /// that CROSS the market boundary count: X-&gt;List says "the router
  /// undervalues things", List-&gt;X says "it overvalues them" - both are
  /// disagreements the router cannot explain with any rule it holds.
  /// Off-market reshuffles (Gc/Melt/Vend among themselves) are value-hierarchy
  /// applications the worth knobs already price - a higher bidder, not an
  /// indictment; the Choker+Cesti pair zeroing a 102-item Turn In was this
  /// distinction missing. If those pile up, the fix is tuning the knobs the
  /// receipts point at, not demoting the class. Confirmations never count.
  /// </summary>
  internal static bool CountsTowardDemotion(string routerVerdict, string playerVerdict)
  {
    if (string.Equals(routerVerdict, playerVerdict, StringComparison.Ordinal))
      return false;
    return OnMarketVerdicts.Contains(routerVerdict) != OnMarketVerdicts.Contains(playerVerdict);
  }

  /// <summary>The full score: base tier from evidence, refined by the override history.</summary>
  internal static ConfidenceTier Tier(in Evidence e, int overrideCount = 0,
    int demoteThreshold = DefaultDemoteThreshold)
    => Refine(BaseTier(e), overrideCount, demoteThreshold);

  /// <summary>Bulk-ability IS the confidence threshold: only Unanimous rows are one-click-confirmable.</summary>
  internal static bool IsBulkEligible(ConfidenceTier tier) => tier == ConfidenceTier.Unanimous;

  /// <summary>
  /// Enumerates a pile's bulk-action set: ONLY the Unanimous rows. Mixed shows in the
  /// pile but needs its own row click; Contradicted never appears here (it was
  /// demoted to Review). This is the whole safety mechanism - the bulk button just
  /// runs whatever this returns.
  /// </summary>
  internal static List<T> BulkSet<T>(IEnumerable<(T Item, ConfidenceTier Tier)> rows)
    => rows.Where(r => IsBulkEligible(r.Tier)).Select(r => r.Item).ToList();

  /// <summary>
  /// Bulk set with player resolutions: a row the player explicitly ruled on (a move
  /// click) is confirmable regardless of tier - the human decision IS the resolution
  /// the confidence gate was waiting for. Evidence tiers still gate everything the
  /// player has not touched.
  /// </summary>
  internal static List<T> BulkSet<T>(IEnumerable<(T Item, ConfidenceTier Tier, bool PlayerResolved)> rows)
    => rows.Where(r => r.PlayerResolved || IsBulkEligible(r.Tier)).Select(r => r.Item).ToList();

  /// <summary>
  /// The Watch pile's one-line count summary (ruling 3). Omits zero categories;
  /// "14 watching: 3 races, 8 thin, 3 bait" with everything present, "0 watching"
  /// when empty.
  /// </summary>
  internal static string WatchSummary(IReadOnlyDictionary<WatchCategory, int> counts)
  {
    var total = counts.Values.Sum();
    var parts = new List<string>();
    void Add(WatchCategory c, string label)
    {
      if (counts.TryGetValue(c, out var n) && n > 0) parts.Add($"{n} {label}");
    }
    Add(WatchCategory.Race, "races");
    Add(WatchCategory.Thin, "thin");
    Add(WatchCategory.Bait, "bait");
    Add(WatchCategory.Other, "other");
    return parts.Count > 0
      ? $"{total} watching: {string.Join(", ", parts)}"
      : $"{total} watching";
  }
}
