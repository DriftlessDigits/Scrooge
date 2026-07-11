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
/// evidence (RoutingItemInputs) — same inputs, same pile, every time. Pure
/// functions: no game reads, no storage, unit-testable without FFXIV.
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

  internal static RoutingVerdict Evaluate(RoutingItemInputs item, int? ventureStock)
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

    var cfg = Plugin.Configuration;
    var stock = ventureStock is int s && s > 0 ? s : (int?)null;

    if (item.SealValue is int panicSeals && stock is int panicStock && panicStock < cfg.VentureBandPanic)
      return new(RoutingExit.Gc,
        $"Venture panic: {panicStock:N0} tokens (< {cfg.VentureBandPanic:N0}) — churning everything GC-eligible. {panicSeals:N0} seals.");

    // --- Value rules: gil-equivalent scores from local evidence ---

    var gcScore = item.SealValue is int seals ? (long)seals * cfg.SealToGilRate : (long?)null;
    var gcReason = item.SealValue is int gs
      ? $"Churn: {gs:N0} seals (~{gcScore:N0} gil at {cfg.SealToGilRate} gil/seal)."
      : "";

    var meltScore = item.MeltValuePerAttempt;
    var meltReason = meltScore is long mv ? $"Melt: yields ~{mv:N0} gil/attempt from your ledger." : "";

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
        ? ListingGate.ClearsEquipmentFloor(sale.Price, sale.SoldAfterDays)
        : sale.Price >= cfg.ListingWorthGil;
      if (clears)
      {
        listScore = sale.Price;
        listReason = sale.SoldAfterDays is int d
          ? $"List: sold at {sale.Price:N0} after {d}d listed."
          : $"List: sold at {sale.Price:N0}.";
      }
    }

    if (listScore is long list)
    {
      // Sub-750 stock: default to churn unless the item is worth a lot.
      if (gcScore is not null && stock is int lowStock && lowStock < cfg.VentureBandLow
          && list < (long)(cfg.ListingFloorGil * cfg.VenturePanicValueMultiplier))
        return new(RoutingExit.Gc,
          $"Venture low: {lowStock:N0} tokens (< {cfg.VentureBandLow:N0}) — churning unless it's worth {(long)(cfg.ListingFloorGil * cfg.VenturePanicValueMultiplier):N0}+. {gcReason}",
          RunnerUp: RoutingExit.List, RunnerUpReason: listReason);

      return Resolve(RoutingExit.List, list, listReason,
        BestOf((RoutingExit.Desynth, meltScore, meltReason),
               (RoutingExit.Gc, gcScore, gcReason),
               (RoutingExit.Vendor, vendorScore, vendorReason)),
        stock);
    }

    // Rule 5 — desynth skillup: scarce, gil can wait. Only blocked when the
    // ledger PROVES the yields are junk (known melt below vendor).
    if (item.DesynthSkillupEligible && (meltScore is null || meltScore >= vendorScore.GetValueOrDefault()))
      return new(RoutingExit.Desynth,
        $"Skillup: {item.DesynthColor?.ToString().ToLowerInvariant()} desynth at ilvl {item.Ilvl} — skillups are scarce, gil can wait.");

    // Rule 6 — GC turn-in: seals beat every gil exit, or low stock tilts it.
    if (gcScore is long gc)
    {
      var bestGil = Math.Max(meltScore ?? 0, vendorScore ?? 0);
      if (stock is int lowStock2 && lowStock2 < cfg.VentureBandLow)
        return new(RoutingExit.Gc,
          $"Venture low: {lowStock2:N0} tokens (< {cfg.VentureBandLow:N0}). {gcReason}");
      if (gc > bestGil)
        return Resolve(RoutingExit.Gc, gc, gcReason,
          BestOf((RoutingExit.Desynth, meltScore, meltReason),
                 (RoutingExit.Vendor, vendorScore, vendorReason)),
          stock);
    }

    // Rule 7 — melt for gil: yields must beat vendor meaningfully.
    if (meltScore is long melt && vendorScore is long vendor2)
    {
      if (melt > vendor2 * MeltOverVendorFactor)
        return Resolve(RoutingExit.Desynth, melt, meltReason,
          (RoutingExit.Vendor, vendorScore, vendorReason), stock);
      if (melt > vendor2)
        return new(RoutingExit.Desynth, meltReason, IsReview: true,
          RunnerUp: RoutingExit.Vendor,
          RunnerUpReason: $"{vendorReason} Melt lead is thin — attempt time may not pay.");
    }

    // Evidence-only: gear the router knows nothing about is YOUR call,
    // not a vendor trip. Leans List (the gate's Unknown flows to listing
    // as today) but lands in Review — the router won't guess.
    if (item.IsEquipment && item.LastSale is null && meltScore is null && gcScore is null)
      return new(RoutingExit.List,
        "No local evidence for this gear — never sold or melted one. Check the MB if it looks valuable.",
        IsReview: true, RunnerUp: RoutingExit.Vendor, RunnerUpReason: vendorReason);

    // Rule 8 — nothing beat the vendor.
    if (vendorScore is long vendor)
      return new(RoutingExit.Vendor, $"Vendor: {vendor:N0} gil — no better exit in evidence.");

    // Unvendorable, no evidence for anything else — honest shrug.
    return new(RoutingExit.Vendor, "No viable exit known (can't even vendor it).",
      IsReview: true);
  }

  /// <summary>
  /// Finalizes a value-rule winner against its best-scoring alternative.
  /// Within the review band the verdict degrades to Review — unless venture
  /// stock is below the full band and GC is the contender, in which case the
  /// borderline call tilts to churn (BP4 Q5: escalating bands).
  /// </summary>
  private static RoutingVerdict Resolve(
    RoutingExit winner, long winnerScore, string winnerReason,
    (RoutingExit Exit, long? Score, string Reason) runnerUp,
    int? ventureStock)
  {
    if (runnerUp.Score is not long rScore || rScore <= 0)
      return new(winner, winnerReason);

    var bandPct = Plugin.Configuration.RoutingReviewBandPct;
    var withinBand = winnerScore > 0
      && Math.Abs(winnerScore - rScore) <= winnerScore * bandPct / 100.0;

    if (!withinBand)
      return new(winner, winnerReason);

    // Borderline + thin venture stock: the tie-break goes to churn.
    var gcContender = winner == RoutingExit.Gc ? winner
      : runnerUp.Exit == RoutingExit.Gc ? runnerUp.Exit
      : (RoutingExit?)null;
    if (gcContender is RoutingExit gcExit
        && ventureStock is int stock && stock < Plugin.Configuration.VentureBandFull)
    {
      var (reason, other, otherReason) = gcExit == winner
        ? (winnerReason, runnerUp.Exit, runnerUp.Reason)
        : (runnerUp.Reason, winner, winnerReason);
      return new(RoutingExit.Gc,
        $"{reason} Borderline vs {other} — tilted to churn ({stock:N0} tokens < {Plugin.Configuration.VentureBandFull:N0}).",
        RunnerUp: other, RunnerUpReason: otherReason);
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
