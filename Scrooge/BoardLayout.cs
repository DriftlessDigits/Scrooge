using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Where the call on a ledger row came from. Four states and no fifth: the
/// router made it, the player made it, nobody has (the Review pile), or the
/// row's own existence is the call (a standing listing).
/// </summary>
internal enum CallSource
{
  /// <summary>The router's own verdict stands, unmoved (or confirmed) by the player.</summary>
  Router,
  /// <summary>The player ruled - either moving the row off the router's call, or ruling a Review row.</summary>
  Player,
  /// <summary>Drawn in Review with no ruling on it. No exit is pressed, because none was chosen.</summary>
  Unruled,
  /// <summary>
  /// The router called it on thin ice (the Defer pile, 08-06). Structurally
  /// this IS the router's own call - the same cell is pressed and the round
  /// runs the same verb - so it is not a fifth kind of decision, it is the
  /// router's decision wearing its doubt out loud. It gets its own source only
  /// because the Call column has to say "defer" rather than "router": the
  /// difference the player cares about is not who decided, it is how sure we
  /// were and whether he is invited to argue.
  /// </summary>
  Deferred,
  /// <summary>
  /// A standing listing IS a prior ruling (Drift, 08-02: "standing is a ruling").
  /// The status quo is the call, nobody owes it a click, and the router speaks
  /// only to CONTEST it - a contest is drawn Unruled, a dismissal re-affirms
  /// this. The ask is the pressed thing, and the ask is not a cell.
  /// </summary>
  Standing,
}

/// <summary>
/// One row's call, as the Call column says it and as the score strip draws it.
/// <see cref="Pressed"/> is the whole reason this is a type rather than a string:
/// an unruled row must show FOUR neutral cells, and a ruled one exactly one
/// pressed cell - the same fact, asked two ways, from one answer.
/// </summary>
internal readonly record struct CallState(
  CallSource Source,
  RoutingExit Call,
  RoutingExit RouterExit,
  bool RouterWasReview,
  // False when the player's verb has no strip column (a staged Pull): the call
  // is real, the label prints it, but no cell may claim it. Default true - the
  // bag rows' four exits all have cells.
  bool CallHasCell = true)
{
  /// <summary>
  /// The cell drawn "pressed". Null on an unruled row - nothing has been picked
  /// - null on a standing row, whose call lives in its ask, not in a cell - and
  /// null when the call is a verb with no column (a staged Pull).
  /// </summary>
  internal RoutingExit? Pressed
    => Source is CallSource.Unruled or CallSource.Standing || !CallHasCell ? null : Call;
}

/// <summary>
/// Which exits EXIST for a row - not which have evidence. The game decides these
/// (market search category, the sheet's Desynth flag, GC seal eligibility, vendor
/// price), the router has always respected them, and the cells must too: a click
/// on a doorless cell used to rule a row toward an exit the orchestrators could
/// only silently skip. A closed door is drawn as a dead cell whose hint names the
/// fact; an OPEN door with a null score stays a live cell - "no evidence yet" is
/// a real choice ("melt one to learn"), "no such door" is not a choice at all.
/// </summary>
internal readonly record struct ExitDoors(bool List, bool Melt, bool Gc, bool Vendor)
{
  /// <summary>Every door open - the default wherever eligibility is unknown, so unknowns keep today's behavior.</summary>
  internal static readonly ExitDoors AllOpen = new(true, true, true, true);

  internal bool Has(RoutingExit exit) => exit switch
  {
    RoutingExit.List => List,
    RoutingExit.Desynth => Melt,
    RoutingExit.Gc => Gc,
    RoutingExit.Vendor => Vendor,
    _ => true,
  };

  /// <summary>The dead cell's hover text: the fact, in player language.</summary>
  internal static string ClosedHint(RoutingExit exit) => exit switch
  {
    RoutingExit.List => "Can't be sold on the market board.",
    RoutingExit.Desynth => "Can't be desynthed.",
    RoutingExit.Gc => "The Grand Company doesn't take this one.",
    RoutingExit.Vendor => "No vendor will buy this one.",
    _ => "",
  };
}

/// <summary>
/// THE CALL-STATE MODEL (ruled ledger, stage 1). The score cells ARE the
/// controls, so the whole gesture vocabulary is: which cell is pressed, what the
/// Call column says, and whether a click on a given cell changes anything.
///
/// <para>Pure - no game, no storage, no ImGui (LanePricing/BoardPiles mold,
/// linked into Scrooge.Tests). The window owns the CONSEQUENCE of a click (it
/// writes the same override the move buttons always wrote); this owns only the
/// question of what the click means.</para>
/// </summary>
internal static class BoardCalls
{
  /// <summary>
  /// The four exits, in the one order every row wears them. Same order, same
  /// format, every row - the strip is only readable at a glance if it never moves.
  /// </summary>
  internal static readonly RoutingExit[] Exits =
  {
    RoutingExit.List, RoutingExit.Desynth, RoutingExit.Gc, RoutingExit.Vendor,
  };

  /// <summary>The column heading for an exit.</summary>
  internal static string ColumnLabel(RoutingExit exit) => exit switch
  {
    RoutingExit.List => "List",
    RoutingExit.Desynth => "Melt",
    RoutingExit.Gc => "GC",
    RoutingExit.Vendor => "Vend",
    RoutingExit.Hold => "Hold",
    RoutingExit.Ban => "Ban",
    _ => "?",
  };

  /// <summary>The lowercase form the Call column uses inside "YOU (router: melt)".</summary>
  internal static string ShortName(RoutingExit exit) => ColumnLabel(exit).ToLowerInvariant();

  /// <summary>The strip index an exit is drawn at, or -1 when it has no column (Hold / Ban).</summary>
  internal static int IndexOf(RoutingExit exit) => Array.IndexOf(Exits, exit);

  /// <summary>
  /// The call on a row.
  ///
  /// <para><paramref name="needsRuling"/> is the window's "drawn in Review and
  /// nobody has ruled it" - Review proper, and the Contradicted demotions that
  /// land there. It wins outright: a row waiting on eyes has no call to press.</para>
  ///
  /// <para>A row the ROUTER sent to Review and the player then ruled reads as the
  /// player's call whatever he picked, including the router's own suggestion -
  /// the router never called it, it declined to.</para>
  /// </summary>
  /// <para><paramref name="deferred"/> is asked LAST, after the player's own
  /// hand: a row he moved is his call whatever ice we were on, and dressing his
  /// decision in our doubt would be the board editorializing about the human.</para>
  internal static CallState Of(RoutingExit routerExit, bool routerWasReview,
    RoutingExit currentExit, bool needsRuling, bool deferred = false)
  {
    if (needsRuling)
      return new CallState(CallSource.Unruled, currentExit, routerExit, routerWasReview);
    if (routerWasReview || currentExit != routerExit)
      return new CallState(CallSource.Player, currentExit, routerExit, routerWasReview);
    if (deferred)
      return new CallState(CallSource.Deferred, currentExit, routerExit, routerWasReview);
    return new CallState(CallSource.Router, currentExit, routerExit, routerWasReview);
  }

  /// <summary>What the Call column says. Five shapes, no sixth.</summary>
  internal static string Label(in CallState state) => state.Source switch
  {
    CallSource.Router => "router",
    // The verb is named because a Defer row is drawn AWAY from its exit pile
    // (one home): "defer" alone would leave the player reading four cells to
    // find out what is about to happen to the thing.
    CallSource.Deferred => $"defer: {ShortName(state.Call)}",
    // "router:", never "rtr:" (strings-three) - an abbreviation only we can expand.
    CallSource.Player => state.RouterWasReview
      ? "YOU (router: review)"
      : $"YOU (router: {ShortName(state.RouterExit)})",
    CallSource.Standing => "standing",
    _ => "unruled",
  };

  /// <summary>
  /// The call on a LISTED row - the one place the four-score contract meets the
  /// standing book. A standing listing is a prior ruling: uncontested it draws
  /// <see cref="CallSource.Standing"/> and owes nobody a click; a triage contest
  /// draws <see cref="CallSource.Unruled"/> with the router's proposal riding as
  /// RouterExit; a staged verb is the player's answer. The router never re-calls
  /// the uncontested book - there is no Router source on a listed row.
  /// </summary>
  internal static CallState OfListed(RoutingExit proposal, bool proposalWasReview,
    bool contested, StandingAction staged = StandingAction.None)
  {
    if (staged != StandingAction.None)
    {
      var exit = ExitOfAction(staged);
      return new CallState(CallSource.Player, exit ?? proposal, proposal, proposalWasReview,
        CallHasCell: exit is not null);
    }
    if (contested)
      return new CallState(CallSource.Unruled, proposal, proposal, proposalWasReview);
    return new CallState(CallSource.Standing, RoutingExit.List, RoutingExit.List, false);
  }

  /// <summary>
  /// The strip cell a staged triage verb presses, or null for a verb with no
  /// column (Pull retrieves without an exit; None stages nothing). The inverse
  /// of <see cref="ActionOfExit"/> on the four exits.
  /// </summary>
  internal static RoutingExit? ExitOfAction(StandingAction action) => action switch
  {
    StandingAction.Reprice => RoutingExit.List,
    StandingAction.Vendor => RoutingExit.Vendor,
    StandingAction.Melt => RoutingExit.Desynth,
    StandingAction.Gc => RoutingExit.Gc,
    _ => null,
  };

  /// <summary>
  /// The triage verb a cell click stages on a LISTED row (walk unit 4): List =
  /// reprice to the number the round would write, the other three = pull-for-that-exit. The
  /// inverse of <see cref="ExitOfAction"/>.
  /// </summary>
  internal static StandingAction ActionOfExit(RoutingExit exit) => exit switch
  {
    RoutingExit.List => StandingAction.Reprice,
    RoutingExit.Desynth => StandingAction.Melt,
    RoutingExit.Gc => StandingAction.Gc,
    RoutingExit.Vendor => StandingAction.Vendor,
    _ => StandingAction.None,
  };

  /// <summary>
  /// The board group a STAGED listed row is drawn in: the pile of the verb the
  /// player chose, so a staged melt sits in Melt and a staged pull sits with
  /// the vendor work it shares a door with. Unstaged rows keep their triage
  /// pile - this only follows an answer that has actually been given.
  /// </summary>
  internal static BoardPile PileOfAction(StandingAction action, BoardPile fallback) => action switch
  {
    StandingAction.Reprice => BoardPile.Reprice,
    StandingAction.Vendor or StandingAction.Pull => BoardPile.PullAndVendor,
    StandingAction.Melt => BoardPile.Melt,
    StandingAction.Gc => BoardPile.Churn,
    _ => fallback,
  };

  /// <summary>
  /// The Call column text for a listed row. A staged TRIAGE VERB (vendor / pull /
  /// reprice) is not always a strip exit - "pull" has no column - so the player's
  /// answer prints the verb he actually chose rather than forcing it through
  /// <see cref="Label"/>'s exit grammar.
  /// </summary>
  internal static string ListedLabel(in CallState state, StandingAction staged)
    => state.Source == CallSource.Player
      ? $"YOU ({staged.ToString().ToLowerInvariant()})"
      : Label(state);

  /// <summary>
  /// Does clicking this cell change anything? Everything except a click on the
  /// already-pressed cell of an already-ruled row - which is the no-op the move
  /// buttons have always been (the green button that refused its own click).
  /// On an unruled row EVERY cell is live, including the router's own: there the
  /// click IS the ruling. On a standing row every cell is live too - no cell is
  /// pressed (the ask is the call), so any click is the player contesting his
  /// own standing position, which is his right.
  /// </summary>
  internal static bool IsActionable(in CallState state, RoutingExit clicked)
    => state.Source is CallSource.Unruled or CallSource.Standing
       || !state.CallHasCell
       || clicked != state.Call;

  /// <summary>
  /// THE TELL, on every cell that draws a melt number (08-03). A prior wears a
  /// "~" in front of its digits so an estimate can never sit in the same column
  /// as a measurement and read identically - the cell has no room for the band
  /// sentence, but it has room for one character that means "we did not measure
  /// this one". Every other exit, and every other grade, prints bare: the mark
  /// only means something if it is rare.
  /// </summary>
  internal static string GradeMark(RoutingExit exit, MeltGrade grade)
    => exit == RoutingExit.Desynth && grade == MeltGrade.Prior ? "~" : "";

  /// <summary>
  /// The grade, said in a sentence, for a score cell's tooltip. Empty for every
  /// exit but Melt, and for a melt number with no grade on it (a pre-grade
  /// score) - silence, never a guess about where a number came from.
  /// </summary>
  internal static string GradeHint(RoutingExit exit, MeltGrade grade)
    => exit != RoutingExit.Desynth ? "" : grade switch
    {
      MeltGrade.Measured => "Measured: your own desynths of this item.",
      MeltGrade.Prior => "Estimated - nobody has ever melted one of these. This is the ilvl band's average from your melt history, not this item's.",
      MeltGrade.Skillup => "Priced at your skill-up knob - this is what a skill-up is worth to you, not gil from mats.",
      _ => "",
    };

  /// <summary>
  /// The score the router weighed for one exit. Null = that exit had no evidence
  /// when the call was made (drawn as a dash, never as a zero - a zero would be a
  /// claim nobody made).
  /// </summary>
  internal static long? ScoreOf(RoutingScores? scores, RoutingExit exit)
    => scores is not { } s ? null : exit switch
    {
      RoutingExit.List => s.List,
      RoutingExit.Desynth => s.Melt,
      RoutingExit.Gc => s.Gc,
      RoutingExit.Vendor => s.Vendor,
      _ => null,
    };
}

/// <summary>One pile group's header arithmetic: how many rows, and what they are worth.</summary>
internal readonly record struct PileSummary(int Count, long ExpectedGil, int Unpriced);

/// <summary>
/// THE BOARD'S COLUMNS, left to right, as one named list (item 9, 08-06). The
/// table used to sort on a bare index with the four score columns living at
/// 1..4 by arithmetic - which held exactly as long as nothing was ever inserted
/// to their left. Two things then were: the triage FLAG, which had been a
/// coloured glyph you could read but not sort, and ILVL, which had been a
/// tooltip. Naming the columns is what makes inserting one a rename instead of
/// an off-by-one in a sort nobody would notice until a gil column ordered by
/// item level.
/// </summary>
internal enum BoardColumn
{
  /// <summary>The confidence tier as a sortable value - "which rows want me".</summary>
  Flag,
  Item,
  /// <summary>Item level. 0 = unknown (a listed lane the scoring pass never reached), sorted like an unpriced score.</summary>
  Ilvl,
  List,
  Melt,
  Gc,
  Vend,
  /// <summary>Never sorted: the call is a sentence about the row, not a rank.</summary>
  Call,
}

/// <summary>
/// The board's grouping and header aggregation. Pure, for the same reason
/// <see cref="BoardPiles"/> is: the window should be able to draw the answer,
/// never to compute it.
/// </summary>
internal static class BoardLayout
{
  /// <summary>
  /// The group order down the board, and it is the EYES AXIS first: Review
  /// (action withheld until answered), then Defer (action proceeds, flagged),
  /// then the exits in workflow order (action proceeds, nothing to say). One
  /// scan down the top of the board is the whole three-state contract.
  ///
  /// <para><see cref="BoardPile.Silent"/> is deliberately absent, and that is
  /// the third state being drawn correctly: a protected hold and an observed
  /// ban are confident verdicts NOT to engage, so they have no row. The old
  /// Watch pile is absent because it no longer exists.</para>
  ///
  /// <para>ONE HOME (ruled 08-06): a Defer row appears HERE and nowhere else.
  /// Its four-score strip still presses the verb the round will run, so the
  /// planned action is never hidden - but the exit piles below count only
  /// their confident rows, which means an exit header reads lower than what
  /// the round executes. Accepted trade: one scan of Defer is everything
  /// acting on thin ice, and a row drawn twice is a row that can be answered
  /// twice.</para>
  /// </summary>
  internal static readonly BoardPile[] GroupOrder =
  {
    BoardPile.Review,
    BoardPile.Defer,
    BoardPile.List,
    BoardPile.Reprice,
    BoardPile.PullAndVendor,
    BoardPile.Melt,
    BoardPile.Churn,
  };

  /// <summary>
  /// The column at a table index, defaulting to <see cref="BoardColumn.Item"/>
  /// for anything out of range - the same name sort the board has always fallen
  /// back to when ImGui hands over a spec it cannot place.
  /// </summary>
  internal static BoardColumn ColumnAt(int index)
    => index >= 0 && index <= (int)BoardColumn.Call ? (BoardColumn)index : BoardColumn.Item;

  /// <summary>The exit a score column sorts on, or null for the columns that are not scores.</summary>
  internal static RoutingExit? ExitOfColumn(BoardColumn column) => column switch
  {
    BoardColumn.List => RoutingExit.List,
    BoardColumn.Melt => RoutingExit.Desynth,
    BoardColumn.Gc => RoutingExit.Gc,
    BoardColumn.Vend => RoutingExit.Vendor,
    _ => null,
  };

  /// <summary>
  /// THE FLAG COLUMN'S ORDER: how loudly a row is asking for you, ascending, so
  /// one click on the header stacks the work at the top.
  ///
  /// <para>A row waiting on a ruling outranks every tier, including a
  /// Contradicted one that has already been ruled - the tier is what the
  /// EVIDENCE says, and needing a human is what the BOARD says, and the second
  /// is the one that decides whether tonight's round can start. The predicate
  /// behind that flag is <see cref="GatePlan.NeedsRuling"/>, the same one the
  /// launch control refuses over, so sorting by attention and pressing the
  /// launch button agree about which rows are in the way.</para>
  ///
  /// <para>DEFER SITS BETWEEN THEM (08-06), and the gap it opens is the whole
  /// point of the three states. Rank 0 is "I won't move without you"; rank 1
  /// is "I'm moving, and I'd like you to look"; everything below is "I'm
  /// moving." A deferred row is usually Mixed, so ranking it by tier would
  /// have buried it under Contradicted rows that already have their answer -
  /// the eyes axis outranks the evidence axis here for the same reason
  /// needsRuling does.</para>
  /// </summary>
  internal static int AttentionRank(ConfidenceTier tier, bool needsRuling, bool deferred = false)
    => needsRuling ? 0
     : deferred ? 1
     : tier switch
     {
       ConfidenceTier.Contradicted => 2,
       ConfidenceTier.Mixed => 3,
       _ => 4,
     };

  internal static string GroupTitle(BoardPile pile) => pile switch
  {
    BoardPile.Review => "Review",
    BoardPile.Defer => "Defer",
    BoardPile.List => "Sell / List",
    BoardPile.Reprice => "Reprice",
    BoardPile.PullAndVendor => "Vendor",
    BoardPile.Melt => "Melt",
    BoardPile.Churn => "GC turn-in",
    _ => pile.ToString(),
  };

  /// <summary>
  /// The exit a pile's rows are headed for - which is also the score column its
  /// header sums. Review has none: nothing in it has been aimed anywhere yet.
  /// </summary>
  internal static RoutingExit? ExitOf(BoardPile pile) => pile switch
  {
    BoardPile.List => RoutingExit.List,
    BoardPile.PullAndVendor => RoutingExit.Vendor,
    BoardPile.Melt => RoutingExit.Desynth,
    BoardPile.Churn => RoutingExit.Gc,
    _ => null,
  };

  /// <summary>
  /// Folds a group's row values into its header line's operands. A row with no
  /// value for the pile's exit is COUNTED but not summed, and the count of those
  /// is carried out - a header that quietly summed nulls as zero would report a
  /// smaller pile than it holds and never say why.
  /// </summary>
  internal static PileSummary Summarize(IEnumerable<long?> rowValues)
  {
    var count = 0;
    var total = 0L;
    var unpriced = 0;
    foreach (var v in rowValues)
    {
      count++;
      if (v is long gil && gil > 0) total += gil;
      else unpriced++;
    }
    return new PileSummary(count, total, unpriced);
  }

  /// <summary>The group header's own line: what is in the pile and what it is worth.</summary>
  internal static string HeaderLine(BoardPile pile, in PileSummary s)
  {
    var rows = $"{s.Count} row{(s.Count == 1 ? "" : "s")}";
    var gil = s.ExpectedGil > 0 ? $", ~{s.ExpectedGil:N0} gil" : "";
    var unpriced = s.Unpriced > 0 ? $" ({s.Unpriced} unpriced)" : "";
    return $"{GroupTitle(pile)} - {rows}{gil}{unpriced}";
  }

  /// <summary>
  /// The Melt header, currencies SPLIT (strings pass, 08-02): the melt scorer
  /// deliberately prices skill-ups into row scores at Drift's knobs, so a header
  /// that summed those scores as gil promised "~226,875 gil" of which 200k
  /// never arrives as gil - and the desynth line's honest ~18k then read as a
  /// bug. Mats speak in gil; skill-ups speak in skill-ups; the promise and the
  /// delivery finally share a currency. Floored at zero: knob-dominated piles
  /// must round to a small honest number, never a negative claim.
  /// </summary>
  /// <param name="estimatedRows">
  /// Rows whose melt number is a band PRIOR, not a measurement (08-03). The
  /// header sums both grades into one "gil in mats" figure, so the figure has to
  /// say how much of itself nobody has ever weighed - the same tell the cells
  /// wear, at the altitude the promise is made.
  /// </param>
  internal static string MeltHeaderLine(in PileSummary s, int redRows, int yellowRows,
    int knobRed, int knobYellow, int estimatedRows = 0)
  {
    var knobWorth = (long)redRows * knobRed + (long)yellowRows * knobYellow;
    var gilInMats = Math.Max(0, s.ExpectedGil - knobWorth);
    var rows = $"{s.Count} row{(s.Count == 1 ? "" : "s")}";

    var parts = new List<string>();
    if (gilInMats > 0) parts.Add($"~{gilInMats:N0} gil in mats");
    if (redRows > 0) parts.Add($"{redRows} red skill-up{(redRows == 1 ? "" : "s")}");
    if (yellowRows > 0) parts.Add($"{yellowRows} yellow skill-up{(yellowRows == 1 ? "" : "s")}");
    var worth = parts.Count > 0 ? $", {string.Join(" + ", parts)}" : "";
    var notes = new List<string>();
    if (s.Unpriced > 0) notes.Add($"{s.Unpriced} unpriced");
    if (estimatedRows > 0) notes.Add($"{estimatedRows} estimated");
    var tail = notes.Count > 0 ? $" ({string.Join(", ", notes)})" : "";
    return $"{GroupTitle(BoardPile.Melt)} - {rows}{worth}{tail}";
  }

  /// <summary>
  /// ONE ORDERING RULE FOR EVERY NUMERIC COLUMN, on every table that draws board
  /// vocabulary: values first in the asked direction, name as the stable tie-break,
  /// and the rows with NO value parked at the bottom of BOTH directions. "No number"
  /// is not a small number, and letting it masquerade as one is how a dash ends up at
  /// the top of a descending sort on gil.
  ///
  /// <para>Generic in the row because the standing book's tab sorts its own rows by
  /// the same rule and for the same reason - two tables reading one column vocabulary
  /// (see <see cref="BoardColumn"/>) may not disagree about where an absence sits.</para>
  /// </summary>
  internal static List<T> NullsLast<T, TKey>(
    List<T> rows, Func<T, TKey?> key, Func<T, string> name, bool ascending)
    where TKey : struct
  {
    var valued = rows.Where(r => key(r) is not null);
    var blank = rows.Where(r => key(r) is null).OrderBy(name, StringComparer.OrdinalIgnoreCase);
    var ordered = ascending
      ? valued.OrderBy(r => key(r)!.Value)
      : valued.OrderByDescending(r => key(r)!.Value);
    return ordered.ThenBy(name, StringComparer.OrdinalIgnoreCase).Concat(blank).ToList();
  }
}

/// <summary>
/// SHARED REASONING, TAKEN OFF THE ROW. Two fragments the router appends to EVERY row of
/// a pile - the seal-runway discount clause and the skillup-scarcity parenthetical - are
/// rationale about the pile, not operands about the item. Repeated verbatim down twenty
/// rows they read as noise.
///
/// <para><b>THE HEADER NOTES DIED IN 3b-6</b> (pen 5, dark-mode rule). Both fragments
/// used to be re-rendered whole at the top of the riders page: the skillup one recited
/// the two worth knobs back at the player, which is a surface explaining the
/// configuration that produced the policy, and the seal one narrated the stock curve.
/// The DECISIONS both fragments were rationale for are still said with their live
/// operands - the melt row's own reason carries "worth 100,000 gil to you", and the seal
/// narration rides the GC option's line in the detail pane. What is left here is the
/// removal half: the row's narration loses the rationale, and nothing re-says it.</para>
/// </summary>
internal static class BoardNarration
{
  /// <summary>The skillup clause <see cref="RoutingRules"/> appends, leading space included.</summary>
  internal const string SkillupClause = " (skillups are scarce)";

  /// <summary>
  /// Removes the shared fragments from a row's own narration and tidies the seam
  /// they left. Collapsing runs of spaces and lifting a stranded space before a
  /// full stop is what keeps "worth 50,000 gil to you ." from being the price of
  /// saying the rule once.
  /// </summary>
  internal static string Strip(string? reason, params string?[] fragments)
  {
    var text = reason ?? "";
    foreach (var f in fragments)
    {
      if (string.IsNullOrEmpty(f)) continue;
      text = text.Replace(f, "");
    }
    while (text.Contains("  ")) text = text.Replace("  ", " ");
    text = text.Replace(" .", ".").Replace(" ,", ",");
    return text.Trim();
  }
}
