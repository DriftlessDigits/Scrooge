using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Orchestrates hawk runs: navigating to the sell view, processing items
/// one at a time, and swapping retainers when full.
/// </summary>
internal sealed class HawkRunOrchestrator : IDisposable
{
  private readonly TaskManager _taskManager;
  private readonly ItemPricingPipeline _pricing;
  private readonly IAddonLifecycle.AddonEventDelegate _skipRetainerDialog;
  private readonly Action _removeTalkListeners;

  /// <summary>The buyback-dialog guard this run arms while it vendors.</summary>
  private readonly VendorDismissGuard _vendorDismiss = new("HawkRun");

  private Queue<ListableItem>? _hawkQueue;
  private IDisposable? _catchallBlock;
  private int _hawkRetainerSlotsUsed;

  /// <summary>
  /// THE BANK this run is spending (Rounds unit 3): every decision_cache row still
  /// inside <c>ReconFreshHours</c>, read ONCE at run start.
  ///
  /// <para>Once, not per item, because the alternative is a SQLite hit inside a task
  /// queue that fires several times a second - and because the set cannot grow
  /// during the run: nothing banks decisions while the list stage is listing. It can
  /// only AGE, which is why the per-item gate re-asks the freshness predicate against
  /// the current clock rather than trusting the cutoff this read was taken at. A run
  /// long enough to cross the boundary walks the classic chain for the rows that fell
  /// out of the window, which is the correct answer and costs nothing to be right
  /// about.</para>
  ///
  /// <para>Empty is the fail-closed state, and it is what an unreadable database
  /// produces: every item pays the full ComparePrice chain, exactly as the bell run
  /// did before this unit existed.</para>
  /// </summary>
  private Dictionary<(uint ItemId, bool IsHq), DecisionCacheRow> _banked = [];

  /// <summary>The shared run-host lifecycle (state, progress, value, stall terminal).</summary>
  private readonly RunLifecycle _run = new(TimeSpan.FromSeconds(45));

  // The spine expectations and the roster hop live in RetainerSellNav now - the
  // road to a retainer's sell view has two travellers since Rounds unit 2 (this
  // run and recon), and one definition is what keeps them from disagreeing about
  // what "at a bell" means. Aliased here so the bell run's own prose still reads
  // in its own vocabulary.
  private static ExpectedState ListExpected => RetainerSellNav.ListExpected;
  private static List<FacetReading> ReadListState() => RetainerSellNav.ReadListState();

  // M4 inventory-scope zombie round: the full tradeable-bag container observed at
  // run start, and the retainers this run attributed lane_held flags to. The round
  // fires ONLY at natural completion (full observation); every partial exit
  // (retainers filled, cancel, dispose) leaves the flags open - fail toward open.
  private HashSet<uint>? _observedInventoryIds;
  private HashSet<string>? _visitedRetainers;

  /// <summary>True while a hawk run is in progress.</summary>
  internal bool IsRunning => _run.IsRunning;

  /// <summary>The live run, for the standard progress readout (see RunHostRender).</summary>
  internal RunLifecycle Run => _run;

  /// <summary>
  /// THE BELL'S WEDGE WATCHDOG - the one it never had (stability sweep, 2026-08-16).
  ///
  /// <para>Every other executor in the plugin carries this backstop; the bell run was
  /// the hole. Its <see cref="RunLifecycle"/> carries a 45s stall bound and NOTHING
  /// ASKED IT - unlike recon, whose Tick is driven from the plugin's framework loop -
  /// so a queue killed by the TaskManager's AbortOnTimeout left <c>IsRunning</c> true
  /// forever: the round's busy gate stayed shut, the flow froze on a listing stage
  /// nothing was working, and no death was ever reported to name the gap.</para>
  ///
  /// <para>The teardown is <see cref="HawkDie"/> - the SAME funnel the 20/20 exit
  /// already uses, latch and all. A wedge is a death by another door, not a second
  /// kind of ending.</para>
  /// </summary>
  private readonly QueueWedgeWatchdog _wedge;

  internal HawkRunOrchestrator(
    TaskManager taskManager,
    ItemPricingPipeline pricing,
    IAddonLifecycle.AddonEventDelegate skipRetainerDialog,
    Action removeTalkListeners)
  {
    _taskManager = taskManager;
    _pricing = pricing;
    _skipRetainerDialog = skipRetainerDialog;
    _removeTalkListeners = removeTalkListeners;
    _wedge = new QueueWedgeWatchdog(
      isLive: () => _run.IsRunning,
      isBusy: () => _taskManager.IsBusy,
      onWedged: () =>
      {
        Svc.Chat.PrintError(
          "[Scrooge] Bell run stopped early - a step timed out and its queue died. "
          + "Run closed; everything already listed stayed listed.");
        HawkDie("a step timed out and the task queue died");
      });
  }

  /// <summary>
  /// Plugin-unload teardown. The bell holds a SelectYesno listener and a live
  /// lifecycle while it runs, and neither may outlive the plugin - a dead delegate on
  /// the addon lifecycle is a crash waiting for the next buyback dialog. Idempotent:
  /// <see cref="Abort"/>'s wasLive latch means a second call reports nothing, and
  /// disarming an unarmed watchdog is a no-op.
  /// </summary>
  public void Dispose()
  {
    _wedge.Disarm();
    Abort();
  }

  /// <summary>
  /// Fail-closed teardown on error/abort. A cancel is a PARTIAL exit - it never runs
  /// the inventory-scope zombie round (the container was not fully worked), so open
  /// lane_held flags stay open.
  /// </summary>
  internal void Abort()
  {
    _wedge.Disarm();
    // The wasLive/Cancel/CancelRun/handover/conditional-report sequence is
    // RunTeardown.Die's - recon spelled out the identical one. What is left here is
    // what only the bell holds.
    RunTeardown.Die(_run, RunKind.Bell, "the bell run was cancelled", () =>
    {
      _hawkQueue = null;
      _catchallBlock?.Dispose();
      _catchallBlock = null;
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);
    });
  }

  /// <summary>
  /// THE BELL'S IN-QUEUE DEATH FUNNEL. Both doors that kill a run from INSIDE its own
  /// task chain land here: the 20/20 wall (every sell list full before the queue
  /// drained) and the wedge watchdog.
  ///
  /// <para>Distinct from <see cref="Abort"/> because the run genuinely got somewhere -
  /// so the ledger row is ENDED, not cancelled - and because the caller supplies its
  /// own reason for the round's halt banner. The lifecycle's own refusal to leave a
  /// terminal state is the latch (review ruling S18): a run already dead by another
  /// path must not report a SECOND death, which would overwrite the round's halt with
  /// the wrong gap and re-narrate an ending the transcript already has.</para>
  ///
  /// <para>PARTIAL exit either way, so no zombie round fires - the inventory container
  /// was not fully worked and open lane_held flags stay open (fail toward open).</para>
  /// </summary>
  /// <returns>True if this call was the one that killed the run.</returns>
  private bool HawkDie(string reason)
  {
    if (!_run.Cancel(DateTime.UtcNow)) return false;
    _wedge.Disarm();
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);
    Plugin.Ledger.EndRun();
    var bell = Plugin.CurrentRun;
    Plugin.CurrentRun = null;
    _hawkQueue = null;
    // No flash here (ruled 08-15) - the death halts the round, and the halt flashes.
    RunFlow.ReportDied(RunKind.Bell, reason, bell);
    return true;
  }

  /// <summary>
  /// Scans the player's tradeable bags for the item ids present right now - the full
  /// container an inventory-scope lane_held flag points at, as this run first sees it.
  /// Null when the inventory manager is unreadable (the round then treats observation
  /// as incomplete and closes nothing). Base item ids only (HQ is a slot flag); a
  /// superset is safe here - more ids present means fewer closes, i.e. fail toward open.
  /// </summary>
  private static HashSet<uint>? ScanBagInventoryItemIds()
  {
    var ids = new HashSet<uint>();
    // TryForEachSlot, not ForEachSlot: an unreadable manager must answer null here
    // (observation incomplete) rather than an honest-looking empty set.
    return Bags.TryForEachSlot(s => { if (s.ItemId != 0) ids.Add(s.ItemId); })
      ? ids
      : null;
  }

  /// <summary>
  /// The Hawk completion boundary's M4 round: close inventory-scope lane_held flags
  /// whose item has left the bags this run observed. Gated by RunLifecycle so it fires
  /// only on FULL observation (natural completion with a readable start-of-run bag
  /// scan); a partial run produces no inputs and closes nothing. Mirrors the pinch
  /// board round wired in SnapshotListings, but scoped to the inventory container.
  /// </summary>
  private void HawkZombieRound(bool fullyObserved)
  {
    if (_observedInventoryIds is not { } observed || _visitedRetainers == null) return;

    var inputs = RunLifecycle.HawkRoundInputs(fullyObserved, observed);
    if (inputs == null) return;

    foreach (var retainer in _visitedRetainers)
    {
      try
      {
        GilStorage.ZombieRoundLaneHeldFlags(retainer, StandingMemory.FlagScope.Inventory, inputs);
      }
      catch (Exception ex)
      {
        Svc.Log.Warning($"[Standing] Hawk zombie lane_held round failed for {retainer}: {ex.Message}");
      }
    }
  }

  // OpenHawkView is GONE (probation closed, ruled 2026-08-29): door-less since
  // the 08-15 bell-bar trim took its only caller, and two weeks of play - the
  // whole 3.0 ship and verification - never missed it. The manual pick-and-list
  // surface (HawkWindow, Select for Sale, this entry) deleted together.

  /// <summary>
  /// The Ledger's one-click entry: already in a retainer's sell view -> start the
  /// run now; at the bell roster -> navigate to the first retainer with sell space
  /// (the Hawk Wares hop) and start the run on arrival. This is what makes the
  /// bulk-confirm button live at the bell instead of demanding the old two-step.
  /// </summary>
  internal unsafe void NavigateAndStartHawkRun(List<ListableItem> items)
  {
    // Every refusal on this road is LOUD - a confirm button that no-ops
    // silently reads as a broken button (it was, twice).
    if (items.Count == 0)
    {
      Svc.Chat.PrintError("[Scrooge] Nothing to run - no eligible items.");
      return;
    }
    if (_taskManager.IsBusy)
    {
      Svc.Chat.PrintError("[Scrooge] Another run is still working - wait for it (or /scrooge to check).");
      return;
    }

    // Walk the transition ladder through the one evaluator: already in the sell
    // view -> fire; at the bell roster -> self-navigate the Hawk Wares hop;
    // nowhere near a bell -> refuse loudly, naming the walk. This is the same
    // two-branch behavior the method always had, now spoken in the spine's
    // vocabulary instead of an ad-hoc addon probe.
    var eval = SpineEvaluator.Evaluate(ListExpected, ReadListState());
    switch (eval.Rung)
    {
      case Spine.Rung.Fire:
        StartHawkRunCore(items);
        break;
      case Spine.Rung.SelfNavigate:
        // EnqueueNavigateToSellView still owns its own deeper refusals (all
        // retainers 20/20, list not ready) - the spine got us to the roster.
        EnqueueNavigateToSellView(_ => StartHawkRunCore(items));
        break;
      default:
        // THE GRACE (2026-07-26). A transient-class gap inside a live round is not
        // believed on one read. The round walks the player to the bell and
        // AutoRetainer summons a retainer the instant it opens; during that summon
        // neither bell addon is ready, and the read that landed in the gap halted a
        // round with Drift standing at the bell. So the gap has to PERSIST before it
        // kills the run - see TransientGrace for why duration is the axis that
        // separates the two cases.
        //
        // Both of this executor's expectations are transient-class (two addon reads),
        // so the whole refusal is waitable; GracePlan makes that a property of the
        // evaluation rather than a special case for Place.
        if (GracePlan.ShouldWaitOut(eval, playerPressed: !Plugin.Accountant.RoundActive)
            && SpineGrace.Hold("the retainer bell", GracePlan.PlaceGraceMs,
                 met: () => SpineEvaluator.Evaluate(ListExpected, ReadListState()).Rung
                   is Spine.Rung.Fire or Spine.Rung.SelfNavigate,
                 onReturned: () => NavigateAndStartHawkRun(items),
                 onExpired: () => RefuseList(eval)))
          break;

        RefuseList(eval);
        break;
    }
  }

  /// <summary>
  /// The bell run's refusal, said once. A run that never started still ENDED (WALK
  /// unit 9). Before the stages flowed, this refusal stopped where the player was
  /// looking - he had just pressed the button. Now the bell can fire from the flow,
  /// and a refusal nobody reported would leave a stage marked done at fire time with
  /// no run behind it, and the round walking on to the turn-in as though the bags had
  /// been listed. It reports, the round halts, and the gap is named.
  /// </summary>
  private static void RefuseList(SpineEvaluation eval)
  {
    Svc.Chat.PrintError($"[Scrooge] {eval.Message}");
    RunFlow.ReportDied(RunKind.Bell, eval.Message, run: null); // refused before the run existed
  }

  /// <summary>
  /// The bell run's half of the shared roster hop: <see cref="RetainerSellNav"/>
  /// owns the road, this owns the sentence said when there is no shelf to land on.
  /// </summary>
  private bool EnqueueNavigateToSellView(Action<int> onArrived)
    => RetainerSellNav.EnqueueToSellView(
      _taskManager, _skipRetainerDialog, _removeTalkListeners,
      RetainerSellNav.HawkAllFull, onArrived);

  /// <summary>
  /// The run start proper, minus the TaskManager busy guard - callable from the
  /// tail of the sell-view navigation queue (where the manager is by definition
  /// busy running the very task that arrived here). All fail-closed checks stay.
  /// Every caller arrives via NavigateAndStartHawkRun - there is no entry that
  /// ASSUMES the sell view anymore (07-22: the old assuming entry was the Fresh
  /// Yields hop's silent trap).
  /// </summary>
  private unsafe void StartHawkRunCore(List<ListableItem> items)
  {
    if (items.Count == 0)
      return;

    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
    {
      Svc.Chat.PrintError("[Scrooge] Not in retainer sell view - couldn't reach one. Summon your retainers and retry.");
      return;
    }

    // Read before mutating any run state — fail closed if the retainer
    // can't be resolved (avoids a half-started run).
    var retainerName = GameSafe.ActiveRetainerName();
    if (retainerName == null)
    {
      Svc.Chat.PrintError("[Scrooge] Couldn't read the active retainer — try reopening the sell list.");
      return;
    }

    _pricing.ClearState();
    _banked = ReadBankedDecisions();
    _run.Start(items.Count, RunValueUnit.Gil, DateTime.UtcNow, $"List {items.Count} items");
    Plugin.CurrentRun = new RunData { Mode = RunMode.Hawk };
    _hawkQueue = new Queue<ListableItem>(items);
    _hawkRetainerSlotsUsed = 0;

    // M4 round prep: snapshot the full tradeable-bag container this run observes,
    // and seed the visited-retainer set. Captured at START (before listing removes
    // items from bags), so a still-held thin item reads present and its flag stays.
    _observedInventoryIds = ScanBagInventoryItemIds();
    _visitedRetainers = new HashSet<string> { retainerName };

    Plugin.Ledger.StartNewRun();
    Plugin.Ledger.SetTotalItems(items.Count);

    // Set retainer name for log grouping (ClickRetainer doesn't fire when already inside a retainer)
    Plugin.Ledger.SetCurrentRetainer(retainerName);

    // Read current retainer's listing count from RetainerSellList
    _hawkRetainerSlotsUsed = GameSafe.RetainerSellListLength() ?? 0;

    // Auto-dismiss retainer greeting dialogs (needed for retainer swaps)
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", _skipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", _skipRetainerDialog);

    // Auto-confirm vendor dismiss dialog
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);

    // Start processing
    _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
    _wedge.Arm();
  }

  /// <summary>
  /// Reads the bank, fail-closed. An unreadable decision cache is not an error worth
  /// stopping a listing run over - it is simply a run with nothing banked, which is
  /// the behaviour this executor had for its entire life before unit 3.
  /// </summary>
  private static Dictionary<(uint ItemId, bool IsHq), DecisionCacheRow> ReadBankedDecisions()
  {
    try
    {
      return GilStorage.GetFreshDecisionCache(ReconFreshness.Cutoff(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Plugin.Configuration.ReconFreshHours));
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Bell] Decision cache unreadable, every item pays the board: {ex.Message}");
      return [];
    }
  }

  /// <summary>
  /// THE FORK, per item: does this listing spend a banked decision or pay the board?
  ///
  /// <para>Asked HERE, at enqueue time, because the answer decides which tasks the
  /// item gets - and declining the cache here costs literally nothing, since the
  /// classic chain is what gets enqueued instead. Asking it later, mid-panel, would
  /// mean discovering the cache was unusable while standing at a sell panel with no
  /// board read and no cheap way to get one.</para>
  ///
  /// <para>The freshness question is <see cref="ReconFreshness"/>'s, verbatim - one
  /// rule, two doors - and it is re-asked against the CURRENT clock rather than the
  /// cutoff <see cref="_banked"/> was read at, so a long run degrades honestly as
  /// rows age out from under it.</para>
  /// </summary>
  private CachedPostPlan PlanPost(ListableItem item)
  {
    var row = _banked.TryGetValue((item.ItemId, item.IsHq), out var banked)
      ? banked : (DecisionCacheRow?)null;

    return CachedPostGate.Decide(row,
      DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      Plugin.Configuration.ReconFreshHours,
      ItemPricingPipeline.GuardsNow(item.ItemId));
  }

  /// <summary>
  /// Processes the next item in the hawk queue. If the current retainer is full,
  /// swaps to the next retainer with space before continuing.
  /// </summary>
  private unsafe bool? HawkProcessNext()
  {
    if (_hawkQueue == null || _hawkQueue.Count == 0)
    {
      // All done — the shared road back out (RetainerSellNav owns the close-up chain
      // and the S18 latch's reasoning; both sell-view runs end on it).
      RetainerSellNav.EnqueueSellViewCloseUp(_taskManager, _removeTalkListeners, () => {
        if (!_run.Complete(DateTime.UtcNow)) return true;
        _wedge.Disarm();
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", _vendorDismiss.Confirm);
        Plugin.Ledger.EndRun();
        // Natural completion: the full inventory container was worked. Close any
        // inventory-scope lane_held flag whose item has left the bags (M4 round).
        HawkZombieRound(fullyObserved: true);
        var bell = Plugin.CurrentRun;
        Plugin.CurrentRun = null;
        _hawkQueue = null;
        // No flash here (ruled 08-15) - the completion handler owns the taskbar.
        // The bell just PUT listings on the board. Nothing else in the plugin
        // knows about them until the next pinch re-reads the sell lists, so this
        // completion is the write-side event the standing book has been missing.
        RunFlow.ReportDone(RunKind.Bell, bell);
        return true;
      });
      return true;
    }

    // Peek first — route Always Vendor items before checking slot capacity
    var item = _hawkQueue.Peek();

    // Path A: direct vendor sell, no MB, no retainer slot — the standing Always
    // Vendor rule, or a routed Vendor verdict riding the same mechanics.
    if (item.IsAlwaysVendor || item.RoutedVendor)
    {
      _hawkQueue.Dequeue();
      _taskManager.Enqueue(() => { if (Plugin.CurrentRun != null) Plugin.CurrentRun.CurrentItem = new PricingItem { ItemId = item.ItemId }; return true; }, $"HawkInitItem_{item.Name}");
      _taskManager.Enqueue(() => RetainerPanelActions.ClickInventoryItem(item), $"HawkClickItem_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = GilTrackingState.Block("hawk_vendor"); return true; }, $"HawkBlockCatchall_{item.Name}");
      _taskManager.Enqueue(RetainerPanelActions.ClickHaveRetainerSellItems, $"HawkVendorSell_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => { TrackVendorSale(item); return true; }, $"HawkTrackVendor_{item.Name}");
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = null; return true; }, $"HawkUnblockCatchall_{item.Name}");
      _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
      return true;
    }

    // Slot capacity check — only for non-vendor items
    if (_hawkRetainerSlotsUsed >= 20)
    {
      // Swap to next retainer (item stays in queue since we only peeked)
      _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "HawkSwapCloseSellList");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.CloseRetainer, "HawkSwapCloseRetainer");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(HawkFindNextRetainer, "HawkFindNextRetainer");
      return true;
    }

    // Path C: Normal MB flow — safe to dequeue now
    _hawkQueue.Dequeue();

    // THE TWO DOORS (Rounds unit 3). Both open the panel and both post a real
    // listing; they differ only in whether the four-second server round trip is paid
    // again. A fresh banked decision was bought minutes ago by the Look half at a
    // real board, so the act leg sets the price and confirms. Anything else - no row,
    // a stale row, a hold, a price today's guards refuse - walks the classic chain
    // below, which is what this executor always did.
    var plan = PlanPost(item);

    _taskManager.Enqueue(() => {
      if (Plugin.CurrentRun != null)
        Plugin.CurrentRun.CurrentItem = new PricingItem {
          ItemId = item.ItemId,
          CachedPost = plan.RidesCache ? plan : null,
        };
      return true;
    }, $"HawkInitItem_{item.Name}");
    _taskManager.Enqueue(() => RetainerPanelActions.ClickInventoryItem(item), $"HawkClickItem_{item.Name}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(RetainerPanelActions.ClickPutUpForSale, $"HawkPutUpForSale_{item.Name}");
    _taskManager.DelayNext(100);
    if (!plan.RidesCache)
    {
      // The one chain every board-reading door in the plugin now shares: ask, then
      // wait for the WHOLE board across the escalating windows. This replaced the
      // bell's flat keep-open wait (ruled 2026-08-10) - see
      // ItemPricingPipeline.EnqueueBoardRead for what the flat window was costing.
      _pricing.Board.EnqueueBoardRead("Hawk", item.Name);
    }
    else
    {
      // THE CACHED POST INHERITS THE BEAT (review ruling S14, 2026-08-12). Skipping the
      // board read skipped DelayMarketBoard with it, and nothing replaced it - so the
      // one door in the plugin that WRITES was also its fastest and most uniform
      // cadence, opening panels on two flat 100ms steps and nothing else. Recon added
      // jitter to its own per-item gap for exactly this reason.
      //
      // It is the SAME method the chain enqueues, not a second spelling of it: one
      // knob, one jitter, and no way for the two doors to drift apart. Enqueued here
      // in the else, so an item never waits both this and the read's own copy.
      _taskManager.Enqueue(_pricing.Board.DelayMarketBoard, $"HawkCachedBeat_{item.Name}");
    }
    _taskManager.Enqueue(_pricing.SetNewPrice, $"HawkSetPrice_{item.Name}");
    _taskManager.Enqueue(() => HandlePostPrice(item), $"HawkPostPrice_{item.Name}");
    // NOTE: HawkProcessNext is NOT enqueued here — HandlePostPrice owns it

    return true;
  }

  /// <summary>
  /// After SetNewPrice completes, decides whether to increment retainer slots
  /// (item was listed on MB) or vendor-sell (price check failed, auto-vendor enabled).
  /// HandlePostPrice is the single gateway to the next item — every path ends by
  /// enqueuing HawkProcessNext.
  /// </summary>
  private unsafe bool? HandlePostPrice(ListableItem item)
  {
    var result = Plugin.CurrentRun?.CurrentItem?.Result ?? PricingResult.Pending;

    if (result == PricingResult.VendorSell)
    {
      // Path B: price check failed → vendor sell instead
      _taskManager.Enqueue(() => RetainerPanelActions.ClickInventoryItem(item), $"HawkReClickItem_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = GilTrackingState.Block("hawk_vendor"); return true; }, $"HawkBlockCatchall_{item.Name}");
      _taskManager.Enqueue(RetainerPanelActions.ClickHaveRetainerSellItems, $"HawkVendorSell_{item.Name}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(() => { TrackVendorSale(item); return true; }, $"HawkTrackVendor_{item.Name}");
      _taskManager.Enqueue(() => { _catchallBlock?.Dispose(); _catchallBlock = null; return true; }, $"HawkUnblockCatchall_{item.Name}");
      _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
      return true;
    }

    if (result == PricingResult.Listed)
    {
      _hawkRetainerSlotsUsed++;
      // V20: stamp the standing routing receipt - the item's List verdict executed.
      RoutingReceiptStamp.Executed(item.ItemId, item.IsHq, "Listed");

      // THE WRITE SIDE (WALK unit 6): we just put this on the board, and until now
      // nothing in the plugin knew until a pinch re-read the sell lists. Every
      // operand is already in hand - the applied ask, the stack, the retainer whose
      // sell view we are standing in. Banked when the run ends.
      var listed = Plugin.CurrentRun?.CurrentItem?.FinalPrice ?? 0;
      if (listed > 0)
      {
        StandingBookFeed.Listed(item.ItemId, item.IsHq,
          GameSafe.ActiveRetainerName() ?? "", listed, item.Quantity);
        // The listed register (F9): the gauge speaks what the run is DOING.
        Plugin.CurrentRun?.RecordListed(listed, item.Quantity);
      }
    }

    // One item processed (listed, or held/skipped) - advance the lifecycle and reset
    // its stall watchdog. Listing value is tracked by PinchRunLog; vendor value is
    // recorded on the vendor paths (TrackVendorSale).
    _run.RecordProgress(1, 0, DateTime.UtcNow);
    _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
    return true;
  }

  /// <summary>Tracks a vendor sale for summary, log, and chat output.</summary>
  private void TrackVendorSale(ListableItem item)
  {
    // Don't track if vendor sell failed (e.g., non-vendorable item)
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return;

    // Read the CAPTURED verdict, not Result: the pipeline overwrites Result with
    // VendorSell to route the item here, so switching on Result made both named
    // reasons unreachable and every auto-vendored item read "Price check failed"
    // (07-24). VendorFallbackFrom is the pre-clobber operand.
    var reason = item.IsAlwaysVendor
      ? "You always vendor this one."
      : item.RoutedVendor
      // The router's verdict executing, in the router's own words (Drift, 08-23:
      // "do I really always vendor that one?" - the flag conflation dressed every
      // routed vendor as a standing rule).
      ? RunLogVoice.Reasons.NoBetterExit
      : Plugin.CurrentRun?.CurrentItem?.VendorFallbackFrom switch
        {
          // One floor verdict, so the sentence names WHICH floor bound rather than
          // always claiming the vendor did (2026-08-21). Under the Enclave mode this
          // arm is unreachable - auto-vendor is refused there outright.
          PricingResult.BelowFloor => RunLogVoice.Reasons.SoldUnderFloor(
            PriceFloor.Effective(
              Plugin.Configuration.PriceFloorMode,
              Plugin.CurrentRun?.CurrentItem?.VendorPrice,
              Plugin.Configuration.MinimumListingPrice)),
          _ => RunLogVoice.Reasons.NoBoardData
        };

    var totalGil = VendorSaleBook.Record(item.ItemId, item.Name, item.IsHq, item.Quantity, reason);

    Plugin.Ledger.IncrementProcessed();
    _run.RecordProgress(1, totalGil, DateTime.UtcNow); // one item done + gil earned
    // THE GAUGE'S LIFECYCLE TOO (F9, ruled 08-22 - the lap's "0 gil" over 17k
    // vendored). _run above is the hawk's own readout; the wizard's progress
    // line draws Plugin.CurrentRun's lifecycle, and this beat is what finally
    // reaches it. Value only - the item beat is the round's own.
    Plugin.CurrentRun?.RecordVendored(totalGil);
  }

  /// <summary>
  /// Finds the next retainer with available sell slots and navigates to their sell view.
  /// Called when the current retainer hits 20/20 mid-run.
  /// </summary>
  private unsafe bool? HawkFindNextRetainer()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
      return false;

    var retainerList = new AddonMaster.RetainerList(addon);
    var retainers = retainerList.Retainers;

    for (int i = 0; i < retainers.Length; i++)
    {
      var count = GameNavigation.GetRetainerListingCount(addon, i);
      if (count < 20)
      {
        _hawkRetainerSlotsUsed = count;
        _visitedRetainers?.Add(retainers[i].Name); // round this retainer too at completion

        _taskManager.Enqueue(() => GameNavigation.ClickRetainer(i), "HawkSwapClickRetainer");
        _taskManager.DelayNext(100);
        _taskManager.Enqueue(GameNavigation.ClickSellItems, "HawkSwapClickSellItems");
        _taskManager.DelayNext(500);
        _taskManager.Enqueue(HawkProcessNext, "HawkProcessNext");
        return true;
      }
    }

    // No retainers with space — abort remaining items
    var stranded = _hawkQueue?.Count ?? 0;
    Svc.Chat.PrintError($"[Scrooge] All retainers full. {stranded} items could not be listed.");
    _taskManager.Enqueue(() => { _removeTalkListeners(); return true; });
    _taskManager.Enqueue(() => {
      // It DID list until the shelves filled - the death is reported (so the round
      // halts and names the gap) carrying the count, because "it did list until then"
      // is a fact the rail owns.
      HawkDie($"every retainer's sell list is full (20/20), {stranded} {(stranded == 1 ? "item" : "items")} left unlisted");
      return true;
    }, "HawkRunEnd");
    return true;
  }
}
