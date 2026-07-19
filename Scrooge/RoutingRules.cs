using System;

namespace Scrooge;

/// <summary>
/// One of the five exits an item can route to, plus Hold for protected items.
/// Review is not an exit — it's a verdict state (see RoutingVerdict.IsReview).
/// </summary>
internal enum RoutingExit
{
  /// <summary>Earns real gil on the market board — Hawk run lists it.</summary>
  List,
  /// <summary>Worthless everywhere else — NPC vendor.</summary>
  Vendor,
  /// <summary>Player-marked never-touch. Observed, untouched.</summary>
  Ban,
  /// <summary>Skillup value or yields beat the alternatives — melt it.</summary>
  Desynth,
  /// <summary>GC Expert Delivery — seals beat gil, or venture stock demands it.</summary>
  Gc,
  /// <summary>Protected (gearset / spiritbond / materia) — never auto-routed.</summary>
  Hold,
}

/// <summary>
/// The rules engine's answer for one item. The reason string is the product —
/// it's what makes the pile trustworthy enough to one-confirm. When two exits
/// score too close to call, IsReview is set and both reasons are carried:
/// the router prefers a small honest Review pile over a confident wrong routing.
/// </summary>
internal readonly record struct RoutingVerdict(
  RoutingExit Exit,
  string Reason,
  bool IsReview = false,
  RoutingExit? RunnerUp = null,
  string RunnerUpReason = "");

/// <summary>
/// The routing brain's decision core. Deterministic rules over pre-gathered
/// evidence (RoutingItemInputs) and a batch-snapshotted config — same inputs,
/// same pile, every time. Pure functions: no game reads, no storage, no
/// statics — unit-testable without FFXIV (see Scrooge.Tests).
///
/// Rule order (first match wins): ban, protection, always-vendor flags;
/// venture-panic hard override; then the value rules — list evidence,
/// desynth skillup, GC seals, melt-for-gil, vendor fallback. Items whose
/// evidence is genuinely ambiguous land in Review instead of a guess.
/// </summary>
internal static class RoutingRules
{
  /// <summary>
  /// Rule 7's "meaningfully": melt must beat vendor by this factor before
  /// desynth-for-gil wins. A melt value barely above vendor isn't worth the
  /// attempt time; between 1x and this factor the call goes to Review.
  /// </summary>
  private const double MeltOverVendorFactor = 1.5;

  internal static RoutingVerdict Evaluate(RoutingItemInputs item, RoutingBatch batch)
  {
    // --- Flag rules: the player already decided these ---

    if (item.IsBanned)
      return new(RoutingExit.Ban, "On your ban list.");

    if (item.IsProtected)
      return new(RoutingExit.Hold, item.ProtectionReason.Length > 0
        ? $"Protected: {item.ProtectionReason}."
        : "Protected.");

    if (item.IsAlwaysVendor)
      return new(RoutingExit.Vendor, "Always-vendor flag.");

    // --- Venture panic: sub-500 stock churns everything GC-eligible ---
    // Tilt only acts on a POSITIVE token read; null (read failed) and 0
    // (ambiguous until the token id is verified in-game) mean no tilt —
    // the router never panics on missing evidence.

    var cfg = batch.Rules;
    var stock = batch.VentureStock is int s && s > 0 ? s : (int?)null;
    var burn = batch.WeeklyVentureBurn;

    if (item.SealValue is int panicSeals && stock is int panicStock && panicStock < cfg.VentureBandPanic)
      return new(RoutingExit.Gc,
        $"Venture panic: {panicStock:N0} tokens (< {cfg.VentureBandPanic:N0}) — turning in everything GC-eligible. {panicSeals:N0} seals.");

    // --- Value rules: gil-equivalent scores from local evidence ---

    var sealRate = batch.SealToGilRate;
    // The placeholder rate is a guess wearing numbers — the reason says so
    // until venture returns measure the real rate.
    var rateTag = batch.SealRateEmpirical ? "" : ", rough";
    var gcScore = item.SealValue is int seals ? (long)seals * sealRate : (long?)null;
    var gcReason = item.SealValue is int gs
      ? $"Turn in: {gs:N0} seals (~{gcScore:N0} gil at {sealRate} gil/seal{rateTag})."
      : "";

    var meltScore = item.MeltValuePerAttempt;
    var meltReason = meltScore is long mv ? $"Desynth: yields ~{mv:N0} gil/attempt from your ledger." : "";

    // Skillup pricing (Sam 07-18: price the skillup, don't gate it). A rare
    // red/yellow skillup makes the desynth candidate worth AT LEAST the
    // configured gil value; it then competes in the ordinary comparison, so
    // "very-very-high gil beats a skillup" is emergent (a 200k sale outbids a
    // 100k red; a 20k sale loses to it; near-worth lands in Review honestly).
    if (item.DesynthSkillupEligible)
    {
      long worth = item.DesynthColor == DesynthSkillupColor.Red
        ? cfg.SkillupWorthRed : cfg.SkillupWorthYellow;
      if (worth > (meltScore ?? 0))
      {
        meltScore = worth;
        meltReason = $"Skillup: {item.DesynthColor?.ToString().ToLowerInvariant()} desynth at ilvl {item.Ilvl} — worth ~{worth:N0} gil to you (skillups are scarce).";
      }
    }

    var vendorScore = item.VendorPrice > 0 ? (long)item.VendorPrice : (long?)null;
    var vendorReason = vendorScore is long vv ? $"Vendor: {vv:N0} gil." : "";

    // Rule 4 — list evidence: own sale clears the floor. Equipment gets the
    // price x velocity floor (shared with the listing gate); non-gear a
    // simpler gil-only floor.
    long? listScore = null;
    var listReason = "";
    if (item.LastSale is { } sale)
    {
      var clears = item.IsEquipment
        ? ListingGate.ClearsEquipmentFloor(sale.Price, sale.SoldAfterDays, item.MarketVelocity, cfg)
        : sale.Price >= cfg.ListingWorthGil;
      if (clears)
      {
        listScore = sale.Price;
        listReason = sale.SoldAfterDays is int d
          ? $"List: sold at {sale.Price:N0} gil after {d}d listed."
          : $"List: sold at {sale.Price:N0} gil.";
      }
    }

    // Rule 4 winner check honors the priced skillup: List only wins outright
    // when the sale actually outbids the desynth candidate (which a skillup
    // inflates); otherwise fall through to the value rules below.
    if (listScore is long list && list >= (meltScore ?? 0))
    {
      // Sub-750 stock: default to churn unless the item is worth a lot.
      if (gcScore is not null && stock is int lowStock && lowStock < cfg.VentureBandLow
          && list < (long)(cfg.ListingFloorGil * cfg.VenturePanicValueMultiplier))
        return new(RoutingExit.Gc,
          $"Venture low: {lowStock:N0} tokens (< {cfg.VentureBandLow:N0}) — turning in unless it's worth {(long)(cfg.ListingFloorGil * cfg.VenturePanicValueMultiplier):N0}+. {gcReason}",
          RunnerUp: RoutingExit.List, RunnerUpReason: listReason);

      return Resolve(RoutingExit.List, list, listReason,
        BestOf((RoutingExit.Desynth, meltScore, meltReason),
               (RoutingExit.Gc, gcScore, gcReason),
               (RoutingExit.Vendor, vendorScore, vendorReason)),
        stock, burn, cfg);
    }

    // Rule 6 — GC turn-in: seals beat every gil exit, or low stock tilts it.
    if (gcScore is long gc)
    {
      var bestGil = Math.Max(meltScore ?? 0, vendorScore ?? 0);
      // Sub-750 stock defaults to churn UNLESS the alternative is worth a lot —
      // the same value escape the list branch has. Only sub-500 panic is
      // unconditional (locked bands, BP4 Q5); without this guard a 50k melt
      // churned for 20k of seals with a confident reason.
      var lowCeiling = (long)(cfg.ListingFloorGil * cfg.VenturePanicValueMultiplier);
      if (stock is int lowStock2 && lowStock2 < cfg.VentureBandLow && bestGil < lowCeiling)
      {
        var alt = BestOf((RoutingExit.Desynth, meltScore, meltReason),
                         (RoutingExit.Vendor, vendorScore, vendorReason));
        return new(RoutingExit.Gc,
          $"Venture low: {lowStock2:N0} tokens (< {cfg.VentureBandLow:N0}) — turning in unless it's worth {lowCeiling:N0}+. {gcReason}",
          RunnerUp: alt.Score is not null ? alt.Exit : null,
          RunnerUpReason: alt.Score is not null ? alt.Reason : "");
      }
      // Community veto (almanac cross-check): gear with no LOCAL sale can
      // never produce a list score, so seals won by forfeit — the market was
      // never consulted. DC-scope settled sales are the missing witness: when
      // the community pays more than the seals are worth, the item routes to
      // the Hawk run instead (which prices it off the live board through the
      // same community lane). Resolve applies the normal review band and
      // venture tilt, so a near-tie still degrades honestly. Runs AFTER the
      // low-stock guard on purpose: thin venture stock still churns.
      if (item.IsMarketable && item.LastSale is null
          && item.CommunityMedian is long cm
          && item.CommunitySampleCount >= cfg.CommunityMinSamples
          && cm > gc && cm > (meltScore ?? 0))
        return Resolve(RoutingExit.List, cm,
          $"List: the DC pays ~{cm:N0} gil ({item.CommunitySampleCount} sales, Universalis community) — beats {gcReason.TrimEnd('.')}. The Hawk run prices it off the live MB.",
          (RoutingExit.Gc, gcScore, gcReason), stock, burn, cfg);

      if (gc > bestGil)
        return Resolve(RoutingExit.Gc, gc, gcReason,
          BestOf((RoutingExit.Desynth, meltScore, meltReason),
                 (RoutingExit.Vendor, vendorScore, vendorReason)),
          stock, burn, cfg);
    }

    // Rule 7 — melt for gil: yields must beat vendor meaningfully. No vendor
    // price is read as NO vendor floor (0), not an unknown one — deliberate:
    // for unvendorable gear any known-positive melt is the only gil exit,
    // and "unknown floor" would strand the item in the shrug below.
    if (meltScore is long melt && melt > 0)
    {
      var vendorFloor = vendorScore ?? 0;
      if (melt > vendorFloor * MeltOverVendorFactor)
        // List rides as a runner-up candidate: a sale just under a priced
        // skillup's worth reaches here, and the review band should see it.
        return Resolve(RoutingExit.Desynth, melt, meltReason,
          BestOf((RoutingExit.Vendor, vendorScore, vendorReason),
                 (RoutingExit.List, listScore, listReason)),
          stock, burn, cfg);
      if (melt > vendorFloor)
        return new(RoutingExit.Desynth, meltReason, IsReview: true,
          RunnerUp: RoutingExit.Vendor,
          RunnerUpReason: $"{vendorReason} Desynth lead is thin — attempt time may not pay.");
    }

    // Evidence-only: gear the router knows nothing about is YOUR call,
    // not a vendor trip. The Universalis almanac upgrades this from a guess
    // when it can — a market that moves keeps the List call (the Hawk run
    // prices off the live MB at list time); a dead market means a listing
    // never sells, so vendor is the honest exit. No almanac data = today's
    // behavior: leans List but lands in Review — the router won't guess.
    if (item.IsEquipment && item.LastSale is null && meltScore is null && gcScore is null)
    {
      // Untradable gear can't be listed at all — with no melt or seal
      // evidence either, vendor is the only executable exit. Confident
      // verdict, never Review: this is the dungeon-clear vendor-trash tail,
      // and Universalis can never settle it (no market to ask about), so a
      // Review here would re-ask the same dead question every single run.
      if (!item.IsMarketable)
      {
        if (vendorScore is long uv)
          return new(RoutingExit.Vendor,
            $"Untradable, no desynth or seal evidence — vendor is the only exit: {uv:N0} gil.");
        return new(RoutingExit.Vendor,
          "No gil exit at all (untradable, no desynth, no seals) — current-tier gear is usually for wearing, not selling.",
          IsReview: true);
      }

      // DC sale history is stronger evidence than home-world velocity when it
      // exists: velocity ~0 on one world hides gear that sells DC-wide
      // (buyers travel for gear). Checked first; velocity remains the
      // fallback witness.
      if (item.CommunityMedian is long cMed
          && item.CommunitySampleCount >= cfg.CommunityMinSamples)
      {
        if (cMed >= cfg.ListingWorthGil)
          return new(RoutingExit.List,
            $"Never sold one locally, but the DC buys it: ~{cMed:N0} gil ({item.CommunitySampleCount} sales, Universalis community) — the Hawk run prices it off the live MB.");
        return new(RoutingExit.Vendor,
          $"DC-wide it only fetches ~{cMed:N0} gil ({item.CommunitySampleCount} sales, Universalis community) — below your {cfg.ListingWorthGil:N0} worth floor. {vendorReason}",
          RunnerUp: RoutingExit.List,
          RunnerUpReason: "List anyway if you think the community read is wrong.");
      }

      if (item.MarketVelocity is double marketV)
      {
        // Finding 9 — price x velocity are ONE witness. Reaching here means the
        // PRICE witness is silent: no local sale, and no community median that
        // cleared the sample bar above. Velocity measures MOVEMENT, not WORTH,
        // so on its own it can no longer confidently route a marketable item in
        // EITHER direction — both halves of the Cashmere Hood / Green Beret
        // defect. A live velocity at an unknown price is not a confident List
        // (an item can "move" at 1 gil forever); a dead world velocity does not
        // prove a DC-tradable item is worthless (dead-world listings still sell
        // to world-hoppers). Lean List — marketable, the Hawk run prices it off
        // the live MB — but land in Review so the player supplies the price read.
        var moves = marketV >= ListingGate.MarketVelocityFloor(cfg);
        return new(RoutingExit.List,
          moves
            ? $"Moves here (~{marketV:0.##}/day on your world, Universalis) but you've never sold one — no price on record. List and let the Hawk run price it, or vendor if it's junk."
            : $"Doesn't move on your world (~{marketV:0.##}/day, Universalis), but it's DC-tradable — dead-world listings still sell to world-hoppers. List-and-forget, or vendor if you know it's junk.",
          IsReview: true,
          RunnerUp: RoutingExit.Vendor,
          RunnerUpReason: vendorReason.Length > 0 ? vendorReason : "Vendor if you know it's junk.");
      }
      return new(RoutingExit.List,
        "No local evidence for this gear — never sold or desynthed one. Check the MB if it looks valuable.",
        IsReview: true, RunnerUp: RoutingExit.Vendor, RunnerUpReason: vendorReason);
    }

    // Rule 8 — nothing beat the vendor. A sale that failed the floor is the
    // "why not list" context (reaching here with LastSale set means rule 4
    // rejected it) — say so instead of making the human wonder.
    if (vendorScore is long vendor)
    {
      var soldNote = item.LastSale is { } ls
        ? $" Sold at {ls.Price:N0} once — below your list floor."
        : "";
      return new(RoutingExit.Vendor, $"Vendor: {vendor:N0} gil — no better exit in evidence.{soldNote}");
    }

    // Unvendorable, no evidence for anything else — honest shrug.
    return new(RoutingExit.Vendor, "No viable exit known (can't even vendor it).",
      IsReview: true);
  }

  /// <summary>
  /// Finalizes a value-rule winner against its best-scoring alternative.
  /// Within the review band the verdict degrades to Review — unless venture
  /// stock tilts the borderline call: stock below the full band tilts TO churn
  /// (BP4 Q5: escalating bands), and a 7-day projection still cruising tilts
  /// AWAY from churn (saturation: the marginal seal funds a venture weeks out).
  /// The projection - stock minus measured weekly burn - is the operand, not
  /// current stock: "where will I be", not "where am I". No burn measurement
  /// means no saturation tilt, never a guess.
  /// </summary>
  private static RoutingVerdict Resolve(
    RoutingExit winner, long winnerScore, string winnerReason,
    (RoutingExit Exit, long? Score, string Reason) runnerUp,
    int? ventureStock, int? weeklyBurn, RoutingConfig cfg)
  {
    if (runnerUp.Score is not long rScore || rScore <= 0)
      return new(winner, winnerReason);

    var withinBand = winnerScore > 0
      && Math.Abs(winnerScore - rScore) <= winnerScore * cfg.RoutingReviewBandPct / 100.0;

    if (!withinBand)
      return new(winner, winnerReason);

    // Borderline + thin venture stock: the tie-break goes to churn.
    var gcContender = winner == RoutingExit.Gc ? winner
      : runnerUp.Exit == RoutingExit.Gc ? runnerUp.Exit
      : (RoutingExit?)null;
    if (gcContender is RoutingExit gcExit
        && ventureStock is int stock && stock < cfg.VentureBandFull)
    {
      var (reason, other, otherReason) = gcExit == winner
        ? (winnerReason, runnerUp.Exit, runnerUp.Reason)
        : (runnerUp.Reason, winner, winnerReason);
      return new(RoutingExit.Gc,
        $"{reason} Borderline vs {other} — tilted to turn-in ({stock:N0} tokens < {cfg.VentureBandFull:N0}).",
        RunnerUp: other, RunnerUpReason: otherReason);
    }

    // Borderline + saturated projection: the tie-break goes AWAY from churn.
    if (gcContender is RoutingExit satGc
        && ventureStock is int satStock && weeklyBurn is int wb && wb > 0
        && (long)satStock - wb > cfg.VentureBandCruise)
    {
      var projected = satStock - wb;
      var (keepExit, keepReason, gcSideReason) = satGc == winner
        ? (runnerUp.Exit, runnerUp.Reason, winnerReason)
        : (winner, winnerReason, runnerUp.Reason);
      return new(keepExit,
        $"{keepReason} Borderline vs turn-in — seals saturated (~{projected:N0} tokens in 7d at your burn, still > {cfg.VentureBandCruise:N0}), tilted away.",
        RunnerUp: RoutingExit.Gc, RunnerUpReason: gcSideReason);
    }

    return new(winner, winnerReason, IsReview: true,
      RunnerUp: runnerUp.Exit, RunnerUpReason: runnerUp.Reason);
  }

  /// <summary>Highest-scoring alternative among candidates that have evidence.</summary>
  private static (RoutingExit Exit, long? Score, string Reason) BestOf(
    params (RoutingExit Exit, long? Score, string Reason)[] candidates)
  {
    (RoutingExit, long?, string) best = (RoutingExit.Vendor, null, "");
    foreach (var c in candidates)
      if (c.Score is long score && score > (best.Item2 ?? long.MinValue))
        best = c;
    return best;
  }
}
