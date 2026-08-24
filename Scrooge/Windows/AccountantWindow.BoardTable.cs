using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Scrooge.Board;

namespace Scrooge.Windows;

/// <summary>
/// THE BOARD, AS A TABLE (ruled ledger, stage 1). One row model, one column layout, one
/// set of flags - shared by every page the walk draws, because the whole readability claim
/// of the one table is that the pages LOOK like one table.
///
/// <para>The cells ARE the controls, and they are their own subject: what a cell shows,
/// says and does when clicked lives in <c>AccountantWindow.ScoreCell.cs</c>, shared with
/// the On Market tab. What is here is the table around them - the row a draw loop can
/// never ask twice, the eight columns, the sort, the groups and the badges.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  // --- The ruled board (stage 1) ---
  /// <summary>
  /// The row the detail pane is showing, by its stable board key. Survives a
  /// refresh: the key is the item's bag slot (or the listing's retainer), not its
  /// index, so a re-scan that reorders the board keeps the pane on the same row.
  /// </summary>
  private string? _selectedKey;

  /// <summary>
  /// One line of the board, whichever half of the Ledger it came from. Everything
  /// the table draws is precomputed here so the draw loop can never ask a question
  /// twice and get two answers, and so sorting can order the WHOLE board on one
  /// list before it is split back into its groups.
  /// </summary>
  private sealed class BoardRow
  {
    /// <summary>Stable across refreshes: the bag slot, or the listing's retainer.</summary>
    public required string Key { get; init; }
    public required BoardPile Group { get; init; }
    public required string Name { get; init; }
    public int Quantity { get; init; }
    /// <summary>"ilvl 640" for bag gear, "@ Kif" for a standing listing.</summary>
    public string Qualifier { get; init; } = "";
    /// <summary>Item level, its own sortable column since item 9. 0 = unknown, which sorts like an unpriced score.</summary>
    public int Ilvl { get; init; }
    /// <summary>
    /// This row is waiting on a human - <see cref="GatePlan.NeedsRuling"/>, the
    /// one definition. Precomputed with the call it produces, so the Call
    /// column's wording and the Flag column's sort can never read the predicate
    /// twice and get two answers.
    /// </summary>
    public bool NeedsRuling { get; init; }
    /// <summary>
    /// This row acts on the system's own call and says so - the Defer contract.
    /// Mutually exclusive with <see cref="NeedsRuling"/> by construction (see
    /// <see cref="DeferPlan.State"/>): a row cannot both be waiting on a human
    /// and be running without one.
    /// </summary>
    public bool Deferred { get; init; }
    /// <summary>Which named doubt the row acted through. None on a confident row, and on a Review row (nothing was decided to be doubtful about).</summary>
    public DoubtBranch Doubt { get; init; }
    /// <summary>The doubt, in the one line the row wears - on the HOVER since SF-P4. Empty when there is none.</summary>
    public string DoubtLine { get; init; } = "";
    /// <summary>
    /// The same doubt in the fewest words (SF-P4), drawn IN the Item cell beside
    /// the name - the sentence it summarises is one hover down. Empty exactly
    /// when <see cref="DoubtLine"/> is: no doubt, no tag.
    /// </summary>
    public string DoubtTag { get; init; } = "";
    /// <summary>
    /// A verb this lane had staged, withdrawn because the call moved under it
    /// (addendum 3). Empty on every other row. It rides the Item cell beside the doubt
    /// line for the same reason that one does: it is a SENTENCE about this row.
    /// </summary>
    public string ReAskNote { get; init; } = "";
    /// <summary>The four exit scores as the router weighed them, in strip order. Null = no evidence.</summary>
    public long?[] Scores { get; init; } = new long?[4];
    /// <summary>
    /// What this row is asking on the board RIGHT NOW (Movement 4). Null on a bag
    /// row - nothing of it is standing. Gathered once here and read by both the
    /// List cell (<see cref="BoardAskCell"/>) and the pane's state line, so the two
    /// surfaces cannot quote different asks for the same lane.
    /// </summary>
    public long? Ask { get; init; }
    /// <summary>What the Melt score IS - measured, the ilvl band's prior, or the skill-up knob (08-03).</summary>
    public MeltGrade MeltGrade { get; init; } = MeltGrade.None;
    /// <summary>The call. Bag rows via BoardCalls.Of, listed rows via OfListed (unit 4).</summary>
    public CallState? Call { get; init; }
    public required string CallText { get; init; }
    public ConfidenceTier Tier { get; init; }
    /// <summary>
    /// The human already ruled this row - a cell click, a staged verb, a re-applied
    /// persisted ruling. Precomputed here with the rest of the row's facts because the
    /// walk reads it (<see cref="TriageRowInput.PlayerResolved"/>) and so does the
    /// gate, and two derivations of "has he answered this" is how a case comes to be
    /// owed an answer it already has.
    /// </summary>
    public bool PlayerResolved { get; init; }
    /// <summary>
    /// A STAGE TAKES THIS PILE ENTIRE - the melt run's rows, tier and all. It is
    /// already told to <see cref="GatePlan.NeedsRuling"/> for the same reason it is
    /// carried here: a row the round spends regardless is not waiting on anybody, so
    /// it RIDES, and the walk must land it on the riders page rather than manufacture
    /// a case out of a decision nobody is being asked to make.
    /// </summary>
    public bool RidesWholePile { get; init; }
    /// <summary>
    /// WHERE THE ROUND WILL ACTUALLY SPEND IT, which is not always where it is drawn:
    /// a deferring row is drawn in Defer and spent in its exit pile (the 08-06 one-home
    /// rule). The riders page leads with the destination, so it needs the answer the
    /// group heading no longer gives.
    /// </summary>
    public BoardPile Destination { get; init; }
    public RoutedItem? Routed { get; init; }
    public InboxRow? Standing { get; init; }
    /// <summary>Which exits exist for this row. A closed door's cell is dead - hover says why, a click does nothing.</summary>
    public ExitDoors Doors { get; init; } = ExitDoors.AllOpen;

    /// <summary>The variant's item id, whichever side of the board it rode in on. 0 = unknown.</summary>
    public uint ItemId => Routed?.ItemId ?? Standing?.Item.ItemId ?? 0;

    /// <summary>
    /// The market has something to say about this row - it is standing on the board,
    /// or the List door is open. An untradeable piece gets no mirror button (Drift,
    /// 08-23: "no need for the board button if I can't list an item") - the mirror
    /// would open on a market the item cannot enter.
    /// </summary>
    public bool BoardSpeaks => Standing is not null || Doors.List;
  }

  /// <summary>
  /// Materializes the board. SILENT rows are deliberately excluded - a
  /// protected hold and an observed ban are confident verdicts not to engage,
  /// and a board that drew them would be asking the player to look at every
  /// decision the system is surest about. (This is where the Watch pile's
  /// tenants used to be skipped into a roll-up; the roll-up is gone.)
  /// </summary>
  private List<BoardRow> BuildBoardRows(List<InboxRow> standingRows)
  {
    var rows = new List<BoardRow>(_cache.Items.Count + standingRows.Count);

    foreach (var item in _cache.Items)
    {
      var pile = item.ActivePile;
      if (pile == BoardPile.Silent) continue;

      // ONE definition of unruled - the gate's own predicate, now including the
      // fact the queue used to keep to itself (item 9, 08-06): a pile a stage
      // takes ENTIRE is not waiting on anybody. The launch strip's count and the
      // cells drawn neutral read the same test in both directions, so they can
      // never disagree. A Mixed row in a gated pile is a decision the bell will
      // refuse and must LOOK like one; a Mixed row in the Melt pile is a row the
      // melt run will take regardless, and drawing it unruled was the board
      // asking for a click nothing was waiting on.
      //
      // AND, since Defer (08-06): a Mixed row that DEFERS is not waiting either
      // - the round spends it on schedule. The predicate is told, rather than
      // the board deciding for itself, for the same reason ridesWholePile is:
      // the launch refusal reads this and the two must never disagree about
      // which rows are in the way.
      var deferred = item.Deferred;
      var needsRuling = GatePlan.NeedsRuling(item.Confidence, item.PlayerResolved,
        pile == BoardPile.Review, ridesWholePile: pile == BoardPile.Melt, deferred: deferred);
      var call = BoardCalls.Of(item.Verdict.Exit, item.Verdict.IsReview, item.Pile,
        needsRuling, deferred);
      var scores = new long?[BoardCalls.Exits.Length];
      for (var i = 0; i < scores.Length; i++)
        scores[i] = BoardCalls.ScoreOf(item.Verdict.Scores, BoardCalls.Exits[i]);

      rows.Add(new BoardRow
      {
        Key = $"bag:{item.Container}:{item.SlotIndex}:{item.ItemId}:{item.IsHq}",
        Group = item.DrawnPile,
        Deferred = deferred,
        Doubt = deferred ? item.Doubt : DoubtBranch.None,
        DoubtLine = deferred ? DeferPlan.RowLine(item.Doubt) : "",
        DoubtTag = deferred ? DeferPlan.RowTag(item.Doubt) : "",
        Name = Format.Hq(item.Name, item.IsHq),
        Quantity = item.Quantity,
        Qualifier = $"ilvl {item.Ilvl}",
        Ilvl = item.Ilvl,
        NeedsRuling = needsRuling,
        Scores = scores,
        MeltGrade = item.Verdict.Scores?.MeltGrade ?? MeltGrade.None,
        Call = call,
        CallText = BoardCalls.Label(call),
        Tier = item.Confidence,
        PlayerResolved = item.PlayerResolved,
        RidesWholePile = pile == BoardPile.Melt,
        Destination = pile,
        Routed = item,
        Doors = item.Doors,
      });
    }

    foreach (var row in standingRows)
    {
      var pile = EffectiveStandingPile(row.Item);
      if (pile == BoardPile.Silent) continue;

      var natural = BoardPiles.ForStanding(row.Item.Result);
      var tier = _cache.ScoreStanding(row.Item, natural);

      // THE PINCH-SIDE lane_held REHOME (ruled 08-06, interim). ForStanding sends
      // a thin lane_held to Defer now, and that is the ONE listed shape that
      // defers: keeping the standing ask already IS the act, so there is
      // nothing withheld and nothing for the round to do differently. LABELING
      // ONLY - no behavior changed here, and the standing-book optimization
      // walk still owns the question of whether that ask should have moved.
      //
      // Every OTHER listed row is a CONTEST against a standing call, and a
      // contest is Review by contract: the round will not touch a standing
      // listing that nobody staged a verb for, so calling it deferred would be
      // the board claiming an action that no code path performs. "The advisor
      // advises" cuts both ways - it may not promise a verb it will not run.
      var deferredListed = natural == BoardPile.Defer;
      _actions.TryGetValue(row.Item, out var stagedVerb);
      var action = stagedVerb.Action;
      var call = BoardCalls.OfListed(ListedProposal(natural),
        natural == BoardPile.Review, contested: !deferredListed, action);
      var callText = deferredListed && action == StandingAction.None
        ? "defer: keep"
        : BoardCalls.ListedLabel(call, action);

      // The lane's own doubt, from the walk that produced this row's relist
      // preview; a held flag whose walk is long gone falls back to the branch
      // its reason class implies.
      var laneDoubt = _cache.ListedDoubtFor(
          row.Item.ItemId, row.Item.IsHq, row.Item.RetainerName)
        ?? BoardListings.DoubtOfFlagReason(row.Flag?.Reason ?? "");
      var doubt = deferredListed ? DeferPlan.Classify(laneDoubt, tier) : DoubtBranch.None;

      rows.Add(new BoardRow
      {
        Key = $"listed:{row.Item.ItemId}:{row.Item.IsHq}:{row.Item.RetainerName}",
        // The RE-ASK tell (addendum 3): this lane had a verb, a re-read moved its call,
        // and the verb was withdrawn. Said on the row because the row is where he will
        // answer it - and only while it is still true, since re-staging clears it.
        ReAskNote = _reAsked.GetValueOrDefault(
          (row.Item.ItemId, row.Item.IsHq, row.Item.RetainerName), ""),
        // An answered contest is drawn with its answer: the staged verb's own
        // group, so a staged melt sits in Melt beside the melt work it joins.
        Group = BoardCalls.PileOfAction(action, pile),
        Deferred = deferredListed && action == StandingAction.None,
        Doubt = doubt,
        // The kept-ask line, not the generic branch sentence: what this row did
        // was keep a number, and the number is the part worth reading.
        DoubtLine = deferredListed && action == StandingAction.None
          ? DeferPlan.KeptAskLine(row.Item.CurrentListingPrice ?? row.Flag?.OldPrice)
          : "",
        DoubtTag = deferredListed && action == StandingAction.None
          ? DeferPlan.KeptAskTag(row.Item.CurrentListingPrice ?? row.Flag?.OldPrice)
          : "",
        Name = Format.Hq(row.Item.ItemName, row.Item.IsHq),
        Quantity = row.Item.Quantity,
        Qualifier = $"@ {row.Item.RetainerName}",
        // The listed lane's ilvl came off the same routing inputs its scores
        // did; 0 when the scoring pass never reached the lane, which the column
        // draws as unknown rather than as item level zero.
        Ilvl = _cache.ListedFactsFor(row.Item.ItemId, row.Item.IsHq)?.Ilvl ?? 0,
        // A contest wants eyes whatever its evidence tier says (the same fact
        // OfListed reads to draw the row Unruled) - unless the player has
        // already answered it with a staged verb, or the row is the deferring
        // lane_held shape, which asks for nothing.
        NeedsRuling = !deferredListed && action == StandingAction.None,
        Scores = _cache.ListedRowScores(row.Item.ItemId, row.Item.IsHq, row.Item.RetainerName),
        // The standing ask, off the lane's own facts - the scan's price, or the
        // flag's memory of it when the scan never reached this one. Same operand
        // the kept-ask line above reads, gathered once for every reader of it.
        Ask = (row.Item.CurrentListingPrice ?? row.Flag?.OldPrice) is int ask && ask > 0 ? (long?)ask : null,
        MeltGrade = _cache.ListedMeltGrade(row.Item.ItemId, row.Item.IsHq),
        Call = call,
        CallText = callText,
        Tier = tier,
        // A staged verb IS the answer (the judgment queue's own rule: "ANY staged
        // action is a ruling - the human touched the row").
        PlayerResolved = action != StandingAction.None,
        // No listed row ever rides whole-pile: a standing listing is pulled or
        // repriced at the retainer one lane at a time (JudgmentQueue's note).
        RidesWholePile = false,
        Destination = BoardCalls.PileOfAction(action, pile),
        Standing = row,
        Doors = _cache.ListedDoors(row.Item.ItemId, row.Item.IsHq),
      });
    }

    return rows;
  }

  /// <summary>
  /// THE BOARD CALLS PAGE'S TABLE (Task 3, 08-15). Same eight columns, same order, same
  /// four-score strip lining up column-for-column from the top row to the bottom - that
  /// was always the entire readability claim, and it is unchanged.
  ///
  /// <para><b>The pile groups are gone with the panel.</b> They were the full board's
  /// answer to "where am I" over a screen holding every row on the ledger; a page that
  /// holds one kind of decision has nothing to group BY, and collapsible headers over
  /// four rows would be ceremony charging rent on the room the walk bought. The sort
  /// stays: it is per-page now, and a page is exactly the set a sort should order.</para>
  /// </summary>
  private void DrawPageTable(List<BoardRow> rows)
  {
    if (BeginBoardTable("BoardLayout", rows) is not { } sorted) return;

    foreach (var row in sorted)
      DrawBoardRow(row);

    ImGui.EndTable();
  }

  /// <summary>
  /// The six flags every board table wears. One declaration, because the whole
  /// readability claim of the one table rests on the pages LOOKING like one table -
  /// three copies of a six-flag list is three chances for a page to lose its sort or
  /// grow a border the page beside it does not have.
  /// </summary>
  private const ImGuiTableFlags BoardTableFlags = ImGuiTableFlags.RowBg
    | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.BordersOuterH
    | ImGuiTableFlags.Sortable | ImGuiTableFlags.Resizable
    | ImGuiTableFlags.NoBordersInBodyUntilResize;

  /// <summary>
  /// The preamble every board page shares: nothing to draw over an empty page, the
  /// flags, the columns, and the rows already in the order this table's own header
  /// asked for. Returns null when there is no open table to fill - the caller has
  /// nothing to end. What each page does with the sorted rows is its own business:
  /// Board Calls draws them straight, the riders page groups them first.
  /// </summary>
  private static List<BoardRow>? BeginBoardTable(string id, List<BoardRow> rows)
  {
    if (rows.Count == 0) return null;
    if (!ImGui.BeginTable(id, 8, BoardTableFlags)) return null;

    SetupBoardColumns();

    var (index, ascending) = TableSort.Spec();
    return SortBoard(rows, BoardLayout.ColumnAt(index), ascending);
  }

  /// <summary>
  /// The eight columns, in <see cref="BoardColumn"/>'s order - the enum is the layout,
  /// so a column can never be inserted without the sort following it (see
  /// <see cref="BoardLayout"/>). Wide enough for the glyph AND the sort arrow: a
  /// sortable column whose header clips its own arrow is a control nobody discovers.
  ///
  /// <para>Shared by every snap table the walk draws. The columns ARE the decision
  /// buttons, and two pages that set them up separately would be two layouts one edit
  /// away from disagreeing about which index the sort spec names.</para>
  /// </summary>
  private static void SetupBoardColumns()
  {
    ImGui.TableSetupColumn("!", ImGuiTableColumnFlags.WidthFixed, 30f);
    ImGui.TableSetupColumn("Item",
      ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 1f);
    ImGui.TableSetupColumn("ilvl",
      ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 38f);
    foreach (var exit in BoardCalls.Exits)
      ImGui.TableSetupColumn(BoardCalls.ColumnLabel(exit),
        ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 68f);
    ImGui.TableSetupColumn("Call", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 150f);
    ImGui.TableHeadersRow();
  }

  /// <summary>
  /// THE RIDERS PAGE'S TABLE (ruled 08-16; pull deleted 08-22). The riders stopped
  /// being a one-glance destination list and became the Board decisions table's
  /// sibling: the same eight columns, the same cells, the same rows. A score cell
  /// rules the row in place for the quick override, and a click on the ITEM NAME
  /// SELECTS - the same gesture it is on every other page (F2: one gesture, one
  /// meaning; disagreement is ruled inline from the detail pane, and the whole
  /// pulled-rider pipeline is gone).
  ///
  /// <para>Its own ImGui id, deliberately: sort state is per-table, and a page whose
  /// columns re-sorted because the player sorted the page before it would be a board
  /// re-ordering itself behind his back.</para>
  ///
  /// <para><b>The groups came back here and only here.</b> Board Calls holds one kind
  /// of decision and has nothing to group by; the riders are the whole rest of the
  /// round's spend, and "where is this going" is the question a reader scanning them
  /// is actually asking. What did NOT come back is the launch-control era around
  /// them - no banners, no bulk staging, no jump, no location tags. Those belonged to
  /// the standing piles, and riders are bag rows: the plan layer routes every standing
  /// row to Board Calls before it gets here (see <see cref="TriageWalk.PageFor"/>).</para>
  /// </summary>
  private void DrawRidersTable(List<BoardRow> rows)
  {
    if (BeginBoardTable("RidersLayout", rows) is not { } sorted) return;

    // GROUPED BY DESTINATION, not by the pile the row is drawn in. The old panel
    // grouped on the eyes pile because that was where a row LIVED; here a Defer row
    // already wears its amber badge and its doubt tag on the row itself, and what a
    // reader of the riders page needs from a heading is where the ride is about to
    // spend the thing. Destination is what the ride DOES.
    foreach (var pile in BoardLayout.GroupOrder)
    {
      var group = sorted.Where(r => r.Destination == pile).ToList();
      if (group.Count == 0) continue;
      DrawRiderGroup(pile, group);
    }

    ImGui.EndTable();
  }

  /// <summary>
  /// One destination pile: its header - count and what the pile is worth - and its
  /// rows. The header is a fold, open by default, on a STABLE id: nothing about a
  /// frame may re-fold a group the player left open.
  /// </summary>
  private void DrawRiderGroup(BoardPile pile, List<BoardRow> group)
  {
    var exit = BoardLayout.ExitOf(pile);
    var index = exit is RoutingExit e ? BoardCalls.IndexOf(e) : -1;
    // A pile with no destination column has nothing to price its rows AGAINST -
    // feeding it nulls would make the header claim "(3 unpriced)" about rows that
    // were never aimed anywhere. It gets a plain count; "unpriced" is the exit
    // piles' word.
    var summary = index >= 0
      ? BoardLayout.Summarize(group.Select(r => r.Scores[index]))
      : new PileSummary(group.Count, 0, 0);

    BeginFullWidthRow();
    ImGui.PushStyleColor(ImGuiCol.Text, RiderColor(pile));
    // The Melt header splits its currencies: knob-priced skill-ups are not gil and
    // are not summed as gil, and the gil half has to say how much of itself is a
    // band prior nobody ever weighed.
    var headerLine = pile == BoardPile.Melt
      ? BoardLayout.MeltHeaderLine(summary,
          group.Count(r => r.Routed?.SkillupColor == DesynthSkillupColor.Red),
          group.Count(r => r.Routed?.SkillupColor == DesynthSkillupColor.Yellow),
          _cache.SkillupRed, _cache.SkillupYellow,
          group.Count(r => r.MeltGrade == MeltGrade.Prior))
      : BoardLayout.HeaderLine(pile, summary);
    var open = ImGui.TreeNodeEx($"{headerLine}###ridegrp{pile}",
      ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanFullWidth
      | ImGuiTreeNodeFlags.NoTreePushOnOpen);
    ImGui.PopStyleColor();
    var hovered = ImGui.IsItemHovered();
    EndFullWidthRow();
    if (hovered) ImGui.SetTooltip(GroupHint(pile));
    if (!open) return;

    foreach (var row in group)
      DrawBoardRow(row);
  }

  /// <summary>
  /// What a destination pile MEANS, said once on the header rather than row by row.
  /// Only the piles a rider can be headed for are here: Review is never a destination
  /// (it is the question, not an answer) and Defer is not one either, because the
  /// grouping is by destination and a deferring row is still spent at its exit.
  /// </summary>
  private static string GroupHint(BoardPile pile) => pile switch
  {
    BoardPile.List => "Earns real gil on the market board. The round's bell run lists it.",
    BoardPile.Reprice => "A standing listing needs its price fixed (capped climb, or a deep-cut warning awaiting your confirm). The bell reprices what you confirmed.",
    BoardPile.PullAndVendor => "No better exit in evidence: pull the listing and/or vendor-sell. The bell does it at a retainer.",
    BoardPile.Melt => "Skillup value or yields beat the alternatives. The round's melt stage opens the salvage window with this pile pre-selected.",
    BoardPile.Churn => "Seals beat gil (or venture stock demands it). The round's turn-in stage, at your GC's Expert Delivery.",
    _ => "",
  };

  /// <summary>A pile's colour - the board's own palette, unchanged, so a melt reads the
  /// same amber on the riders group header it read on the board, and the same amber
  /// again on its case's Melt button.</summary>
  private static Vector4 RiderColor(BoardPile pile) => pile switch
  {
    BoardPile.List => ScroogeColors.Earned,
    BoardPile.Reprice => ScroogeColors.Warning,
    BoardPile.Churn => ScroogeColors.Warning,
    BoardPile.Melt or BoardPile.Defer => ScroogeColors.Amber,
    _ => ScroogeColors.Muted,
  };

  /// <summary>How far a full-width row may run before the clip rect stops caring.</summary>
  private const float FullWidthRowCeiling = 400f;

  /// <summary>
  /// Opens a table row whose content runs the FULL width of the window instead of
  /// being clipped to the Item column. ImGui tables have no cell span, and a group
  /// header belongs to the whole row rather than to one column. Widening the clip
  /// rect is how that is said; the column widths come from TableSetupColumn and are
  /// untouched by it.
  /// </summary>
  private static void BeginFullWidthRow()
  {
    ImGui.TableNextRow();
    ImGui.TableNextColumn();
    var min = ImGui.GetCursorScreenPos();
    var right = ImGui.GetWindowPos().X + ImGui.GetWindowWidth() - ImGui.GetStyle().ScrollbarSize;
    // The widened rect must still respect the WINDOW it lives in. The push
    // deliberately does not intersect the current clip (that is the column clip it
    // exists to escape), which means it escapes the scroll region's clip too - and a
    // header scrolled past the child's edge kept painting at its off-screen position,
    // floating over whatever the game had there. Clamping the rect's Y to the window
    // bounds keeps the full-width trick and hands scrolled-out rows the zero-height
    // clip they always deserved.
    var winTop = ImGui.GetWindowPos().Y;
    var winBottom = winTop + ImGui.GetWindowHeight();
    var top = Math.Clamp(min.Y, winTop, winBottom);
    var bottom = Math.Clamp(min.Y + FullWidthRowCeiling, top, winBottom);
    ImGui.PushClipRect(new Vector2(min.X, top), new Vector2(right, bottom), false);
    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(160f, right - min.X - 8f));
  }

  private static void EndFullWidthRow()
  {
    ImGui.PopTextWrapPos();
    ImGui.PopClipRect();
  }

  /// <summary>
  /// Orders the WHOLE board, groups and all. A row with no evidence for the sorted
  /// exit sorts to the bottom in both directions - "no number" is not a small
  /// number, and letting it masquerade as one is how a dash ends up at the top of
  /// a descending sort on gil. An unknown ilvl (a listed lane the scoring pass
  /// never reached) is the same kind of absence and gets the same treatment.
  /// </summary>
  private static List<BoardRow> SortBoard(List<BoardRow> rows, BoardColumn column, bool ascending)
  {
    if (BoardLayout.ExitOfColumn(column) is RoutingExit exit)
      return ByValue(rows, r =>
        // The List column sorts on the number it DRAWS (Movement 4) - the ask on a
        // standing row, the preview on a bag row - through the same call the cell
        // uses. Sorting the preview under a column showing the ask would order the
        // board by a number nobody can see.
        exit == RoutingExit.List
          ? BoardAskCell.Number(r.Doors.Has(exit) ? r.Ask : null, r.Scores[BoardCalls.IndexOf(exit)])
          : r.Scores[BoardCalls.IndexOf(exit)], ascending);

    if (column == BoardColumn.Ilvl)
      return ByValue(rows, r => r.Ilvl > 0 ? r.Ilvl : null, ascending);

    // THE FLAG COLUMN: the board sorted by how loudly it is asking for you. The
    // rank is pure and shared with the launch control's own definition of a row
    // that needs a human - see BoardLayout.AttentionRank.
    if (column == BoardColumn.Flag)
      return ByValue(rows,
        r => (long)BoardLayout.AttentionRank(r.Tier, r.NeedsRuling, r.Deferred), ascending);

    return (ascending
      ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
      : rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
  }

  /// <summary>
  /// The board's numeric columns, ordered by <see cref="BoardLayout.NullsLast"/> -
  /// named here so the column switch above reads as the board's own vocabulary rather
  /// than as four calls into a generic.
  /// </summary>
  private static List<BoardRow> ByValue(List<BoardRow> rows, Func<BoardRow, long?> key, bool ascending)
    => BoardLayout.NullsLast(rows, key, r => r.Name, ascending);

  // ---- Rows ----

  private void DrawBoardRow(BoardRow row)
  {
    ImGui.TableNextRow();

    // THE FLAG, in a column of its own now (item 9). It was a coloured glyph
    // wedged in front of the name: readable at a glance, and impossible to sort
    // on - so "show me everything that wants me" was a scroll, on a board whose
    // whole promise is one sortable table. Same glyph, same colours, same
    // sentence on hover; it just answers a sort spec now.
    ImGui.TableNextColumn();
    // THE FLAG SAYS THE EYES AXIS FIRST (08-06). A Defer row is usually Mixed,
    // and the Mixed badge's sentence - "needs your row click" - is exactly
    // false about it: nothing is waiting. Its own glyph, its own sentence, and
    // the same rank the column sorts on, so the badge and the sort agree.
    if (row.Deferred) DeferBadge(row);
    else TierBadge(row.Tier);

    ImGui.TableNextColumn();
    // THE TINT, KEPT - and made to mean something. An unsettled row wears its
    // tier on the name itself, so the row still reads without the glyph beside
    // it; a settled one draws plain, because a board where every row is
    // coloured is a board with no colour in it.
    var tinted = row.Tier != ConfidenceTier.Unanimous;
    if (tinted) ImGui.PushStyleColor(ImGuiCol.Text, TierColor(row.Tier));
    var label = row.Quantity > 1 ? $"{row.Name} x{row.Quantity}" : row.Name;
    if (ImGui.Selectable($"{label}##sel{row.Key}", _selectedKey == row.Key))
      _selectedKey = row.Key;
    if (tinted) ImGui.PopStyleColor();
    if (ImGui.IsItemHovered() && row.Qualifier.Length > 0)
      ImGui.SetTooltip($"{row.Qualifier} - click for the story and this row's other verbs.");

    // THE FETCH TELL (Drift, 08-06): a row whose Universalis ask is still out
    // wears an amber "..." beside its name, and sheds it THE FRAME the answer
    // lands - the queues drain, the predicate reads false, no refresh needed.
    // Text glyph, not an icon font: the board already speaks in "~", "-", "x".
    if (row.ItemId is uint fetchId and > 0
        && (UniversalisStats.IsPending(fetchId)
          || UniversalisHistory.StateOf(fetchId) is CommunityFetchState.Pending or CommunityFetchState.Retrying))
    {
      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
      ImGui.TextUnformatted("...");
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("A Universalis lookup is still out for this item - the verdict was scored without it. The dots clear when the answer lands; Refresh then re-scores with it.");
    }

    // THE DOUBT, ON THE ROW (Defer, 08-06). It rides the Item cell rather than a
    // column of its own because it is a fact about this row, not a rank - the
    // same reason the Call column never sorts - and because a Defer row is drawn
    // away from its exit pile, so the one thing it owes the reader is why it is
    // over here.
    // SF-P4 (08-15): the cell carries the TAG now. The sentence was a paragraph
    // in a table cell and the column clipped it mid-word - the same disease
    // Movement 4 cured on On Market, cured the same way: tag inline, sentence on
    // the hover, nothing deleted.
    if (row.DoubtTag.Length > 0)
    {
      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
      ImGui.TextUnformatted($"- {row.DoubtTag}");
      ImGui.PopStyleColor();
      if (row.DoubtLine.Length > 0 && ImGui.IsItemHovered())
        ImGui.SetTooltip(row.DoubtLine);
    }

    // THE RE-ASK, in the same seat and in AMBER (addendum 3). Muted would file it with
    // the notes; this is a question the round is waiting on, and it reads like one.
    if (row.ReAskNote.Length > 0)
    {
      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
      ImGui.TextUnformatted($"- {row.ReAskNote}");
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Your ruling was staged against a different call. The re-read moved it, so the round is asking again rather than spending an answer to a question it no longer has. Click a score cell to answer.");
    }

    ImGui.TableNextColumn();
    // Item level, out of the tooltip and into a column that sorts. A listed lane
    // the scoring pass never reached shows a dash - the board's own word for
    // "we don't know", never a zero that would sort like light gear.
    if (row.Ilvl > 0) RightAligned($"{row.Ilvl}", false);
    else ImGui.TextDisabled("-");

    for (var i = 0; i < BoardCalls.Exits.Length; i++)
    {
      ImGui.TableNextColumn();
      DrawScoreCell(row, i);
    }

    ImGui.TableNextColumn();
    var callColor = row.CallText.StartsWith("YOU", StringComparison.Ordinal) ? ScroogeColors.Info
      : row.CallText == "unruled" ? ScroogeColors.Amber
      // Defer is amber too, and deliberately the SAME amber: "this one is not
      // settled" is one fact, and two ambers would invite the player to look
      // for a difference that is said in the word beside them.
      : row.Deferred ? ScroogeColors.Amber
      : ScroogeColors.Muted;
    ImGui.PushStyleColor(ImGuiCol.Text, callColor);
    ImGui.Text(row.CallText);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(CallHint(row));
  }

  private static string CallHint(BoardRow row) => row.Deferred
    ? $"{row.DoubtLine} The round runs this either way - nothing here is waiting on you. "
      + "Click any score cell to send it somewhere else instead."
    : row.CallText switch
    {
      "unruled" => "Nobody has called this one. Click a score cell and that IS the ruling.",
      "router" => "The router's own call, unmoved.",
      _ => "Your call. Click the router's cell to hand it back.",
    };

  // ==========================================================================
  // Badges
  // ==========================================================================

  /// <summary>
  /// A tier's one colour, read by the badge AND by the board's row tint - two
  /// surfaces saying one fact, so they can never say it in two colours.
  /// </summary>
  private static Vector4 TierColor(ConfidenceTier tier) => tier switch
  {
    ConfidenceTier.Unanimous => ScroogeColors.Earned,
    ConfidenceTier.Contradicted => ScroogeColors.Spent,
    _ => ScroogeColors.Amber,
  };

  /// <summary>
  /// The DEFER badge. It was a question mark until SF-P5 (Drift, 08-15: <i>"are
  /// those questions? or statements"</i>) - and it is a statement: the row acted,
  /// it runs either way, and only the hover ever admitted that. The "?" is
  /// reserved for the rows that genuinely ask. The glyph and its sentence live in
  /// <see cref="DeferPlan"/> so the badge cannot say one thing and the pile's own
  /// vocabulary another.
  ///
  /// <para>Amber like the Mixed tier it usually sits on, because "not settled" is
  /// one fact and a second amber would invite the player to hunt for a difference
  /// the sentence already tells him.</para>
  /// </summary>
  private static void DeferBadge(BoardRow row)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
    ImGui.Text(DeferPlan.Badge);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip(DeferPlan.BadgeHint(row.DoubtLine));
  }

  private static void TierBadge(ConfidenceTier tier)
  {
    var (glyph, label) = tier switch
    {
      // V5 (ruled B3 batch): the bulk-confirm these labels taught died with the
      // advisor-era cleanup - the badge now says what the ROUND does with the tier.
      ConfidenceTier.Unanimous => ("*", "Unanimous - the evidence agrees; the round spends it without asking."),
      ConfidenceTier.Contradicted => ("!", "Contradicted - the market disagrees; it rides to the hinge."),
      _ => ("~", "Mixed - thin or partial evidence; your row click decides."),
    };
    ImGui.PushStyleColor(ImGuiCol.Text, TierColor(tier));
    ImGui.Text(glyph);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
  }

  /// <summary>Right-aligns a numeric cell against the column's own width (the score-cell rule).</summary>
  private static void RightAligned(string text, bool disabled)
  {
    var width = Math.Max(1f, ImGui.GetContentRegionAvail().X);
    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, width - ImGui.CalcTextSize(text).X));
    if (disabled) ImGui.TextDisabled(text); else ImGui.Text(text);
  }
}
