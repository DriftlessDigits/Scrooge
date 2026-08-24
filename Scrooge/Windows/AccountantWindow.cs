using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using Scrooge.Board;
using Scrooge.Rounds;

namespace Scrooge.Windows;

/// <summary>
/// THE ROUND'S WINDOW. Window plumbing, the per-frame sequencing, and the seam the round
/// asks its questions through - and nothing else. Evolved from the routing window: it
/// keeps the piles/reasons/overrides/Go/location-parity seed and widens it into the
/// action-named piles of the unified design (design Section 3, plus the Defer pile that
/// replaced Watch in 3.0), and it ABSORBS the old standing-listing inbox (design Section
/// 7) with its full action set preserved. There is no second surface: the TriageWindow is
/// gone.
///
/// <para>Bulk-ability IS the confidence threshold (design Section 4), and it is spent by
/// the ROUND rather than by a per-pile button. The pile confirms are gone: a stage takes
/// the rows that ride - Unanimous, player-ruled, or deferred
/// (<see cref="BoardConfidence.Rides"/>) - and everything else comes to the hinge's walk
/// as a case, which is where a Mixed row and a Contradicted verdict are answered. Every
/// manual decision writes a teaching signal against its verdict class (V14
/// routing_overrides).</para>
/// </summary>
/// <remarks>
/// PARTIAL, ONE FILE PER SURFACE (code-shine 3-3b). The class was seven thousand lines and
/// then four, which is the length at which a surface stops being FOUND - the walk was
/// split out first (Task 3) for exactly that reason, and the rest followed. The roster,
/// which is the whole directory and not a selection from it:
/// <list type="bullet">
///   <item><c>.Deck</c> - what there is to do, derived once per frame;</item>
///   <item><c>.Wizard</c> - the live round's rail, panes and verbs;</item>
///   <item><c>.Idle</c> - the pre-flight checklist and the dashboard's strip;</item>
///   <item><c>.BoardTable</c> - the row model and the one table;</item>
///   <item><c>.ScoreCell</c> - the one scored cell every table draws through;</item>
///   <item><c>.Detail</c> - the pinned pane: one row's whole story;</item>
///   <item><c>.Walk</c> - the hinge's forced walk and its case tribunal;</item>
///   <item><c>.OnMarket</c> - the standing asks, hosted by the gil dashboard;</item>
///   <item><c>.Standing</c> - the absorbed triage lifecycle and its executors.</item>
/// </list>
/// One class throughout - every surface reads the same caches, the same staging dictionary
/// and the same score cells - because they genuinely are one window's worth of state; what
/// they are not is one subject.
///
/// <para>THE ROUND LEFT (code-shine 3-3a; Drift's ruling: <i>"rounds are a process, the UI
/// summarizes state"</i>). The state machine is <see cref="RoundConductor"/>'s, the four
/// commit snapshots are <see cref="HingeCommit"/>'s and the bag scan, the lane scores and
/// every cached storage read are <see cref="LedgerCache"/>'s. What is left here is the
/// board's composition and the paint - the two things that genuinely need a Window - and
/// the seam is <see cref="IRoundBoard"/>: the round asks, this window answers.</para>
/// </remarks>
internal sealed partial class AccountantWindow : Window, IRoundBoard
{
  /// <summary>
  /// THE BOARD'S ONE READ. Every fact a surface here draws was answered once, on the
  /// refresh clock, and is remembered - see <see cref="LedgerCache"/> for why that is a
  /// correctness rule rather than a performance one.
  /// </summary>
  private readonly LedgerCache _cache;

  /// <summary>
  /// THE ERRAND. Fire, flow, halt, resume, teardown, persistence - all of it off this
  /// class since 3-3a. The window keeps a reference for its Draw-time reads and its two
  /// presses (Continue, Make the Rounds); everything else the conductor asks of the
  /// board comes back through <see cref="IRoundBoard"/>.
  /// </summary>
  private readonly RoundConductor _conductor;

  /// <summary>The round itself, for the surfaces outside this window that summarize it.</summary>
  internal RoundConductor Conductor => _conductor;

  public AccountantWindow()
    // THE TITLE SAYS ROUNDS (Movement 3). It read "Scrooge - Ledger" - the same words
    // the transcript window wears, left over from before unit 5 reissued that name -
    // so two windows announced themselves as the Ledger and neither one was wrong
    // enough to fix the other. This window is the Round's, front to back: the door
    // opens it, the preview starts there, the wizard runs there. The ### id is
    // unchanged, so no player's saved window position moves.
    : base("Scrooge - Rounds###AccountantWindow", ImGuiWindowFlags.None)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(560, 320),
      MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };
    Size = new Vector2(720, 560);
    SizeCondition = ImGuiCond.FirstUseEver;
    IsOpen = false;

    _cache = new LedgerCache(RecordRoutedSignal);
    // The per-selection panes are derivations OF the scan, so they die with it. Hung off
    // the cache's own event rather than spelled at each refresh site: a bag scan fired
    // from the bell's listing leg has to void them exactly as the Refresh button does.
    _cache.Rescanned += ClearRowCaches;
    // Same contract for the flag-keyed synthetics: a flag the re-read no longer finds
    // open is a row nothing will draw again, whoever closed it.
    _cache.HeldFlagsRefreshed += PruneFlagItems;
    _conductor = new RoundConductor(this, _cache);
  }

  public override void OnOpen()
  {
    RefreshAll();
  }

  /// <summary>The whole re-read, on this window's terms - see <see cref="LedgerCache.RefreshAll"/>.</summary>
  private void RefreshAll() => _cache.RefreshAll();

  /// <summary>The bag scan alone. The Hawk window and the salvage preview both ask for it by name.</summary>
  internal void Refresh() => _cache.Refresh();

  /// <summary>The held flags alone - re-read after this window mutates one.</summary>
  private void RefreshHeldFlags() => _cache.RefreshHeldFlags();

  /// <summary>The row-scoped draw caches, void as of the scan that just replaced their rows.</summary>
  private void ClearRowCaches()
  {
    _memoirs.Clear();
    _trails.Clear();
    _states.Clear();
  }

  /// <summary>The relist preview for one standing lane - see <see cref="LedgerCache.RelistPreview"/>.</summary>
  internal long? RelistPreview(uint itemId, bool isHq, string retainer)
    => _cache.RelistPreview(itemId, isHq, retainer);

  /// <summary>Rehydrates the persisted round once per session - the door, the draw and the pump all ask.</summary>
  internal void EnsureRoundRestored() => _conductor.EnsureRoundRestored();

  /// <summary>A human-pressed round is underway and flowing - the executors' grace-window operand.</summary>
  internal bool RoundActive => _conductor.RoundActive;

  /// <summary>A Round exists but is HOLDING - halted, paused, or restored fail-closed.</summary>
  internal bool RoundHeld => _conductor.RoundHeld;

  /// <summary>There is a Round to be in - flowing or held. The screen keying's one operand.</summary>
  internal bool RoundLive => _conductor.RoundLive;

  /// <summary>The round's lineage key, as recon's banked rows carry it.</summary>
  internal long RoundRunId => _conductor.RoundRunId;

  /// <summary>
  /// THE WALK AND THE SPENT CONTESTS DIE WITH THE ROUND (Task 3). The walk is a
  /// session's worth of pulls and verdicts against one hinge arrival, and none of it is
  /// a commit - a walk that outlived its errand would hand the NEXT round a set of
  /// answers to questions the next board has not asked. Deliberately not persisted, for
  /// the same reason.
  ///
  /// <para>The player's own contests are pruned rather than cleared (3-3b). A contest
  /// whose verb is gone was SPENT - the bell drained it, or the round retired the row -
  /// and it and its cached item are session litter that nothing will ever draw again. A
  /// contest still holding a staged verb is a live answer the player gave, and it
  /// survives this boundary exactly as <see cref="_actions"/> does: the reset also runs
  /// at the START of a round, and a contest staged from On Market seconds before Make
  /// the Rounds must not vanish under the press.</para>
  /// </summary>
  public void RoundTornDown()
  {
    _walk = null;
    _playerContests.RemoveAll(c => !_actions.ContainsKey(c));
    var live = _playerContests.Select(LaneOf).ToHashSet();
    foreach (var lane in _contestItems.Keys.Where(k => !live.Contains(k)).ToList())
      _contestItems.Remove(lane);
  }

  // ==========================================================================
  // Draw
  // ==========================================================================

  public override void Draw()
  {
    // Universalis answers land async - re-run the bag piles when data arrives so
    // "no evidence" verdicts settle, but never while the player is mid-decision.
    var uniLanded = _cache.UniversalisLanded();
    var playerTouched = _cache.Items.Any(i =>
      i.OverrideRecorded || i.Pile != i.Verdict.Exit || i.InReview != i.Verdict.IsReview)
      || _actions.Count > 0;
    if (uniLanded && !playerTouched && !Plugin.GcTurnIn.IsRunning)
      Refresh();

    // Rehydrate a persisted in-progress round once, here: every orchestrator is
    // constructed by first draw, so the staleness chat notice is safe.
    EnsureRoundRestored();

    var standingRows = BuildStandingRows();

    // The bell counts toward "is there anything here" now: a bag full of mats and
    // no routable gear is a round with real work, and the old emptiness test would
    // have shown "nothing on the ledger" over exactly the 48 yields + 6 dyes the
    // one-door bell exists to find (07-24).
    var bellHasRows = _cache.BellGateRows.Count > 0 || _cache.CofferCount > 0;
    var nothingHere = _cache.Items.Count == 0 && standingRows.Count == 0 && _cache.Listed.Count == 0 && !bellHasRows;

    // ONE answer per frame. The launch strip, the deck and the run log's rail all
    // read the same errand, so it is derived once here and handed down - deriving
    // it per surface is how two of them come to describe one round differently.
    //
    // DERIVED OUTSIDE THE TABS, deliberately: the run log's stage rail reads _conductor.Deck,
    // and it must not go quiet because the player wandered onto another tab. The
    // empty ledger still skips it - there is no errand to describe.
    // A LIVE ROUND ALWAYS GETS A DECK, however empty the board is (unit 5). The old
    // shape skipped the derivation on an empty ledger, which was harmless while the
    // deck was one section of a worklist; it is a trap now that the deck IS the
    // window. A round whose piles emptied under it - the last stage drained them, or
    // the bags were worked elsewhere - would render the "nothing here" page with no
    // rail, no verbs, and no way to abandon the errand it is still running.
    DeckState? deck = null;
    if (!nothingHere || _conductor.Plan.Active)
    {
      var computed = ComputeDeck(standingRows);
      _conductor.PublishDeck(computed);
      deck = computed;
    }

    // THE TAB BAR IS GONE (Movement 3). It held two surfaces: the Round and the
    // standing asks. On Market is status of the WORLD - what you have up, right now -
    // and reading it had come to cost a started errand, so it moved to the gil
    // dashboard where world status lives (this class still draws it: see
    // DrawOnMarketPanel). What is left is the errand, so the window IS the errand and
    // has nothing to tab between.
    DrawRoundScreen(standingRows, deck, nothingHere);
  }

  // The Listed pile is DEAD (gate 9b, ruled 2026-08-03): a pre-On-Market relic.
  // Its facts relocated rather than duplicated - the both-tenses standing-book
  // headline and "put up today" live in the On Market header now, capacity
  // surfaces at round time in the launch strip, per-item age belongs to Slow
  // Movers (the shelf audit), and "N need rulings" was always the gate's job.
  // _cache.Listed itself survives: it is the book's baseline and the pinch-time
  // estimate's operand, and it never was the pile.

  // The WATCH PILE'S DRAW IS GONE (Defer, 08-06), and with it the last
  // structure that treated "we are sure about this" as something to render.
  // Its three tenant classes went three ways: thin lane_held rows are Defer
  // rows in the board's own table now, protected holds are one summary line in
  // the header (a config fact, not a decision), and observed bans draw nothing
  // at all - a confident verdict not to engage is the silent board.

  // MeltPileCount retired 2026-08-12 (the minors batch): zero consumers. The melt
  // stage's count comes off the deck like every other stage's, and a second public
  // spelling of a derived number is exactly the thing that drifts from the first.
}
