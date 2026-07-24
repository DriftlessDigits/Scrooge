using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Scrooge.Windows;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// The Venture Coffer rider (WALK, Sam's 2026-07-23 ruling: "if there is a Venture
/// Coffer in the inventory, it needs to be used to unlock an item"). A coffer is
/// hidden routable inventory - itself no exit, its contents all of them - so the
/// sweep opens ALL coffers at the FRONT of disposition (before the melt), and the
/// unlocked items land in the bags for the next Refresh to route (the melt-before-
/// list lesson: don't leave routable value locked while a stage counts the pile).
///
/// A RIDER, not a stage: nothing about the sweep's stage list changes. The deck's
/// Desynth-stage fire opens coffers FIRST, then proceeds to the melt (see
/// LedgerWindow.FireSweepStage). Sweep-context only - the standalone manual desynth
/// button never opens coffers.
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
  /// it refuses Desynthesize. The advisor cannot self-clear an occupied state (it
  /// will not close your bell), so it refuses loudly, naming the gap. (The deck
  /// already gates the Desynth stage on un-occupied, so in the sweep this is
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

  /// <summary>True while the rider is opening coffers.</summary>
  internal bool IsRunning { get; private set; }

  /// <summary>
  /// Bumped every time the rider closes WITHOUT finishing (occupancy refusal, guard
  /// trip, watchdog). Exposed for parity with the other executors; the deck does NOT
  /// halt on it - the rider fires ahead of the melt PREVIEW (which is what marks the
  /// Desynth stage done), so a dead coffer loop never lies to the deck about the melt.
  /// It narrates its own death and hands control on to the melt regardless.
  /// </summary>
  internal int AbortEpoch { get; private set; }

  internal CofferOrchestrator()
  {
    _taskManager = new TaskManager
    {
      TimeLimitMS = 15000,
      AbortOnTimeout = true,
    };
  }

  public void Dispose()
  {
    _taskManager.Abort();
    Svc.Framework.Update -= WatchdogTick;
  }

  /// <summary>
  /// The sweep entry point. Opens ALL Venture Coffers in the bags, then invokes
  /// <paramref name="onComplete"/> exactly once - whether it opened many, none, or
  /// died mid-loop - so the melt always proceeds after the coffers (or after the
  /// silent no-op when the config is off / there are no coffers / a run is busy).
  /// The onComplete carries the melt's own fire (open the salvage preview), so the
  /// coffers genuinely precede it and their contents are in the bags before the
  /// melt window scans.
  /// </summary>
  internal unsafe void OpenAllForSweep(Action onComplete)
  {
    // Config escape hatch (default on). Off -> the rider is inert; melt proceeds.
    if (!Plugin.Configuration.OpenVentureCoffers)
    {
      onComplete();
      return;
    }

    if (IsRunning || _taskManager.IsBusy)
    {
      // Something is already running; don't stack. Hand straight to the melt.
      onComplete();
      return;
    }

    int cofferQty = CountCoffers();
    if (cofferQty == 0)
    {
      onComplete(); // nothing to open - stay silent, proceed to melt
      return;
    }

    // Spine: refuse loudly if occupied, then let the melt path handle its own gate.
    var eval = SpineEvaluator.Evaluate(OpenExpected, ReadOpenState());
    if (!eval.CanFire)
    {
      Svc.Chat.PrintError($"[Scrooge] {eval.Message} Coffers left unopened; the melt continues.");
      onComplete();
      return;
    }

    // Free-slot guard: opening against a near-full inventory risks a lost item or a
    // stuck state (mirrors the melt's MinFreeInventorySlots precedent).
    int freeSlots = CountFreeInventorySlots();
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

    IsRunning = true;
    Plugin.CurrentRun = new RunData { Mode = RunMode.Coffer };
    Plugin.PinchRunLog.StartNewRun(isCofferRun: true);
    Plugin.PinchRunLog.SetCurrentRetainer("Venture Coffers");
    Plugin.PinchRunLog.SetTotalItems(cofferQty);

    Svc.Framework.Update -= WatchdogTick; // defensive: never double-subscribe
    Svc.Framework.Update += WatchdogTick;

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

    int cofferQty = CountCoffers();
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

    int freeSlots = CountFreeInventorySlots();
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
      if (CountCoffers() < cofferQtyBefore) return true; // open confirmed
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
    Plugin.PinchRunLog.IncrementProcessed();

    var after = SnapshotBags();
    var gained = CofferLogic.NewItems(before, after);

    if (gained.Count == 1)
    {
      var y = gained[0];
      var name = GilTracker.GetItemName(y.ItemId);
      var qty = y.Qty > 1 ? $"{y.Qty}x " : "";
      Plugin.PinchRunLog.AddEntry(ItemOutcome.Unlocked, Format.Hq(name, y.IsHq),
        $"unlocked {qty}from a Venture Coffer");
    }
    else if (gained.Count > 1)
    {
      // More than one new item appeared in the window (a stacked yield, or two
      // things landing together) - name them all, honestly, on one entry.
      var parts = new List<string>();
      foreach (var y in gained)
        parts.Add($"{(y.Qty > 1 ? y.Qty + "x " : "")}{Format.Hq(GilTracker.GetItemName(y.ItemId), y.IsHq)}");
      Plugin.PinchRunLog.AddEntry(ItemOutcome.Unlocked, "Venture Coffer",
        "unlocked " + string.Join(", ", parts));
    }
    else
    {
      // Yield not seen in time - honest per-open line; the end summary catches it.
      Plugin.PinchRunLog.AddEntry(ItemOutcome.Unlocked, "Venture Coffer",
        "opened (yield not detected in time - see the run summary)");
    }
  }

  /// <summary>
  /// Normal end: the coffers are gone. Adds the rider-wide "N coffers -> N items"
  /// summary (the set-diff backstop over the whole run) and closes the run log.
  /// </summary>
  private void EndRider()
  {
    var after = SnapshotBags();
    var allNew = CofferLogic.NewItems(_riderBefore, after);
    int itemCount = 0;
    foreach (var y in allNew) itemCount += y.Qty;

    Plugin.CurrentRun?.AddRunEntry(RunEvent.Summary,
      $"{_opened} coffer{(_opened == 1 ? "" : "s")} -> {itemCount} item{(itemCount == 1 ? "" : "s")} unlocked");

    Svc.Framework.Update -= WatchdogTick;
    Plugin.PinchRunLog.EndRun();
    Plugin.CurrentRun = null;
    IsRunning = false;
    Svc.Chat.Print($"[Scrooge] Opened {_opened} Venture Coffer{(_opened == 1 ? "" : "s")}.");

    Handoff();
  }

  /// <summary>
  /// The ONE way the rider dies early: every abort path funnels here so the run log
  /// is cancelled, the busy flag drops, the epoch bumps, and - crucially - the melt
  /// still gets its handoff. A dead coffer loop must not swallow the sweep.
  /// </summary>
  private void CloseRiderAborted(string reason)
  {
    Svc.Framework.Update -= WatchdogTick;
    IsRunning = false;
    Plugin.PinchRunLog.CancelRun();
    Plugin.CurrentRun = null;
    AbortEpoch++;
    _ = reason;
    Handoff();
  }

  /// <summary>Invokes the stored melt continuation exactly once.</summary>
  private void Handoff()
  {
    var cont = _onComplete;
    _onComplete = null;
    cont?.Invoke();
  }

  /// <summary>
  /// Backstop against the ECommons TaskManager clearing its queue on TimeLimitMS
  /// without telling us (the desynth watchdog's twin): a live rider's queue is never
  /// empty, so IsRunning &amp;&amp; !IsBusy means the queue died. Still hands off to the melt.
  /// </summary>
  private void WatchdogTick(Dalamud.Plugin.Services.IFramework _)
  {
    if (!IsRunning)
    {
      Svc.Framework.Update -= WatchdogTick;
      return;
    }
    if (_taskManager.IsBusy) return;

    Svc.Chat.PrintError(
      "[Scrooge] Coffer rider stalled (task queue died) - stopping. Any coffers left are untouched; open them by hand or re-run the sweep.");
    CloseRiderAborted("watchdog: task queue died");
  }

  private int Jitter(int baseMs, int band)
  {
    var offset = (int)(((_random.NextDouble() * 2.0) - 1.0) * band);
    return Math.Max(1, baseMs + offset);
  }

  // --- Bag helpers (the four main inventory pages, mirroring the melt's scan) ---

  private static readonly InventoryType[] MainBags =
  {
    InventoryType.Inventory1,
    InventoryType.Inventory2,
    InventoryType.Inventory3,
    InventoryType.Inventory4,
  };

  /// <summary>Total quantity of Venture Coffers across the four main bags.</summary>
  private static unsafe int CountCoffers()
  {
    var im = InventoryManager.Instance();
    if (im == null) return 0;
    int total = 0;
    foreach (var ct in MainBags)
    {
      var c = im->GetInventoryContainer(ct);
      if (c == null) continue;
      for (int i = 0; i < c->Size; i++)
      {
        var s = c->GetInventorySlot(i);
        if (s != null && CofferLogic.IsVentureCoffer(s->ItemId))
          total += s->Quantity;
      }
    }
    return total;
  }

  /// <summary>
  /// Snapshots the bags as (itemId, HQ) -&gt; total quantity, for the set-diff
  /// attribution. Only the four main pages - coffer yields land here.
  /// </summary>
  private static unsafe Dictionary<(uint ItemId, bool Hq), int> SnapshotBags()
  {
    var map = new Dictionary<(uint, bool), int>();
    var im = InventoryManager.Instance();
    if (im == null) return map;
    foreach (var ct in MainBags)
    {
      var c = im->GetInventoryContainer(ct);
      if (c == null) continue;
      for (int i = 0; i < c->Size; i++)
      {
        var s = c->GetInventorySlot(i);
        if (s == null || s->ItemId == 0) continue;
        bool hq = (s->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
        var key = (s->ItemId, hq);
        map.TryGetValue(key, out var have);
        map[key] = have + (int)s->Quantity;
      }
    }
    return map;
  }

  /// <summary>Counts empty slots across the four main bags (the melt's guard, mirrored).</summary>
  private static unsafe int CountFreeInventorySlots()
  {
    var im = InventoryManager.Instance();
    if (im == null) return 0;
    int free = 0;
    foreach (var ct in MainBags)
    {
      var c = im->GetInventoryContainer(ct);
      if (c == null) continue;
      for (int i = 0; i < c->Size; i++)
      {
        var s = c->GetInventorySlot(i);
        if (s != null && s->ItemId == 0) free++;
      }
    }
    return free;
  }
}
