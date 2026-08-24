using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE RECON RUN (Rounds unit 2) - the hawk's front half: read the board, bank the
/// decision, CANCEL.
///
/// <para>Recon buys the ground truth the act half spends. It walks the listable bag
/// at a bell exactly as a listing run would - context menu, Put Up for Sale, the
/// market-board ask, the full pricing spine - and then declines the last act. No
/// price is written, no listing posted, no sell slot consumed. What it leaves behind
/// is knowledge: a decision receipt per item, a market-memory diff per board, a tape
/// grown by every history packet, and one <c>decision_cache</c> row saying what we
/// would have asked and why.</para>
///
/// <para><b>Why this is worth minutes.</b> Recon-before-desynth is the round's
/// irreversibility interlock. The melt destroys the item; if it ran before the List
/// door had seen a real board, the round would be answering "is this worth more
/// melted than listed?" off banked guesses, and the wrong answer is unrecoverable in
/// a way a bad listing never is. Each item pays the same ~4s server round trip a
/// hawk item pays. That was ruled worth paying, and the rail narrates the cost
/// honestly from its own measured pace rather than hiding it.</para>
///
/// <para><b>Why it is its own class rather than a mode on the hawk.</b> The two runs
/// share exactly one thing - the road to a retainer's sell view - and that is what
/// was extracted (<see cref="RetainerSellNav"/>). Past the sell view they agree on
/// nothing: the hawk routes always-vendor items, counts sell slots, swaps retainers
/// at 20/20, feeds the standing book, stamps routing receipts and runs the
/// inventory-scope zombie round at completion. Recon does none of those, because it
/// changes nothing about the world. Threading a mode flag through six branches of
/// one class to avoid duplicating zero shared logic would have made the bell run
/// bimodal in every method it owns, and left "recon lists nothing" a promise
/// scattered across those branches instead of a fact about which class ran.</para>
///
/// <para><b>The work set is not chosen, it DERIVES</b> (ruled 2026-08-10): the
/// listable scan, filtered to items whose banked decision is missing or older than
/// <c>ReconFreshHours</c>. Everything fresh means the stage counts zero and the
/// cursor skips it silently like any empty stage - nothing procedural decides. See
/// <see cref="ReconFreshness"/>, which owns the predicate for this door and for the
/// cached post's.</para>
/// </summary>
internal sealed class ReconRunOrchestrator : IDisposable
{
  private readonly TaskManager _taskManager;
  private readonly ItemPricingPipeline _pricing;
  private readonly Func<int, int> _applyJitter;
  private readonly IAddonLifecycle.AddonEventDelegate _skipRetainerDialog;
  private readonly Action _removeTalkListeners;

  private Queue<ListableItem>? _queue;

  /// <summary>
  /// The shared run-host lifecycle (state, progress, stall terminal).
  ///
  /// <para>A 60s stall bound rather than the bell's 45s, and the reason is the
  /// retry ladder: recon runs the escalating market-board await windows (keep-open
  /// + 3s + 5s + 10s), so a single stubborn item can legitimately go ~20s without
  /// recording progress. A watchdog tighter than the wait it is watching kills
  /// honest runs, which is the same reasoning RunData already carries for its own
  /// 60s bound.</para>
  /// </summary>
  private readonly RunLifecycle _run = new(TimeSpan.FromSeconds(60));

  /// <summary>True while a recon run is in progress.</summary>
  internal bool IsRunning => _run.IsRunning;

  /// <summary>The live run, for the standard progress readout (see RunHostRender).</summary>
  internal RunLifecycle Run => _run;

  internal ReconRunOrchestrator(
    TaskManager taskManager,
    ItemPricingPipeline pricing,
    Func<int, int> applyJitter,
    IAddonLifecycle.AddonEventDelegate skipRetainerDialog,
    Action removeTalkListeners)
  {
    _taskManager = taskManager;
    _pricing = pricing;
    _applyJitter = applyJitter;
    _skipRetainerDialog = skipRetainerDialog;
    _removeTalkListeners = removeTalkListeners;
  }

  /// <summary>
  /// RECON'S WORK SET, composed fresh. The full listable bag scan (the Hawk
  /// window's own gate answer - full scope, everything listable, RULED), deduped by
  /// variant and filtered to the stale-or-missing half.
  ///
  /// <para><b>FULL SCOPE FOR EVERY EVIDENCE-DERIVED VERDICT</b> (flagged in the 08-10
  /// review, kept as ruled - the spec says full scope). Rows the confidence gate
  /// routes elsewhere still get walked, and that is a CHOICE: those verdicts are
  /// derived from the same market evidence recon is out here buying, so filtering on
  /// them would mean trusting last night's answer to decide whether tonight's is
  /// worth asking for.</para>
  ///
  /// <para><b>Except the always-vendor list</b> (Drift, 08-23: "that IS the exit. no
  /// need to try to price it" - the Demimateria III that provoked it took a full
  /// recon visit, banked a Hold, and was vendored by the same round's hawk). The
  /// list is the player's own standing ruling, not a derived verdict - no market
  /// read can change what the round does with the item, so the ~4s round trip buys
  /// evidence for a question that is already answered. The 08-10 rationale never
  /// covered this class; the exclusion narrows scope for it alone. A Re-Look skips
  /// them too: it overrules the CACHE, not the player's config.</para>
  ///
  /// <para>Composed at FIRE time by the caller, never handed in from a frame ago:
  /// the bell learned that lesson (its rows are recomposed at fire), and recon's
  /// filter is time-dependent besides - a row that was stale when the deck drew it
  /// is still stale a second later, but the reverse case (a row that went stale
  /// while the player read the deck) is exactly the item recon exists to catch.</para>
  /// </summary>
  /// <param name="bypassFreshness">
  /// The Re-Look (unit 4): read the WHOLE listable set, filter ignored. Never a
  /// default - it is a verb the player pressed, and a bypass that could arrive by
  /// omission would eventually arrive by accident.
  /// </param>
  internal static List<ListableItem> ComposeWorkSet(bool bypassFreshness = false)
  {
    List<ListableItem> listable;
    try { listable = ListableInventoryScanner.Scan(); }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Recon] Bag scan failed: {ex.Message}");
      return [];
    }

    // The always-vendor list is the exit, ruled by the player - a board read can't
    // move it, so recon never spends a visit on it (see the scope note above).
    listable.RemoveAll(static r => r.IsAlwaysVendor);

    // A Re-Look never reads the cache at all: the bank is exactly what it is
    // overruling, and asking it could only produce an answer the verb ignores.
    if (bypassFreshness)
      return ReconFreshness.AllVariants(listable, r => (r.ItemId, r.IsHq));

    Dictionary<(uint, bool), long> banked;
    try { banked = GilStorage.GetDecisionCacheBankTimes(); }
    catch (Exception ex)
    {
      // Storage unreadable: every row reads as never-banked, so recon walks the
      // whole bag. Slow, never wrong - the opposite failure (treat everything as
      // fresh) would hand the act half a cache nobody could confirm exists.
      Svc.Log.Warning($"[Recon] Decision cache unreadable, treating everything as stale: {ex.Message}");
      banked = [];
    }

    return ReconFreshness.WorkSet(
      listable, r => (r.ItemId, r.IsHq), banked,
      DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      Plugin.Configuration.ReconFreshHours);
  }

  /// <summary>
  /// Fail-closed teardown on error/abort/dispose. Recon holds nothing that can be
  /// half-written - it consumes no slots, posts nothing, and each item's cache row
  /// is banked whole at that item's own decision - so a partial exit simply leaves
  /// the un-walked half stale, which is the state that self-corrects.
  /// </summary>
  /// <param name="reason">
  /// The executor's own words for the death, verbatim - it becomes the named gap on
  /// the round's halt banner. Defaults to the plain cancellation this method was
  /// written for (dispose); the pricing pipeline hands its own sentence when an
  /// unguarded throw takes the pass down (review ruling S9), and so does the overlay's
  /// draw catch (S20) - a halt that says "cancelled" over a draw error names the
  /// wrong gap on the banner.
  /// </param>
  internal void Abort(string reason = "the recon run was cancelled")
    // The wasLive/Cancel/CancelRun/handover/conditional-report sequence is
    // RunTeardown.Die's - the bell spelled out the identical one. What is left here
    // is what only recon holds.
    => RunTeardown.Die(_run, RunKind.Recon, reason, () =>
    {
      _queue = null;
      _removeTalkListeners();
    });

  /// <summary>
  /// Plugin-unload teardown: a live recon must not outlive the plugin. Idempotent -
  /// <see cref="Abort"/>'s wasLive latch means a second call reports nothing, and the
  /// listener removal is safe with no run in flight.
  /// </summary>
  public void Dispose() => Abort();

  /// <summary>
  /// THE STALL WATCHDOG. Asked once per framework tick: a live run that has gone
  /// its whole stall bound without a single item completing is wedged, not slow.
  /// The teardown is the ordinary fail-closed one, and the death is reported in the
  /// executor's own words so the round's halt banner names the gap.
  /// </summary>
  internal void Tick()
  {
    if (!_run.CheckStall(DateTime.UtcNow)) return;

    var done = _run.Done;
    var total = _run.Total;
    _queue = null;
    Plugin.Ledger.CancelRun();
    var recon = Plugin.CurrentRun;
    Plugin.CurrentRun = null;
    _removeTalkListeners();
    Svc.Chat.PrintError("[Scrooge] Recon stopped responding - stopping the pass.");
    RunFlow.ReportDied(RunKind.Recon,
      $"the market board stopped answering, aborted at {done}/{total}", recon);
  }

  /// <summary>
  /// The round's entry: already in a retainer's sell view -> start now; at the bell
  /// roster -> hop to the first retainer with sell space and start on arrival;
  /// nowhere near a bell -> refuse loudly, naming the walk.
  ///
  /// <para>The with-space hop is NOT an artefact of borrowing the hawk's road.
  /// Verified in game 2026-08-10: a 20/20 sell list refuses to open the sell panel
  /// at all - the game gates the PANEL, not the posting - so recon genuinely needs
  /// one free slot to stand in. Because it never fills that slot, one serves the
  /// whole pass, which is why there is no mid-run swap leg here and one in the
  /// hawk.</para>
  ///
  /// <para><b>Every refusal on this road leaves a Re-Look armed</b> (review ruling
  /// S6). There are four - another run still working, every sell list 20/20, the
  /// grace window expiring on un-ready bell addons, and the sell view unreachable
  /// once inside <see cref="StartReconRunCore"/> - and not one of them reads a board.
  /// The latch is consumed by the work, so a refused Re-Look is still owed and the
  /// next attempt still walks the whole bag.</para>
  /// </summary>
  internal void NavigateAndStartReconRun(List<ListableItem> items)
  {
    // Every refusal on this road is LOUD, and every one of them REPORTS: recon is a
    // round stage marked done at fire time, so a refusal nobody reported would
    // leave the round walking on to the melt as though the boards had been read.
    if (items.Count == 0)
    {
      // NOT a death, and this is the one refusal on the road that isn't. An empty
      // work set means every listable decision is still fresh - which is the stage
      // having nothing to do, not the stage failing. The deck's count answers zero
      // on that same fact and the cursor skips the stage silently; this path only
      // exists for the narrow race where the last stale item leaves the bags between
      // the frame that counted it and the press that fired. Halting the round over
      // "there was nothing to do" would make a good night look like a broken one.
      Svc.Chat.Print("[Scrooge] Nothing to recon - every listable item's decision is still fresh.");
      return;
    }
    if (_taskManager.IsBusy)
    {
      Svc.Chat.PrintError("[Scrooge] Another run is still working - wait for it (or /scrooge to check).");
      RunFlow.ReportDied(RunKind.Recon, "another run was still working", run: null);
      return;
    }

    var eval = SpineEvaluator.Evaluate(RetainerSellNav.ListExpected, RetainerSellNav.ReadListState());
    switch (eval.Rung)
    {
      case Spine.Rung.Fire:
        StartReconRunCore(items);
        break;
      case Spine.Rung.SelfNavigate:
        // The deeper refusal lives inside the hop (all retainers 20/20, list not
        // ready). Unlike the bell's call site, recon REPORTS it: the 20/20 wall is
        // the one refusal this stage was warned about, and a silent false here
        // would be a stage marked done with no run behind it.
        if (!RetainerSellNav.EnqueueToSellView(
              _taskManager, _skipRetainerDialog, _removeTalkListeners,
              RetainerSellNav.ReconAllFull, _ => StartReconRunCore(items)))
          RunFlow.ReportDied(RunKind.Recon,
            "recon needs one open sell slot - every sell list is 20/20", run: null);
        break;
      default:
        // THE GRACE (2026-07-26), for the same reason the bell has it: the round
        // walks the player to the bell and AutoRetainer summons a retainer the
        // instant it opens, and during that summon neither bell addon is ready. A
        // read that lands in the gap must not kill a run with the player standing
        // exactly where he was told to stand.
        if (GracePlan.ShouldWaitOut(eval, playerPressed: !Plugin.Accountant.RoundActive)
            && SpineGrace.Hold("the retainer bell", GracePlan.PlaceGraceMs,
                 met: () => SpineEvaluator.Evaluate(
                     RetainerSellNav.ListExpected, RetainerSellNav.ReadListState()).Rung
                   is Spine.Rung.Fire or Spine.Rung.SelfNavigate,
                 onReturned: () => NavigateAndStartReconRun(items),
                 onExpired: () => RefuseRecon(eval)))
          break;

        RefuseRecon(eval);
        break;
    }
  }

  /// <summary>Recon's refusal, said once and reported once.</summary>
  private static void RefuseRecon(SpineEvaluation eval)
  {
    Svc.Chat.PrintError($"[Scrooge] {eval.Message}");
    RunFlow.ReportDied(RunKind.Recon, eval.Message, run: null); // refused on the road - no run behind it
  }

  /// <summary>
  /// The run start proper, minus the TaskManager busy guard - callable from the tail
  /// of the sell-view navigation queue (where the manager is by definition busy
  /// running the very task that arrived here). All fail-closed checks stay.
  /// </summary>
  private unsafe void StartReconRunCore(List<ListableItem> items)
  {
    if (items.Count == 0) return;

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
    {
      Svc.Chat.PrintError("[Scrooge] Not in retainer sell view - couldn't reach one. Summon your retainers and retry.");
      RunFlow.ReportDied(RunKind.Recon, "couldn't reach a retainer's sell view", run: null);
      return;
    }

    // Read before mutating any run state - fail closed if the retainer can't be
    // resolved (avoids a half-started run).
    var retainerName = GameSafe.ActiveRetainerName();
    if (retainerName == null)
    {
      Svc.Chat.PrintError("[Scrooge] Couldn't read the active retainer - try reopening the sell list.");
      RunFlow.ReportDied(RunKind.Recon, "couldn't read the active retainer", run: null);
      return;
    }

    // THE RE-LOOK LATCH IS SPENT HERE, AND NOWHERE EARLIER (review ruling S6). Every
    // refusal recon owns has now been asked and answered - the busy gate, the 20/20
    // wall, the grace window, the two reads above - so this line is the first moment
    // the pass is certainly going to read boards. A latch spent at the fire site was
    // spent by all four refusals too, and the player's next Resume then re-derived an
    // ordinary recon that found nothing stale and checked the stage off having read
    // nothing. The work set for THIS run was already composed against the latch
    // upstream; consuming it now cannot change what the run walks.
    Plugin.Accountant.Conductor.ConsumeReLookLatch();

    _pricing.ClearState();
    _run.Start(items.Count, RunValueUnit.None, DateTime.UtcNow, $"Recon {items.Count} items");
    Plugin.CurrentRun = new RunData { Mode = RunMode.Recon };
    _queue = new Queue<ListableItem>(items);

    Plugin.Ledger.StartNewRun();
    Plugin.Ledger.SetTotalItems(items.Count);
    // The retainer whose panel we are borrowing. Recon reads the SAME board from
    // whichever retainer it stands at - the market board is not per-retainer - so
    // this is a log-grouping fact, never a scoping one.
    Plugin.Ledger.SetCurrentRetainer(retainerName);

    // Auto-dismiss retainer greeting dialogs.
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", _skipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", _skipRetainerDialog);

    _taskManager.Enqueue(ReconProcessNext, "ReconProcessNext");
  }

  /// <summary>
  /// Walks the next item: open its context menu, ask for the sell panel, ask the
  /// board, wait for the WHOLE board, run the spine, bank, cancel.
  ///
  /// <para><b>The escalating await windows are deliberate.</b> The chain holds the
  /// decision until the captured board matches the proxy's own total (the x-of-y
  /// work, 08-02: 949 of 1,744 receipts had been deciding at depth exactly 10 - the
  /// page boundary - while pages for the same board were milliseconds out). Recon
  /// banks its answer for a whole day and the act half spends it without re-asking,
  /// so a censored page-1 read here is the most expensive one in the plugin. This is
  /// where recon differed from the bell run it borrowed the chain from - until the
  /// bell adopted the same ladder (ruled 2026-08-10, unit 3), which is why the whole
  /// read is now one shared composition rather than two spellings of it.</para>
  ///
  /// <para>Pacing between panel opens is the hawk's, unchanged: 100ms after the
  /// context menu, 100ms after Put Up for Sale, then the jittered
  /// <c>GetMBPricesDelayMS</c> that <c>DelayMarketBoard</c> inserts whenever the
  /// item has no cached price - which, on a variant-deduped work set, is every
  /// item. Recon therefore opens panels strictly slower than a hawk run does, and
  /// every gap it opens them across is jittered.</para>
  /// </summary>
  private bool? ReconProcessNext()
  {
    if (_queue == null || _queue.Count == 0)
    {
      // The shared road back out (RetainerSellNav owns the close-up chain and the
      // S18 latch's reasoning; both sell-view runs end on it).
      RetainerSellNav.EnqueueSellViewCloseUp(_taskManager, _removeTalkListeners, () => {
        if (!_run.Complete(DateTime.UtcNow)) return true;
        Plugin.Ledger.EndRun();
        var recon = Plugin.CurrentRun;
        Plugin.CurrentRun = null;
        _queue = null;
        // No flash here (ruled 08-15) - the completion handler owns the taskbar.
        // Recon changed nothing about the market, so there is no write-side event
        // to raise here - no standing book entry, no inventory-scope zombie round
        // (nothing left the bags). The completion exists so the round's cursor
        // moves and the Ledger re-reads the cache rows this pass just banked.
        RunFlow.ReportDone(RunKind.Recon, recon);
        return true;
      });
      return true;
    }

    var item = _queue.Dequeue();

    _taskManager.Enqueue(() => {
      if (Plugin.CurrentRun != null)
        Plugin.CurrentRun.CurrentItem = new PricingItem { ItemId = item.ItemId };
      return true;
    }, $"ReconInitItem_{item.Name}");
    _taskManager.Enqueue(() => RetainerPanelActions.ClickInventoryItem(item), $"ReconClickItem_{item.Name}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(RetainerPanelActions.ClickPutUpForSale, $"ReconPutUpForSale_{item.Name}");
    _taskManager.DelayNext(100);
    // The shared board read (unit 3 folded the ladder into ONE composition): ask,
    // then hold for the whole board across the escalating windows. Recon used to
    // spell this out itself because it was the only door that waited properly; since
    // the bell adopted the ladder there is one chain and both doors enqueue it.
    _pricing.Board.EnqueueBoardRead("Recon", item.Name);
    // SetNewPrice is where recon's whole difference lives: it runs the spine, writes
    // the receipt, banks the decision cache row, and cancels the panel. See
    // ItemPricingPipeline.BankReconDecision.
    _taskManager.Enqueue(_pricing.SetNewPrice, $"ReconSpine_{item.Name}");
    // A jittered beat before the next panel opens, on top of the two 100ms steps
    // above - the same cadence discipline every click in the plugin gets.
    _taskManager.DelayNext(_applyJitter(Plugin.Configuration.MarketBoardKeepOpenMS));
    _taskManager.Enqueue(() => {
      _run.RecordProgress(1, 0, DateTime.UtcNow);
      return true;
    }, $"ReconBeat_{item.Name}");
    _taskManager.Enqueue(ReconProcessNext, "ReconProcessNext");

    return true;
  }
}
