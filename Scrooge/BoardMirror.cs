using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// THE BOARD MIRROR (Drift, 08-22: "recreate the listing interface a bit, just
/// within triage"): the last-seen board and the banked sale tape for one item,
/// composed into the same two tables the game's own item detail shows - except
/// every listing row wears the WALK'S OWN CALL beside it. Built for the shakeout:
/// the case page speaks verdicts, and this is the raw evidence behind them,
/// badge-for-badge against the engine's actual read.
///
/// <para><b>The badges are receipts, not a second judgment.</b> The walk banks its
/// queue arithmetic on the decision it returns - the stepped prefix's size
/// (<see cref="LaneDecision.CraziesSkipped"/>), the census
/// (<see cref="LaneCensus.AboveCeiling"/>, <see cref="LaneCensus.Sellers"/>) - and
/// crashers are always the sorted queue's prefix, dreamers always its suffix, by
/// construction. So the labels here are pure arithmetic over (the same sorted
/// queue, the decision's own counts): no threshold is re-read, no test is re-run,
/// and the two-compositions drift the LaneEvaluation extraction exists to prevent
/// cannot start here. The one guard is honesty's: if the foreign row count does
/// not match the census the decision was taken against, every badge reads
/// Unjudged rather than guessing which board the walk saw.</para>
///
/// <para>Pure - no game reads, no storage, no statics (the LanePricing mold,
/// linked into Scrooge.Tests).</para>
/// </summary>
internal static class BoardMirror
{
  /// <summary>How many rows each table shows - the game's own page depth.</summary>
  internal const int RowCap = 20;

  /// <summary>The walk's call on one listing row, worn as a badge.</summary>
  internal enum MirrorCall
  {
    /// <summary>Our own retainer's row - the walk never judges it (Decide excludes own rows from the queue).</summary>
    Own,
    /// <summary>Stepped over: a lone crazy, or part of an outnumbered pack (A11).</summary>
    Crasher,
    /// <summary>The real queue - the line a buyer actually walks.</summary>
    Competitor,
    /// <summary>Above the ceiling rail - wishful asks the walk cannot cut in front of.</summary>
    Dreamer,
    /// <summary>Not in the judged line: an NQ row while the HQ lane is being priced (A12).</summary>
    OffLane,
    /// <summary>No walk on record for this board, or the board moved since it ran - no honest badge exists.</summary>
    Unjudged,
  }

  /// <summary>One board row, in buyer order, with the walk's call.</summary>
  internal readonly record struct MirrorRow(
    long UnitPrice, int Quantity, bool IsHq, bool IsOwn, string Retainer, MirrorCall Call);

  /// <summary>One settled sale off the banked tape, newest first.</summary>
  internal readonly record struct MirrorSaleRow(
    long UnitPrice, int Quantity, bool IsHq, string Buyer, string WhenText);

  /// <summary>
  /// The composed mirror: both tables capped at <see cref="RowCap"/>, each under a
  /// header that carries its own age - the same honesty grammar every other
  /// surface speaks (a read is only as good as its clock).
  /// </summary>
  internal readonly record struct MirrorModel(
    IReadOnlyList<MirrorRow> Rows, string BoardHeader,
    IReadOnlyList<MirrorSaleRow> Sales, string TapeHeader);

  /// <summary>
  /// Composes the mirror. <paramref name="decision"/> is the walk's verdict over
  /// THIS banked board (the LedgerCache preview seam - one composition, now three
  /// callers); null means no walk could run and every row reads Unjudged.
  /// </summary>
  internal static MirrorModel Compose(
    IReadOnlyList<MarketEvents.BoardListing> snapshot, long scanAt,
    IReadOnlyList<SaleHistorySchema.BankedSale> sales, bool itemIsHq,
    LaneDecision? decision, long nowUnix)
  {
    // The buyer's order, exactly as Decide sorts its queue: price ascending, ties
    // broken toward the better quality (at equal money every buyer takes the
    // better item first). Own rows keep their seat in the same line - the game
    // shows them there, and so do we.
    var rows = snapshot
      .OrderBy(l => l.UnitPrice)
      .ThenByDescending(l => l.IsHq && !itemIsHq)
      .ToList();

    // The judged line (A12): combined for an NQ item, HQ-only for an HQ item -
    // the same filter the preview seam feeds Evaluate. Rows outside it are
    // OffLane by definition, whatever the walk said.
    var inLane = rows.Select(l => itemIsHq ? l.IsHq : true).ToList();
    var foreignPrices = rows.Where((l, i) => inLane[i] && !l.IsOwn)
      .Select(l => l.UnitPrice).ToList();
    var foreignInLane = foreignPrices.Count;

    // The honesty guard: badge off the decision's banked counts only when the
    // decision was taken against THIS board - headcount AND geometry (ruled
    // 08-23, docket #2: the Pastel Green Dye board shuffled under a banked
    // decision whose seller count happened to collide, and a 2-gil seat
    // inherited "competitor" from a walk that never saw it). A count is a
    // coincidence; the banked crasher span and anchor row are an identity.
    // Any doubt and every badge reads Unjudged - a guessed badge is worse
    // than no badge.
    var judged = false;
    var skipped = 0;
    var dreamers = 0;
    if (decision is { } d && d.Census.Sellers == foreignInLane && GeometryMatches(d, foreignPrices))
    {
      judged = true;
      skipped = d.CraziesSkipped;
      dreamers = d.Census.AboveCeiling;
    }

    var calls = new List<MirrorRow>(rows.Count);
    var foreignSeen = 0;
    for (var i = 0; i < rows.Count; i++)
    {
      var l = rows[i];
      MirrorCall call;
      if (!inLane[i]) call = MirrorCall.OffLane;
      else if (l.IsOwn) call = MirrorCall.Own;
      else if (!judged) call = MirrorCall.Unjudged;
      else
      {
        // Crashers are the foreign prefix, dreamers the foreign suffix - the
        // walk's own geometry (both walks stop at the first row they keep, and
        // the rail is a price line the sort has already grouped).
        call = foreignSeen < skipped ? MirrorCall.Crasher
          : foreignSeen >= foreignInLane - dreamers ? MirrorCall.Dreamer
          : MirrorCall.Competitor;
        foreignSeen++;
      }
      calls.Add(new MirrorRow(l.UnitPrice, l.Quantity, l.IsHq, l.IsOwn, l.Retainer, call));
    }

    var boardHeader = rows.Count == 0
      ? "No board rows banked - the last scan saw an empty board, or this board has never been read."
      : rows.Count > RowCap
        ? $"Board as of {Durations.Ago(Math.Max(0, nowUnix - scanAt))} - {rows.Count} rows, showing the first {RowCap}"
        : $"Board as of {Durations.Ago(Math.Max(0, nowUnix - scanAt))} - {rows.Count} row{(rows.Count == 1 ? "" : "s")}";

    // The tape rides newest-first off the ring, both qualities - the flag column
    // says which line each sale settled in.
    var tape = sales
      .OrderByDescending(s => s.SaleTime)
      .Take(RowCap)
      .Select(s => new MirrorSaleRow(s.UnitPrice, s.Quantity, s.IsHq, s.BuyerName,
        Durations.Ago(Math.Max(0, nowUnix - s.SaleTime))))
      .ToList();

    var tapeHeader = tape.Count == 0
      ? "No sales banked - the tape has never spoken for this item."
      : sales.Count > tape.Count
        ? $"Sale tape - newest {tape.Count} of {sales.Count} banked"
        : $"Sale tape - {tape.Count} sale{(tape.Count == 1 ? "" : "s")} banked";

    return new MirrorModel(calls.Take(RowCap).ToList(), boardHeader, tape, tapeHeader);
  }

  /// <summary>
  /// The identity half of the honesty guard (docket #2): the decision's banked
  /// geometry replayed against the snapshot's foreign in-lane prices, in buyer
  /// order. Three checks, each only as strong as what the decision banked:
  /// a step's span must still stand at the front (crasher floor AND ceiling at
  /// their exact seats - a step that banked no span cannot prove itself and
  /// fails closed), and a listing anchor must still be the first kept row's ask.
  /// Decisions with no row identity at all (empty-board and premium prices)
  /// pass on whatever they did bank - the headcount gate still applies above.
  /// </summary>
  private static bool GeometryMatches(LaneDecision d, IReadOnlyList<long> foreign)
  {
    var skipped = d.CraziesSkipped;
    if (skipped > foreign.Count) return false;
    if (skipped > 0)
    {
      if (d.CrasherFloor is not long floor || d.CrasherCeiling is not long ceiling) return false;
      if (foreign[0] != floor || foreign[skipped - 1] != ceiling) return false;
    }
    if (d.AnchorIsListing && d.Anchor is long anchor)
    {
      if (skipped >= foreign.Count || foreign[skipped] != anchor) return false;
    }
    return true;
  }

  /// <summary>The badge's one word, spelled once.</summary>
  internal static string CallWord(MirrorCall call) => call switch
  {
    MirrorCall.Own => "yours",
    MirrorCall.Crasher => "crasher",
    MirrorCall.Competitor => "competitor",
    MirrorCall.Dreamer => "dreamer",
    MirrorCall.OffLane => "off-lane",
    _ => "",
  };
}
