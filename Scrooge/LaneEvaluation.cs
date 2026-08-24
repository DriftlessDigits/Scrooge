using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// The full answer the pricing spine gives for one lane: the electorate that
/// voted, the lane it built, the pace it read, and the decision it reached.
/// Callers that only want the number read <see cref="Decision"/>; the receipt
/// writer and the debug narration read the rest.
/// </summary>
internal readonly record struct LaneAnswer(
  RegimeSegmentModel Segment,
  LaneModel? Lane,
  double? Velocity,
  LaneDecision Decision);

/// <summary>
/// THE PRICING SPINE, MINUS THE GAME (extracted 08-02 for the listed-rows
/// build). One composition, two callers: the live pinch (packet + captured
/// board in hand) and the ledger's relist preview (banked tape + banked board
/// snapshot). The steps and their order are the pipeline's, verbatim:
/// quality-filter newest-first -> segment (A2: the electorate before the
/// arithmetic) -> local lane -> community substitution when the local lane is
/// too thin -> segment velocity (A5: one window, two readings) -> the
/// cross-quality rail + the NQ premium rung operands -> Decide -> the Sarcenet
/// clamp. Extracting it is what keeps the preview honest: a preview computed
/// by a second, slightly different composition would drift, and a drifted
/// preview is a contest nobody actually raised.
///
/// <para>Pure - no game reads, no storage, no statics (the LanePricing mold,
/// linked into Scrooge.Tests). Every input arrives as data; the callers own
/// where the data came from and how fresh it is.</para>
/// </summary>
internal static class LaneEvaluation
{
  /// <param name="localSales">The local ring for this item - both qualities, any order.</param>
  /// <param name="board">
  /// The decision queue, own rows included (Decide excludes them itself). For an
  /// NQ item this is the COMBINED physical board with each row's raw quality
  /// flagged - one queue, the list a buyer actually walks (A12); for an HQ item
  /// it stays HQ-only, because HQ-seeking buyers skip cheap NQ and that line is
  /// theirs alone.
  /// </param>
  /// <param name="itemIsHq">
  /// The quality of the ITEM being priced (A12) - the discriminator for which
  /// board rows are cross-quality, and the gate on building the HQ conviction
  /// lane. Distinct from <paramref name="hqPricing"/> on purpose: an HQ item
  /// priced off its NQ lane (HQ toggle off) still faces no better quality.
  /// </param>
  /// <param name="communityProvider">
  /// DC-scope settled sales, consulted ONLY when the local lane is too thin to
  /// speak - a PROVIDER, not a list, so the caller's fetch-queueing side effect
  /// fires exactly when the pipeline's always did: on need, never eagerly.
  /// Null provider or null result = no community data; the lane stays local.
  /// </param>
  /// <param name="fallbackVelocityProvider">
  /// The pace when no LOCAL segment can report one: the packet's rate or the
  /// almanac's, resolved by the caller - lazily, for the same reason.
  /// </param>
  internal static LaneAnswer Evaluate(
    IReadOnlyList<LaneSale> localSales,
    List<LaneListing> board,
    bool itemIsHq,
    bool hqPricing,
    LaneConfig cfg,
    long now,
    Func<IReadOnlyList<LaneSale>?>? communityProvider,
    Func<double?>? fallbackVelocityProvider,
    long? currentPrice)
  {
    // The electorate, before the arithmetic (A2): which of these sales still
    // describe the market we are pricing into. Quality-matched and newest
    // first - a regime is the newest RUN, so the segment walks backward from
    // now. Sales outside the active segment are DEMOTED, not down-weighted;
    // half-life keeps its unchanged job of smoothing within the segment (A6).
    var laneSales = localSales.Where(s => s.IsHq == hqPricing)
                              .OrderByDescending(s => s.Timestamp)
                              .ToList();
    var segment = RegimeSegment.Resolve(laneSales, cfg, now);
    var lane = LanePricing.BuildLane(segment.Sales, hqPricing, cfg, now);

    // Community fallback (lane design call #6): when the LOCAL lane is too thin
    // to price, consult DC-scope SALE history as a labeled community lane
    // before holding. Foreign listings never price anything, ever.
    if (lane == null || lane.SampleCount < cfg.MinHistorySamples)
    {
      var communitySales = communityProvider?.Invoke();
      if (communitySales != null)
      {
        var communityLane = LanePricing.BuildLane(communitySales, hqPricing, cfg, now, LaneSource.Community);
        if (communityLane != null && communityLane.SampleCount >= cfg.MinHistorySamples)
          lane = communityLane;
      }
    }

    // Velocity is a property of the ACTIVE SEGMENT (A5): one window, two
    // readings, so the anchor and the pace can never disagree. The fallback
    // rate survives only where there is no local segment to read.
    var velocity = lane?.Source == LaneSource.Local
      ? RegimeSegment.VelocityPerDay(segment.Sales)
      : null;
    velocity ??= fallbackVelocityProvider?.Invoke();

    // The HQ conviction lane (A12): what the better quality itself demonstrably
    // clears, so the walk can tell an honestly cheap HQ from an HQ crasher.
    // The SAME electorate discipline as every other lane - segment first, then
    // build - because a conviction is a receipt, and receipts get no weaker
    // standard than prices. LOCAL sales only, deliberately: stepping over a
    // better item takes "swagger and receipts" (Drift, 2026-08-05), and the
    // community's receipts are hearsay for that job. No HQ tape, no conviction
    // - the row stays a competitor and we price under it, which is the old
    // cross-quality cap surviving as the fail-closed default.
    LaneModel? hqLane = null;
    if (!itemIsHq)
    {
      var hqSales = localSales.Where(s => s.IsHq)
                              .OrderByDescending(s => s.Timestamp)
                              .ToList();
      if (hqSales.Count > 0)
      {
        var hqSegment = RegimeSegment.Resolve(hqSales, cfg, now);
        hqLane = LanePricing.BuildLane(hqSegment.Sales, isHq: true, cfg, now);
      }
    }

    // Rung 2 of the premium ladder (Phase 3b): the HQ side of an item with no
    // HQ tape of its own prices off its own NQ tape plus the premium. The NQ
    // operand runs the SAME electorate pipeline, just on the other quality -
    // a borrowed number gets no weaker discipline than the item's own.
    long? nqBandTop = null;
    if (hqPricing && !LanePricing.CanSpeak(lane, cfg))
    {
      var nqSales = localSales.Where(s => !s.IsHq)
                              .OrderByDescending(s => s.Timestamp)
                              .ToList();
      var nqSegment = RegimeSegment.Resolve(nqSales, cfg, now);
      var nqLane = LanePricing.BuildLane(nqSegment.Sales, isHq: false, cfg, now);
      // A lane that speaks always carries a band (LanePricing.BuildLane reads it
      // off the median's own walk), so the band top IS the NQ line.
      if (LanePricing.CanSpeak(nqLane, cfg))
        nqBandTop = (long)Math.Round(nqLane!.BandHigh);
    }

    // A10: the board is the operand and the tape is the witness.
    var decision = LanePricing.Decide(board, lane, velocity, cfg,
      currentPrice, hqLane, nqBandTop, itemIsHq);

    // The Sarcenet clamp (Phase 3b): a borrowed answer may never price above
    // everything that has actually cleared HERE.
    long maxLocalSettled = 0;
    foreach (var s in localSales)
      if (s.IsHq == hqPricing && s.UnitPrice > maxLocalSettled)
        maxLocalSettled = s.UnitPrice;
    decision = LanePricing.ClampToLocalClearing(decision, lane?.Source, maxLocalSettled);

    return new LaneAnswer(segment, lane, velocity, decision);
  }

  /// <summary>
  /// The honest relist NUMBER for a listed row's List cell (walk ruling 3,
  /// 08-02): what the spine would write today, or null when no honest List
  /// exit exists - a hold, or a number the floors refuse (refusing to price
  /// crasher-chasing is A10's personality; the cell draws a DASH).
  ///
  /// <para>An anchor that is a listing to undercut previews as one gil under
  /// it. The live pinch applies the configured undercut mode at the door; a
  /// preview that reproduced the mode (including the humanized roll) would be
  /// claiming a precision the door does not owe it. One gil under is the
  /// strictest honest reading of "in front of that row".</para>
  /// </summary>
  /// <param name="floor">
  /// THE ONE FLOOR LAW (ruled 2026-08-21), pre-resolved by the caller. The preview
  /// asks the pinch's own question - does the honest ask clear
  /// <c>max(minimum, mode floor)</c> - so a cell can never show a relist target the
  /// pinch would then refuse.
  /// </param>
  internal static long? HonestRelist(in LaneDecision decision, EffectiveFloor floor)
  {
    if (decision.Anchor is not long anchor || anchor <= 0)
      return null;
    var price = decision.AnchorIsListing || decision.CrossQualityCapped
      ? anchor - 1
      : anchor;
    if (price < 1) return null;
    return floor.Refuses(price) ? null : price;
  }
}
