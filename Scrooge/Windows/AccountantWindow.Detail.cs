using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Scrooge.Board;

namespace Scrooge.Windows;

/// <summary>
/// THE DETAIL PANE: one row's whole story, pinned under the Board Calls table. The
/// surface you come to when a hover is not enough - and the one place the rare per-row
/// verbs live, which is what keeps every row on the board itself zero-button.
///
/// <para>Every storage read here is cached per SELECTION and voided by the scan that
/// replaced the rows (see <c>ClearRowCaches</c>). The pane redraws sixty times a second;
/// nothing it quotes may cost a query when it does.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  // The row memoir's per-selection cache (Drift, 08-02: the card must bear out
  // what the book knows). Computed lazily on first draw of a selected row,
  // cleared with every Refresh - two indexed point reads, never per frame.
  private readonly Dictionary<string, List<string>> _memoirs = [];

  /// <summary>
  /// The receipt trail's per-selection cache, on the memoir's terms: one indexed
  /// read per selected row, cleared with every Refresh, never per frame. The
  /// pane is a fixed surface that redraws sixty times a second - anything it
  /// reads from storage has to be answered once and remembered.
  /// </summary>
  private readonly Dictionary<string, List<string>> _trails = [];

  /// <summary>
  /// The state line's per-selection cache (Movement 2), on the memoir's and the
  /// trail's terms: the operands behind "where is this thing" are gathered once per
  /// selected row and cleared with every Refresh. The one storage read among them
  /// is a single-variant point read, and even that must not happen per frame.
  /// </summary>
  private readonly Dictionary<string, BoardStateLine.StateFacts> _states = [];

  /// <summary>
  /// The two floors the Board Calls page's split may not cross (item 9, 08-06; made
  /// content-aware 08-21). BOTTOM, NOT SIDE: the page is a score-cell table whose four
  /// columns must line up at a glance in a 560px-minimum window, and a side pane would
  /// take a third of that from the one surface that cannot afford to lose it and hand
  /// it to prose - which reads FINE narrow and badly short.
  ///
  /// <para>The pane is nailed under the table's own scroll region rather than drawn
  /// after it: the point of the pane is reading one row's story while comparing it
  /// against the rows above, and a pane that scrolled away with them could not do the
  /// one job it has.</para>
  ///
  /// <para><b>The fixed 38% share is gone</b> (ruled 08-21, pen 4). A flat proportion
  /// paid the pane the same slice whether the table held two rows or fifteen, so a
  /// short board wasted half the page on white space under its rows and a long one
  /// scrolled in a slot while the pane sat mostly empty. The table asks for what its
  /// rows need and the pane claims the remainder; these two numbers are only the
  /// promise that neither can be squeezed to nothing.</para>
  /// </summary>
  // 132 -> 200 (Drift, 08-23, mid-shakeout: "can we make the detail pane a bit
  // bigger?"). At 132 a full board pushed the pane to its floor and the moves
  // list clipped mid-exit - the pane's own screenshot receipt shows Melt cut in
  // half. 200 keeps the heading, both state lines and all four moves readable
  // before the pane's internal scrollbar has to earn its keep.
  private const float PaneMinHeight = 200f;
  private const float TableMinHeight = 72f;

  /// <summary>
  /// THE RICH DETAIL PANE (item 9, 08-06; re-ordered by Movement 2, 08-13).
  /// Stage 1 left a spare version here whose only job was that nothing was lost
  /// when the per-row prose and the non-exit verbs came off the table. This is
  /// the deep dive it was a placeholder for, in four beats, always in this order:
  ///
  /// <list type="number">
  ///   <item>the NAME - which row this is, and nothing about its situation;</item>
  ///   <item><b>the STATE</b> - where the item IS right now, what you last sold
  ///     one for, and what the DC pays. <see cref="BoardStateLine"/>. This LEADS,
  ///     by ruling: Drift's diagnosis of the hinge was that the pane offered exits
  ///     before it offered orientation, and "is this thing already listed?" is the
  ///     question every other line here is read against;</item>
  ///   <item>the MOVES - the four numbers with what fed each one, and the rare
  ///     per-row verbs that are not one of the four exits. Framed as movements FROM
  ///     the state above rather than as a standalone menu. The verbs live HERE and
  ///     nowhere else: a zero-button row stays a zero-button row (stage 1's whole
  ///     point), and the pane is where the exceptions go;</item>
  ///   <item>the ARGUMENT - the router's own narration, verbatim, plus the
  ///     objections the row earned (contradiction, closed doors, runner-up), the
  ///     memoir's precedent, and the receipt trail. It is what you read AFTER you
  ///     know where the thing is and what could be done with it.</item>
  /// </list>
  ///
  /// <para>Tooltips stay as garnish. Everything the cells and headers hover is
  /// still true and still there; this is the surface you come to when a hover
  /// is not enough, and it is pinned (see <see cref="DrawBoardDecisionsPage"/>) so
  /// coming to it never costs you your place on the board.</para>
  ///
  /// <para>Height 0 = fill what the scroll region left. The pane's own scrollbar
  /// takes the overflow, so a long story on a short window costs the reader a
  /// scroll INSIDE the pane rather than pushing the verbs off the screen.</para>
  /// </summary>
  private void DrawDetailPane(List<BoardRow> rows)
  {
    ImGui.BeginChild("##ledgerDetail", new Vector2(0, 0), true);

    var row = rows.FirstOrDefault(r => r.Key == _selectedKey);
    if (row is null)
    {
      // Wrapped, not TextDisabled: the invitation is a sentence now and a sentence
      // that runs off the edge of the pane invites nothing.
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
      ImGui.TextWrapped(BoardStateLine.NoSelection);
      ImGui.PopStyleColor();
      ImGui.EndChild();
      return;
    }

    // MOVEMENT 2, IN FOUR BEATS (ruled 08-13). Name it, say WHERE IT IS, offer the
    // moves from there, then argue. The old order put the router's argument and its
    // four exits ahead of the one fact the reader needs to interpret either of them.
    DrawRowHeading(row);
    DrawStateLead(row);
    ImGui.Separator();

    DrawMoves(row);

    if (row.Routed is { } item) DrawRoutedStory(row, item);
    else if (row.Standing is { } standing) DrawStandingStory(row, standing);

    DrawReceiptTrail(row);

    ImGui.EndChild();
  }

  /// <summary>
  /// WHO THIS IS. Name, qualifier, pile and call - identity only. Everything that
  /// used to ride here about the row's SITUATION moved into the state line below
  /// it, which is the whole of Movement 2: the pane says what it is, then where it
  /// is, and only then what could be done about it.
  /// </summary>
  private void DrawRowHeading(BoardRow row)
  {
    var tint = row.Standing is { } standing
      ? standing.IsFresh ? ScroogeColors.Amber : ScroogeColors.Warning
      : ScroogeColors.Header;
    ImGui.PushStyleColor(ImGuiCol.Text, tint);
    ImGui.Text(row.Name);
    ImGui.PopStyleColor();
    ImGui.SameLine();
    var call = row.Routed is not null ? $" - {row.CallText}" : "";
    ImGui.TextDisabled($"{row.Qualifier} - {BoardLayout.GroupTitle(row.Group)}{call}");
    // THE BOARD MIRROR's door, here too (Drift, 08-23: "I would like that
    // 'Board' button here as well") - same identity resolution as the case
    // page's button, so the mirror opens on the variant this pane is telling
    // the story of. Untradeable rows get no door (BoardRow.BoardSpeaks).
    if (row.BoardSpeaks)
    {
      ImGui.SameLine();
      if (ImGui.SmallButton($"board##mirrorDetail{row.Key}"))
        Plugin.BoardMirror.Open(row.ItemId,
          row.Routed?.IsHq ?? row.Standing?.Item.IsHq ?? false, row.Name, row.Ask);
    }
  }

  /// <summary>
  /// THE STATE, FIRST (Movement 2, ruled 2026-08-13). Drift's diagnosis of the hinge,
  /// verbatim: the awkward part is "understanding the state of the state to make a
  /// decision - is this thing already listed?" This is the sentence that answers it,
  /// before a single exit is offered.
  ///
  /// <para>Two weights, because there are two witnesses: your own book in the
  /// reading tint, the data center's in the muted one. Composition, gap handling and
  /// every word of it belong to <see cref="BoardStateLine"/>; this draws what it is
  /// handed and owns no wording. A row the book knows nothing about draws nothing,
  /// which is honest and is also impossible in practice - "In your bags." and
  /// "Standing on the board." are always available.</para>
  /// </summary>
  private void DrawStateLead(BoardRow row)
  {
    var state = BoardStateLine.Compose(StateFactsFor(row), DateTimeOffset.Now);

    if (state.Where.Length > 0)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
      ImGui.TextWrapped(state.Where);
      ImGui.PopStyleColor();
    }

    if (state.Market.Length > 0)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
      ImGui.TextWrapped(state.Market);
      ImGui.PopStyleColor();
    }
  }

  /// <summary>
  /// THE OPTIONS, AS MOVEMENTS FROM THAT STATE. The four-score arithmetic and the
  /// rare per-row verbs, under one heading that says what they now are: not a menu
  /// of things this item could be in the abstract, but the moves available from
  /// where the line above just said it is.
  ///
  /// <para>Nothing about what either surface DOES changed here - the score cells
  /// still rule, the verbs still only stage - they simply follow the state instead
  /// of preceding it.</para>
  /// </summary>
  private void DrawMoves(BoardRow row)
  {
    ImGui.Spacing();
    ImGui.TextDisabled(BoardStateLine.MovesHeader);
    DrawScoreMath(row);
    DrawRowVerbs(row);
  }

  /// <summary>
  /// WHAT THE BOOK HOLDS ABOUT WHERE THIS ROW IS - gathered once per selected row
  /// and cached on the memoir's terms, so the one small own-sale read runs per
  /// selection and never per frame. Fail-soft: a storage hiccup costs the sale
  /// clause, never the state line.
  ///
  /// <para>Every operand is a fact somebody else already banked: the ask and the
  /// retainer off the standing row, the listing's stamp off the receipt the On
  /// Market tab is built from, the DC's read off the same scoring pass the four
  /// numbers came from, and the melt provenance off THIS Round's yield set. None of
  /// it is derived here, and nothing missing is filled in.</para>
  /// </summary>
  private BoardStateLine.StateFacts StateFactsFor(BoardRow row)
  {
    if (_states.TryGetValue(row.Key, out var cached)) return cached;

    var isHq = row.Routed?.IsHq ?? row.Standing?.Item.IsHq ?? false;
    var itemId = row.ItemId;

    long? salePrice = null;
    DateTimeOffset? saleAt = null;
    if (itemId != 0)
    {
      try
      {
        if (GilStorage.GetLastSalePriceWithTime(itemId, isHq) is { } sale)
        {
          salePrice = sale.Price;
          saleAt = DateTimeOffset.FromUnixTimeSeconds(sale.Timestamp).ToLocalTime();
        }
      }
      catch (Exception ex)
      {
        Svc.Log.Debug($"[Board] Own-sale read failed for {itemId}: {ex.Message}");
      }
    }

    BoardStateLine.StateFacts facts;
    if (row.Standing is { } standing)
    {
      var item = standing.Item;
      // The row already gathered the ask for its List cell (Movement 4). One
      // operand, two readers - the pane and the column cannot quote different
      // asks for the same lane because there is only one to quote.
      // The listing's stamp is the RECEIPT's, the same one the On Market tab and
      // the trail read. A lane with no open receipt says nothing about age rather
      // than borrowing the flag's clock, which measures a different thing.
      DateTimeOffset? listedAt = null;
      foreach (var market in _cache.OnMarketRows)
        if (market.ItemId == item.ItemId && market.IsHq == item.IsHq
            && string.Equals(market.Retainer, item.RetainerName, StringComparison.Ordinal))
        {
          listedAt = DateTimeOffset.FromUnixTimeSeconds(market.ListedAt).ToLocalTime();
          break;
        }

      var listed = _cache.ListedFactsFor(item.ItemId, item.IsHq) ?? default;
      facts = new BoardStateLine.StateFacts(
        Standing: true,
        Retainer: item.RetainerName,
        Ask: row.Ask,
        ListedAt: listedAt,
        LastSalePrice: salePrice,
        LastSaleAt: saleAt,
        CommunityMedian: listed.CommunityMedian,
        CommunitySamples: listed.CommunitySamples);
    }
    else
    {
      var routed = row.Routed;
      facts = new BoardStateLine.StateFacts(
        Standing: false,
        // The one provenance the round can show a receipt for: a variant this
        // Round's own melt runs yielded. Every other road into the bags is
        // unrecorded, and the state line refuses to guess which one it was.
        FromTonightsMelt: routed is not null
          && _conductor.Hinge.FromRoundMelt(routed.ItemId, routed.IsHq),
        LastSalePrice: salePrice,
        LastSaleAt: saleAt,
        CommunityMedian: routed?.CommunityMedian ?? 0,
        CommunitySamples: routed?.CommunitySampleCount ?? 0);
    }

    _states[row.Key] = facts;
    return facts;
  }

  /// <summary>
  /// THE ROUTER'S ARGUMENT for a bag row - why it called what it called, which
  /// doors are shut, what the book remembers, and what came second. It sits BELOW
  /// the moves now (Movement 2): an argument is what you read after you know where
  /// the thing is and what you could do with it, not before.
  /// </summary>
  private void DrawRoutedStory(BoardRow row, RoutedItem item)
  {
    ImGui.Spacing();

    // The shared clauses are on the group header now; the row says the rest.
    var reason = BoardNarration.Strip(item.Verdict.Reason,
      _cache.SealRate.Narration, BoardNarration.SkillupClause);

    // A verdict built on community fallback while world data warms wears a
    // "~ community read" suffix - the two-truths-per-reload smell, made honest.
    // Per-item since 08-06: THIS row's almanac ask, not anyone's.
    if (item.CommunityFallback && UniversalisStats.IsPending(item.ItemId))
      reason += "  ~ community read, world data pending";

    // A Contradicted row must STATE the market number that overruled it.
    if (item.Confidence == ConfidenceTier.Contradicted)
    {
      var note = BoardListings.ContradictionNote(
        item.CommunityMedian > 0 ? item.CommunityMedian : null,
        item.CommunitySampleCount, item.MarketVelocity);
      if (note.Length > 0) reason += $" {note}";
    }

    ImGui.TextWrapped(reason);

    // The closed doors, said in the story too (Drift, 08-02: the crossed cell
    // alone was not enough notice). One line, only when a door is closed.
    var closed = new List<string>();
    if (!item.Doors.List) closed.Add("can't be sold on the market board");
    if (!item.Doors.Melt) closed.Add("can't be desynthed");
    if (!item.Doors.Gc) closed.Add("the Grand Company doesn't take it");
    if (!item.Doors.Vendor) closed.Add("no vendor will buy it");
    if (closed.Count > 0)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Stale);
      ImGui.TextWrapped($"This one {string.Join(", and ", closed)}.");
      ImGui.PopStyleColor();
    }

    // The book's memory of this variant (RowMemoir): the tape's weight, the
    // last listing trial's verdict, the player's own precedent.
    var memoir = MemoirFor(row.Key, item.ItemId, item.IsHq, row.Standing is not null);
    if (memoir.Count > 0)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
      foreach (var line in memoir)
        ImGui.TextWrapped(line);
      ImGui.PopStyleColor();
    }

    if (item.Verdict.RunnerUpReason.Length > 0)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
      ImGui.TextWrapped($"Runner-up ({item.Verdict.RunnerUp}): {item.Verdict.RunnerUpReason}");
      ImGui.PopStyleColor();
    }
  }

  /// <summary>
  /// Gathers the memoir's facts for one variant - cached per refresh, so the
  /// two point reads run once per selection, never per frame. Fail-soft: a
  /// storage hiccup yields an empty memoir, never a broken card.
  /// </summary>
  private List<string> MemoirFor(string key, uint itemId, bool isHq, bool isStanding)
  {
    if (_memoirs.TryGetValue(key, out var cached)) return cached;

    var lines = new List<string>();
    try
    {
      var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

      var tapeSales = 0; long latestSale = 0;
      foreach (var s in GilStorage.ReadSaleHistory(itemId))
        if (s.IsHq == isHq) { tapeSales++; if (s.SaleTime > latestSale) latestSale = s.SaleTime; }

      var trial = GilStorage.GetAskTrial(itemId, isHq);

      var ruling = "";
      foreach (var kv in _cache.PersistedRulings)
        if (kv.Key.Item1 == itemId && kv.Key.Item2 == isHq
            && Enum.TryParse<RoutingExit>(kv.Value, out var exit))
          ruling = BoardCalls.ShortName(exit);

      lines = RowMemoir.Lines(new RowMemoir.MemoirFacts(
        TapeSales: tapeSales,
        LatestSaleDaysAgo: latestSale > 0 ? (int)Math.Max(0, (now - latestSale) / 86400) : null,
        TrialPrice: trial?.Price,
        TrialDaysStood: trial is { } t ? (int)Math.Max(0, ((t.ClosedAt ?? now) - t.CreatedAt) / 86400) : null,
        TrialState: trial?.State ?? "",
        TrialTimeToClearDays: trial?.TimeToClearDays,
        LastRuling: ruling,
        // The open-trial line's operand (pen 7): the receipt is the variant's, the
        // sentence is this row's.
        IsStanding: isStanding));
    }
    catch (Exception ex)
    {
      Svc.Log.Debug($"[Board] Memoir read failed for {itemId}: {ex.Message}");
    }

    _memoirs[key] = lines;
    return lines;
  }

  /// <summary>
  /// THE CONTEST'S ARGUMENT for a standing row - what the pinch (or the held flag)
  /// says about the ask that is already up there. Below the moves, for the same
  /// reason the bag rows' is.
  ///
  /// <para>The heading's old bare "(3d)" is gone: it counted days since the FLAG was
  /// raised, wore no label, and now sits inches from the state line's "listed today"
  /// - two clocks, one unlabelled, disagreeing about one row. The state line owns
  /// when, off the receipt, or says nothing.</para>
  /// </summary>
  private void DrawStandingStory(BoardRow row, InboxRow standing)
  {
    var item = standing.Item;
    ImGui.Spacing();

    var reason = standing.IsFresh ? BuildReason(item) : standing.Flag!.Detail;
    if (row.Tier == ConfidenceTier.Contradicted)
    {
      var note = BoardListings.ContradictionNote(
        item.HistoryMedianPrice is int m && m > 0 ? m : (long?)null,
        item.HistorySaleCount, null,
        payer: "settled sales pay"); // LOCAL lane history - never dressed as DC-wide
      if (note.Length > 0) reason += $" {note}";
    }

    ImGui.TextWrapped(reason);
  }

  // ---- The moves, part one: the four-score math ----

  /// <summary>
  /// THE FOUR NUMBERS, WITH THEIR ARITHMETIC. The strip above shows what each
  /// exit is worth; this shows what each number is MADE of - which witness the
  /// List score weighed, whether the melt figure was measured or borrowed from
  /// an ilvl band, how many seals at what rate (discount and all) the GC number
  /// multiplies out to, and what the counter pays.
  ///
  /// <para>Same order and same glyphs as the strip, deliberately: the reader
  /// arrives here having just clicked a cell, and the line he wants must be
  /// where his eye already is. The composition is pure and tested
  /// (<see cref="BoardDetail.Scores"/>); this only draws it, and highlights
  /// the row the call is on.</para>
  /// </summary>
  private void DrawScoreMath(BoardRow row)
  {
    ImGui.Spacing();
    var lines = BoardDetail.Scores(ScoreOperandsFor(row));
    if (!ImGui.BeginTable($"##math{row.Key}", 3,
      ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.RowBg)) return;

    ImGui.TableSetupColumn("exit", ImGuiTableColumnFlags.WidthFixed, 40f);
    ImGui.TableSetupColumn("gil", ImGuiTableColumnFlags.WidthFixed, 78f);
    ImGui.TableSetupColumn("source", ImGuiTableColumnFlags.WidthStretch, 1f);

    for (var i = 0; i < lines.Count; i++)
    {
      var pressed = row.Call is { } c && c.Pressed == BoardCalls.Exits[i];
      ImGui.TableNextRow();
      ImGui.TableNextColumn();
      if (pressed)
        ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(PressedCellBg));
      ImGui.TextColored(pressed ? ScroogeColors.Earned : ScroogeColors.Muted, lines[i].Label);

      ImGui.TableNextColumn();
      // A glyph is not a number: dim it the same way the strip's cells do.
      RightAligned(lines[i].Value, lines[i].Value is "-" or "x");

      ImGui.TableNextColumn();
      if (lines[i].Source.Length > 0)
        ImGui.TextWrapped(lines[i].Source);
    }

    ImGui.EndTable();
  }

  /// <summary>
  /// The operands behind one row's four numbers. A bag row carries them on its
  /// RoutedItem; a listed row's were banked by the same scoring pass that
  /// produced its scores (see <see cref="ListedFacts"/>). Both paths read the
  /// SAME seal rate and skill-up knobs the batch scored with - never a fresh
  /// config read, which is how a pane comes to explain a number with operands
  /// the decision never saw.
  /// </summary>
  private BoardDetail.ScoreOperands ScoreOperandsFor(BoardRow row)
  {
    // The fetch state is read AT PANE DRAW, not banked with the scores - the
    // pane answers "what does this blank mean right now", and "the DC has
    // answered since this was scored" is exactly the state a banked read
    // could never report.
    if (row.Routed is { } item)
      return new BoardDetail.ScoreOperands(row.Scores, row.MeltGrade, row.Doors,
        item.LastSalePrice, item.CommunityMedian, item.CommunitySampleCount, item.CommunityFallback,
        UniversalisHistory.StateOf(item.ItemId),
        item.SealValue, _cache.SealRate, item.SkillupColor, _cache.SkillupYellow, _cache.SkillupRed);

    var facts = row.Standing is { } standing
      ? _cache.ListedFactsFor(standing.Item.ItemId, standing.Item.IsHq) ?? default
      : default;
    return new BoardDetail.ScoreOperands(row.Scores, row.MeltGrade, row.Doors,
      facts.OwnSale, facts.CommunityMedian, facts.CommunitySamples, facts.CommunityFallback,
      row.Standing is { } t ? UniversalisHistory.StateOf(t.Item.ItemId) : CommunityFetchState.Unavailable,
      facts.SealValue, _cache.SealRate, facts.SkillupColor, _cache.SkillupYellow, _cache.SkillupRed,
      // The verdict line already quotes this ask; the List blank must agree with it.
      FloorRefusedAsk: row.Standing is { Item: { Result: PricingResult.BelowFloor, RejectedPrice: int refused } }
        ? refused : null);
  }

  // ---- The argument's second half: the receipt trail ----

  /// <summary>
  /// THE RECEIPT TRAIL: what this variant's asks have actually done. Every
  /// decision receipt we wrote for it, newest first - when, on which retainer,
  /// at what price, and how it ended - with the A9 grades where the grader
  /// reached them.
  ///
  /// <para><b>This is the one surface the A9 stamps reach.</b> Bare verdicts, no
  /// narration, each with the seat the receipt took (queue_position, rendered in
  /// the house's 1-based seat grammar), and the survivorship caveat on the hover:
  /// a sold ask always reads PASS/CLEARED, so a clean trail is a survivorship
  /// board and never a scoreboard. The caveat's full argument lives once, on
  /// ReceiptGrading.</para>
  /// </summary>
  private void DrawReceiptTrail(BoardRow row)
  {
    var (itemId, isHq) = row.Routed is { } item
      ? (item.ItemId, item.IsHq)
      : row.Standing is { } standing ? (standing.Item.ItemId, standing.Item.IsHq) : (0u, false);
    if (itemId == 0) return;

    var lines = TrailFor(row.Key, itemId, isHq);
    if (lines.Count == 0) return;

    ImGui.Spacing();
    ImGui.TextDisabled("Your asks for this one");
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(BoardDetail.TrailCaveat());
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
    foreach (var line in lines)
      ImGui.TextWrapped($"  {line}");
    ImGui.PopStyleColor();
  }

  /// <summary>
  /// Reads the trail for one variant - cached per refresh on the memoir's terms,
  /// so the one indexed read runs once per selection and never per frame.
  /// Fail-soft: a storage hiccup yields an empty trail, never a broken pane.
  /// </summary>
  private List<string> TrailFor(string key, uint itemId, bool isHq)
  {
    if (_trails.TryGetValue(key, out var cached)) return cached;

    var lines = new List<string>();
    try
    {
      var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      foreach (var receipt in BoardDetail.Trail(GilStorage.GetReceiptTrail(itemId, isHq)))
        lines.Add(BoardDetail.TrailLine(receipt, now));
    }
    catch (Exception ex)
    {
      Svc.Log.Debug($"[Board] Receipt trail read failed for {itemId}: {ex.Message}");
    }

    _trails[key] = lines;
    return lines;
  }

  // ---- The moves, part two: the rare per-row verbs ----

  /// <summary>
  /// THE VERBS THAT ARE NOT ONE OF THE FOUR EXITS. They live here and only here:
  /// the board's rows are zero-button by ruling (stage 1), and this pane is
  /// where the exceptions were always going to go.
  ///
  /// <para><b>First law - they stage intent, they never fire.</b> Every verb
  /// below writes into the same staging dictionary a score-cell click writes
  /// into, and the ROUND spends it at the retainer. Nothing here reaches an
  /// orchestrator: out-of-round execution was deliberately killed (07-26 /
  /// stage 2a - one launch control, one errand), and a pane button that fired a
  /// run would put a sixth answer back on "what happens if I press this".</para>
  ///
  /// <para>A BAG row has none. Its whole vocabulary is the four cells - there is
  /// no pull to stage (it is already in the bags) and no per-row launch to
  /// offer - so it says that plainly rather than growing a button to fill the
  /// space.</para>
  /// </summary>
  private void DrawRowVerbs(BoardRow row)
  {
    ImGui.Spacing();

    if (row.Standing is not { } standing)
    {
      ImGui.TextDisabled("No other moves - the four numbers above are this row's whole vocabulary, and the round spends it.");
      return;
    }

    var item = standing.Item;
    if (Plugin.StandingOrchestrator.IsRunning)
    {
      ImGui.TextDisabled("Batch in progress...");
      return;
    }

    // Vend / Pull / Reprice / Dismiss, preserved whole from the triage inbox.
    // They lived on the row until the row went zero-button; nothing about what
    // they do has changed, and none of them runs anything.
    _actions.TryGetValue(item, out var currentVerb);
    var current = currentVerb.Action;
    var canReprice = item.Result is PricingResult.CapBlocked;
    var key = $"{item.ItemId}_{item.IsHq}_{item.RetainerName}";
    var pile = EffectiveStandingPile(item);

    DrawActionToggle(item, key, StandingAction.Vendor, "Vend", current, pile);
    // Pull's neighbour had a hover and it did not, on the one verb whose consequence
    // is irreversible - the item leaves for gil at the counter's price.
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Stages a counter sale for this round - the bell pulls it back and the vendor rider sells it at the NPC price. Nothing happens until the round runs.");
    ImGui.SameLine(0, 2);
    DrawActionToggle(item, key, StandingAction.Pull, "Pull", current, pile);
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Stages a retrieval for this round - the bell pulls it at the retainer, or the pinch's vendor rider gets there first. Nothing happens until the round runs.");
    if (canReprice)
    {
      ImGui.SameLine(0, 2);
      // "Reprice", not "Reprc" - the board's own no-abbreviation rule, applied to
      // the button that was breaking it. The ImGui id is the ## half, so the label
      // is free to say the word; the widget's identity does not move with it.
      DrawActionToggle(item, key, StandingAction.Reprice, "Reprice", current, pile);
    }
    ImGui.SameLine(0, 2);
    if (ImGui.SmallButton($"Dismiss##pane{key}"))
    {
      DismissRow(standing);
      _selectedKey = null;
    }
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("The standing ask stands - and the contest that raised this row is graded as overruled.");
  }

  private void DrawActionToggle(PricingItem item, string key, StandingAction action, string label,
    StandingAction current, BoardPile pile)
  {
    var isActive = current == action;
    if (isActive)
    {
      var highlight = action switch
      {
        StandingAction.Vendor => new Vector4(0.8f, 0.2f, 0.2f, 1f),
        StandingAction.Pull => new Vector4(0.2f, 0.5f, 0.8f, 1f),
        StandingAction.Reprice => new Vector4(0.7f, 0.6f, 0.1f, 1f),
        _ => new Vector4(0.4f, 0.4f, 0.4f, 1f),
      };
      ImGui.PushStyleColor(ImGuiCol.Button, highlight);
    }

    if (ImGui.SmallButton($"{label}##{key}_{action}"))
    {
      if (isActive)
        _actions.Remove(item);
      else
      {
        _actions[item] = new StagedVerb(action, CallClassOf(item));
        Answered(item);
        RecordStandingSignal(item, pile, action);
      }
    }

    if (isActive)
      ImGui.PopStyleColor();
  }
}
