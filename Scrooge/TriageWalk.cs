using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// The three pages of the walk, in the order they are walked. The order is the
/// enum's order and nothing else reads a page's index for meaning - adding or
/// reordering a page later is composition, not architecture (Drift, 08-15:
/// "build and iterate on").
/// </summary>
internal enum PageKind
{
  /// <summary>Standing-listing decisions, board-shaped: the columns are the verbs.</summary>
  BoardDecisions,
  /// <summary>"N ride." One glance, one press - disagreement is ruled in place from
  /// the detail pane, never by moving the row (F2, ruled 08-22).</summary>
  Riders,
  /// <summary>The contested few, one case at a time. The room the north star demands.</summary>
  BagDecisions,
}

/// <summary>
/// ONE ROW HANDED TO THE WALK - the routing facts that decide which page it lands
/// on, plus the strings the page will draw. Task 3 builds these off the WorkItems
/// it already holds; the walk derives no evidence of its own and re-reads no board.
///
/// <para><b>The pile is the EFFECTIVE pile</b> - <see cref="BoardPiles.Effective"/>
/// already applied, Contradicted demotions already landed in Review. The walk is a
/// re-presentation of the same board (spec section 1), so a second opinion about
/// where a row belongs is exactly the thing it must not hold.</para>
/// </summary>
/// <param name="Key">Stable row identity, unique within one composition. Verdicts are
/// keyed on it, which is how they survive a recompose.</param>
/// <param name="IsStanding">The row came off the standing book - a listing already on
/// the board, whose question is a verb. Bag rows are false.</param>
/// <param name="Pile">Where the board draws it, after the confidence layer.</param>
/// <param name="Tier">The evidence-agreement score behind that pile.</param>
/// <param name="PlayerResolved">The human already ruled this row (before the walk).</param>
/// <param name="Deferred">Acting on thin ice - the third Rides() door (08-06).</param>
/// <param name="Title">What the row is called. Display only.</param>
/// <param name="Note">The row's one line of context - the doubt branch, the re-ask's
/// "asking again" sentence, the standing flag. Display only, composed upstream.</param>
internal readonly record struct TriageRowInput(
  string Key,
  bool IsStanding,
  BoardPile Pile,
  ConfidenceTier Tier,
  bool PlayerResolved,
  bool Deferred,
  string Title = "",
  string Note = "");

/// <summary>
/// A row as the walk drew it: the input it came from, and the page it landed on.
/// The input rides along whole so the ImGui layer never re-derives a routing fact
/// to decide how to draw a row (the rail learned that lesson - see IdleRow).
/// </summary>
internal readonly record struct TriageRow(TriageRowInput Input, PageKind Page)
{
  internal string Key => Input.Key;
  internal string Title => Input.Title;
  internal string Note => Input.Note;
}

/// <summary>
/// A page's ONE control (spec section 2 - every page has exactly one). Label and
/// enablement are composed here rather than in the window because the Bag Calls
/// refusal is a rule, not a paint job: it is the launch refusal's former job, moved
/// to the page that owns the question.
/// </summary>
internal readonly record struct PageControl(string Label, bool Enabled, string Refusal);

/// <summary>
/// One page of the walk. Pages are DATA - the split axis lives in the composition,
/// not in a type per page.
/// </summary>
internal sealed class TriagePage
{
  internal TriagePage(PageKind kind, List<TriageRow> rows, PageControl control)
  {
    Kind = kind;
    Rows = rows;
    Control = control;
  }

  internal PageKind Kind { get; }
  internal IReadOnlyList<TriageRow> Rows { get; }
  internal PageControl Control { get; }

  /// <summary>Empty pages self-skip - the cursor passes through them (spec section 3).</summary>
  internal bool IsEmpty => Rows.Count == 0;
}

/// <summary>
/// THE TRIAGE WALK (Task 2, spec ruled 2026-08-15). The hinge stops being one
/// overwhelming panel and becomes a short forced walk of small pages, each shaped
/// for its decision.
///
/// <para><b>North star</b> (Drift, 08-15): "All of the machinery that we are building
/// is mostly there to feed the triage. Short-changing the triage, or not giving the
/// decisions the room and UI needed to make a good call, is self-defeating." Every
/// rule below serves that sentence, and the one that serves it hardest is the split:
/// easy decisions keep the snap format, hard decisions are unshackled from it
/// ("don't shackle hard decisions with a format that works well for easy
/// decisions").</para>
///
/// <para><b>NOTHING HERE COMMITS.</b> The walk collects verdicts and hands
/// them to the hinge's existing Continue, which commits exactly what it commits
/// today. A walk that wrote as it went would be a second commit path beside the one
/// the bell already owns.</para>
///
/// <para><b>No pile semantics change.</b> Every row arrives with the pile the board
/// already gave it; the walk only decides which page draws it. The eyes axis (08-06)
/// survives structurally - Silent rows draw nowhere, Defer rows ride visibly, Review
/// rows are the case page.</para>
///
/// <para>Pure and Dalamud-free (the BoardPiles / AccountantPlan mold, linked into
/// Scrooge.Tests).</para>
/// </summary>
internal sealed class TriageWalk
{
  /// <summary>The pages, in walked order. Always three - an empty one is drawn by
  /// nobody but still exists, because "which page am I on" is answered by Kind and
  /// never by an index into a list that shrinks.</summary>
  internal static readonly PageKind[] Order =
  {
    PageKind.BoardDecisions, PageKind.Riders, PageKind.BagDecisions,
  };

  private List<TriageRowInput> _inputs = new();
  private readonly Dictionary<string, BoardPile> _verdicts = new(StringComparer.Ordinal);

  private TriageWalk() { }

  /// <summary>The composed pages. Recomposed in place on evidence change.</summary>
  internal IReadOnlyList<TriagePage> Pages { get; private set; } = Array.Empty<TriagePage>();

  /// <summary>
  /// Where the walk stands. <c>Cursor == Pages.Count</c> is PAST THE LAST PAGE, which
  /// is the walk's end and the state the hinge's Continue reads.
  /// </summary>
  internal int Cursor { get; private set; }

  /// <summary>Case verdicts the human ruled during the walk, keyed by row. These are
  /// player resolutions and they stick across a recompose.</summary>
  internal IReadOnlyDictionary<string, BoardPile> Verdicts => _verdicts;

  /// <summary>
  /// Composes the walk once, at the hinge's arrival, and parks the cursor on the
  /// first page that has anything to say.
  /// </summary>
  internal static TriageWalk Compose(IEnumerable<TriageRowInput> rows)
  {
    var walk = new TriageWalk();
    walk.Rebuild(rows);
    walk.Cursor = walk.FirstNonEmpty();
    return walk;
  }

  /// <summary>
  /// RECOMPOSE ON EVIDENCE CHANGE (spec section 3) - a re-pinch landed, a recon
  /// answered, the board says something new.
  ///
  /// <para><b>Every decision already made survives</b>, because every one of them is
  /// keyed on the row rather than on a page position: case verdicts stick (they are
  /// player resolutions), and the cursor keeps its page KIND rather
  /// than its index - the page a player is standing on does not move under him because
  /// an earlier page emptied out.</para>
  ///
  /// <para>A row whose CALL changed re-asks through the existing
  /// <see cref="StandingReAsk"/> semantics upstream; by the time it reaches here it is
  /// simply an input with a new pile, and it lands on whichever page that pile names.
  /// A decision on a row that is no longer in the walk is dropped - it has nothing left
  /// to be a decision about.</para>
  /// </summary>
  internal void Recompose(IEnumerable<TriageRowInput> rows)
  {
    var wasPastEnd = Cursor >= Pages.Count;
    var kind = wasPastEnd ? (PageKind?)null : Pages[Cursor].Kind;

    Rebuild(rows);

    // Decisions about rows that left the board leave with them.
    var live = new HashSet<string>(_inputs.Select(r => r.Key), StringComparer.Ordinal);
    foreach (var stale in _verdicts.Keys.Where(k => !live.Contains(k)).ToList())
      _verdicts.Remove(stale);

    if (kind is not PageKind held)
    {
      // Past the end stays past the end. New work does not drag a finished walker
      // backwards - the Continue's own refusal (IsComplete) is what stops him.
      Cursor = Pages.Count;
      return;
    }

    var idx = IndexOfKind(held);
    // The page he was standing on may have emptied under him; the same self-skip rule
    // that got him there carries him forward.
    Cursor = Pages[idx].IsEmpty ? NextNonEmptyFrom(idx + 1) : idx;
  }

  /// <summary>
  /// THE PAGE ASSIGNMENT - the invariant the whole walk rests on: <b>every row lands on
  /// exactly one page, or on none at all if it is Silent.</b> Read top to bottom; the
  /// first clause that fires owns the row.
  ///
  /// <list type="number">
  /// <item>Silent draws nowhere. It is the third eyes state - "I got this, and what I
  /// got is: don't" - and it has no board presence to re-present.</item>
  /// <item>Review is a case. Review always wins here for the same reason it wins in
  /// <see cref="BoardPiles.ForRoutingExit"/> - a row that needs eyes needs eyes
  /// whichever half of the board raised it, including a standing lane the pinch could
  /// not price.</item>
  /// <item>Standing rows are Board Calls: reprice, standing pull-and-vendor, the
  /// melt-beats-ask contest, player contests, and the re-ask's withdrawn-verb rows,
  /// whose "asking again" sentence draws here.</item>
  /// <item>What RIDES is a rider - Unanimous (bulk-eligible), Defer, or a PRIOR-round
  /// ruling replayed onto this row (F5, ruled 08-22). A replayed ruling is a resolved
  /// row, not a question: staleness is already guarded at the replay seam (a changed
  /// verdict misses and re-asks), so re-seating it as a case was double protection
  /// paid in attention - a queue named "decisions" holding non-decisions. Rulings made
  /// LIVE in this walk are different: they stay on their case page via
  /// <see cref="Verdicts"/>, revisitable until the walk commits.</item>
  /// <item>Anything left needs eyes and gets a case. By construction this is empty -
  /// a Mixed bag row either defers or is already in Review - but the invariant is that
  /// a row is never silently dropped, and the honest home for a row nobody can act on
  /// is the page where somebody answers it. JUDGMENT CALL - the spec's table has no
  /// entry for this row, flagged for Fable QA.</item>
  /// </list>
  /// </summary>
  internal static PageKind? PageFor(in TriageRowInput row)
  {
    if (row.Pile == BoardPile.Silent) return null;
    if (row.Pile == BoardPile.Review) return PageKind.BagDecisions;
    if (row.IsStanding) return PageKind.BoardDecisions;
    if (BoardConfidence.Rides(row.Tier, row.PlayerResolved, row.Deferred)) return PageKind.Riders;
    return PageKind.BagDecisions;
  }

  /// <summary>
  /// RULE A CASE. The verdict is the human's, so it is a player resolution and it
  /// sticks - through a recompose, through a re-ask, through the rest of the walk.
  /// Nothing commits: the hinge's Continue spends these.
  /// </summary>
  internal bool Decide(string key, BoardPile exit)
  {
    if (!_inputs.Any(r => string.Equals(r.Key, key, StringComparison.Ordinal)
                          && PageFor(r) == PageKind.BagDecisions))
      return false;
    _verdicts[key] = exit;
    Rebuild(_inputs);
    return true;
  }

  /// <summary>
  /// The cases still owed an answer. A row arrives answered if the human ruled it
  /// before the walk (<c>PlayerResolved</c>) or ruled it during one.
  /// </summary>
  internal IReadOnlyList<TriageRow> UndecidedCases
    => Pages[IndexOfKind(PageKind.BagDecisions)].Rows
      .Where(r => !IsDecided(r.Input))
      .ToList();

  private bool IsDecided(in TriageRowInput row)
    => row.PlayerResolved || _verdicts.ContainsKey(row.Key);

  /// <summary>How many answers the walk is still waiting on. The rail's operand.</summary>
  internal int CallsLeft => UndecidedCases.Count;

  /// <summary>
  /// THE WALK'S END. Both halves are load-bearing: the human walked every page that
  /// had anything on it, AND no case is left unanswered. Bag decisions IS the launch
  /// refusal's former job - Review rows withheld until answered - and dropping the
  /// second half would move that refusal nowhere and delete it.
  /// </summary>
  internal bool IsComplete => Cursor >= Pages.Count && CallsLeft == 0;

  /// <summary>Back is DISABLED at the first non-empty page, not hidden (ruled): a
  /// control that vanishes teaches the player the walk is shorter than it is.</summary>
  internal bool CanBack => Cursor > FirstNonEmpty();

  /// <summary>Next is always pressable while there is a page to leave.</summary>
  internal bool CanNext => Cursor < Pages.Count;

  /// <summary>Advances, passing through empty pages.</summary>
  internal void Next()
  {
    if (Cursor >= Pages.Count) return;
    Cursor = NextNonEmptyFrom(Cursor + 1);
  }

  /// <summary>Steps back, passing through empty pages. Stops at the first non-empty
  /// page - back is always free, and free never means off the front of the walk.</summary>
  internal void Back()
  {
    var first = FirstNonEmpty();
    for (var i = Math.Min(Cursor, Pages.Count) - 1; i >= first; i--)
      if (!Pages[i].IsEmpty)
      {
        Cursor = i;
        return;
      }
  }

  /// <summary>
  /// THE RAIL LINE - position plus remaining work (ruled 08-15):
  /// <c>triage - page 2/3, 4 calls left</c>.
  ///
  /// <para><b>The position counts non-empty pages only.</b> "page 2/3" over a walk with
  /// one empty page would promise a page the player will never see, and he would count
  /// presses to find out we were rounding.</para>
  ///
  /// <para><b>Zero says nothing</b> - the clause is absent rather than "0 calls left",
  /// the same honesty <see cref="IdlePlan.WaitingDecisions"/> keeps. "Calls left" counts
  /// UNDECIDED CASES only; a board row with no staged verb is not counted, because a
  /// standing lane the player leaves alone is an answer (the book keeps its ask) and
  /// counting it would make the rail demand work the walk does not.
  /// JUDGMENT CALL - flagged for Fable QA.</para>
  /// </summary>
  internal string Rail()
  {
    var total = Pages.Count(p => !p.IsEmpty);
    if (total == 0) return "";

    var left = CallsLeft;
    var tail = left <= 0 ? "" : $", {left} call{(left == 1 ? "" : "s")} left";
    if (Cursor >= Pages.Count)
      return left <= 0 ? "triage - walked" : $"triage - walked{tail}";

    var position = Pages.Take(Cursor + 1).Count(p => !p.IsEmpty);
    return $"triage - page {position}/{total}{tail}";
  }

  // --------------------------------------------------------------------------

  private void Rebuild(IEnumerable<TriageRowInput> rows)
  {
    _inputs = rows.ToList();
    var byPage = Order.ToDictionary(k => k, _ => new List<TriageRow>());
    foreach (var input in _inputs)
      if (PageFor(input) is PageKind page)
        byPage[page].Add(new TriageRow(input, page));

    Pages = Order.Select(k => new TriagePage(k, byPage[k], ControlFor(k, byPage[k]))).ToList();
  }

  /// <summary>
  /// Every page's control is Next; only Bag decisions can refuse, and its refusal names the
  /// count rather than the rule ("3 cases still need an answer" - the reader's next
  /// question is always how many).
  /// </summary>
  private PageControl ControlFor(PageKind kind, List<TriageRow> rows)
  {
    if (kind != PageKind.BagDecisions) return new PageControl("Next", true, "");
    var owed = rows.Count(r => !IsDecided(r.Input));
    return owed == 0
      ? new PageControl("Next", true, "")
      : new PageControl("Next", false, $"{owed} case{(owed == 1 ? "" : "s")} still need"
        + $"{(owed == 1 ? "s" : "")} an answer");
  }

  private int IndexOfKind(PageKind kind) => Array.IndexOf(Order, kind);

  private int FirstNonEmpty() => NextNonEmptyFrom(0);

  private int NextNonEmptyFrom(int start)
  {
    for (var i = Math.Max(0, start); i < Pages.Count; i++)
      if (!Pages[i].IsEmpty) return i;
    return Pages.Count;
  }
}
