using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Scrooge.Board;
using Scrooge.Rounds;

namespace Scrooge.Windows;

/// <summary>
/// THE TRIAGE WALK, RENDERED (Task 3, spec ruled 2026-08-15). The hinge stopped being
/// one overwhelming panel and became a short forced walk of small pages, each shaped
/// for its decision.
///
/// <para><b>North star</b> (Drift, 08-15): "All of the machinery that we are building is
/// mostly there to feed the triage. Short-changing the triage, or not giving the
/// decisions the room and UI needed to make a good call, is self-defeating." The split
/// is what serves it hardest - easy decisions keep the snap format (the columns ARE the
/// decision buttons), hard decisions are unshackled from it ("don't shackle hard
/// decisions with a format that works well for easy decisions").</para>
///
/// <para><b>This file holds no rules.</b> Which page a row lands on, whether a control
/// may be pressed, what the rail says, whether the walk is done, and every sentence a
/// case speaks are <see cref="TriageWalk"/>'s and <see cref="TriageCases"/>' - pure,
/// tested, Dalamud-free. What is here is the paint: tables, buttons, colours, and the
/// gathering of banked facts into <see cref="CaseEvidence"/>. A routing or evidence
/// decision written below this line would be a decision nobody can pin a test to.</para>
/// </summary>
internal sealed partial class AccountantWindow
{
  /// <summary>
  /// THE LIVE WALK, or null when the hinge is not on screen. Composed at the hinge's
  /// arrival and torn down with the round (see <c>CancelRound</c>) and with the
  /// Continue that spends it - it is a set of answers to THIS board's questions, and
  /// nothing about it is persisted.
  /// </summary>
  private TriageWalk? _walk;

  /// <summary>
  /// The evidence version the live walk was composed against. When
  /// <see cref="_cache.EvidenceStamp"/> moves past it - a re-pinch landed, a recon answered,
  /// the player pressed Refresh - the walk recomposes and every decision already made
  /// survives, because every one of them is keyed on the row (spec section 3).
  /// </summary>
  private int _walkStamp = -1;

  /// <summary>
  /// Which case the Bag decisions page is showing. An INDEX into that page's rows, reset
  /// with every composition: the docket strip is a position in a list the walk owns, and
  /// remembering a seat across a recompose would park the player on whatever row slid
  /// into it.
  /// </summary>
  private int _caseCursor;

  /// <summary>
  /// The banked facts behind each case, gathered ONCE per composition. Assembly reads
  /// storage - the override history, the melt rollup, the board snapshot - and a
  /// tribunal that re-read them sixty times a second would be the per-frame query the
  /// memoir cache exists to prevent. The case itself is assembled per frame from these,
  /// which is pure string work over facts already in hand.
  /// </summary>
  private readonly Dictionary<string, CaseEvidence> _caseFacts = [];

  /// <summary>Every board row this frame, by key - the live row behind a walk row. The
  /// walk decides WHICH rows and in what order; the window draws the current ones.</summary>
  private readonly Dictionary<string, BoardRow> _rowsByKey = [];

  // ==========================================================================
  // Composition and the walk's frame
  // ==========================================================================

  /// <summary>
  /// THE HINGE'S WHOLE SURFACE. Compose on arrival, recompose on evidence change, draw
  /// the page the cursor is on, then the navigation bar. The Continue verb keeps its
  /// seat in the wizard's step bar; the walked pane draws THE SAME verb (ruled 08-16:
  /// "would be nice to have a continue button here as well") - one press body, one
  /// gate, two doors, the bell bar's own precedent. A pane that says "Continue below
  /// spends it" while owning the empty seat the reader is looking at was a direction
  /// to a button standing next to where the button belongs.
  /// </summary>
  private void DrawWalk(List<BoardRow> rows, DeckState deck)
  {
    _rowsByKey.Clear();
    foreach (var row in rows) _rowsByKey[row.Key] = row;

    if (_walk is null || _walkStamp != _cache.EvidenceStamp)
    {
      var inputs = rows.Select(WalkInput).ToList();
      if (_walk is null) _walk = TriageWalk.Compose(inputs);
      else _walk.Recompose(inputs);
      _walkStamp = _cache.EvidenceStamp;
      _caseFacts.Clear();
      _caseCursor = 0;
    }

    var walk = _walk;
    if (walk.Pages.All(p => p.IsEmpty))
    {
      ImGui.TextDisabled("Nothing to rule - the board is settled.");
      ImGui.PushID("walkedContinue");
      DrawContinueVerb(deck);
      ImGui.PopID();
      return;
    }

    // Past the last page. The walk is walked; all that is left is the Continue, so the
    // pane draws the verb itself (same press as the step bar's - one gate, two doors)
    // rather than pointing the reader at a button somewhere below.
    if (walk.Cursor >= walk.Pages.Count)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Earned);
      ImGui.TextWrapped("Walked. Nothing here is committed yet - Continue spends it.");
      ImGui.PopStyleColor();
      // The step bar draws this same verb on this same frame; the scope keeps the
      // two doors' ids apart so hover and press each belong to the door under the
      // cursor.
      ImGui.PushID("walkedContinue");
      DrawContinueVerb(deck);
      ImGui.PopID();
      DrawWalkNav(walk, null);
      return;
    }

    var page = walk.Pages[walk.Cursor];

    // The bar's height is reserved BEFORE the page claims the rest, exactly as the
    // wizard reserves its own: a long page must never push the walk's controls off the
    // bottom of the window.
    var navHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetTextLineHeightWithSpacing();
    var body = Math.Max(96f, ImGui.GetContentRegionAvail().Y - navHeight);

    if (ImGui.BeginChild("##walkPage", new Vector2(0, body), false))
      switch (page.Kind)
      {
        case PageKind.BoardDecisions: DrawBoardDecisionsPage(page); break;
        case PageKind.Riders: DrawRidersPage(walk, deck, page); break;
        default: DrawBagDecisionsPage(walk, page); break;
      }
    ImGui.EndChild();

    DrawWalkNav(walk, page);
  }

  /// <summary>
  /// ONE BOARD ROW, AS THE WALK NEEDS IT. Every field is a fact the board already
  /// computed - the walk derives no evidence of its own and re-reads no board (spec
  /// section 1), so this is a projection and never a second opinion.
  ///
  /// <para><see cref="TriageRowInput.Deferred"/> carries the ride, not just the defer:
  /// a Mixed row in a pile a stage takes ENTIRE is not waiting on anybody either, and
  /// <see cref="BoardConfidence.Rides"/> is the walk's riders test. Telling it the same
  /// fact <see cref="GatePlan.NeedsRuling"/> is told is what keeps the riders page and
  /// the launch gate from disagreeing about which rows are in the way.</para>
  /// </summary>
  private static TriageRowInput WalkInput(BoardRow row) => new(
    Key: row.Key,
    IsStanding: row.Standing is not null,
    Pile: row.Group,
    Tier: row.Tier,
    PlayerResolved: row.PlayerResolved,
    Deferred: row.Deferred || row.RidesWholePile,
    Title: row.Quantity > 1 ? $"{row.Name} x{row.Quantity}" : row.Name,
    Note: WalkNote(row));

  /// <summary>
  /// The row's ONE line of context, in the order it earns attention: the re-ask first
  /// (a question the round is waiting on), then the doubt branch (why a deferring row is
  /// riding), then nothing. Composed here rather than in the walk because it is the
  /// sentence the board already shows - the walk only carries it.
  /// </summary>
  private static string WalkNote(BoardRow row)
    => row.ReAskNote.Length > 0 ? row.ReAskNote
      : row.DoubtLine.Length > 0 ? row.DoubtLine
      : "";

  /// <summary>
  /// THE NAVIGATION BAR (spec section 3). Back is DISABLED at the first non-empty page,
  /// never hidden - a control that vanishes teaches the player the walk is shorter than
  /// it is - and Next carries the page's own control: its label, its enablement, and its
  /// refusal, all three composed by <see cref="TriagePage.Control"/> because the Bag
  /// Calls refusal is a rule and not a paint job.
  /// </summary>
  private void DrawWalkNav(TriageWalk walk, TriagePage? page)
  {
    ImGui.Separator();

    ImGui.BeginDisabled(!walk.CanBack);
    if (ImGui.Button("Back###walkBack")) walk.Back();
    ImGui.EndDisabled();
    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
      ImGui.SetTooltip(walk.CanBack
        ? "Back a page. Nothing you ruled is lost - the walk commits nothing until Continue."
        : "This is the first page of the walk.");

    ImGui.SameLine();
    ImGui.TextDisabled(walk.Rail());

    if (page is not TriagePage current) return;

    var control = current.Control;
    ImGui.SameLine();
    ImGui.BeginDisabled(!control.Enabled || !walk.CanNext);
    if (ImGui.Button($"{control.Label}###walkNext")) walk.Next();
    ImGui.EndDisabled();
    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && control.Refusal.Length > 0)
      ImGui.SetTooltip(control.Refusal);

    if (control.Enabled || control.Refusal.Length == 0) return;

    // The refusal is said INLINE as well as on the hover: a disabled button whose only
    // explanation is a tooltip is a control that refuses silently, which is the shape of
    // gate nobody finds by looking at it.
    ImGui.SameLine();
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
    ImGui.Text(control.Refusal);
    ImGui.PopStyleColor();
  }

  /// <summary>
  /// Walks the cursor to Bag decisions - the Continue refusal's click target. Written in the
  /// walk's OWN moves rather than by setting a cursor: Back and Next know about empty
  /// pages and this must not learn it a second time.
  /// </summary>
  private void JumpToBagDecisions()
  {
    if (_walk is not { } walk) return;
    var target = Array.IndexOf(TriageWalk.Order, PageKind.BagDecisions);
    var guard = TriageWalk.Order.Length + 1;
    while (guard-- > 0 && walk.Cursor != target)
    {
      if (walk.Cursor < target) { if (!walk.CanNext) return; walk.Next(); }
      else { if (!walk.CanBack) return; walk.Back(); }
    }
  }

  /// <summary>The live board row behind a walk row, or null when the board dropped it
  /// between compositions. A vanished row draws nothing rather than a stale one.</summary>
  private BoardRow? LiveRow(TriageRow row)
    => _rowsByKey.TryGetValue(row.Key, out var live) ? live : null;

  // ==========================================================================
  // Page 1 - Board decisions (snap grammar, board-shaped)
  // ==========================================================================

  /// <summary>
  /// THE BOARD DECISIONS PAGE: standing-listing decisions in the board's own eight columns,
  /// where <b>the columns ARE the decision buttons</b> (spec section 2). Staging a verb
  /// is the page's row action and nothing about <see cref="StagedVerb"/> or the re-ask
  /// semantics moved - these are the same cells, the same <see cref="RuleRow"/>, the same
  /// teaching signal. Only the host changed.
  ///
  /// <para>One control: Next. No bulk control at current row counts, and if that changes
  /// it is a composition knob rather than a redesign.</para>
  /// </summary>
  private void DrawBoardDecisionsPage(TriagePage page)
  {
    var rows = page.Rows.Select(LiveRow).OfType<BoardRow>().ToList();

    ImGui.TextColored(ScroogeColors.Header,
      $"Board decisions - {rows.Count} standing {(rows.Count == 1 ? "ask" : "asks")} want a verb");
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Each column is a decision. Click a score cell and that IS the ruling - "
        + "the round's bell run spends it at that row's own retainer. Leaving a row alone is "
        + "also an answer: the book keeps its ask.");
    ImGui.Separator();

    // THE CONTENT-AWARE SPLIT (ruled 08-21, pen 4): the table takes what its rows
    // actually need - the header row plus one line per row, cell padding and all - and
    // the detail pane claims everything left (it opens at height 0, which is "fill").
    //
    // ROW COUNT, NOT MEASUREMENT. The want is arithmetic over the row count and the
    // current style, never a read-back of the frame just painted: a height derived
    // from what was drawn last frame changes what is drawn this frame, which is how a
    // pane oscillates a pixel a frame forever. Row counts only move when the board
    // does, so the split is stable mid-round and moves once when the work does.
    //
    // The clamps are the promise that neither pane can collapse: the table never falls
    // under its own floor, and it is never allowed to take so much that the pane drops
    // below PaneMinHeight - a long board scrolls inside its slot rather than starving
    // the story pane underneath it.
    var available = ImGui.GetContentRegionAvail().Y;
    var spacing = ImGui.GetStyle().ItemSpacing.Y;
    var rowHeight = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().CellPadding.Y * 2f;
    var wanted = rowHeight * (rows.Count + 1) + spacing;
    var ceiling = Math.Max(TableMinHeight, available - PaneMinHeight - spacing);
    var tableHeight = Math.Clamp(wanted, TableMinHeight, ceiling);

    if (ImGui.BeginChild("##boardCallsScroll", new Vector2(0, tableHeight), false))
      DrawPageTable(rows);
    ImGui.EndChild();

    DrawDetailPane(rows);
  }

  // ==========================================================================
  // Page 2 - The Riders (snap grammar, one glance)
  // ==========================================================================

  /// <summary>
  /// THE RIDERS PAGE: "N ride" - the page's whole contract, said in the header
  /// because it is the only thing a reader needs to know here.
  ///
  /// <para><b>ONE GESTURE, ONE MEANING</b> (F2, ruled 08-22 - "why does a click
  /// remove it?"). The pull is dead: the item name SELECTS into the detail pane
  /// here exactly as it does everywhere else, and disagreement with an
  /// auto-verdict is ruled IN PLACE - a score cell for the quick override, or the
  /// pane's moves under the row's full story. The pulled-rider hop to a case page
  /// added nothing the pane does not already hold (Drift: "I'm having a hard time
  /// picturing a case where I can't make a decision based on information in the
  /// detail pane"), so the escalation was deleted rather than renamed. Cases
  /// remain only where the ENGINE summons the human.</para>
  ///
  /// <para><b>THE TOP BLOCK IS DECISION NUMBERS, NOT NARRATOR</b> (pen 5, ruled 08-20;
  /// built 3b-6). It used to open with two paragraphs of mechanics - what a skill-up is
  /// worth to you at each colour, and why the seal curve had discounted the rate - which
  /// is the dark-mode rule's exact violation: a surface reciting the configuration that
  /// produced the policy. Both facts already live where they belong (the skill-up worth
  /// is in the melt row's own reason, the seal narration is on the GC option's line in
  /// the detail pane), so the prose died rather than moved. What takes the seat is the
  /// one number this page cannot get from the table underneath it: whether the seal
  /// wallet holds the turn-ins that are about to ride. The GROUP ROLL-UPS follow, in the
  /// table's own destination headings - deriving them a second time up here would be two
  /// arithmetics for one pile.</para>
  /// </summary>
  private void DrawRidersPage(TriageWalk walk, DeckState deck, TriagePage page)
  {
    var count = page.Rows.Count;
    ImGui.TextColored(ScroogeColors.Header, $"{count} ride{(count == 1 ? "s" : "")}");
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("These execute with the round on the engine's own verdicts. Click a "
        + "name for the story; rule any row you disagree with right there, or from its "
        + "score cells.");
    ImGui.TextDisabled("Nothing here is committed - Continue at the end of the walk spends it.");

    var rides = page.Rows.Select(LiveRow).OfType<BoardRow>().ToList();

    // THE SEAL WALLET, FIRST. Only when turn-ins actually ride: a wallet reading on a
    // page with no GC rows is a number about nothing. The fit is the deck's own - the
    // same read the plan line and the mid-run halt use, never a second one.
    if (rides.Any(r => r.Destination == BoardPile.Churn)
        && deck.Seals is { } seals && seals.Line is { Length: > 0 } sealLine)
      RiderNote(seals.Overflows ? ScroogeColors.Amber : ScroogeColors.Muted, sealLine);

    ImGui.Separator();

    // THE SAME SPLIT AS BOARD DECISIONS (F2): the table takes what its rows need,
    // the detail pane fills the rest - because the pane is now where a rider's
    // disagreement gets ruled, it must be present, not a page away.
    var available = ImGui.GetContentRegionAvail().Y;
    var spacing = ImGui.GetStyle().ItemSpacing.Y;
    var rowHeight = ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().CellPadding.Y * 2f;
    var wanted = rowHeight * (rides.Count + 2) + spacing;
    var ceiling = Math.Max(TableMinHeight, available - PaneMinHeight - spacing);
    var tableHeight = Math.Clamp(wanted, TableMinHeight, ceiling);

    if (ImGui.BeginChild("##ridersScroll", new Vector2(0, tableHeight), false))
      DrawRidersTable(rides);
    ImGui.EndChild();

    DrawDetailPane(rides);
  }

  /// <summary>One wrapped line of shared reasoning above the riders - the group
  /// banner's <c>BannerNote</c>, in the one seat that survived the banners.</summary>
  private static void RiderNote(Vector4 color, string text)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.TextWrapped(text);
    ImGui.PopStyleColor();
  }

  // ==========================================================================
  // Page 3 - Bag decisions (the case-first tribunal)
  // ==========================================================================

  /// <summary>
  /// THE BAG DECISIONS PAGE: <b>one case at a time</b> (ruled 08-15 - "as long as this is for
  /// the HARD stuff, 1 at a time is fine"), which is the room the north star demands. A
  /// docket strip says where in the pile you are and lets you jump; deciding auto-advances
  /// to the next case still owed an answer.
  ///
  /// <para>The page IS the launch refusal's former job: Review rows withheld until
  /// answered. Its Next refuses while any case is undecided, and the same count feeds the
  /// hinge's Continue - one arithmetic, two seats.</para>
  /// </summary>
  private void DrawBagDecisionsPage(TriageWalk walk, TriagePage page)
  {
    var rows = page.Rows;
    if (rows.Count == 0) return;

    _caseCursor = Math.Clamp(_caseCursor, 0, rows.Count - 1);
    DrawDocket(walk, rows);
    ImGui.Separator();

    if (LiveRow(rows[_caseCursor]) is not { } row)
    {
      ImGui.TextDisabled("That row left the board between reads. Refresh, or step to the next case.");
      return;
    }

    // A row the player already ruled (a persisted ruling re-applied from
    // routing_overrides, or a cell click before the walk composed) arrives at the
    // tribunal ANSWERED - the docket says so - and the case must light the same
    // call. Destination is where the round will spend it, which for a resolved row
    // IS the standing ruling. Reading only this walk's verdicts left an x-chip case
    // showing four grey buttons (live receipt 08-15: the Facet Ring) - the exact
    // "two derivations of answered" drift PlayerResolved's own doc note warns about.
    var decided = walk.Verdicts.TryGetValue(row.Key, out var verdict) ? verdict
      : row.PlayerResolved ? row.Destination
      : (BoardPile?)null;
    var tribunal = TriageCases.Assemble(FactsFor(row), decided);
    DrawCase(walk, row, tribunal);
  }

  /// <summary>
  /// THE DOCKET STRIP: case x of y, and a jump to any of them. Answered cases wear the
  /// Earned tick, the one you are on wears Amber; that is the whole vocabulary, and it is
  /// the same two colours the rail already uses for done and current.
  /// </summary>
  private void DrawDocket(TriageWalk walk, IReadOnlyList<TriageRow> rows)
  {
    var owed = walk.CallsLeft;
    ImGui.TextColored(ScroogeColors.Header,
      $"Bag decisions - case {_caseCursor + 1} of {rows.Count}");
    ImGui.SameLine();
    ImGui.TextDisabled(owed == 0
      ? "all answered"
      : $"{owed} still {(owed == 1 ? "needs" : "need")} an answer");

    for (var i = 0; i < rows.Count; i++)
    {
      if (i > 0) ImGui.SameLine(0, 3);
      var answered = walk.Verdicts.ContainsKey(rows[i].Key) || rows[i].Input.PlayerResolved;
      var color = i == _caseCursor ? ScroogeColors.Amber
        : answered ? ScroogeColors.Earned
        : ScroogeColors.Muted;
      ImGui.PushStyleColor(ImGuiCol.Text, color);
      if (ImGui.SmallButton($"{(answered ? "x" : "?")}{i + 1}##docket{rows[i].Key}"))
        _caseCursor = i;
      ImGui.PopStyleColor();
      if (ImGui.IsItemHovered()) ImGui.SetTooltip(rows[i].Title);
    }
  }

  /// <summary>
  /// ONE CASE, in the tribunal layout the spec ruled: why it is here, what each contender
  /// argues and on what receipts, then the verdict. The spec's fourth register - "how
  /// this decision goes wrong" - is gone (3b-1); see the note at its old seat below.
  ///
  /// <para><b>Every exit is a button; only the contenders argue.</b> The override grammar
  /// never narrows (ruled 08-15) - a click on an unargued exit is still a valid override -
  /// but arguments cost the reader attention, and spending it on a fourth-place exit
  /// nobody is weighing is how the room the north star bought fills with noise.</para>
  /// </summary>
  private void DrawCase(TriageWalk walk, BoardRow row, TriageCase tribunal)
  {
    ImGui.TextColored(ScroogeColors.Header, tribunal.Title);
    ImGui.SameLine();
    ImGui.TextDisabled(row.Qualifier);
    // THE BOARD MIRROR's door (Drift, 08-22): the raw rows behind the verdict,
    // one press away from the case that speaks it. Same identity resolution as
    // BuildFacts - the mirror must open on the variant the case is arguing.
    // Untradeable rows get no door (BoardRow.BoardSpeaks).
    if (row.BoardSpeaks)
    {
      ImGui.SameLine();
      if (ImGui.SmallButton($"board##mirror{row.Key}"))
        Plugin.BoardMirror.Open(row.ItemId,
          row.Routed?.IsHq ?? row.Standing?.Item.IsHq ?? false, tribunal.Title, row.Ask);
    }

    // THE STATE LEADS HERE TOO (ruled 08-16: the Silvergrace question - "does this
    // mean I'm currently listing this on the market?" was answerable only with
    // builder knowledge). Same composer as the pane's lead, so the case and the
    // pane can never tell different stories. The Where layer only: the market
    // layer stays the pane's - the contenders below argue the tape themselves,
    // and saying it twice above them would be wallpaper.
    var lead = BoardStateLine.Compose(StateFactsFor(row), DateTimeOffset.Now).Where;
    if (lead.Length > 0)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
      ImGui.TextWrapped(lead);
      ImGui.PopStyleColor();
    }

    // THE VERDICT LEADS (F4, ruled 08-22) - except on a contradiction-door case
    // (ruled 08-23): there the DISPUTE is why the case exists, and a verdict the
    // next line impeaches would read as the case arguing with itself. The case
    // itself decided the order (TriageCase.DisputeLeads); this paints it.
    if (tribunal.DisputeLeads) DrawCaseReferral(tribunal);
    if (tribunal.Verdict is { Length: > 0 } verdictLine)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Header);
      ImGui.TextWrapped(verdictLine);
      ImGui.PopStyleColor();
    }
    if (!tribunal.DisputeLeads) DrawCaseReferral(tribunal);

    // THE LAST-SOLD SEAT (V31, ruled B1 coda): the liquidity gauge is a DISPLAY, not a
    // gate - one muted line under the referral, drawn only when a witness has a date.
    // (DrawCaseReferral above skips an empty referral - the dead-heat fold, 08-23.)
    if (tribunal.LastSold is { Length: > 0 } lastSold)
      ImGui.TextDisabled(lastSold);
    ImGui.Spacing();

    foreach (var option in tribunal.Options)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, RiderColor(option.Exit));
      ImGui.TextUnformatted(CaseVoice.ExitWord(option.Exit));
      ImGui.PopStyleColor();
      ImGui.SameLine();
      ImGui.TextUnformatted(Format.Gil(option.Worth));
      ImGui.SameLine();
      ImGui.TextDisabled(option.Provenance);

      foreach (var line in option.For)
      {
        ImGui.TextColored(ScroogeColors.Earned, "  +");
        ImGui.SameLine();
        ImGui.TextWrapped(line);
      }
      foreach (var caveat in option.Caveats)
      {
        ImGui.TextColored(ScroogeColors.Amber, "  !");
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
        ImGui.TextWrapped(caveat);
        ImGui.PopStyleColor();
      }
      ImGui.Spacing();
    }

    // THE TRAPS SECTION IS GONE (pre-ruled 08-20, built 3b-1). "How this one goes wrong"
    // was a real want served by an instrument that failed twice: it wore alarm-red for
    // advisory content, and it re-argued verdicts the referral and the options above it
    // had already settled. Nothing replaced it here - the evidence a reader needs is the
    // referral, the contenders and their caveats, and a fourth register arguing with
    // them was the noise the case page exists to keep out.

    ImGui.Separator();
    DrawVerdictButtons(walk, row, tribunal);
  }

  /// <summary>
  /// The referral, wrapped and coloured as the case's own headline - the sentence
  /// that answers "why am I looking at this". One painter, two seats: it draws
  /// above the verdict on a contradiction-door case (the dispute leads, 08-23)
  /// and below it everywhere else. An empty referral (the dead-heat fold) draws
  /// nothing at all.
  /// </summary>
  private static void DrawCaseReferral(TriageCase tribunal)
  {
    if (tribunal.Referral.Length == 0) return;
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
    ImGui.TextWrapped(tribunal.Referral);
    ImGui.PopStyleColor();
  }

  /// <summary>
  /// THE VERDICT. Every exit in <see cref="TriageCase.Exits"/> gets a button - the
  /// override grammar never narrows - and a door the game does not offer this item is
  /// dead rather than missing, with the cross's own sentence on the hover (the same
  /// contract the score cells keep).
  ///
  /// <para>The press does TWO things and both are load-bearing: it tells the walk (so the
  /// case is answered, the docket ticks and the Continue's count drops) and it makes the
  /// real ruling through <see cref="RuleRow"/> - the same staging, the same teaching
  /// signal, the same paths the board's cells always wrote. Nothing commits; the hinge's
  /// Continue spends it, exactly as it does today.</para>
  /// </summary>
  private void DrawVerdictButtons(TriageWalk walk, BoardRow row, TriageCase tribunal)
  {
    var argued = tribunal.Options.ToDictionary(o => o.Exit, o => o.Worth);

    for (var i = 0; i < tribunal.Exits.Count; i++)
    {
      if (i > 0) ImGui.SameLine(0, 4);
      var pile = tribunal.Exits[i];
      var exit = TriageBridge.ExitFor(pile);
      var open = exit is RoutingExit e && row.Doors.Has(e);
      var chosen = tribunal.DecidedExit == pile;

      // An argued exit carries its worth in the label: the number is the thing being
      // weighed, and making the reader match a button to a paragraph above it is the
      // shackling the case page exists to undo.
      var label = argued.TryGetValue(pile, out var worth)
        ? $"{CaseVoice.ExitWord(pile)} {Format.Gil(worth)}"
        : CaseVoice.ExitWord(pile);

      if (chosen) ImGui.PushStyleColor(ImGuiCol.Button, ChosenVerdictBg);
      ImGui.BeginDisabled(!open);
      if (ImGui.Button($"{label}##verdict{row.Key}:{i}") && exit is RoutingExit door)
      {
        RuleRow(row, door);
        walk.Decide(row.Key, pile);
        AdvanceToNextUndecided(walk);
      }
      ImGui.EndDisabled();
      if (chosen) ImGui.PopStyleColor();

      if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) continue;
      ImGui.SetTooltip(!open && exit is RoutingExit shut
        ? ExitDoors.ClosedHint(shut)
        : chosen
          ? "Your call, standing. Click another exit to change it - nothing is committed until Continue."
          : $"Rules this one to {CaseVoice.ExitWord(pile)}. The round spends it at the Continue.");
    }

    // THE MISSING VERB (Drift, 2026-08-23, the Hemicyon Hide case: "what button
    // tells it to stand?"). Every case demanded an exit and the Continue refused
    // until it got one - there was no honest answer for "do nothing with this
    // copy right now." Hold answers the docket WITHOUT RuleRow: nothing stages,
    // nothing commits, the item stays where it is, and the round asks again next
    // time. BoardPile.Silent is the verdict's honest home - "a confident verdict
    // not to engage" - and verdict piles are never acted on (only RuleRow's
    // staging is spent), so Silent here cannot leak into the routing model.
    ImGui.SameLine(0, 4);
    var heldBack = tribunal.DecidedExit == BoardPile.Silent;
    if (heldBack) ImGui.PushStyleColor(ImGuiCol.Button, ChosenVerdictBg);
    if (ImGui.Button($"hold##verdict{row.Key}:hold"))
    {
      // An exit pressed earlier mutated the routed item (InReview off,
      // PlayerResolved on) - hold restores the unruled Review state so the round
      // withholds the row, which is Review's own contract. Pile stays as the
      // router left it: InReview=true is what wins the pile mapping, and the
      // recorded exit beneath it is inert. Idempotent when hold is pressed first.
      if (row.Routed is { } routed)
      {
        routed.InReview = true;
        routed.PlayerResolved = false;
      }
      walk.Decide(row.Key, BoardPile.Silent);
      AdvanceToNextUndecided(walk);
    }
    if (heldBack) ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip(heldBack
        ? "Held - the case is answered and nothing will be done. Click an exit to change it."
        : "Answers the case without doing anything: the item stays where it is,\n"
          + "nothing is staged, and Scrooge will ask again next round.");
  }

  /// <summary>The chosen verdict's wash - the same green the board's pressed cell wears,
  /// so "this is the call" is one colour across both surfaces.</summary>
  private static readonly Vector4 ChosenVerdictBg = new(0.20f, 0.36f, 0.20f, 0.85f);

  /// <summary>
  /// Deciding a case ADVANCES to the next one still owed an answer (ruled 08-15). It
  /// wraps: the docket is a ring, and stopping at the end would strand the player on a
  /// decided case with unanswered ones behind him. All decided = stay put, because there
  /// is nowhere honest to go and the page's Next has just become pressable.
  /// </summary>
  private void AdvanceToNextUndecided(TriageWalk walk)
  {
    var page = walk.Pages[Array.IndexOf(TriageWalk.Order, PageKind.BagDecisions)];
    var owed = walk.UndecidedCases;
    if (owed.Count == 0) return;

    for (var step = 1; step <= page.Rows.Count; step++)
    {
      var i = (_caseCursor + step) % page.Rows.Count;
      if (owed.Any(o => string.Equals(o.Key, page.Rows[i].Key, StringComparison.Ordinal)))
      {
        _caseCursor = i;
        return;
      }
    }
  }

  // ==========================================================================
  // CaseEvidence: the banked facts, gathered
  // ==========================================================================

  /// <summary>
  /// EVERYTHING ONE CASE IS BUILT FROM, gathered once per composition and handed in
  /// whole. Nothing here is derived by the case layer, and nothing here is invented by
  /// this one: a fact the plugin has not banked is left at its default, the sentence it
  /// would have fed does not fire, and no line hedges in its place ("SPEND all of it",
  /// spec section 2 - all of it, and only it).
  /// </summary>
  private CaseEvidence FactsFor(BoardRow row)
  {
    if (_caseFacts.TryGetValue(row.Key, out var cached)) return cached;
    var facts = BuildFacts(row);
    _caseFacts[row.Key] = facts;
    return facts;
  }

  private CaseEvidence BuildFacts(BoardRow row)
  {
    var itemId = row.ItemId;
    var isHq = row.Routed?.IsHq ?? row.Standing?.Item.IsHq ?? false;
    var ilvl = row.Ilvl;

    // The evidence the TIER was scored from - banked with the row for the bag half, and
    // rebuilt through the one shared function for the standing half. Either way it is the
    // same evidence the board judged, which is what keeps the case's Alexander sentence
    // about the axis this verdict actually failed.
    var evidence = row.Routed is { } routed
      ? routed.Evidence
      : row.Standing is { } standing
        ? LedgerCache.StandingEvidence(standing.Item, BoardPiles.ForStanding(standing.Item.Result))
        : default;

    // The verdict the contradiction is AGAINST: the router's own exit on a bag row (the
    // demotion moved the pile, never the verdict), the natural pile on a standing one.
    var contradicted = row.Routed is { } r2
      ? BoardPiles.ForRoutingExit(r2.Verdict.Exit, isReview: false)
      : row.Standing is { } s2 ? BoardPiles.ForStanding(s2.Item.Result) : (BoardPile?)null;

    var (ownSale, ownSaleAge, ownSaleDays) = LastSaleFacts(itemId, isHq);
    var tapeSoldAge = TapeLastSoldAge(itemId, isHq);
    var melt = MeltRollup(itemId, isHq);
    var (laneMedian, laneSamples, laneAgeDays, undercutters, laneSpread) =
      LaneFacts(itemId, isHq, row.Ask);
    var band = ilvl > 0 ? _cache.MeltPriors?.For(ilvl) : null;
    var community = row.Routed?.CommunityMedian
      ?? _cache.ListedFactsFor(itemId, isHq)?.CommunityMedian ?? 0;
    var seals = row.Routed?.SealValue
      ?? _cache.ListedFactsFor(itemId, isHq)?.SealValue;

    // THE ROUND'S OWN LOOK (ruled 08-16: "triage must use the latest information we
    // have"). Recon runs before the hinge by stage order, and the evidence stamp
    // re-composes the walk when it completes - so by the time a case draws, a fresh
    // banked Look is the best price in the building, and it is the SAME row the hawk
    // posts from ("Priced from your Look"). Same freshness rule as both existing
    // doors (ReconFreshness - one rule, now three doors). A held look (null price)
    // stays out: "the evidence would not carry a price" is not an ask.
    //
    // AND IT CARRIES ITS DERIVATION (pen 8a, 3b-5). The banked row already holds the
    // lane outcome the price was struck against and the evidence sentence behind it -
    // both were sitting in the same record the price came out of, and the case dropped
    // them. Reading them here costs nothing extra: it is the row we already opened.
    static (long Ask, long AgeSeconds, string Anchor, string Evidence)? FreshReconAsk(
      uint itemId, bool isHq)
    {
      try
      {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (GilStorage.GetDecisionCache(itemId, isHq) is { DecidedPrice: > 0 } look
            && ReconFreshness.IsFresh(look.BankedAt, now, Plugin.Configuration.ReconFreshHours))
          return (look.DecidedPrice.Value, Math.Max(0, now - look.BankedAt),
            look.Outcome ?? "", look.Evidence ?? "");
      }
      catch (Exception ex)
      {
        Svc.Log.Warning($"[Triage] Banked Look unreadable for {itemId}: {ex.Message}");
      }
      return null;
    }
    var reconLook = FreshReconAsk(itemId, isHq);

    return new CaseEvidence(
      Key: row.Key,
      Title: row.Name,
      Reason: TriageBridge.ReasonFor(row.Tier, row.Scores.Any(s => s is not null)),
      Scores: ExitScores(row, ownSale, community, _cache.SealRateMeasured, reconLook?.Ask ?? 0),
      OwnSalePrice: ownSale,
      OwnSaleAgeDays: ownSaleAge,
      OwnSaleDaysToSell: ownSaleDays,
      MeltAttempts: melt.Attempts,
      MeltAverage: melt.Average,
      MeltSpreadLow: melt.Low,
      MeltSpreadHigh: melt.High,
      MeltBandIlvl: band is { } b ? b.BandFloor : 0,
      MeltBandAverage: band is { } b2 ? b2.ValuePerAttempt : 0,
      SealCount: seals ?? 0,
      SealGilRate: _cache.SealRate.EffectiveRate,
      VendorPrice: LedgerCache.VendorPriceOf(itemId),
      LaneMedian: laneMedian,
      LaneSampleCount: laneSamples,
      MinSamples: evidence.MinSamples,
      // The lane's own disagreement, off the same read the median came from (3-3b) -
      // so the scattered-lane caveat fires on the lane the paragraph above it is
      // describing, and fires at the same boundary the tier calls "tight".
      LaneSpread: laneSpread,
      LaneAgeDays: laneAgeDays,
      StaleDays: evidence.StaleDays,
      VelocityPerDay: row.Routed?.MarketVelocity ?? UniversalisStats.TryGet(itemId, isHq)?.Velocity,
      Undercutters: undercutters,
      BoardReadAgeDays: laneAgeDays,
      ContradictionAxis: BoardConfidence.SalesVerdictAccord(evidence),
      ContradictedExit: contradicted,
      Lean: evidence.Lean,
      RecentSalesCount: evidence.RecentSalesCount,
      ReconAsk: reconLook?.Ask ?? 0,
      ReconAskAgeSeconds: reconLook?.AgeSeconds ?? 0,
      ReconAskAnchor: reconLook?.Anchor ?? "",
      ReconAskEvidence: reconLook?.Evidence ?? "",
      // THE OPERAND THE CONTRADICTION VOICE WAS MISSING (pen 7). Same fact the walk's
      // own row input carries and the same one the evidence above was built from: a
      // standing row has a listing on the board, a bag row does not. Passing it here is
      // what stops a bagged row being told its sales disagree with taking it off a board
      // it was never on - and what lets the bag arm name its DC witness honestly.
      IsStanding: row.Standing is not null,
      // WHICH DOOR SENT IT (ruled B5; sharpened 08-23): "a dead heat" is the band
      // door's claim ALONE. IsReview here painted every scored review as a tie -
      // a Look-priced 20,454 List reviewed against a 2,154 GC is a referral, not
      // a heat - so the flag now reads the verdict's own BandTie, which only the
      // review band's door ever sets. Every other review speaks thin-evidence.
      RouterDeclined: row.Routed?.Verdict.BandTie ?? false,
      // THE TAPE'S WITNESS FOR THE LAST-SOLD SEAT (V31): when the lane's own ring has
      // a settled sale, its date answers - "sold yesterday, not by me" is exactly the
      // liquidity fact the seat exists for. Null falls back to the own-sale receipt
      // inside LastSoldLine, and a row with neither draws nothing.
      LastSoldAgeDays: tapeSoldAge);
  }

  /// <summary>
  /// How many days since this variant last changed hands on the banked tape, or null
  /// when it never has (or the book is closed). One MAX over the dedup index, on the
  /// case-assembly clock like every read above it - never per frame.
  /// </summary>
  private static int? TapeLastSoldAge(uint itemId, bool isHq)
  {
    try
    {
      if (GilStorage.NewestSaleTime(itemId, isHq) is not long soldAt || soldAt <= 0)
        return null;
      return (int)Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - soldAt) / 86_400);
    }
    catch { return null; }
  }

  /// <summary>
  /// The four exits' scored worth, with what each number IS beside it. Only exits the
  /// scorers actually PRICED are here: a missing score is not a zero, and an ExitScore of
  /// 0 would put a contender on the page arguing for nothing.
  /// </summary>
  private static List<ExitScore> ExitScores(
    BoardRow row, long ownSale, long communityMedian, bool sealRateMeasured,
    long reconAsk = 0)
  {
    var scores = new List<ExitScore>(4);
    for (var i = 0; i < BoardCalls.Exits.Length; i++)
    {
      if (row.Scores[i] is not long worth) continue;
      var exit = BoardCalls.Exits[i];
      if (!row.Doors.Has(exit)) continue;
      var pile = BoardPiles.ForRoutingExit(exit, isReview: false);
      var provenance = pile switch
      {
        BoardPile.List => TriageBridge.ListProvenance(ownSale, communityMedian),
        BoardPile.Melt => TriageBridge.MeltProvenance(row.MeltGrade),
        BoardPile.Churn => TriageBridge.SealProvenance(sealRateMeasured),
        _ => WorthProvenance.VendorFloor,
      };
      scores.Add(new ExitScore(pile, worth, provenance));
    }

    // THE OWN SALE CAN NEVER BE DROPPED BETWEEN THE TABLE AND THE PANEL (F7,
    // ruled 08-22 - the Silvergrace case QUOTED "You last sold one earlier this
    // month for 12,299" two lines above a referral claiming no evidence exists).
    // When the List door is open and no score reached the strip, the settled own
    // sale IS the List contender - Measured, the strongest receipt the plugin
    // holds. Age never disqualifies it here (age decays authority, never flips
    // the sign); the case's own-sale line carries the age for the reader.
    if (ownSale > 0
        && row.Doors.Has(RoutingExit.List)
        && !scores.Any(s => s.Exit == BoardPile.List))
      scores.Add(new ExitScore(BoardPile.List, ownSale, WorthProvenance.Measured));

    // THE LOOK FILLS THE ROUTER'S BLIND EYE (ruled 08-16). The router prices List
    // from an own sale or the community median only - a blind lane forfeits, a
    // 4-gil floor wins, and the case then drew a list verb with no number while
    // recon's banked ask for the same variant sat minutes fresh in the db (the
    // Reading Glasses receipt: case said melt 1,246 vs vendor 1; the Look said
    // 9,997 and the hawk posted it). When the router left List unscored and the
    // door is open, the fresh Look IS the contender - provenance Asked, because a
    // would-post ask is a number nobody has paid.
    if (reconAsk > 0
        && row.Doors.Has(RoutingExit.List)
        && !scores.Any(s => s.Exit == BoardPile.List))
      scores.Add(new ExitScore(BoardPile.List, reconAsk, WorthProvenance.Asked));

    return scores;
  }

  /// <summary>
  /// Your last settled sale of this variant, its age, and how long it sat. All three come
  /// off ONE banked row (<c>last_sale_prices</c>), because the sentence they feed names
  /// all three and a price paired with somebody else's clock would be worse than no
  /// sentence.
  /// </summary>
  private (long Price, int AgeDays, int? DaysToSell) LastSaleFacts(uint itemId, bool isHq)
  {
    try
    {
      if (_cache.LastSaleFor(itemId, isHq) is not { } sale || sale.Price <= 0)
        return (0, 0, null);
      var age = (int)Math.Max(0,
        (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sale.Timestamp) / 86_400);
      return (sale.Price, age, sale.SoldAfterDays);
    }
    catch { return (0, 0, null); }
  }

  /// <summary>Your own desynths of this one, with the spread the average is worth
  /// nothing without. Zero attempts is the honest "you have never melted one" the band
  /// prior fires on.</summary>
  private static (int Attempts, long Average, long Low, long High) MeltRollup(uint itemId, bool isHq)
  {
    try { return Plugin.DesynthYieldStore?.SourceRollup(itemId, isHq) ?? (0, 0, 0, 0); }
    catch { return (0, 0, 0, 0); }
  }

  /// <summary>
  /// The lane, off the last board read this plugin banked: what the middle of it was
  /// asking, how many listings backed that, how old the read is, how many sellers sat
  /// under your own ask, and how many times the board has moved since.
  ///
  /// <para>OWN LISTINGS ARE EXCLUDED from the median and the count. "What is the lane
  /// asking" is a question about everybody else - folding your own ask into it makes the
  /// number agree with you by construction.</para>
  ///
  /// <para><b>And how much it disagrees with itself</b> (3-3b): the same sorted list's
  /// quartiles over its own middle, through the one measure
  /// (<see cref="LanePricing.BandSpread"/>) the board-now "scattered" caveat reads. The
  /// band is the ASK lane's, because every other number this method returns is - a
  /// spread taken off the sale tape and printed beside an ask median would be two
  /// instruments wearing one sentence, which is exactly the weld 3b-4 broke.</para>
  ///
  /// <para>THE BOARD-MOVES COUNT LEFT WITH THE TRAPS (3b-1). It had one consumer, the
  /// fossilized-read trap, and the register it fed is gone.</para>
  /// </summary>
  private static (long Median, int Samples, int AgeDays, int Undercutters, double Spread)
    LaneFacts(uint itemId, bool isHq, long? ownAsk)
  {
    try
    {
      var (prior, scanAt) = GilStorage.GetBoardSnapshot(itemId);
      var lane = prior.Where(l => !l.IsOwn && (!isHq || l.IsHq))
        .Select(l => l.UnitPrice).OrderBy(p => p).ToList();
      if (lane.Count == 0 && scanAt <= 0) return (0, 0, 0, 0, 0.0);

      var median = lane.Count == 0 ? 0 : lane[lane.Count / 2];
      var age = scanAt > 0
        ? (int)Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - scanAt) / 86_400)
        : 0;
      var under = ownAsk is long ask && ask > 0 ? lane.Count(p => p < ask) : 0;
      var spread = lane.Count == 0 ? 0.0 : LanePricing.BandSpread(
        lane[lane.Count / 4], lane[Math.Min(lane.Count - 1, lane.Count * 3 / 4)], median);
      return (median, lane.Count, age, under, spread);
    }
    catch { return (0, 0, 0, 0, 0.0); }
  }
}
