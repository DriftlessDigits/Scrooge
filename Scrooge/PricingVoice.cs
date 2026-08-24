using Scrooge.Windows;

namespace Scrooge;

/// <summary>
/// WHAT THE PRICING PASS SAYS, composed away from the game (review pricing split,
/// 2026-08-16).
///
/// <para>Four sentences and one run-log row, all of them derived from a
/// <see cref="PricingItem"/> and a <see cref="LaneDecision"/> and nothing else. They
/// used to sit as private statics inside the pipeline, wedged between unsafe addon
/// reads, where nothing could reach them - which meant the wording of a HOLD (the row
/// a player actually comes back to) was the one part of the pass with no pins on it.
/// Pure decision core in the <see cref="RunLogVoice"/> mold: no game reads, no storage,
/// no config statics. Compiled into Scrooge.Tests as a linked source.</para>
///
/// <para>Two operands are passed in rather than read here, and both for the same
/// reason - they are the only things these sentences needed from a running plugin.
/// The <c>EffectiveFloor</c> arrives as an argument (the floor's own words differ
/// between the player's minimum, the vendor and the Enclave), and the run-log row is
/// RETURNED rather than filed, because
/// <c>Plugin.Ledger</c> is a surface and this is a voice.</para>
/// </summary>
internal static class PricingVoice
{
  /// <summary>
  /// The row's own name column, quality included. The voice composers leave the item
  /// out of the sentence (Movement 1), so the name has to arrive as the name.
  /// </summary>
  internal static string VoiceName(PricingItem? item, string cleanName)
    => RunLogVoice.Name(cleanName, item?.IsHq ?? false);

  /// <summary>
  /// WHY THE LANE HELD, in one plain sentence for the run log. The long-form
  /// <see cref="HeldEvidence"/> is unchanged and still what the chat line, the
  /// triage flag and the decision cache quote - it just rides the hover now instead
  /// of the line. Two lengths of one fact, never two versions of it: the short one
  /// only ever names WHICH evidence ran out, and the long one carries the numbers.
  /// </summary>
  internal static string HeldReason(PricingItem? item)
    => item?.MbTimedOut == true
      ? RunLogVoice.Reasons.BoardSilent
      : RunLogVoice.Reasons.TooFewSales;

  /// <summary>
  /// WHY RECON WOULD NOT WRITE A PRICE - the guard that turned it down, or the
  /// evidence that ran out. Named per branch rather than collapsed into one sentence
  /// because a recon hold is the row a player comes back to, and "not enough sales"
  /// on an item the FLOOR refused sends him looking at the wrong thing.
  ///
  /// <para>A held spine with rows on the board is a different silence from a held
  /// spine with none: the board had sellers and not one of them was worth standing
  /// behind, which is a statement about the board, not about the tape.</para>
  /// </summary>
  /// <param name="floor">
  /// The effective floor as the one law resolved it, so the below-floor sentence can
  /// name the RIGHT floor (the Enclave pays twice vendor and says so; the player's own
  /// minimum says so too). Passed rather than read - see the type doc.
  /// </param>
  internal static string ReconHoldReason(PricingItem? item, in LaneDecision decision, EffectiveFloor floor)
  {
    if (item?.Result is PricingResult.BelowFloor)
      return RunLogVoice.Reasons.BelowFloor(floor, item.RejectedPrice ?? item.MbPrice ?? 0);
    if (item?.MbTimedOut == true)
      return RunLogVoice.Reasons.BoardSilent;
    return decision.Census.Sellers > 0
      ? RunLogVoice.Reasons.NothingWorthStandingBehind
      : RunLogVoice.Reasons.TooFewSales;
  }

  /// <summary>
  /// WHY THE LANE HELD, in the words every surface quotes. A genuine MB
  /// no-response reads identically to thin history by the time we get here, so
  /// <c>MbTimedOut</c> distinguishes it and the line says "didn't respond" instead
  /// of "too thin" - the difference between an item with no market and an item we
  /// failed to ask about.
  ///
  /// <para>Extracted for the second caller: recon holds the same items for the same
  /// reasons and must bank the same sentence. Composed once so the run log, the
  /// chat line, the triage flag detail and the decision cache cannot come to carry
  /// four wordings of one fact.</para>
  /// </summary>
  internal static string HeldEvidence(PricingItem? item)
  {
    var thin = item?.Lane?.Evidence ?? "history too thin to build a lane";
    if (item?.MbTimedOut == true)
      return $"market board didn't respond ({item.MbAttempts} attempts) - held; will retry next pinch.";
    return item?.CommunityQueued == true
      ? $"{thin} Checking community sales history — will retry next pinch."
      : thin;
  }

  /// <summary>
  /// One run-log line per non-routine lane outcome, named with its evidence —
  /// never a generic costume (disguised lines make features look unbuilt, and
  /// these lines double as calibration data). A plain Undercut stays quiet: it
  /// is the ordinary adjusted path the run log already counts, and under A10 it
  /// is what most boards produce.
  ///
  /// <para>Returns the row rather than filing it: null means "this outcome has
  /// nothing to say", which is the common case.</para>
  /// </summary>
  internal static (ItemOutcome Outcome, string Name, VoiceLine Voice)? LaneOutcomeEntry(
    PricingItem? item, string cleanName)
  {
    if (item?.Lane is not { } lane)
      return null;

    var outcome = lane.Outcome switch
    {
      LaneOutcome.CrazySkipped => ItemOutcome.CrazySkipped,
      LaneOutcome.EmptyBoard => ItemOutcome.EmptyBoard,
      LaneOutcome.PremiumFromNq => ItemOutcome.PremiumFromNq,
      _ => (ItemOutcome?)null,
    };
    if (outcome is not { } o)
      return null;

    // Line assembled here, where the final applied price is known: the
    // outcome is what actually happened to the listing, not the mid-decision
    // fact (a wall being ignored is folded into the reason, not the verdict).
    // A held item never reaches this method, so FinalPrice is a real ask.
    return (o, VoiceName(item, cleanName),
      RunLogVoice.Priced(item.CurrentListingPrice, item.FinalPrice ?? 0, lane.Census,
        lane.CraziesSkipped, lane.CrasherFloor, lane.CrasherCeiling, lane.Evidence));
  }
}
