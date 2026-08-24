using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Pure core for the Ledger's LISTED-ITEM rows (M6 session 3). Renders from
/// CAPTURED data only - the listings table, settled-sale lanes, market events -
/// never a fresh game read. In the LanePricing/Ledger mold: no game reads, no
/// storage, no Dalamud statics, linked into Scrooge.Tests. The window builds the
/// inputs off real facts and asks this core for the labels, the outlier/vendor
/// verdicts, the doubt branch a held flag acted through, and the contradiction
/// note.
/// </summary>

/// <summary>Age-tier of a standing listing, by config bands.</summary>
internal enum ListedAgeTier { Fresh, Aging, Stale }

/// <summary>Stale-tier bands for a standing listing (config-snapshotted).</summary>
internal sealed record ListedAgeConfig
{
  /// <summary>At or past this many days on the board, a listing reads as aging.</summary>
  public int AgingDays { get; init; } = 7;
  /// <summary>At or past this many days, a listing reads as stale.</summary>
  public int StaleDays { get; init; } = 30;
}

/// <summary>
/// One own standing listing, projected off the listings table for the Listed
/// pile. A minimal captured-data record (NOT ListingRecord, which lives in the
/// Dalamud-side model) so the grouping/totals stay unit-testable.
/// </summary>
internal readonly record struct ListedLine(
  string Retainer,
  uint ItemId,
  string ItemName,
  bool IsHq,
  long UnitPrice,
  int Quantity,
  long FirstSeen);

/// <summary>Per-retainer roll-up for a collapsible Listed group: count + gil at ask.</summary>
internal readonly record struct RetainerListedTotals(string Retainer, int Count, long GilAtAsk);

internal static class BoardListings
{
  // ========================================================================
  // Item 1 - per-retainer grouping + totals (gil at ask)
  // ========================================================================

  /// <summary>
  /// Groups own standing listings into per-retainer roll-ups, richest group
  /// first. Gil-at-ask is the unit price times the stack - what the board is
  /// asking if every listing cleared at its current price.
  /// </summary>
  internal static List<RetainerListedTotals> GroupByRetainer(IEnumerable<ListedLine> listings)
    => listings
      .GroupBy(l => l.Retainer)
      .Select(g => new RetainerListedTotals(
        g.Key,
        g.Count(),
        g.Sum(l => l.UnitPrice * Math.Max(1, l.Quantity))))
      .OrderByDescending(r => r.GilAtAsk)
      .ThenBy(r => r.Retainer, StringComparer.Ordinal)
      .ToList();

  // ========================================================================
  // Item 2 - honest age labels (no invented precision)
  // ========================================================================

  /// <summary>Whole days a listing has been on the board since first_seen (never negative).</summary>
  internal static int AgeDays(long firstSeen, long now)
    => (int)Math.Max(0, (now - firstSeen) / 86400);

  /// <summary>Age-tier of a listing under the config bands.</summary>
  internal static ListedAgeTier Tier(int ageDays, ListedAgeConfig cfg)
    => ageDays >= cfg.StaleDays ? ListedAgeTier.Stale
     : ageDays >= cfg.AgingDays ? ListedAgeTier.Aging
     : ListedAgeTier.Fresh;

  /// <summary>
  /// Whether a first_seen is an EXACT age or only a lower bound. A listing that
  /// was already present at our first-ever board observation cannot have a true
  /// age we measured - we can't see before we started looking - so its age is a
  /// floor (">="). Anything first seen strictly after we began observing is exact.
  /// Structural, not a per-row migration marker (the tripwire forbids new writes).
  /// </summary>
  internal static bool AgeIsExact(long firstSeen, long firstObservationEver)
    => firstObservationEver <= 0 || firstSeen > firstObservationEver;

  /// <summary>
  /// The honest age label: "3d listed" when the age is measured, ">=3d listed"
  /// when first_seen is only a lower bound (a migration/first-scan backfill).
  /// Never invents precision the data does not have.
  /// </summary>
  internal static string AgeLabel(int ageDays, bool exact)
    => exact ? $"{ageDays}d listed" : $">={ageDays}d listed";

  // ========================================================================
  // Item 3 - outlier own-listing detection (the Highland Fence smell)
  // ========================================================================

  /// <summary>
  /// An own listing priced at or above the wall boundary (median x CeilingMult)
  /// is a self-inflicted wall: it sits unsold for months because nothing ever
  /// LOOKED at existing listings (the 55M-for-months case). Needs a real lane -
  /// a thin lane cannot say a price is an outlier.
  /// </summary>
  internal static bool IsOutlierListing(long listedPrice, LaneModel lane, LaneConfig cfg)
    => lane.SampleCount >= cfg.MinHistorySamples
       && lane.Median > 0
       && listedPrice >= lane.Median * cfg.CeilingMult;

  /// <summary>The Review-row reason for an outlier own listing - names the gap.</summary>
  internal static string OutlierReason(long listedPrice, LaneModel lane)
  {
    var going = (long)Math.Round(lane.Median);
    var mult = lane.Median > 0 ? listedPrice / lane.Median : 0;
    return $"Listed at {listedPrice:N0}; the tape settles around {going:N0} - {mult:0.#}x above it. "
         + "It has been sitting because nothing looked at it. Pull and reprice, or vendor.";
  }

  // ========================================================================
  // Item 4 - vendor-floor pull-forward (vendor pays more than the board)
  // ========================================================================

  /// <summary>
  /// True when the lane's own clearing price is at or below the NPC vendor price:
  /// the board can never beat the vendor, so keep-listing is strictly worse than
  /// pull-and-vendor. Needs a positive vendor price to compare against.
  /// </summary>
  internal static bool VendorBeatsBoard(long lanePrice, int vendorPrice)
    => vendorPrice > 0 && lanePrice <= vendorPrice;

  /// <summary>The Pull-and-Vendor reason when the vendor beats the board.</summary>
  internal static string VendorFloorReason(long lanePrice, int vendorPrice)
    => $"The vendor pays {vendorPrice:N0}, the line stands at ~{lanePrice:N0} - "
     + "never keep listing what the vendor beats. Pull and vendor.";

  // ========================================================================
  // Melt beats the ask - the contest nobody was raising (ruled 2026-08-06)
  // ========================================================================

  /// <summary>The machine reason for the melt-beats-ask contest, on the flag row and its receipts.</summary>
  internal const string MeltBeatsAskReason = "melt_beats_ask";

  /// <summary>
  /// THE MEASURED-ONLY RULE (Drift's ruling, 2026-08-06): <i>"a prior is a rumor,
  /// not evidence."</i> The observation is older - on 08-03 a Bread Rack stood at
  /// 600 while its own desynths returned ~1,064 an attempt, money left on the
  /// board that no surface mentioned; the cells were clickable, but only if you
  /// happened to look. This raises the question.
  ///
  /// <para>Only a MEASURED melt may ask it. A band prior is an expectation about
  /// gear of this weight, and inviting a pull on one would be inviting an action
  /// against a measurement nobody made - two items sharing an ilvl band share
  /// its number exactly, which is the whole reason the grade exists. Skill-up
  /// worth is barred for the same reason from the other direction: it is a knob,
  /// not a yield.</para>
  ///
  /// <para>No margin, by ruling: the question is "does melting beat selling",
  /// and one gil either way is still an honest answer to it. The flag informs;
  /// the player decides; nothing here acts.</para>
  /// </summary>
  internal static bool MeltBeatsAsk(long standingAsk, long? meltScore, MeltGrade grade)
    => standingAsk > 0
       && grade == MeltGrade.Measured
       && meltScore is long melt && melt > standingAsk;

  /// <summary>
  /// Whether to actually RAISE it. A dismissal is an answer ("I know, and it
  /// stands"), so the same question must not be asked again the next time the
  /// ledger refreshes - which would be seconds later. It re-arms when the ASK
  /// moves, because that is a different question about a different price.
  /// <paramref name="dismissedAtAsk"/> is the ask on the newest dismissed flag
  /// of this class for this lane, or null when the player has never answered.
  /// </summary>
  internal static bool ShouldRaiseMeltContest(long standingAsk, long? meltScore, MeltGrade grade,
    long? dismissedAtAsk)
    => MeltBeatsAsk(standingAsk, meltScore, grade) && dismissedAtAsk != standingAsk;

  /// <summary>
  /// The flag's copy: both numbers, plainly, and the two doors. It states the
  /// inversion and invites a click - it never claims the melt is the right call,
  /// because the ask may be standing on purpose and the advisor does not know
  /// that. First law: we advise.
  /// </summary>
  internal static string MeltBeatsAskDetail(long standingAsk, long meltValue)
    => $"Melt beats the ask: standing at {standingAsk:N0}, your own desynths of this one "
     + $"return ~{meltValue:N0} an attempt. Pull and melt, or leave it standing - your call.";

  /// <summary>
  /// One standing lane's live numbers, as the scorer has them at refresh time -
  /// the operands the contest's question is asked of.
  /// </summary>
  internal readonly record struct MeltContestLane(long Ask, long? MeltScore, MeltGrade Grade);

  /// <summary>
  /// THE RAISER CLOSES ITS OWN. Self-heal cannot touch this class - a pricing
  /// pass never asks its question - so if nothing here closed it, an open flag
  /// would live forever on numbers that stopped being true. That is precisely
  /// the V36 immortal-tenant disease this book just spent a migration killing,
  /// and a new instance of it is not an acceptable price for a new flag.
  ///
  /// <para>Two ways the question stops standing, and one answer for both. The
  /// ASK MOVED past the melt (a reprice, a manual raise, a fresh melt reading):
  /// the lane is still here, the predicate simply no longer fires. Or THE LANE
  /// LEFT - sold, pulled, expired - and is absent from the standing set entirely.
  /// A flag whose lane nobody is standing in is answering a question about a
  /// listing that does not exist.</para>
  ///
  /// <para>Symmetric with the raise by construction: it closes exactly when
  /// <see cref="MeltBeatsAsk"/> would not fire, so the two can never disagree
  /// about whether the condition holds. Dismissal memory is untouched - that
  /// lives on the dismissed row and re-arms on a changed ask, which is a
  /// different mechanism answering a different question.</para>
  /// </summary>
  internal static List<long> MeltContestsToClose(
    IEnumerable<(long Id, (uint ItemId, bool IsHq, string Retainer) Lane)> openFlags,
    IReadOnlyDictionary<(uint ItemId, bool IsHq, string Retainer), MeltContestLane> standing)
  {
    var toClose = new List<long>();
    foreach (var (id, lane) in openFlags)
    {
      if (!standing.TryGetValue(lane, out var live))
      {
        toClose.Add(id); // the listing this was about is gone
        continue;
      }
      if (!MeltBeatsAsk(live.Ask, live.MeltScore, live.Grade))
        toClose.Add(id);
    }
    return toClose;
  }

  // ========================================================================
  // The doubt branch a HELD flag acted through
  // ========================================================================

  /// <summary>
  /// A persisted flag's doubt branch, from its machine reason
  /// (triage_flags.reason), for the rows whose live lane decision is long gone.
  /// Retired spellings stay readable: rows written before A10 keep their
  /// strings forever, and a flag nobody can classify is a flag whose pivot
  /// never reaches the tape.
  ///
  /// <para>This REPLACES the old Watch categorizer (races / slow sellers /
  /// bait). Those were descriptions of why a row was being looked at; a doubt
  /// branch is a description of what the walk could not settle, which is the
  /// thing worth counting across weeks.</para>
  ///
  /// <para><see cref="DoubtBranch.None"/> for a flag class that is not a doubt
  /// at all - a cap-block or an undercut guard is a rule firing exactly as
  /// written, and folding those into a branch would dilute every tally the
  /// tape ever reports.</para>
  /// </summary>
  internal static DoubtBranch DoubtOfFlagReason(string reason) => reason switch
  {
    // No queue and no tape - the spine's own genuine-silence branch.
    "lane_held" => DoubtBranch.NoTape,
    // Pre-A10 spellings of "the whole board is below the band", which is the
    // shape the dead-heat test replaced.
    "race_declined" => DoubtBranch.DeadHeat,
    _ => DoubtBranch.None,
  };

  // ========================================================================
  // Item 8 addendum - a Contradicted row must STATE its objection inline
  // ========================================================================

  /// <summary>
  /// The market evidence that overruled a Contradicted verdict, spelled inline -
  /// the deciding number can no longer be invisible behind a bare "!" badge.
  /// "...but the DC pays ~X on N sales / moves ~Y/day". Empty when no market
  /// evidence backs the contradiction (the badge stands alone).
  /// <paramref name="payer"/> names the evidence's PROVENANCE honestly: "the DC
  /// pays" for community history, "settled sales pay" for local lanes - the
  /// note must never dress local numbers as DC-wide ones.
  /// </summary>
  internal static string ContradictionNote(long? median, int sales, double? velocityPerDay,
    string payer = "the DC pays")
  {
    var parts = new List<string>(2);
    if (median is long m && m > 0 && sales > 0)
      parts.Add($"{payer} ~{m:N0} on {sales} sale{(sales == 1 ? "" : "s")}");
    if (velocityPerDay is double v && v > 0)
      parts.Add($"moves ~{v:0.##}/day");
    return parts.Count > 0 ? $"...but {string.Join(" / ", parts)}." : "";
  }

  // ========================================================================
  // The next-round note - what happens NEXT to a listing that has been sitting
  // ========================================================================

  /// <summary>
  /// The forward-tense line for a standing ask: the price the next round will
  /// write, and how far that moves the current ask. The preview is the SAME
  /// number the On Market tab's List cell renders - this only dresses it, and
  /// invents nothing.
  ///
  /// <para>Three honest silences. A null preview (no lane, item never visited,
  /// or the spine refusing to price) reads EMPTY - a placeholder dash on every
  /// unpriced row would be a wall of nothing pretending to be data. A preview
  /// equal to the ask reads "holds", because "(-0%)" dresses a no-op as a cut.
  /// And a move smaller than a tenth of a percent says "&lt;0.1%" rather than
  /// rounding itself into a hold it is not.</para>
  /// </summary>
  internal static string NextRoundNote(long currentAsk, long? preview)
  {
    if (preview is not long next || next <= 0) return "";
    if (currentAsk <= 0) return $"{next:N0}";
    if (next == currentAsk) return "holds";

    var pct = (next - currentAsk) * 100.0 / currentAsk;
    var magnitude = Math.Abs(pct);
    var pctText = magnitude >= 1 ? $"{magnitude:0}%"
                : magnitude >= 0.1 ? $"{magnitude:0.#}%"
                : "<0.1%";
    return $"{next:N0} ({(pct > 0 ? "+" : "-")}{pctText})";
  }
}
