using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>What one own write-side act did to the board.</summary>
internal enum WriteKind
{
  /// <summary>A listing we PUT on the board (a Hawk run listed it).</summary>
  Listed,
  /// <summary>A listing we TOOK OFF the board (pulled, or pulled-and-vendored).</summary>
  Removed,
  /// <summary>A standing listing whose ask we MOVED (triage reprice).</summary>
  Repriced,
}

/// <summary>
/// One own write-side act, with every operand the run already had in hand:
/// which listing, whose retainer, at what ask, how many. <see cref="PriorPrice"/>
/// is meaningful only for <see cref="WriteKind.Repriced"/> (the ask it moved FROM);
/// it is 0 everywhere else.
/// </summary>
internal readonly record struct OwnWrite(
  long WrittenAt,
  WriteKind Kind,
  uint ItemId,
  bool IsHq,
  string Retainer,
  long UnitPrice,
  long PriorPrice,
  int Quantity,
  string Source = "")
{
  /// <summary>What this act added to (or took from) the value standing on the board.</summary>
  internal long BookDelta => Kind switch
  {
    WriteKind.Listed => UnitPrice * Math.Max(1, Quantity),
    WriteKind.Removed => -(UnitPrice * Math.Max(1, Quantity)),
    WriteKind.Repriced => (UnitPrice - PriorPrice) * Math.Max(1, Quantity),
    _ => 0,
  };
}

/// <summary>
/// THE STANDING BOOK (WALK unit 6). Drift's question on 07-24 was "how much gil in
/// total is up right now?" - and he answered it by reconciling four screenshots by
/// hand, for a number the plugin held every operand for.
///
/// <para>The Listed header could only ever speak in LAST-PINCH TENSE, because the
/// only thing that ever taught the plugin about its own listings was a pinch
/// re-reading the sell lists. A Hawk run that put twenty items on the board wrote
/// nothing anywhere; the board's own value silently went stale until the next
/// pinch happened to re-learn our own actions. That missing write-side event is
/// what this closes: a run that CREATES or REMOVES listings already knows the
/// exact (item, retainer, price, qty) rows it touched, and now says so at
/// completion.</para>
///
/// <para><b>THE RESYNC IS STRUCTURAL, NOT PROCEDURAL.</b> The book is
/// baseline-plus-writes-since, where the baseline is the last full pinch scan and
/// "since" is that scan's timestamp. Nothing ever has to remember to reconcile,
/// prune, or zero anything: the next pinch advances the baseline, and every write
/// older than it falls out of the window on its own. There is no drift for a
/// procedure to fail to correct - which is the whole reason it is shaped this way
/// rather than as a running total someone has to maintain.</para>
///
/// <para>Pure and Dalamud-free (linked into the test project): the feed supplies
/// the baseline, the writes, and the sales; this answers what the board is worth
/// and how honestly it can say so.</para>
/// </summary>
internal static class StandingBook
{
  /// <summary>
  /// The live book-kept value standing on the board.
  ///
  /// <list type="bullet">
  ///   <item><paramref name="baselineGil"/> - gil-at-ask from the listings table,
  ///     the last GROUND TRUTH read (a pinch walked every sell list).</item>
  ///   <item><paramref name="writesSince"/> - our own listings placed, pulled, and
  ///     repriced since that read.</item>
  ///   <item><paramref name="salesGilSince"/> - what SOLD since that read
  ///     (retainer sales; chat capture sees these live, so the book deflates as
  ///     things clear rather than waiting for the next pinch to notice).</item>
  /// </list>
  ///
  /// Clamped at zero: a book that has drifted negative is wrong, and a negative
  /// "gil on the board" is a worse lie than a zero.
  /// </summary>
  internal static long Value(long baselineGil, IEnumerable<OwnWrite> writesSince, long salesGilSince)
  {
    var total = baselineGil - salesGilSince;
    foreach (var write in writesSince)
      total += write.BookDelta;
    return Math.Max(0, total);
  }

  /// <summary>
  /// The listing COUNT standing on the board, book-kept the same way <see cref="Value"/>
  /// is: the last scan's count, plus our own placings and pullings since, minus what
  /// sold. A listing is one sell-list slot whatever its stack size - which is also what
  /// a pinch walks, and why this exists: the pinch estimate must price the roster the
  /// run will actually visit, not the one the last read happened to see (ruled 08-16
  /// round walk - the estimate said 93 items over a live roster of 145, and the miss
  /// wore the pace bank's name until the arithmetic was run backwards).
  /// </summary>
  internal static int Count(int baselineCount, IEnumerable<OwnWrite> writesSince, int soldCountSince)
  {
    var total = baselineCount - soldCountSince;
    foreach (var write in writesSince)
      total += write.Kind switch
      {
        WriteKind.Listed => 1,
        WriteKind.Removed => -1,
        _ => 0, // a reprice moves an ask that was already standing
      };
    return Math.Max(0, total);
  }

  /// <summary>
  /// Gil we PUT UP in a window - the day's write-side sum. Only real new listings
  /// count: a reprice moves an ask that was already standing, and a pull is not
  /// putting anything up, so neither belongs in "what did I list today".
  /// </summary>
  internal static long PutUp(IEnumerable<OwnWrite> writes, long since)
  {
    long total = 0;
    foreach (var write in writes)
      if (write.Kind == WriteKind.Listed && write.WrittenAt >= since)
        total += write.BookDelta;
    return total;
  }

  /// <summary>
  /// Whether the book is currently saying anything a plain listings read would not.
  /// A book with no writes and no sales since the last scan IS the ground truth,
  /// and should say so plainly instead of hedging about book-keeping.
  /// </summary>
  internal static bool IsBookKept(int writeCount, long salesGilSince)
    => writeCount > 0 || salesGilSince > 0;

  /// <summary>
  /// The Listed header in BOTH TENSES - what is standing now, and how long ago we
  /// last actually looked. The provenance is the point: a book-kept number is an
  /// inference from our own acts layered on an aging read, and it must never wear
  /// the same confidence as a fresh walk of the sell lists.
  ///
  /// <para><paramref name="groundTruthAgeSeconds"/> null (never scanned) says so
  /// outright rather than implying a measurement nobody made.</para>
  /// </summary>
  internal static string Headline(long value, bool bookKept, long? groundTruthAgeSeconds)
  {
    // "book-kept" and "ground-truth" were our provenance words (strings-three);
    // the player-language fact is the same distinction: a plain number is what
    // the last look saw, a ~number is that look plus our own moves since it.
    if (groundTruthAgeSeconds is not long age)
      return bookKept
        ? $"~{value:N0} at ask (counting our own moves - no look at the retainers yet)"
        : $"{value:N0} at ask (no look at the retainers yet)";

    var read = $"last look {Durations.Elapsed(age)} ago";
    return bookKept
      ? $"~{value:N0} at ask ({read}, plus our own moves since)"
      : $"{value:N0} at ask ({read})";
  }
}
