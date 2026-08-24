using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// The evidence-refined confidence score. See <see cref="ConfidenceTier"/> for the
/// v0 honesty contract.
/// </summary>
internal static class BoardConfidence
{
  /// <summary>Strong-market witness threshold: this many settled sales in the lookback window backs (or contradicts) a verdict. The Alexander Miniature bar (design Section 4).</summary>
  internal const int StrongRecentSales = 3;

  /// <summary>Sales/day at or below this reads as a dead market - a List/Reprice verdict over it is contradicted.</summary>
  internal const double DeadVelocityPerDay = 0.01;

  /// <summary>Override count against a verdict class at or above which a Unanimous tier demotes to Mixed (v0 teaching).</summary>
  internal const int DefaultDemoteThreshold = 2;

  /// <summary>
  /// HOW MUCH THE SALE TAPE MAY DISAGREE WITH ITSELF and still be trusted to seat a
  /// verdict without asking - as a fraction of the going rate, measured by
  /// <see cref="LanePricing.BandSpread"/>. Tape noise past this line is a lane the model
  /// is not sure enough about: the tier drops to Mixed and the row comes to the player.
  ///
  /// <para><b>THIS IS THE UN-WELD</b> (drift call 2, ruled B1.5a 2026-08-21). This
  /// boundary and <see cref="LanePricing.ScatteredBandPct"/> were ONE constant serving
  /// two different instruments: this one reads the SALE TAPE and decides whether the
  /// model seats a verdict; that one reads the BOARD AS IT STANDS NOW and picks the word
  /// "scattered" for the case's lane caveat. Same measure, different evidence, different
  /// job - and with one number behind both, tuning either moved the other silently. Two
  /// names, two dials, <b>same value today</b>: this is an un-weld, not a retune, and
  /// nothing about what the plugin does changed when it landed.</para>
  ///
  /// <para><b>IT IS A CONSTANT, NOT A KNOB, AND IT MOVES ONLY WITH EVIDENCE</b> (RULED
  /// B1.5a). It is not a taste slider - it is the model's own "sure enough to seat
  /// without asking" threshold, and a player nudging it would be tuning the machine's
  /// confidence in itself by feel. The retune signal is override-grading evidence (did
  /// the rows this line seated turn out to be the ones he overrode?), never how the
  /// number looks. Walked via the place-in-line chain: a tight tape earns the bold
  /// step-past-the-crasher seating; a scattered tape demotes the verdict and it comes to
  /// the player.</para>
  /// </summary>
  internal const double TapeNoiseCeiling = 0.50;

  /// <summary>
  /// HOW OLD A READ MAY BE and still count as evidence the tier will seat a verdict on.
  /// Past this the read is stale, whoever it came from - this measures EVIDENCE
  /// FRESHNESS and nothing else.
  ///
  /// <para><b>NOT the same 14 as <see cref="RoutingRules.LocalSaleSeniorityDays"/></b>
  /// (3b-8, RULED B1.5b/c). That one asks whose witness outranks whose; this one asks
  /// whether any witness is still current. Two agreeing 14s measuring two different
  /// things, kept apart on purpose so a retune of either cannot silently move the other.
  /// It was a bare literal at two call sites until now.</para>
  ///
  /// <para><b>A constant, not a knob, moved only by evidence</b> (the B1.5a frame) - and
  /// a model internal: no player-facing sentence quotes it. The case's stale caveat says
  /// the READ is stale; the threshold that decided so is the model's business.</para>
  /// </summary>
  internal const int EvidenceStaleDays = 14;

  /// <summary>
  /// THE BOOK'S MATURITY BAR (ruled 08-22, pre-ship: "grade a new vs experienced
  /// db - that is the true risk"). Absence of evidence is only evidence when the
  /// book has been LISTENING: on an experienced database, "no market anywhere" is
  /// a real observation and the vendor-by-absence door grades confident; on a new
  /// one the identical silence means "haven't looked yet", and confidence built
  /// on it is ignorance wearing a tier. A book is experienced once EITHER witness
  /// stream has genuinely accumulated - banked settled sales (the tape listens at
  /// every board read) or warmed almanac answers (the fetch-on-miss lifecycle).
  /// The first recon banks hundreds of tape rows and the first window-round warms
  /// the almanac, so the bar self-dissolves inside the first real session.
  /// Constants, not knobs - the B1.5a class, retuned only on evidence.
  /// </summary>
  internal const int MatureBookBankedSales = 100;
  internal const int MatureBookAlmanacAnswers = 25;

  /// <summary>Whether the book is still NEW - too young for its silences to testify.</summary>
  internal static bool IsNewBook(long bankedSales, long almanacAnswers)
    => bankedSales < MatureBookBankedSales && almanacAnswers < MatureBookAlmanacAnswers;

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
    int StaleDays,
    long? VerdictWorth = null,
    long? MarketBid = null,
    bool BookIsNew = false);

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
  ///   verdict (Alexander) - but only by OUTBIDDING it (Drift's 07-22 ruling: a
  ///   verdict whose scored worth the player already priced, e.g. a 100k red
  ///   skillup, is not re-litigated by an 11k market that merely exists; the
  ///   market gets a voice, not a veto). No scored worth on record = the old
  ///   existence rule, so triage rows and pre-value exits keep the full
  ///   Alexander guard. A weak or ABSENT market AGREES with the verdict -
  ///   "nobody buys this" is exactly why it should come off the board, so the
  ///   vendor-trash pile is confident by construction.
  /// - ON-market (list/reprice): a live market AGREES; a demonstrably DEAD market
  ///   (known ~0 velocity, no recent sales) CONTRADICTS - a listing just sits. No
  ///   market evidence at all is Unknown, because you cannot confidently list into a
  ///   market you have never measured.
  /// - Neutral (review/watch): makes no market claim - Unknown.
  /// </summary>
  /// <summary>
  /// Whether the market's bid actually BEATS the verdict's own scored worth.
  /// With no worth on record the market wins by default (the pre-ruling
  /// existence behavior). With a worth on record, the market must bring a
  /// number and that number must exceed it - a tie or a missing price witness
  /// is not an outbid, and the player's priced decision holds the lane.
  /// </summary>
  internal static bool MarketOutbidsWorth(in Evidence e)
    => e.VerdictWorth is not long worth
       || (e.MarketBid is long bid && bid > worth);

  internal static Accord SalesVerdictAccord(in Evidence e)
  {
    switch (e.Lean)
    {
      case VerdictLean.OffMarket:
        return StrongMarket(e) && MarketOutbidsWorth(e) ? Accord.Disagree : Accord.Agree;
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
  /// - Neutral: never Unanimous - a verdict that makes no market claim has no
  ///   market evidence to be unanimous ABOUT. (Neutral rows are Review's, and
  ///   Review is never a bulk action.)
  /// </summary>
  internal static ConfidenceTier BaseTier(in Evidence e)
  {
    var sales = SalesVerdictAccord(e);
    if (sales == Accord.Disagree || e.LocalCommunityAccord == Accord.Disagree)
      return ConfidenceTier.Contradicted;

    if (e.Lean == VerdictLean.Neutral)
      return ConfidenceTier.Mixed;

    if (e.Lean == VerdictLean.OffMarket)
    {
      // THE ABSENCE DOOR (the maturity gate's one seat). This Unanimous rests on
      // the market being weak or ABSENT - and absence testifies only from an
      // experienced book. A new book's identical silence grades Mixed: the
      // verdict still gets made, it just arrives with its homework instead of
      // riding. A MEASURED market the verdict outbid (StrongMarket, no outbid)
      // is positive evidence and stays confident on any book - a young book may
      // be confident about what it has seen, never about what it has not looked
      // for.
      if (e.BookIsNew && !StrongMarket(e))
        return ConfidenceTier.Mixed;
      return ConfidenceTier.Unanimous; // sales agrees (not strong), community not against
    }

    // ON-market: sales is Agree (live) or Unknown (unmeasured). Unmeasured -> Mixed.
    if (sales != Accord.Agree)
      return ConfidenceTier.Mixed;

    var enoughSamples = e.LaneSampleCount >= e.MinSamples;
    var fresh = e.EvidenceAgeDays <= e.StaleDays;
    // The TAPE's own dial (3b-4), not the board-now "scattered" boundary. Whatever the
    // caller supplied is what gets judged - 0.0 on a bag row, which is the absence
    // answer and reads as agreement, deliberately (see LedgerCache.RoutedEvidence).
    var tight = e.LaneSpread <= TapeNoiseCeiling;
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
  /// Whether an override is doctrine evidence (Drift's 07-19 ruling: a standing
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

  /// <summary>
  /// The executor stamp that constitutes ASSENT for a receipt exit - the player
  /// ran the router's own call to completion. Any other stamp on the exit (a
  /// router-Gc item the player melted) is a reshuffle, not a vote of trust.
  /// </summary>
  internal static string? AssentAction(string exit) => exit switch
  {
    "Gc" => "TurnedIn",
    "Desynth" => "Desynthed",
    "List" => "Listed",
    "Vendor" => "Vendored",
    _ => null,
  };

  /// <summary>
  /// The verdict classes an executed exit vouches for. Gate variants share the
  /// destination: trusting the turn-in IS trusting the gate that routed to it.
  /// </summary>
  internal static string[] ClassesVouchedBy(string exit) => exit switch
  {
    "Gc" => new[] { "Gc", "GateGc" },
    "Desynth" => new[] { "Desynth", "GateDesynth" },
    "List" => new[] { "List", "Reprice" },
    "Vendor" => new[] { "Vendor" },
    _ => System.Array.Empty<string>(),
  };

  /// <summary>
  /// Folds executed, non-overridden receipts into the newest assent time per
  /// verdict class. Input rows whose executed action is not the exit's own
  /// assent action are ignored (executing a DIFFERENT exit vouches for nothing).
  /// </summary>
  internal static Dictionary<string, long> LastAssentByClass(
    IEnumerable<(string Exit, string ExecutedAction, long CreatedAt)> executedReceipts)
  {
    var last = new Dictionary<string, long>(StringComparer.Ordinal);
    foreach (var (exit, action, at) in executedReceipts)
    {
      if (!string.Equals(AssentAction(exit), action, StringComparison.Ordinal))
        continue;
      foreach (var cls in ClassesVouchedBy(exit))
        if (!last.TryGetValue(cls, out var t) || at > t)
          last[cls] = at;
    }
    return last;
  }

  /// <summary>
  /// Whether a boundary-crossing override still stands as doctrine evidence
  /// (Drift's 07-19 ruling: "assent clears dissent"). A crossing counts only
  /// until the player's next demonstrated act of trust in the class - running
  /// the class's own action, unoverridden, forgives everything before it. No
  /// invented decay constant: the re-earn is the player's own hands on the
  /// bell, so a class in active use stays trusted and a class being routed
  /// around stays demoted.
  /// </summary>
  internal static bool CrossingStands(long crossingAt,
    IReadOnlyDictionary<string, long> lastAssentByClass, string routerVerdict)
    => crossingAt > (lastAssentByClass.TryGetValue(routerVerdict, out var t) ? t : 0L);

  /// <summary>The full score: base tier from evidence, refined by the override history.</summary>
  internal static ConfidenceTier Tier(in Evidence e, int overrideCount = 0,
    int demoteThreshold = DefaultDemoteThreshold)
    => Refine(BaseTier(e), overrideCount, demoteThreshold);

  /// <summary>Bulk-ability IS the confidence threshold: only Unanimous rows are one-click-confirmable.</summary>
  internal static bool IsBulkEligible(ConfidenceTier tier) => tier == ConfidenceTier.Unanimous;

  /// <summary>
  /// DOES THIS ROW RIDE? The one predicate under every work set, so the round,
  /// the board and the launch refusal can never come to three answers.
  ///
  /// <para>Three ways on: the evidence cleared the bar, the human ruled it, or
  /// it DEFERS. The third is the headliner's whole structural claim (08-06) -
  /// "Defer rows ACT" is not a UI promise, it is this disjunct. Without it
  /// Defer would be Watch in a new coat: a pile that names its doubt and then
  /// quietly does nothing about it.</para>
  /// </summary>
  internal static bool Rides(ConfidenceTier tier, bool playerResolved, bool deferred)
    => playerResolved || deferred || IsBulkEligible(tier);

  /// <summary>
  /// PEG CONFIDENCE (F3, ruled 08-22 - the Archeo Kingdom Scepter). A verdict whose
  /// deciding number is the player's own ruled constant (a skill-up worth outbidding
  /// the yields) carries the peg's certainty, not the estimate's sample count: the
  /// tier was grading a 100k ruling as a 5-sample guess and referring a clear-cut
  /// case to the human on evidence grounds. The two-register constitution: estimates
  /// self-regulate with evidence; pegs HOLD. The too-close-to-call door is untouched -
  /// a genuine race still lands in Review upstream, and this only speaks where the
  /// verdict already stood.
  /// </summary>
  internal static ConfidenceTier PegOrGraded(bool pegLed, in Evidence e, int overrideCount)
    => pegLed ? ConfidenceTier.Unanimous : Tier(e, overrideCount);

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
    => rows.Where(r => Rides(r.Tier, r.PlayerResolved, deferred: false)).Select(r => r.Item).ToList();

  /// <summary>
  /// Bulk set with the third door open: a DEFER row rides on the system's own
  /// call. Same set-builder every pile button has used since 07-18, one more
  /// disjunct - the safety story is unchanged, because a deferred row is by
  /// construction one the system was willing to act on (see
  /// <see cref="DeferPlan.State"/>: no winner, or the router declined, and the
  /// row is Review instead).
  /// </summary>
  internal static List<T> BulkSet<T>(
    IEnumerable<(T Item, ConfidenceTier Tier, bool PlayerResolved, bool Deferred)> rows)
    => rows.Where(r => Rides(r.Tier, r.PlayerResolved, r.Deferred)).Select(r => r.Item).ToList();

  // The old Watch pile's count summary ("14 watching: 3 races, 8 slow sellers,
  // 3 bait") died with the pile it summarized. A roll-up was the right shape
  // for a place nothing ever happened; Defer is a worklist that ACTS, so its
  // rows are drawn as rows and its header does the same arithmetic every other
  // pile header does. See BoardLayout.HeaderLine.
}
