using System;

namespace Scrooge.Windows;

/// <summary>
/// What happened to ONE item on a run - every run, not only a pinch: the melt, the
/// turn-in, the coffer rider and recon all file rows here, and have since Rounds unit 2.
/// </summary>
public enum ItemOutcome
{
  // RED MEANS BLOCKED, and it means only that (un-overloaded in 3b). This carried the
  // capped reprice until RepriceCapped took it - a row where a price WAS written, drawn
  // in the colour reserved for rows where none was - and it carries the press-time
  // warning no longer either (that is Warned). What is left is the true reading: a rule
  // refused, and nothing went on the board.
  Skipped,      // red — rule blocked, no price set
  NoData,       // yellow — the board answered with nothing; the player prices it or leaves it
  VendorSold,   // green — vendor-sold through retainer
  TurnedIn,     // green — GC Expert Delivery turn-in, paid in seals not gil
  Banned,       // blue — on ban list, observed but not changed
  Desynthed,    // grey — item destroyed via desynthesis, no price math
  Unlocked,     // green — item unlocked from a Venture Coffer by the coffer rider
  // Lane pricing outcomes (A10) — each named with its evidence, never a
  // generic costume. A plain undercut is the quiet ordinary path.
  CrazySkipped, // info — stepped over a lone lowball, cut in front of the cluster behind it
  EmptyBoard,   // green — nobody in the queue, listed at the top of demonstrated clearing
  // yellow — the lane would not write a price, and the ROW says which evidence ran out
  // (PricingVoice.HeldReason: a thin tape, or a board that never answered at all). The
  // run-summary roll-up counts these rows by reading those lines back.
  LaneHeld,
  PremiumFromNq,// green — no HQ tape and no queue, priced off its own NQ tape plus the premium
  // Rounds unit 2: the recon pass read the board, banked the decision, cancelled
  // the panel. Info-coloured, because nothing happened to the market - the line
  // reports what we LEARNED, and colouring it like a listing would claim an act
  // the pass deliberately did not perform.
  Reconned,
  // Rounds unit 3: a REAL listing, posted from a decision recon banked earlier -
  // no board was read this pass. Green like any other listing, because one went on
  // the market; the line itself carries the age, so the transcript can never make
  // a cached post look like a fresh board read.
  PostedFromRecon,
  // The crasher-guard's press-time warning (ruled 2026-08-21). Alarm-red, and its own
  // outcome rather than a Skipped row: nothing was decided, the round is ASKING, and
  // the row carries a proposal waiting on a confirm. Counting it as a skip would put a
  // decision in the tally that nobody made.
  Warned,
  // An APPLIED reprice whose climb the cap held to one step (ruled B7, built 3b-7).
  // It filed under Skipped until now and rendered red "rule blocked, no price set" -
  // over a row where a price WAS set, on the board, this pass. Its own outcome, drawn
  // like the listing it is: red goes back to meaning blocked. Appended, never
  // reordered - the bank stores these by NAME, and a legacy Skipped row stays a
  // Skipped row.
  RepriceCapped,
}

/// <summary>Run-level event type for lifecycle markers and summary lines.</summary>
public enum RunEvent
{
  Start,
  End,
  Summary
}

/// <summary>
/// Shared interface for all log items. Enables mixed-type list with insertion-order
/// rendering.
///
/// <para><b>THE STAMP IS THE ROW'S OWN</b> (review ruling S12, 2026-08-12: "stamp
/// lines at WRITE time"). Every row carries the instant it was written, set at
/// construction and never at banking time. The bank used to read one clock per
/// CHAPTER and give that instant to every row in it, so a stage's first and last
/// line shared a timestamp and <c>max - min</c> over a stage was structurally zero -
/// which made the derivable per-stage durations the round_runs ruling was bought for
/// (option B, "durations are the point") a promise the columns could not keep.</para>
/// </summary>
public interface ILogItem
{
  /// <summary>
  /// When this row was written. Defaulted at construction on every implementer, so a
  /// caller cannot forget it and a row cannot exist without one; settable only for
  /// the rehydrate, which restores the stamp the bank already holds rather than
  /// re-dating a line to the moment it was read back.
  /// </summary>
  DateTimeOffset WrittenAt { get; }
}

/// <summary>A single entry in the pinch run log.</summary>
public record LogEntry(ItemOutcome Outcome, string RetainerName, string ItemName, string Message) : ILogItem
{
  public DateTimeOffset WrittenAt { get; init; } = DateTimeOffset.UtcNow;

  /// <summary>
  /// THE EVIDENCE LAYER (Movement 1): the seller census, the ceiling counts, the
  /// sales range and the pace, shown on hover and never inline. Null on rows with
  /// nothing behind them - a GC turn-in has no board to census.
  ///
  /// <para>An init-only extra rather than a positional field on purpose: every
  /// existing construction site keeps working, and a row that forgets its evidence
  /// loses a tooltip rather than failing to exist.</para>
  /// </summary>
  public string? Hover { get; init; }
}

/// <summary>A run-level entry (start/end markers, summary stats).</summary>
public record RunEntry(RunEvent EventType, string Message) : ILogItem
{
  public DateTimeOffset WrittenAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Retainer section header. Inserted lazily on first entry for each retainer.</summary>
public record RetainerHeader(string RetainerName) : ILogItem
{
  public DateTimeOffset WrittenAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A desynth yield sub-row — one material obtained, rendered indented under
/// the desynthed item's entry. Value is the cached last-sale price (0 = unknown).
/// </summary>
public record YieldEntry(string YieldName, int Qty, bool IsHq, long Value) : ILogItem
{
  public DateTimeOffset WrittenAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// THE TRANSCRIPT'S TWO TRANSLATIONS: a drawn row flattened to the banked shape, and
/// a banked row drawn back into the shape it was written as.
///
/// <para>Pure, and living here rather than in the Ledger window, because neither
/// direction touches ImGui and both are exactly the kind of mapping that is worth
/// pinning: a stamp read off the wrong clock, or an outcome name a later build cannot
/// parse, is a silent loss the drawing code can never surface.</para>
/// </summary>
internal static class RoundLogEntry
{
  /// <summary>
  /// The two layers of one spoken row, packed into the detail column (Movement 1).
  ///
  /// <para>A format inside a text column, and worth naming as such - the same trade
  /// the yield sub-row already makes below, for the same reason. The alternative was
  /// a schema migration and a ninth column serving one of four row shapes, and the
  /// evidence layer is a thing people HOVER, not a thing anything queries. A unit
  /// separator is the delimiter because it is the one character no composed line can
  /// contain: every string on these surfaces is item names, gil, and prose.</para>
  ///
  /// <para>Rows banked before this existed carry no separator and restore with no
  /// hover, which is the honest answer - the evidence was never written down.</para>
  /// </summary>
  internal const char HoverSeparator = '\u001F';

  /// <summary>
  /// One log row, flattened to the banked shape, stamped with its OWN write time.
  ///
  /// <para>The yield sub-row is the only one that does not fit the columns as they
  /// stand - it carries a quantity and a gil value as well as a name - so its two
  /// numbers ride the detail column as <c>qty|value</c>. That is a format inside a
  /// text column and it is worth naming as such: the alternative was three more
  /// columns serving one of four row shapes, and the round's transcript is a thing
  /// people READ, not a thing anything queries by quantity.</para>
  /// </summary>
  internal static RoundLogLine? Flatten(ILogItem item, string stage)
  {
    var ts = item.WrittenAt.ToUnixTimeSeconds();
    return item switch
    {
      LogEntry e => new RoundLogLine(0, ts, stage, RoundLogKind.Entry,
        e.Outcome.ToString(), e.ItemName, Pack(e.Message, e.Hover)),
      RunEntry r => new RoundLogLine(0, ts, stage, RoundLogKind.Run,
        r.EventType.ToString(), "", r.Message),
      RetainerHeader h => new RoundLogLine(0, ts, stage, RoundLogKind.RetainerHeader,
        "", h.RetainerName, ""),
      YieldEntry y => new RoundLogLine(0, ts, stage, RoundLogKind.Yield,
        y.IsHq ? "HQ" : "NQ", y.YieldName, $"{y.Qty}|{y.Value}"),
      _ => null,
    };
  }

  /// <summary>
  /// One banked row, drawn back into the row shape it was written as. Fail-soft in
  /// both directions: an outcome name this build no longer knows renders as the
  /// generic entry, and an unparseable yield keeps its words and loses its numbers.
  /// A transcript is for reading - losing a line entirely would be worse than
  /// losing its styling.
  ///
  /// <para>The banked stamp rides back onto the row (S12). A rehydrated line is not
  /// written twice, so re-dating it here would cost nothing today - but a transcript
  /// whose restored half claims to have been written at reload time is a transcript
  /// that lies about the one column durations derive from.</para>
  /// </summary>
  internal static ILogItem Restore(RoundLogLine line)
  {
    var at = DateTimeOffset.FromUnixTimeSeconds(line.Ts);
    return line.Kind switch
    {
      RoundLogKind.Entry => RestoreEntry(line, at),
      RoundLogKind.RetainerHeader => new RetainerHeader(line.ItemName) { WrittenAt = at },
      RoundLogKind.Yield => RestoreYield(line, at),
      _ => new RunEntry(
        Enum.TryParse<RunEvent>(line.Outcome, out var ev) ? ev : RunEvent.Summary, line.Detail)
        { WrittenAt = at },
    };
  }

  /// <summary>The spoken line and its evidence layer, one column. Empty hover packs nothing.</summary>
  internal static string Pack(string message, string? hover)
    => string.IsNullOrWhiteSpace(hover) ? message : $"{message}{HoverSeparator}{hover}";

  /// <summary>
  /// The pack, undone. Everything past the FIRST separator is the hover, so a
  /// multi-line evidence layer round-trips whole.
  /// </summary>
  internal static (string Message, string? Hover) Unpack(string detail)
  {
    var cut = detail.IndexOf(HoverSeparator);
    return cut < 0 ? (detail, null) : (detail[..cut], detail[(cut + 1)..]);
  }

  private static ILogItem RestoreEntry(RoundLogLine line, DateTimeOffset at)
  {
    var (message, hover) = Unpack(line.Detail);
    return new LogEntry(
      // An outcome this build cannot name is not a lie about the item, it is an
      // absence of one - NoData is the row that already means exactly that.
      Enum.TryParse<ItemOutcome>(line.Outcome, out var outcome) ? outcome : ItemOutcome.NoData,
      "", line.ItemName, message) { WrittenAt = at, Hover = hover };
  }

  private static ILogItem RestoreYield(RoundLogLine line, DateTimeOffset at)
  {
    var parts = line.Detail.Split('|');
    var qty = parts.Length > 0 && int.TryParse(parts[0], out var q) ? q : 1;
    var value = parts.Length > 1 && long.TryParse(parts[1], out var v) ? v : 0;
    return new YieldEntry(line.ItemName, qty, line.Outcome == "HQ", value) { WrittenAt = at };
  }
}
