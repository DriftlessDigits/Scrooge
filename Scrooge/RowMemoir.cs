using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The card's memory (Drift, 08-02: "we know listing isn't the right play, but
/// the ledger doesn't bear that out"). The router's reason states its claim;
/// these lines state what the BOOK knows about the same variant - how thin and
/// how old the sale evidence is, how the last listing trial actually went, and
/// what the player himself last ruled. A card that says "List: sold at 20,803"
/// while our own 21-day failed ask sits banked two tables away is withholding
/// the strongest evidence in the house.
///
/// <para>Pure (the LanePricing/BoardLayout mold: no game, no storage, no ImGui;
/// linked into Scrooge.Tests). The window gathers the facts - once per selected
/// row, cached per refresh, never per frame - and this only composes them.
/// Every line is fact + consequence in player language; a fact the book does
/// not hold produces NO line, never a hedge.</para>
/// </summary>
internal static class RowMemoir
{
  /// <summary>
  /// What the book holds about one variant. Nullable throughout: a memoir only
  /// speaks the facts it has.
  /// </summary>
  /// <param name="TapeSales">Settled sales on the tape for this quality (0 = the tape is silent).</param>
  /// <param name="LatestSaleDaysAgo">Age of the newest of those, in whole days.</param>
  /// <param name="TrialPrice">The last banked ask for this variant, any retainer. Null = never listed on record.</param>
  /// <param name="TrialDaysStood">How long that ask stood (to its close, or to now while open).</param>
  /// <param name="TrialState">The receipt's outcome_state: open / cleared / never_cleared / gone_unobserved.</param>
  /// <param name="TrialTimeToClearDays">Days-to-sale when the trial cleared.</param>
  /// <param name="LastRuling">The player's own last ruling on this variant (short verb), or "".</param>
  /// <param name="IsStanding">
  /// WHETHER THE ROW THIS MEMOIR IS ABOUT IS ITSELF ON THE BOARD (pen 7 - the same seam
  /// <see cref="CaseEvidence.IsStanding"/> closes on the case page). The open-trial line
  /// said "you have a standing ask" off a receipt keyed to the VARIANT, which is true of
  /// the variant and not of the copy in the bags - so a bag row was told it was listed.
  /// The receipt is the same either way; what changes is who the sentence is about.
  /// </param>
  internal readonly record struct MemoirFacts(
    int TapeSales,
    int? LatestSaleDaysAgo,
    long? TrialPrice,
    int? TrialDaysStood,
    string TrialState,
    int? TrialTimeToClearDays,
    string LastRuling,
    bool IsStanding = false);

  /// <summary>The composed lines, top to bottom. Empty when the book is silent on every count.</summary>
  internal static List<string> Lines(in MemoirFacts f)
  {
    var lines = new List<string>();

    // The tape: how much the sale evidence actually is, and how stale. This is
    // the line that turns "sold at 20,803" into an argument you can weigh.
    // "observed", never "on your tape" (Drift, 08-02): plain language, and it
    // claims exactly what the book knows - the board's settled sales we
    // witnessed, anyone's, not just ours.
    if (f.TapeSales > 0)
    {
      var age = f.LatestSaleDaysAgo is int d
        ? $"the latest {OnMarket.DayAge(d)}"
        : "age unknown";
      lines.Add($"{f.TapeSales} sale{(f.TapeSales == 1 ? "" : "s")} observed, {age}.");
    }

    // The last listing trial: the experiment we already ran, and its verdict.
    if (f.TrialPrice is long price)
    {
      var stood = f.TrialDaysStood is int sd
        ? sd == 0 ? "since today" : $"for {sd}d"
        : "";
      switch (f.TrialState)
      {
        case "cleared":
          lines.Add(f.TrialTimeToClearDays is int ttc
            ? $"Your last ask at {price:N0} sold in {(ttc == 0 ? "under a day" : $"{ttc}d")}."
            : $"Your last ask at {price:N0} sold.");
          break;
        case "never_cleared":
          // COVER BOTH ACTS (08-22). This state is written by
          // GilStorage.CloseReceiptsNeverCleared, which fires on a pull AND on an
          // evict - "you pulled it" told a player he had done something the
          // retainer's slot did on its own. The finding is the same either way and
          // the line says the finding: it came off unsold.
          lines.Add($"Your last ask stood {stood} at {price:N0} and came off the board unsold.");
          break;
        case "gone_unobserved":
          lines.Add($"Your last ask at {price:N0} left the board while nobody watched (stood {stood}).");
          break;
        case "open":
          var onBoard = f.TrialDaysStood is int od
            ? od == 0 ? "listed today" : $"{od}d on the board"
            : "on the board";
          // THE OPEN RECEIPT IS ABOUT THE VARIANT, NOT THIS COPY (pen 7). A standing
          // row IS the ask, so it speaks in the second person. A bag row is not on the
          // board at all - telling it "you have a standing ask" left the reader
          // deciding between two things the sentence had merged.
          lines.Add(f.IsStanding
            ? $"You have a standing ask at {price:N0} ({onBoard})."
            : $"An ask at {price:N0} is still open for this item ({onBoard}) - this one is in your bags.");
          break;
      }
    }

    // The player's own precedent - a re-ask should read as a reminder.
    if (f.LastRuling.Length > 0)
      lines.Add($"You last ruled this: {f.LastRuling}.");

    return lines;
  }
}
