using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Scrooge.Windows;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The Venture Coffer rider (WALK, Drift's 2026-07-23 ruling: "if there is a Venture
/// Coffer in the inventory, it needs to be used to unlock an item"). A coffer is
/// hidden routable inventory - itself no exit, its contents all of them - so the
/// round opens ALL coffers before a stage counts the bags, and the unlocked items
/// land in the bags in time to be counted (the melt-before-list lesson: don't leave
/// routable value locked while a stage counts the pile).
///
/// MOVED ONE STOP EARLIER (Drift, 2026-07-24): "IF we are about to move to hawk
/// phase, scan for coffers and pop them, then include them in the listing" - coffer
/// yields are overwhelmingly listable mats and dyes, so they must be in the bags
/// before the bell composes its rows.
///
/// RESTAGED (2026-07-25) when the ruled order became pinch -> melt -> bell. The
/// rider hangs off the FRONT OF THE MELT now, which is the last stop before the
/// bell and therefore still satisfies 07-24's reason - and it is the only stop that
/// CAN host it: the rider needs an un-occupied player, the bell needs an open
/// retainer window, and those are the same game flag with opposite signs. The bell
/// re-routes and re-scans when it fires, so the unlocked items ride the same Hawk
/// run they always did.
///
/// A RIDER, not a stage: nothing about the round's stage list changes. The deck's
/// Desynth-stage fire opens coffers FIRST, then selects and opens the melt pile
/// (see AccountantWindow.FireMeltStage). Round-context only - the standalone manual
/// desynth button never opens coffers.
///
/// TaskManager-driven, mirroring <see cref="DesynthOrchestrator"/>: one abort
/// funnel, a watchdog against a silently-dead queue, a polite delay between opens
/// (coffers take a server round trip; a stack opens in a loop, not a burst). The
/// pure half - the coffer identity, the free-slot guard decision, the set-diff
/// attribution - lives in <see cref="CofferLogic"/>.
/// </summary>
internal sealed class CofferOrchestrator : IDisposable
{
  /// <summary>
  /// The rider's declared expected state (spine). One facet: the game refuses an
  /// item-use while the player is occupied (an open bell, an NPC event), exactly as
  /// it refuses Desynthesize. UNKNOWN occupancy still refuses loudly here, naming
  /// the gap - what changed on 07-25 is that a KNOWN one (a retainer window the
  /// advisor recognizes) is closed by the melt stage's fire before this runs, so the
  /// rider no longer refuses at the exact spot the round just sent the player.
  /// (The deck gates the Desynth stage on the same question, so in the round this is
  /// defense in depth - but the rider owns its own precondition, like every executor.)
  /// </summary>
  internal static readonly ExpectedState OpenExpected = new("open coffers",
    new SpineExpectation(Spine.Facet.Occupancy, "to be un-occupied", Spine.Rung.Refuse));

  /// <summary>Reads the rider's expected-state facet from live game sensors.</summary>
  private static List<FacetReading> ReadOpenState() => new()
  {
    SpineSensors.Unoccupied(),
  };

  /// <summary>Base inter-open delay (ms). Conservative: coffers have a short use
  /// lockout and a server round trip; a stack opens politely, never machine-gun.</summary>
  private const int InterOpenBaseMs = 1500;

  private readonly TaskManager _taskManager;
  private readonly Random _random = new();

  private Action? _onComplete;
  private int _opened;
  private Dictionary<(uint ItemId, bool Hq), int> _riderBefore = new();

  /// <summary>
  /// The rider's run state, on the shared lifecycle rather than a hand-rolled bool
  /// (orchestrators item 15). The terminal latch is what it buys: EndRider and
  /// CloseRiderAborted both hand off to the melt, and only one of them may ever be
  /// the one that does it (S18).
  /// </summary>
  private readonly RunLifecycle _run = new(TimeSpan.FromSeconds(45));

  /// <summary>The lifecycle, for the round's rail and the completion summary.</summary>
  internal RunLifecycle Run => _run;

  /// <summary>True while the rider is opening coffers.</summary>
  internal bool IsRunning => _run.IsRunning;

  /// <summary>
  /// Backstop against the ECommons TaskManager clearing its queue on TimeLimitMS
  /// without telling us (the desynth watchdog's twin). The predicate and the framework
  /// plumbing are <see cref="QueueWedgeWatchdog"/>'s; what stays here is the rider's
  /// own teardown - which, crucially, STILL HANDS OFF to the melt. A dead coffer loop
  /// must not swallow the round.
  /// </summary>
  private readonly QueueWedgeWatchdog _wedge;

  internal CofferOrchestrator()
  {
    _taskManager = new TaskManager
    {
      TimeLimitMS = 15000,
      AbortOnTimeout = true,
    };
    // isLive reads the LIFECYCLE, not a bool kept in step by hand - and it can no
    // longer be true after a terminal transition, so the watchdog's death and the
    // natural end are mutually exclusive rather than merely unlikely.
    _wedge = new QueueWedgeWatchdog(
      isLive: () => _run.IsRunning,
      isBusy: () => _taskManager.IsBusy,
      onWedged: () =>
      {
        Svc.Chat.PrintError(
          "[Scrooge] Coffer rider stalled (task queue died) - stopping. Any coffers left are untouched; open them by hand or re-run the round.");
        CloseRiderAborted("watchdog: task queue died", stalled: true);
      });
  }

  public void Dispose()
  {
    _taskManager.Abort();
    _wedge.Disarm();
  }

  /// <summary>
  /// The round entry point. Opens ALL Venture Coffers in the bags, then invokes
  /// <paramref name="onComplete"/> exactly once - whether it opened many, none, or
  /// died mid-loop - so the melt always proceeds after the coffers (or
  /// after the silent no-op when the config is off / there are no coffers / a run is
  /// busy). The onComplete carries the melt's own fire (re-route, select, open), so
  /// the coffers genuinely precede it and their contents are in the bags before
  /// either the melt pile or the bell's rows are composed.
  /// </summary>
  internal unsafe void OpenAllForRound(Action onComplete)
  {
    // Config escape hatch (default on). Off -> the rider is inert; the melt proceeds.
    if (!Plugin.Configuration.OpenVentureCoffers)
    {
      onComplete();
      return;
    }

    if (IsRunning || _taskManager.IsBusy)
    {
      // Something is already running; don't stack. Hand straight on.
      onComplete();
      return;
    }

    int cofferQty = CoffersInBags();
    if (cofferQty == 0)
    {
      onComplete(); // nothing to open - stay silent, proceed to the melt
      return;
    }

    // Spine: refuse loudly if occupied, then let the next stage handle its own gate.
    //
    // WALK unit 9: by the time this runs, the melt stage's fire has already cleared
    // any KNOWN occupier (OccupancyTransition) - the 07-24 refusal, "expected to be
    // un-occupied, but the retainer bell is open", was the advisor refusing at the
    // very spot it had sent the player. What is left here is the honest rung 4: an
    // occupancy nothing we recognize is holding. It still refuses loudly, and the
    // melt still proceeds.
    var eval = SpineEvaluator.Evaluate(OpenExpected, ReadOpenState());
    if (!eval.CanFire)
    {
      Svc.Chat.PrintError($"[Scrooge] {eval.Message} Coffers left unopened; the melt continues.");
      onComplete();
      return;
    }

    // Free-slot guard: opening against a near-full inventory risks a lost item or a
    // stuck state (mirrors the melt's MinFreeInventorySlots precedent).
    int freeSlots = Bags.FreeSlots();
    if (!CofferLogic.CanOpen(freeSlots))
    {
      Svc.Chat.PrintError(
        $"[Scrooge] {freeSlots} free inventory slot(s) - need at least {CofferLogic.MinFreeInventorySlots} "
        + "before opening Venture Coffers (the unlocked item needs somewhere to land). Coffers left unopened; the melt continues.");
      onComplete();
      return;
    }

    _onComplete = onComplete;
    _opened = 0;
    _riderBefore = SnapshotBags();
    LastRiderGains = [];

    _run.Start(cofferQty, RunValueUnit.None, DateTime.UtcNow,
      $"Open {cofferQty} Venture Coffer{(cofferQty == 1 ? "" : "s")}");
    Plugin.CurrentRun = new RunData { Mode = RunMode.Coffer };
    Plugin.Ledger.StartNewRun();
    Plugin.Ledger.SetCurrentRetainer("Venture Coffers");
    Plugin.Ledger.SetTotalItems(cofferQty);

    _wedge.Arm();

    _taskManager.Enqueue(OpenNext, "CofferOpenNext");
  }

  /// <summary>
  /// Per-coffer chain head. Re-scans coffers (the stack decrements each open),
  /// re-checks the guards, uses one coffer, waits for the yield to land, narrates
  /// it, then paces before the next.
  /// </summary>
  private unsafe bool? OpenNext()
  {
    if (!IsRunning) return true;

    int cofferQty = CoffersInBags();
    if (cofferQty == 0)
    {
      EndRider();
      return true;
    }

    // Re-gate every open: the world can change under a multi-second loop.
    var eval = SpineEvaluator.Evaluate(OpenExpected, ReadOpenState());
    if (!eval.CanFire)
    {
      Svc.Chat.PrintError($"[Scrooge] {eval.Message} Stopping the coffer rider at {_opened} opened.");
      CloseRiderAborted(eval.Message);
      return true;
    }

    int freeSlots = Bags.FreeSlots();
    if (!CofferLogic.CanOpen(freeSlots))
    {
      Svc.Chat.PrintError(
        $"[Scrooge] Down to {freeSlots} free slot(s) - stopping the coffer rider at {_opened} opened before an item is lost.");
      CloseRiderAborted($"inventory nearly full ({freeSlots} free)");
      return true;
    }

    var before = SnapshotBags();
    int cofferQtyBefore = cofferQty;

    _taskManager.Enqueue(() => { UseOneCoffer(); return true; }, "CofferUse");

    // Small "the game reacts" beat, then wait for the open to confirm (the coffer
    // count drops) before reading the yield. Bounded by the shared server ceiling.
    _taskManager.DelayNext(Jitter(600, 150));
    _taskManager.Enqueue(WaitForOpen(cofferQtyBefore, Plugin.Configuration.ServerRoundTripCeilingMs), "CofferWaitOpen");
    _taskManager.Enqueue(() => { NarrateOpen(before); return true; }, "CofferNarrate");

    // Polite inter-open pacing, then the next coffer.
    _taskManager.DelayNext(Jitter(InterOpenBaseMs, 500));
    _taskManager.Enqueue(OpenNext, "CofferOpenNext");
    return true;
  }

  /// <summary>Uses one Venture Coffer via the inventory-context item-use path.</summary>
  private static unsafe void UseOneCoffer()
  {
    var ctx = AgentInventoryContext.Instance();
    if (ctx == null) return;
    // UseItem(itemId, inventoryType = Invalid, itemSlot = 0, a5 = 0): the game finds
    // the item by id. Verified signature (ClientStructs): the tail args are optional.
    ctx->UseItem(CofferLogic.VentureCofferItemId);
  }

  /// <summary>
  /// Polls until the coffer count drops below <paramref name="cofferQtyBefore"/>
  /// (the open confirmed) or the timeout elapses. A timeout is NOT fatal to the
  /// whole rider - the narration handles a not-yet-landed yield honestly - but it
  /// still ends the wait so we don't stall.
  /// </summary>
  private Func<bool?> WaitForOpen(int cofferQtyBefore, int timeoutMs)
  {
    long? deadline = null;
    return () =>
    {
      deadline ??= Environment.TickCount64 + timeoutMs;
      if (CoffersInBags() < cofferQtyBefore) return true; // open confirmed
      if (Environment.TickCount64 > deadline) return true; // give up waiting, narrate what we can
      return false;
    };
  }

  /// <summary>
  /// Attributes the just-opened coffer's yield by diffing the bags around the open
  /// and narrates it. Honest fallback: an empty or ambiguous per-open diff (lag, or
  /// the yield stacked in a way the poll missed) narrates a plain "coffer opened" -
  /// the end summary's rider-wide diff still reports everything that appeared.
  /// </summary>
  private void NarrateOpen(Dictionary<(uint ItemId, bool Hq), int> before)
  {
    _opened++;
    _run.RecordProgress(1, 0, DateTime.UtcNow); // one coffer opened; the rider earns no gil
    Plugin.Ledger.IncrementProcessed();

    var after = SnapshotBags();
    var gained = CofferLogic.NewItems(before, after);

    if (gained.Count == 1)
    {
      var y = gained[0];
      var name = GilTracker.GetItemName(y.ItemId);
      var qty = y.Qty > 1 ? $"{y.Qty}x " : "";
      Plugin.Ledger.AddEntry(ItemOutcome.Unlocked, Format.Hq(name, y.IsHq),
        $"unlocked {qty}from a Venture Coffer");
    }
    else if (gained.Count > 1)
    {
      // More than one new item appeared in the window (a stacked yield, or two
      // things landing together) - name them all, honestly, on one entry.
      var parts = new List<string>();
      foreach (var y in gained)
        parts.Add($"{(y.Qty > 1 ? y.Qty + "x " : "")}{Format.Hq(GilTracker.GetItemName(y.ItemId), y.IsHq)}");
      Plugin.Ledger.AddEntry(ItemOutcome.Unlocked, "Venture Coffer",
        "unlocked " + string.Join(", ", parts));
    }
    else
    {
      // Yield not seen in time - honest per-open line; the end summary catches it.
      Plugin.Ledger.AddEntry(ItemOutcome.Unlocked, "Venture Coffer",
        "opened (yield not detected in time - see the run summary)");
    }
  }

  /// <summary>
  /// Normal end: the coffers are gone. Adds the rider-wide "N coffers -> N items"
  /// summary (the set-diff backstop over the whole run) and closes the run log.
  /// </summary>
  /// <summary>
  /// Every variant this rider's opens produced, rider-wide bag diff, refreshed on
  /// EVERY way out - the normal end and the aborts both, because a bags-full halt
  /// half way through still put real items in the bags (live receipt 08-16: the
  /// dyes). The round's admission reads this to let the rider's products ride the
  /// bell beside the melt's yields (ruled 08-16, Drift: "I can't think of a thing I
  /// pulled from a venture coffer that we didn't just list").
  /// </summary>
  internal List<(uint ItemId, bool IsHq)> LastRiderGains { get; private set; } = [];

  /// <summary>The rider-wide diff, banked for <see cref="LastRiderGains"/>' readers.</summary>
  private List<CofferYield> BankRiderGains()
  {
    var allNew = CofferLogic.NewItems(_riderBefore, SnapshotBags());
    var gains = new List<(uint ItemId, bool IsHq)>(allNew.Count);
    foreach (var y in allNew) gains.Add((y.ItemId, y.IsHq));
    LastRiderGains = gains;
    return allNew;
  }

  private void EndRider()
  {
    // The terminal latch at the completion funnel: the rider's handoff must fire
    // exactly once, and this is where a natural end claims it.
    if (!_run.Complete(DateTime.UtcNow)) return;
    var allNew = BankRiderGains();
    int itemCount = 0;
    foreach (var y in allNew) itemCount += y.Qty;

    Plugin.CurrentRun?.AddRunEntry(RunEvent.Summary,
      $"{_opened} coffer{(_opened == 1 ? "" : "s")} -> {itemCount} item{(itemCount == 1 ? "" : "s")} unlocked");

    _wedge.Disarm();
    Plugin.Ledger.EndRun();
    var rider = Plugin.CurrentRun;
    Plugin.CurrentRun = null;
    Svc.Chat.Print($"[Scrooge] Opened {_opened} Venture Coffer{(_opened == 1 ? "" : "s")}.");

    // The bags changed - the Ledger must re-route before anything reads them.
    // Coffer maps to NO round stage (see FlowPlan.StageOf), so this can only ever
    // refresh; the melt's own run still owns the stage.
    RunFlow.ReportDone(RunKind.Coffer, rider);
    Handoff();
  }

  /// <summary>
  /// The ONE way the rider dies early: every abort path funnels here so the run log
  /// is cancelled, the busy flag drops, and - crucially - the melt still gets its
  /// handoff. A dead coffer loop must not swallow the round.
  /// </summary>
  private void CloseRiderAborted(string reason, bool stalled = false)
  {
    // Liveness read BEFORE the transition (RunTeardown.Die's order): afterwards
    // there is no telling an already-dead rider from one this call just killed, and
    // a second death report overwrites the round's halt banner with the wrong gap.
    var wasLive = _run.IsRunning;
    var now = DateTime.UtcNow;
    if (stalled) _run.Stall(now); else _run.Cancel(now);
    // The abort still banks what the opens produced - a bags-full halt half way
    // through is the ordinary way a big pull ends, and its items are no less real.
    BankRiderGains();
    _wedge.Disarm();
    Plugin.Ledger.CancelRun();
    var rider = Plugin.CurrentRun;
    Plugin.CurrentRun = null;
    if (wasLive)
      RunFlow.ReportDied(RunKind.Coffer, reason, rider);
    Handoff();
  }

  /// <summary>
  /// External cancel for the round's Abandon (SF3-N): the rider dies WITHOUT its
  /// handoff. CloseRiderAborted deliberately still hands off - a dead coffer loop
  /// must not swallow a LIVE round's melt - but here the round itself is ending,
  /// and a handoff would open the salvage window over a desk nobody is sitting at.
  /// </summary>
  internal void Abort(string reason)
  {
    _onComplete = null; // the melt this rider was front-loading dies with its round
    _taskManager.Abort();
    CloseRiderAborted(reason);
  }

  /// <summary>Invokes the stored continuation (the melt's fire) exactly once.</summary>
  private void Handoff()
  {
    var cont = _onComplete;
    _onComplete = null;
    cont?.Invoke();
  }

  private int Jitter(int baseMs, int band) => Pacing.Jitter(_random, baseMs, band);

  // --- Bag helpers (the four main inventory pages, walked by Bags) ---

  /// <summary>
  /// Total quantity of Venture Coffers across the four main bags - the deck reads
  /// this to ARM the melt stage (coffers in the bags are hidden routable inventory,
  /// so the stage has work even with zero rows). Cache it on a refresh; it walks
  /// four containers.
  /// </summary>
  internal static int CoffersInBags()
    => Bags.TotalQuantityWhere(CofferLogic.IsVentureCoffer);

  /// <summary>
  /// Snapshots the bags as (itemId, HQ) -&gt; total quantity, for the set-diff
  /// attribution. Only the four main pages - coffer yields land here.
  /// </summary>
  private static Dictionary<(uint ItemId, bool Hq), int> SnapshotBags()
  {
    var map = new Dictionary<(uint, bool), int>();
    Bags.ForEachSlot(s =>
    {
      if (s.ItemId == 0) return;
      var key = (s.ItemId, s.IsHq);
      map.TryGetValue(key, out var have);
      map[key] = have + s.Quantity;
    });
    return map;
  }
}
