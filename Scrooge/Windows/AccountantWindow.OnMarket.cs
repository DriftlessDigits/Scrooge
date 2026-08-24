using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;

using Scrooge.Board;

namespace Scrooge.Windows;

/// <summary>
/// ON MARKET: what you have up, right now. Status of the WORLD rather than a step of an
/// errand - which is why it is hosted by the gil dashboard since Movement 3 and drawn by
/// this class, whose caches, receipts read and staged verbs it is made of.
///
/// <para>It needs no Round and never did. The only thing a live Round adds is the CONTEST
/// lookup, and with no run there are none - which reads exactly as it should: every ask
/// stands, and nobody owes it a click.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  // --- The player's own contests (walk unit 4): standing asks he clicked a
  // score cell on. They ride the triage machinery - staged verb in _actions,
  // spent by the round, dismissable - but no pinch produced them, so they live
  // here rather than in the run's triage list. Keyed per lane so a re-click
  // finds the same item the staging dictionary already holds. ---
  private readonly List<PricingItem> _playerContests = [];
  private readonly Dictionary<(uint ItemId, bool IsHq, string Retainer), PricingItem> _contestItems = [];

  /// <summary>
  /// THE STANDING ASKS, ON THE DASHBOARD (Movement 3). Reading what is on the market
  /// used to cost a started Round (Drift, 08-13: "if I want to see what is on the Market,
  /// I need to open the Gil Dashboard, start a round (which likely kicks off a pinch),
  /// stop that, and then look at what is on the market"). Reading the world must never
  /// require starting an errand.
  ///
  /// <para>Every row is derived from banked receipts (<see cref="OnMarket.Standing"/>)
  /// at refresh time; the score strip comes from the same Refresh-time caches the
  /// board's rows do, and a lane nobody has scored draws a dash rather than a number.</para>
  /// </summary>
  internal void DrawOnMarketPanel()
  {
    PrimeForDashboard();
    DrawOnMarketTab(BuildStandingRows());
  }

  /// <summary>
  /// The On Market tab: one row per standing ask, in the board's table grammar.
  /// Numerics right-aligned, sortable, banked facts only - see <see cref="OnMarket"/>
  /// for exactly what "standing" can and cannot claim.
  /// </summary>
  private void DrawOnMarketTab(List<InboxRow> standingRows)
  {
    if (_cache.OnMarketRows.Count == 0)
    {
      ImGui.TextDisabled("No open asks on record - nothing has been priced yet, or every receipt has closed.");
      return;
    }

    // The Call column's live lookup: which standing asks are CONTESTED (a board
    // triage row exists for the same item + retainer) and which contests the
    // player already answered (a staged verb). Computed per frame from the same
    // rows the board draws, so this surface and the hinge can never disagree about
    // who owes whom a click. A DEFER row is not a contest: the system already acted and
    // nobody owes it a click, so it must not make a standing ask read contested.
    var contests = new Dictionary<(uint ItemId, bool IsHq, string Retainer), StandingAction?>();
    foreach (var r in standingRows)
    {
      if (EffectiveStandingPile(r.Item) is BoardPile.Defer or BoardPile.Silent) continue;
      contests[(r.Item.ItemId, r.Item.IsHq, r.Item.RetainerName)] =
        _actions.TryGetValue(r.Item, out var a) ? a.Action : null;
    }

    // ONE value claim (gate 9b): this header used to say the receipts' own gil
    // sum while the Listed pile said the both-tenses standing book, and two
    // surfaces claiming "at ask" with different numbers was the bug. The pile is
    // dead; the BOOK's claim - the last scan plus our own moves since, minus
    // what sold - is the honest one, and it lives here now.
    var (count, _) = OnMarket.Headline(_cache.OnMarketRows);
    var bookBaseline = BoardListings.GroupByRetainer(_cache.Listed).Sum(g => g.GilAtAsk);
    var book = StandingBookFeed.Read(bookBaseline, _cache.LastFullScanAt);
    var bookLine = StandingBook.Headline(book.Value, book.BookKept,
      _cache.LastFullScanAt > 0 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - _cache.LastFullScanAt : (long?)null);
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
    ImGui.Text($"{count} ask{(count == 1 ? "" : "s")} on record - {bookLine}");
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip(OnMarket.Caveat()
        + "\n\nThe gil is the standing book, both tenses: the last full board scan, plus everything listed, pulled or repriced since, minus what has sold.");
    if (book.PutUpToday > 0)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Earned);
      ImGui.Text($"{Format.Gil(book.PutUpToday)} put up today");
      ImGui.PopStyleColor();
    }
    ImGui.Separator();

    if (!ImGui.BeginTable("BoardOnMarket", 10, BoardTableFlags)) return;

    ImGui.TableSetupColumn("Item",
      ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 1f);
    ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 108f);
    ImGui.TableSetupColumn("Price",
      ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 92f);
    ImGui.TableSetupColumn("Listed",
      ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 84f);
    ImGui.TableSetupColumn("Pos@list", ImGuiTableColumnFlags.WidthFixed, 62f);
    // The four-score strip, in the board's order and format (walk unit 3): the
    // whole readability claim of the one table extends to the standing book.
    foreach (var exit in BoardCalls.Exits)
      ImGui.TableSetupColumn(BoardCalls.ColumnLabel(exit),
        ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.PreferSortDescending, 68f);
    ImGui.TableSetupColumn("Call", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, 150f);
    ImGui.TableHeadersRow();

    foreach (var row in SortOnMarket(_cache.OnMarketRows))
      DrawOnMarketRow(row, contests);

    ImGui.EndTable();
  }

  /// <summary>
  /// Orders the tab. A row with no number for the sorted column sorts to the
  /// bottom in BOTH directions - the board's rule, for the board's reason: "no
  /// number" is not a small number.
  /// </summary>
  private static List<OnMarketRow> SortOnMarket(List<OnMarketRow> rows)
  {
    var (column, ascending) = TableSort.Spec();

    IOrderedEnumerable<OnMarketRow> Ordered<TKey>(Func<OnMarketRow, TKey> key)
      => ascending ? rows.OrderBy(key) : rows.OrderByDescending(key);

    List<OnMarketRow> WithNullsLast<TKey>(Func<OnMarketRow, TKey?> key) where TKey : struct
      => BoardLayout.NullsLast(rows, key, r => r.ItemName, ascending);

    return column switch
    {
      1 => Ordered(r => r.Retainer).ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase).ToList(),
      2 => WithNullsLast(r => r.Price),
      // Newest first when descending: "Listed" is an AGE in the label and a
      // timestamp underneath, and sorting the timestamp is what the player means.
      3 => Ordered(r => r.ListedAt).ToList(),
      4 => WithNullsLast(r => r.QueuePosition),
      // The List column sorts on the number it DRAWS, through the same call the
      // cell uses - the board's own rule (see SortBoard). On a standing ask that
      // number is the ask; sorting the preview under a column showing the ask
      // would order the tab by a number nobody can see.
      5 => WithNullsLast(r => BoardAskCell.Number(AskOf(r), r.Scores?[0])),
      // The rest of the score strip (columns 6-8, strip order): dashes to the
      // bottom in both directions - the board's rule, for the board's reason.
      >= 6 and <= 8 => WithNullsLast(r => r.Scores?[column - 5]),
      _ => Ordered(r => r.ItemName).ToList(),
    };
  }

  /// <summary>
  /// The standing ask this row is carrying, as the List cell reads it. A non-positive
  /// price is no ask - the receipt banked nothing - and falls through to the preview,
  /// which is <see cref="BoardAskCell.Number"/>'s own rule said in the operand.
  /// </summary>
  private static long? AskOf(OnMarketRow row) => row.Price is long p && p > 0 ? p : null;

  private void DrawOnMarketRow(OnMarketRow row,
    IReadOnlyDictionary<(uint ItemId, bool IsHq, string Retainer), StandingAction?> contests)
  {
    ImGui.TableNextRow();

    ImGui.TableNextColumn();
    ImGui.Text(row.Quantity > 1
      ? $"{Format.Hq(row.ItemName, row.IsHq)} x{row.Quantity}"
      : Format.Hq(row.ItemName, row.IsHq));
    // The queue verdict, inline and amber - but ONLY on rows standing behind
    // bait. Front-of-line rows keep their story in the Pos@list tooltip; an
    // orange note on every row would be wallpaper (the quiet-notes ruling).
    // MOVEMENT 4: the TAG is what the cell carries now - the whole sentence was
    // a paragraph in a table cell - and the sentence it summarises is on the
    // hover, nothing deleted.
    if (row.QueuePosition is > 0 && row.QueueTag is string queueTag)
    {
      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
      ImGui.Text($"- {queueTag}");
      ImGui.PopStyleColor();
      if (row.QueueStory is string queueNote && ImGui.IsItemHovered())
        ImGui.SetTooltip(queueNote);
    }

    ImGui.TableNextColumn();
    ImGui.Text(row.Retainer);

    ImGui.TableNextColumn();
    RightAligned(row.Price is long p ? Format.Gil(p) : "—", row.Price is null);

    ImGui.TableNextColumn();
    RightAligned(row.ListedLabel, false);

    ImGui.TableNextColumn();
    // The whole cell hovers, not just the digits: the number without its
    // story is uninterpretable (slot 16 = bait-riddled dominance or genuine
    // trouble; only the banked prices say which - Drift, 08-02).
    // SEAT GRAMMAR (ruled 2026-08-15): "the column should match the visual
    // list - if we're 4th in line, the column should display 4." The banked
    // queue_position counts foreign rows AHEAD (0 = front), so the displayed
    // seat is that count plus one - the same conversion the receipt trail's
    // "from seat N" uses (BoardDetail.Stamp / LanePricing.SeatOf).
    var seat = row.QueuePosition is int qp ? (long?)(qp + 1) : null;
    // NO HOVER WHEN THERE IS NOTHING TO SAY (audited 08-22). A receipt banked
    // before V35 carries no spans, so QueueStory is null and this hint composes to
    // "" - and ScoreCell's own `{ Length: > 0 }` guard is what turns that into NO
    // tooltip rather than an empty box. That guard is load-bearing here: the ""
    // fallback below is only safe because of it.
    ScoreCell($"##pos{row.ItemId}:{row.IsHq}:{row.Retainer}",
      seat, seat?.ToString() ?? "—", pressed: false, tint: null,
      () => row.QueueStory ?? "", onClick: null);

    // The four-score strip: same order, same format as the board - and the
    // cells ARE the controls here too (unit 4). A click stages the verb the
    // exit maps to; the same cell again withdraws the contest.
    contests.TryGetValue((row.ItemId, row.IsHq, row.Retainer), out var stagedVerb);
    var stagedExit = stagedVerb is { } sv ? BoardCalls.ExitOfAction(sv) : null;
    var doors = _cache.ListedDoors(row.ItemId, row.IsHq);
    for (var i = 0; i < BoardCalls.Exits.Length; i++)
    {
      ImGui.TableNextColumn();
      var exit = BoardCalls.Exits[i];
      var value = row.Scores?[i];
      var pressed = stagedExit == exit;
      var doorOpen = doors.Has(exit);

      // THE LIST CELL SHOWS THE ASK, exactly as the board's does (3-3b). It used to
      // show the relist preview here and the ask over there - one lane, one column
      // header, two gil figures - and the fix is not a shared widget but a shared
      // ANSWER: BoardAskCell.Number decides the number for both, the preview moves
      // to the hover on both, and the column sorts on the same call on both.
      var ask = exit == RoutingExit.List && doorOpen ? AskOf(row) : null;
      var shown = BoardAskCell.Number(ask, value);
      // Same two glyphs as the board: dash = no evidence, cross = no such door.
      var text = shown is long gil
        ? BoardCalls.GradeMark(exit, row.MeltGrade) + Format.Gil(gil)
        : doorOpen ? "—" : "×";

      var voice = !doorOpen ? CellVoice.Closed
        : pressed ? CellVoice.Contest
        // A standing ask is a prior ruling nobody has contested: the List cell's
        // click stages a reprice, and every other cell pulls it for that exit.
        : exit == RoutingExit.List ? CellVoice.Unruled
        : CellVoice.PullFor;

      ScoreCell($"##om{row.ItemId}:{row.IsHq}:{row.Retainer}:{i}", shown, text, pressed,
        BoardAskCell.ShowsAsk(ask) ? ScroogeColors.Info : null,
        () => CellHint(exit, value, row.MeltGrade, ask, voice),
        doorOpen ? () => ToggleStandingContest(row, exit) : null);
    }

    // The Call column, in the board's language: a standing ask IS a prior
    // ruling, so the normal row reads "standing" and owes nobody a click. A
    // contest reads "unruled" in amber; a staged verb is the player's answer.
    ImGui.TableNextColumn();
    var contested = contests.TryGetValue((row.ItemId, row.IsHq, row.Retainer), out var staged);
    var call = BoardCalls.OfListed(RoutingExit.List, false, contested, staged ?? StandingAction.None);
    var callText = BoardCalls.ListedLabel(call, staged ?? StandingAction.None);
    var color = call.Source switch
    {
      CallSource.Player => ScroogeColors.Info,
      CallSource.Unruled => ScroogeColors.Amber,
      _ => ScroogeColors.Muted,
    };
    ImGui.TextColored(color, callText);
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip(call.Source switch
      {
        CallSource.Player => "You answered this contest - the round spends it.",
        CallSource.Unruled => "The router contests this ask - it wants a ruling at the hinge, inside a Round.",
        _ => "The ask stands. Status quo is the call; nobody owes it a click.",
      });
  }

  /// <summary>
  /// The synthetic item for a standing On Market row the player contests - the
  /// SyntheticItem(flag) pattern, player-initiated. Cached per lane: staging is
  /// reference-keyed, so the same lane must always resolve to the same item.
  /// </summary>
  private PricingItem StandingItem(OnMarketRow row)
  {
    var key = (row.ItemId, row.IsHq, row.Retainer);
    if (_contestItems.TryGetValue(key, out var cached)) return cached;

    var item = new PricingItem
    {
      ItemId = row.ItemId,
      IsHq = row.IsHq,
      ItemName = row.ItemName,
      RetainerName = row.Retainer,
      Quantity = row.Quantity,
      CurrentListingPrice = row.Price is long p && p > 0 && p <= int.MaxValue ? (int)p : null,
      VendorPrice = LedgerCache.VendorPriceOf(row.ItemId),
      Result = PricingResult.PlayerContest,
    };
    _contestItems[key] = item;
    return item;
  }

  /// <summary>
  /// A cell click on a standing On Market row (unit 4): toggles the verb the
  /// exit maps to. Same cell again = withdraw the contest entirely - the ask
  /// goes back to standing, exactly as if nothing had been clicked. A different
  /// cell = restage. The teaching signal fires on stage, like the pane toggles.
  /// </summary>
  private void ToggleStandingContest(OnMarketRow row, RoutingExit exit)
  {
    var action = BoardCalls.ActionOfExit(exit);
    if (action == StandingAction.None) return;
    if (!_cache.ListedDoors(row.ItemId, row.IsHq).Has(exit)) return; // the seam's own guard

    var item = StandingItem(row);
    if (_actions.TryGetValue(item, out var current) && current.Action == action)
    {
      _actions.Remove(item);
      _playerContests.Remove(item);
      return;
    }

    if (!_playerContests.Contains(item)) _playerContests.Add(item);
    _actions[item] = new StagedVerb(action, CallClassOf(item));
    Answered(item);
    RecordStandingSignal(item, BoardPiles.ForStanding(item.Result), action);
  }
}
