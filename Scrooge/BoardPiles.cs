using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The Ledger's action-named piles (design Section 3). One worklist grouped by
/// ACTION, not by data source: every WorkItem the advisor has an opinion about
/// lands in exactly one of these, and every pile header names where it executes.
/// Review is the safety pile - ambiguous calls and evidence-contradicted verdicts
/// live here, row-by-row only, never bulk-actionable.
///
/// <para>THREE OF THESE ARE NOT ACTIONS - they are the EYES AXIS (08-06), the
/// three states a row can be in with respect to the player: <see cref="Review"/>
/// (withheld until answered), <see cref="Defer"/> (acting, flagged, overrulable),
/// <see cref="Silent"/> (acting, nothing to say). Every other member names where
/// the row executes. Both live in one enum because a row is in exactly one of
/// them, which is the invariant the board is built on.</para>
/// </summary>
internal enum BoardPile
{
  /// <summary>Too close to call, no evidence, or evidence CONTRADICTS the verdict - the player's eyes, row by row. The action is WITHHELD until answered.</summary>
  Review,
  /// <summary>
  /// Decided on thin ice (the headliner, 08-06). The round runs these exactly
  /// as if they sat in their exit pile - nothing is withheld and nothing waits
  /// - and each row names the doubt branch it acted through. The middle of the
  /// three eyes states: "toss up, but I have opinions; welcome your input, not
  /// required." Replaces the old Watch pile, whose entire mechanism was the
  /// opposite (withholding, and calling it settled).
  /// </summary>
  Defer,
  /// <summary>
  /// "I got this, and what I got is: don't." A confident verdict NOT to engage
  /// - a protected hold, an observed ban - which is the third eyes state and
  /// therefore has no pile presence at all: the board never draws these. The
  /// enum member exists so the routing exits have somewhere honest to land;
  /// see <see cref="BoardLayout.GroupOrder"/>, which omits it deliberately.
  /// </summary>
  Silent,
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
///
/// <para>AMENDED 08-06 (Defer): the tier is no longer the whole eyes story. A
/// Mixed row with a scored winner now DEFERS - it rides, flagged, and blocks
/// nothing - so "Mixed" means "the evidence is partial", not "somebody owes
/// this a click". Contradicted is untouched: it still demotes to Review, which
/// is still where the launch refuses. See <see cref="DeferPlan"/>.</para>
/// </summary>
internal enum ConfidenceTier
{
  /// <summary>Every available evidence axis agrees, and there is enough fresh evidence. Bulk-confirmable.</summary>
  Unanimous,
  /// <summary>Evidence exists but is thin, stale, or partly silent. Rides as a Defer row - flagged with the mixed-exits doubt branch, never withheld.</summary>
  Mixed,
  /// <summary>An evidence axis DISAGREES with the verdict - demoted to Review, immune to bulk actions.</summary>
  Contradicted,
}

/// <summary>
/// Pure pile-assignment and confidence-scoring core (LanePricing/StandingMemory mold:
/// no game reads, no storage, no statics-that-touch-Dalamud, linked into
/// Scrooge.Tests). The Ledger window builds an <see cref="BoardConfidence.Evidence"/>
/// off each row's real facts and asks this core where the row goes and whether it
/// may be bulk-confirmed.
/// </summary>
internal static class BoardPiles
{
  /// <summary>
  /// Bag-routing verdict -> pile. IsReview always wins (the router already flagged
  /// it too-close-to-call).
  ///
  /// <para>Hold and Ban are the SILENT board (ruled 08-06). A protected hold is
  /// the player's own standing instruction and an observed ban is a confident
  /// decision not to engage - "I got this, and what I got is: don't" - so
  /// neither is a doubt and neither draws a row. They used to fill the Watch
  /// pile, which is exactly how a pile of confident non-decisions came to be
  /// read as a worklist. Protected holds get one summary line in the header,
  /// where the other config facts already live; bans get nothing, because
  /// there is nothing to say.</para>
  /// </summary>
  internal static BoardPile ForRoutingExit(RoutingExit exit, bool isReview) =>
    isReview ? BoardPile.Review : exit switch
    {
      RoutingExit.List => BoardPile.List,
      RoutingExit.Vendor => BoardPile.PullAndVendor,
      RoutingExit.Desynth => BoardPile.Melt,
      RoutingExit.Gc => BoardPile.Churn,
      RoutingExit.Hold => BoardPile.Silent,
      RoutingExit.Ban => BoardPile.Silent,
      _ => BoardPile.Review,
    };

  /// <summary>
  /// Standing-listing flag / run-item result -> its NATURAL pile (before the confidence layer
  /// may demote a contradicted verdict to Review). Reprice-eligible holds go to
  /// Reprice; the below-floor / below-min disposal
  /// rows default to Pull-and-Vendor; genuinely-no-evidence NoData needs eyes.
  ///
  /// <para>THE PINCH-SIDE lane_held REHOME (ruled 08-06, interim). A thin
  /// lane_held used to be a Watch state, which read as "settled, leave it".
  /// It is a DEFER row now, and the rehome is LABELING ONLY: keeping the
  /// standing ask already IS the best-guess action the pinch took, so there is
  /// no behavior to change and none was changed. The row just stops pretending
  /// the silence was a verdict and says what it kept and against what nothing.
  /// (The HAWK side of lane_held - a new listing with no lane at all - stays
  /// Review: "won't list without a price from you" is genuinely an answer we
  /// need, and the refusal to invent numbers survives there.)</para>
  /// </summary>
  internal static BoardPile ForStanding(PricingResult result) => result switch
  {
    PricingResult.CapBlocked or PricingResult.UndercutTooDeep => BoardPile.Reprice,
    PricingResult.LaneHeld => BoardPile.Defer,
    PricingResult.BelowFloor => BoardPile.PullAndVendor,
    PricingResult.NoData => BoardPile.Review,
    // A player-raised contest on a standing ask (unit 4, 08-02): adjust-in-place
    // family. The staged verb overrides the drawn group anyway - this is only
    // where the row falls back if the player unstages without dismissing.
    PricingResult.PlayerContest => BoardPile.Reprice,
    // The router's melt-beats-ask contest (ruled 2026-08-06): the row is about pulling for
    // the melt exit, so it draws with the melt work and proposes that verb.
    PricingResult.MeltBeatsAsk => BoardPile.Melt,
    _ => BoardPile.Review,
  };

  /// <summary>
  /// The pile a row is ACTUALLY drawn in: a Contradicted verdict is demoted to Review
  /// (the Alexander Miniature rule) no matter what its natural pile was; everything
  /// else stays where its verdict put it. A player resolution beats the demotion -
  /// Review exists to collect the player's ruling, so once it lands the row goes
  /// where the player put it (otherwise Contradicted rows are stuck forever).
  /// </summary>
  internal static BoardPile Effective(BoardPile natural, ConfidenceTier tier, bool playerResolved = false)
    => !playerResolved && tier == ConfidenceTier.Contradicted ? BoardPile.Review : natural;

  /// <summary>
  /// Precedence for merging a two-reason WorkItem into ONE row (design Section 7 /
  /// brief: "flagged for two reasons = ONE WorkItem with a merged verdict, never two
  /// rows"). Lower index = wins. Review first (any reason that needs eyes makes the
  /// whole item need eyes); then the market-exit actions ordered take-off-market
  /// (Pull-and-Vendor) over adjust-in-place (Reprice) over keep-listing (List); then
  /// the disposal exits; then Defer, then Silent (each only wins when it is the
  /// sole reason). Defer outranks Silent for the same reason Review outranks
  /// everything: the louder contract wins a merge, and "I acted on thin ice"
  /// is louder than "nothing to see".
  /// JUDGMENT CALL - flagged for Fable QA.
  /// </summary>
  private static readonly BoardPile[] MergePrecedence =
  {
    BoardPile.Review,
    BoardPile.PullAndVendor,
    BoardPile.Reprice,
    BoardPile.List,
    BoardPile.Churn,
    BoardPile.Melt,
    BoardPile.Defer,
    BoardPile.Silent,
  };

  /// <summary>
  /// Merges the piles of several reasons on the SAME (item, location) key into the
  /// single pile that WorkItem is drawn in. Empty input is a defensive Review.
  /// </summary>
  internal static BoardPile Merge(IEnumerable<BoardPile> piles)
  {
    var best = -1;
    foreach (var p in piles)
    {
      var idx = System.Array.IndexOf(MergePrecedence, p);
      if (idx >= 0 && (best < 0 || idx < best)) best = idx;
    }
    return best < 0 ? BoardPile.Review : MergePrecedence[best];
  }
}
