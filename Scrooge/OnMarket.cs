using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Pure core for the Ledger's ON MARKET tab (ruled ledger, stage 3): what we have
/// asked for, on which retainer, at what price, and when we asked it - and WHY it
/// took the queue slot it took, in the numbers the decision banked (V35). Renders
/// from BANKED data only - the V20+ decision receipts. No fetch machinery lives
/// here or upstream of here: nothing on this tab can cause a read.
///
/// <para><b>What "currently listed" means, and what it does not.</b> A decision
/// receipt is the record of an ask we wrote. It closes three ways: a GilTrack
/// sale confirm ('cleared'), an observed pull/evict ('never_cleared'), or the
/// pinch reconciler finding the lane absent from a full sell-list read
/// ('gone_unobserved' - see GilStorage.CloseReceiptsGoneUnobserved, 08-02). So an
/// OPEN receipt means <i>the lane stood at the last full read</i> - a listing that
/// sold or expired while the game was shut stays open only until the next pinch
/// proves its absence.</para>
///
/// <para>The tab therefore claims only what the receipt can prove: the ask, the
/// retainer, and the moment it was written ("listed 2h ago at 48,997"). It never
/// says "on the board right now". <see cref="Caveat"/> puts that limit on the
/// surface rather than in a comment nobody reads.</para>
///
/// <para><b>Supersession.</b> Repricing the same listing writes a NEW receipt for
/// the same lane (item, quality, retainer) without closing the old one - the old
/// ask simply stopped standing when its successor was written. That is the same
/// rule the A9 final backfill uses ("superseded beats sold"), so only the NEWEST
/// open receipt per lane speaks for a listing here. Older opens are history, not
/// extra listings, and counting them would inflate the tab.</para>
///
/// <para>In the LanePricing/BoardListings mold: no game reads, no storage, no
/// Dalamud statics, linked into Scrooge.Tests.</para>
/// </summary>

/// <summary>
/// One banked decision receipt, projected for this tab. The window resolves the
/// item name (a game read) and hands it in, exactly as the Listed pile does.
/// </summary>
internal readonly record struct ReceiptLine(
  long Id,
  long CreatedAt,
  uint ItemId,
  bool IsHq,
  string ItemName,
  string Retainer,
  int Quantity,
  long? DecidedPrice,
  int? QueuePosition,
  string OutcomeState,
  //
  // The A9 interim verdict, carried here and not drawn on THIS tab. The stamps do
  // reach a surface - the detail pane's receipt trail draws them off its own read
  // (BoardDetail.Stamp) - but the On Market tab has never spent its copy. It rides
  // here because the column that would show it is a layout decision, not a
  // plumbing one, and the plumbing is already done.
  //
  string? InterimGrade,
  // The V35 story-in-numbers: the anchor cluster's headcount and the gil spans
  // of the stepped-over prefix and the real line. Zero/null = pre-V35, or no
  // such rows in that verdict.
  int ClusterSize = 0,
  long? CrasherFloor = null,
  long? CrasherCeiling = null,
  long? ClusterFloor = null,
  long? ClusterCeiling = null,
  // The V37 headcount: what the stepped-over pack was outnumbered BY - the whole
  // competitor queue behind it, which is the number the A11 test actually read.
  // Null = no pack step in that verdict, or pre-V37.
  int? CrowdBehind = null);

/// <summary>One drawn row: the banked facts, ready to draw.</summary>
internal readonly record struct OnMarketRow(
  uint ItemId,
  bool IsHq,
  string ItemName,
  string Retainer,
  int Quantity,
  // The V26 decided_price - the absolute ask. Null on a receipt that could not name one.
  long? Price,
  // The receipt's created_at: when we decided this ask, not when the item hit the board.
  long ListedAt,
  // "2h ago" - relative to the frame's clock.
  string ListedLabel,
  // The V25 queue_position at decision time. Null = held, or written pre-V25.
  int? QueuePosition,
  // The queue position's story in numbers ("stepped over 16 at 795-999 - the
  // line: 24 at 1,000-1,500"), or null when the receipt banked no spans. Drawn
  // as the Pos@list tooltip: a raw queue number without its story is
  // uninterpretable - slot 16 could be bait-riddled dominance or genuine
  // trouble, and only the prices say which (Drift, 08-02).
  string? QueueStory,
  // The same verdict in the fewest plain words ("behind 16 low balls"), drawn IN
  // the cell since Movement 4 - the story it summarises is one hover away. Null
  // whenever the story is: no banked spans, no tag.
  string? QueueTag,
  // Carried for the A9 column fast-follow. No cell on this tab draws it; the
  // detail pane's trail is where the stamps surface today.
  string? InterimGrade,
  // The four-score strip in board order - List (the relist preview), Melt, GC,
  // Vend (walk unit 3, 08-02). Null = the window supplied no scores; a null
  // ELEMENT = that exit has no evidence, drawn as a dash like every board dash.
  long?[]? Scores = null,
  // What the Melt score IS (08-03): a measurement of this item, the ilvl band's
  // average, or the skill-up knob. The strip has four numbers and no room for
  // provenance, so the grade rides beside them and the cell wears the tell.
  MeltGrade MeltGrade = MeltGrade.None);

internal static class OnMarket
{
  /// <summary>An open receipt is one nothing has closed - see the type doc for the limits.</summary>
  internal const string OpenState = "open";

  // ========================================================================
  // Standing set - the newest open receipt per listing lane
  // ========================================================================

  /// <summary>
  /// The receipts that speak for a standing ask: open, and newest in their lane
  /// (item, quality, retainer). Ties on created_at break on id - the later insert
  /// is the later decision. Newest first, then retainer, then name, so the default
  /// order reads as a worklist and is stable across frames.
  /// </summary>
  internal static List<ReceiptLine> Standing(IEnumerable<ReceiptLine> receipts)
    => receipts
      .Where(r => string.Equals(r.OutcomeState, OpenState, StringComparison.Ordinal))
      .GroupBy(r => (r.ItemId, r.IsHq, r.Retainer))
      .Select(g => g.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).First())
      .OrderByDescending(r => r.CreatedAt)
      .ThenBy(r => r.Retainer, StringComparer.Ordinal)
      .ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase)
      .ToList();

  // ========================================================================
  // Relative time - one grammar, no invented precision
  // ========================================================================

  /// <summary>
  /// "just now" / "12m ago" / "2h ago" / "3d ago" - see <see cref="Durations.Ago"/>,
  /// which owns the grammar. Kept under this name because every table in the Ledger
  /// stamps its rows through here.
  /// </summary>
  internal static string Relative(long secondsAgo) => Durations.Ago(secondsAgo);

  /// <summary>Relative age of a stamp against the frame's clock (never negative).</summary>
  internal static string RelativeAt(long at, long now) => Relative(Math.Max(0, now - at));

  /// <summary>
  /// "today" / "3d ago" for evidence already counted in WHOLE DAYS - see
  /// <see cref="Durations.DayAge"/>, which owns the grammar now (08-22). Kept under
  /// this name because the voice layer's four sentences all call it here, exactly as
  /// <see cref="Relative"/> keeps its name over <see cref="Durations.Ago"/>.
  /// </summary>
  internal static string DayAge(int days) => Durations.DayAge(days);

  // ========================================================================
  // The queue position's story
  // ========================================================================

  /// <summary>
  /// The Pos@list number's story, in the numbers the decision banked (V35) -
  /// never the run log's novel (Drift, 08-02: the run log "writes a novel that I
  /// don't think needs to fit in the ledger. But I do want to know what the
  /// crashers were"). "stepped over 16 at 795-999, outnumbered by the 24 behind
  /// them - the line: 8 at 1,000-1,500". Null when the receipt banked no spans
  /// (pre-V35, or a held row) - no numbers, no story, never a hedge.
  ///
  /// <para>The old glimpse note died here: a live below-count merely restated
  /// Pos@list, because the walk lists behind bait on purpose (Drift, 08-02: "I
  /// can infer N from the pos. number. that isn't useful").</para>
  /// </summary>
  internal static string? QueueStory(ReceiptLine r)
  {
    var parts = new List<string>();
    if (r.QueuePosition is int q and > 0 && r.CrasherFloor is long cf)
    {
      var stepped = q == 1
        ? $"stepped over a lone lowball at {cf:N0} - nobody near it"
        : $"stepped over {q} at {Span(cf, r.CrasherCeiling ?? cf)}";
      // NAME THE COUNT THAT WON THE TEST (V37). A pack is stepped because the
      // sellers BEHIND it outnumber it - and the cluster we then undercut is a
      // different, usually smaller number. Printing only the cluster read
      // "stepped over 4 - the line: 3" on a board twenty deep (Drift, 08-03), so
      // the story now says what the pack lost to.
      if (r.CrowdBehind is int crowd && crowd > 0)
        stepped += $", outnumbered by the {crowd} behind them";
      parts.Add(stepped);
    }
    // TWO LONERS, TWO STORIES (08-03). "Stepped over a lone lowball" and
    // "undercut a loner" were one sentence shape in one visual slot, and they
    // are opposite events: the first is a row we refused to price against
    // because nothing stood near it and it sat far under everything that
    // clears; the second is a legitimate seller with no company, which the
    // lone-crazy test looked at and let stand. The cell has to say which act
    // it was, because the reader's next move differs.
    if (r.ClusterFloor is long lf)
      parts.Add(r.ClusterSize > 1
        ? $"the line: {r.ClusterSize} at {Span(lf, r.ClusterCeiling ?? lf)}"
        : $"undercut the lone competitor at {lf:N0} - alone, but not nonsense");
    return parts.Count == 0 ? null : string.Join(" - ", parts);
  }

  /// <summary>
  /// MOVEMENT 4 - THE QUEUE STORY, RE-LAYERED (ruled 2026-08-13). The story is a
  /// full sentence with four numbers in it, and it was drawn INLINE beside the item
  /// name on every row standing behind bait - a paragraph in a table cell, on a
  /// surface whose whole job is a scan. Nothing is deleted: the sentence moves to
  /// the hover (the same trade Movement 1 made of the run log) and the cell keeps
  /// this - the verdict, in the fewest plain words that still say WHY the row is
  /// sitting where it is.
  ///
  /// <para>Plain words, not the tag vocabulary: "low balls", never
  /// crashers/bait/dreamers - <see cref="RunLogVoice"/>'s rule 4, which exists
  /// precisely so the internal names stay in the evidence layer.</para>
  ///
  /// <para>The stepped-over pack wins when both facts are banked: it is the one
  /// that explains the POSITION, which is the number the reader is looking at when
  /// the tag catches his eye. The line it undercut is in the hover with everything
  /// else. Null when the receipt banked no spans - no numbers, no tag, exactly as
  /// <see cref="QueueStory"/> refuses to hedge.</para>
  /// </summary>
  internal static string? QueueTag(ReceiptLine r)
  {
    if (r.QueuePosition is int q and > 0 && r.CrasherFloor is not null)
      return q == 1 ? "behind a lone low ball" : $"behind {q} low balls";
    if (r.ClusterFloor is not null)
      return r.ClusterSize > 1 ? $"under {r.ClusterSize} sellers" : "under one seller";
    return null;
  }

  /// <summary>"795-999", or just "795" when the span is one price.</summary>
  private static string Span(long floor, long ceiling)
    => floor == ceiling ? $"{floor:N0}" : $"{floor:N0}-{ceiling:N0}";

  // ========================================================================
  // Row build
  // ========================================================================

  /// <summary>
  /// Builds the drawn rows: every standing receipt carrying its banked facts,
  /// including the queue story its decision earned. Banked data only - nothing
  /// here can cause a read, and nothing fresher than the receipt speaks.
  /// </summary>
  internal static List<OnMarketRow> Build(
    IEnumerable<ReceiptLine> receipts,
    long now,
    IReadOnlyDictionary<(uint ItemId, bool IsHq, string Retainer), long?[]>? laneScores = null,
    IReadOnlyDictionary<(uint ItemId, bool IsHq), MeltGrade>? meltGrades = null)
  {
    var rows = new List<OnMarketRow>();
    foreach (var r in Standing(receipts))
    {
      long?[]? scores = null;
      laneScores?.TryGetValue((r.ItemId, r.IsHq, r.Retainer), out scores);
      var grade = MeltGrade.None;
      meltGrades?.TryGetValue((r.ItemId, r.IsHq), out grade);

      rows.Add(new OnMarketRow(
        r.ItemId, r.IsHq, r.ItemName, r.Retainer, Math.Max(1, r.Quantity),
        r.DecidedPrice, r.CreatedAt, RelativeAt(r.CreatedAt, now),
        r.QueuePosition, QueueStory(r), QueueTag(r), r.InterimGrade, scores, grade));
    }
    return rows;
  }

  // ========================================================================
  // Honest headline
  // ========================================================================

  /// <summary>
  /// The tab's headline: the count and the gil we are asking for across it. "at
  /// ask" is deliberate - it is what the board would pay if every row cleared at
  /// its written price, not money we have.
  /// </summary>
  internal static (int Count, long GilAtAsk) Headline(IEnumerable<OnMarketRow> rows)
  {
    var count = 0;
    long gil = 0;
    foreach (var r in rows)
    {
      count++;
      if (r.Price is long p && p > 0) gil += p * Math.Max(1, r.Quantity);
    }
    return (count, gil);
  }

  /// <summary>
  /// The limit, said out loud on the surface rather than buried in a comment. The
  /// tab is a record of asks, not a claim about the board: everything here was
  /// decided by us, and nothing here has been re-checked.
  /// </summary>
  internal static string Caveat()
    => "These are asks we wrote and nothing has closed - a sale confirm or an observed pull "
     + "is what retires a row. A listing that sold or expired while you were away still reads "
     + "here until the next pinch looks. Nothing on this tab fetches anything.";
}
