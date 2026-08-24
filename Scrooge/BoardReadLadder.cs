using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE BOARD READ, as one chain: ask, then wait for the WHOLE board.
///
/// <para><b>Every door that DECIDES A PRICE off a board read comes through here</b>
/// (ruled 2026-08-10, folded into unit 3; the last two hand-rolled spellings were
/// folded in on 2026-08-16). Two callers still do not: the pinch's posted-item re-read
/// flat-waits, and StandingOrchestrator's reprice leg flat-waits on
/// <c>MarketBoardKeepOpenMS</c>. Those are a real gap and they are named here rather
/// than papered over. Until this ladder existed the bell run was the last flat-window
/// door on the decision path: it fired Compare Prices, waited a single jittered
/// <c>MarketBoardKeepOpenMS</c>, and priced on whatever had landed - with no completion
/// check of any kind. That is the censored-read class the x-of-y work fixed for the
/// pinch on 08-02, where 949 of 1,744 receipts had been deciding at depth exactly 10
/// (the page boundary) while further pages for the same board were milliseconds
/// out.</para>
///
/// <para><b>What the censoring actually cost the bell, said honestly.</b> Packets
/// arrive cheapest-first, so the undercut ANCHOR - the front of the line - was
/// usually already in hand when the flat window expired, and most listings landed
/// at a defensible price. What a page-1 read cannot see is DEPTH CONTEXT: how many
/// competitors stand behind the anchor, whether the cheap rows are a wall or a lone
/// crasher, how wide the foreign spread runs. Those are the operands the crasher
/// step, the outnumbering test and the cluster spans are built from - so under
/// server lag the bell was not usually mispricing, it was deciding those questions
/// on a tenth of the evidence, silently, and banking receipts that claimed
/// otherwise.</para>
///
/// <para>The ladder early-outs the instant the board completes, so a responsive
/// item pays approximately nothing for the change: window 0 IS the old flat
/// keep-open wait, jittered exactly as before. Only a slow board spends the 3s/5s/
/// 10s escalation, and only a genuinely silent one spends all of it.</para>
///
/// <para><b>The single enqueue point.</b> The pinch used to spell the same three steps
/// plus four windows twice more (its enqueue path and its insert-at-front path), which
/// is three copies of one chain and three places for a window count to drift. Both now
/// call <see cref="EnqueueBoardRead(Labels)"/> / <see cref="InsertBoardRead"/> with
/// their own <see cref="Labels"/>, because the only thing that ever differed between
/// the copies was what the task manager calls the steps.</para>
/// </summary>
internal sealed class BoardReadLadder
{
  private readonly TaskManager _taskManager;
  private readonly Func<int, int> _applyJitter;
  private readonly MarketBoardHandler _mbHandler;
  private readonly Func<Dictionary<string, int?>> _cachedPricesAccessor;

  /// <summary>
  /// The per-run price cache, resolved through the pipeline that owns it (run cache or
  /// the runless hotkey stub). Reached through a delegate rather than copied because
  /// which dictionary is live changes with the run, and two readers of "the cache"
  /// disagreeing about which one is live is the bug the accessor exists to prevent.
  /// </summary>
  private Dictionary<string, int?> CachedPrices => _cachedPricesAccessor();

  internal BoardReadLadder(
    TaskManager taskManager, Func<int, int> applyJitter, MarketBoardHandler mbHandler,
    Func<Dictionary<string, int?>> cachedPricesAccessor)
  {
    _taskManager = taskManager;
    _applyJitter = applyJitter;
    _mbHandler = mbHandler;
    _cachedPricesAccessor = cachedPricesAccessor;
  }

  /// <summary>Last retry window index (0 = initial + 3 retries = windows 0..3).</summary>
  internal const int MbLastWindow = 3;

  /// <summary>
  /// What the task manager calls the three steps and the four windows, for ONE caller.
  ///
  /// <para>The only thing that ever differed between the hand-rolled copies of this
  /// ladder. The names are load-bearing - they are what a stuck queue is read by in the
  /// log - so they are reproduced byte for byte rather than unified: the orchestrators
  /// name by run tag and item, the pinch names by slot index.</para>
  /// </summary>
  internal readonly record struct Labels(string Delay, string Compare, string AwaitPrefix)
  {
    /// <summary>The orchestrators' naming: run tag + item name.</summary>
    internal static Labels ForRun(string runTag, string itemName)
      => new($"{runTag}DelayMB_{itemName}", $"{runTag}ComparePrice_{itemName}", $"{runTag}AwaitMB_{itemName}");

    /// <summary>The pinch's naming: the item's slot index in the sell list.</summary>
    internal static Labels ForIndex(int index)
      => new($"DelayMB{index}", $"ClickComparePrice{index}", $"AwaitMB{index}");

    internal string Await(int window) => $"{AwaitPrefix}_{window}";
  }

  /// <summary>Appends the whole chain to the back of the queue, named by run tag.</summary>
  internal void EnqueueBoardRead(string runTag, string itemName)
    => EnqueueBoardRead(Labels.ForRun(runTag, itemName));

  /// <summary>Appends the whole chain to the back of the queue.</summary>
  internal void EnqueueBoardRead(Labels labels)
  {
    _taskManager.Enqueue(DelayMarketBoard, labels.Delay);
    _taskManager.Enqueue(ClickComparePrice, labels.Compare);
    for (int w = 0; w <= MbLastWindow; w++)
    {
      var window = w;
      _taskManager.Enqueue(() => AwaitMarketBoardWindow(window), labels.Await(window));
    }
  }

  /// <summary>
  /// The same chain, pushed to the FRONT of the queue. Insert prepends, so the steps go
  /// in reverse and come out in the order <see cref="EnqueueBoardRead(Labels)"/> lays
  /// them: DelayMB -> ClickComparePrice -> AwaitMB 0..3.
  /// </summary>
  internal void InsertBoardRead(Labels labels)
  {
    for (int w = MbLastWindow; w >= 0; w--)
    {
      var window = w;
      _taskManager.Insert(() => AwaitMarketBoardWindow(window), labels.Await(window));
    }
    _taskManager.Insert(ClickComparePrice, labels.Compare);
    _taskManager.Insert(DelayMarketBoard, labels.Delay);
  }

  /// <summary>
  /// Conditionally adds a delay before opening the MB. If we already have a
  /// cached price for this item, skip the delay (and the MB query entirely).
  /// </summary>
  internal unsafe bool? DelayMarketBoard()
  {
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return true;

    if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
    {
      var itemName = addon->ItemName->NodeText.ToString();
      if (!CachedPrices.TryGetValue(itemName, out int? value) || value <= 0)
      {
        // Clean name for the log only - the cache key keeps the raw addon
        // string, which is what the writer used (strings pass, 08-02: raw
        // SeString bytes printed as "H%I&Kudzu ThreadIH").
        Svc.Log.Debug($"{Communicator.CleanItemName(itemName, out _)} has no cached price (or that price was <= 0), delaying next mb open");
        _taskManager.InsertDelayNext(_applyJitter(Plugin.Configuration.GetMBPricesDelayMS));
      }

      return true;
    }

    return false;
  }

  /// <summary>
  /// Opens the "Compare Prices" MB window — unless we have a cached price,
  /// in which case we skip the MB entirely and use the cache.
  /// </summary>
  internal unsafe bool? ClickComparePrice()
  {
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return true;

    if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
    {
      var itemName = addon->ItemName->NodeText.ToString();
      // The pinch inits its item with only a slot index; identity used to land
      // at SetNewPrice - AFTER the await windows that need it. The proxy read
      // verifies the board against the item UNDER PRICING, so an unstamped
      // item read as "pricing 0" and refused every board it was owed (live
      // 08-03, first post-reload pinch: 144 refusals, 35 false-censored
      // decides). The compare click IS the ask - stamp identity here, where
      // the addon is in hand. Idempotent: retry windows re-enter this method.
      var currentItem = Plugin.CurrentRun?.CurrentItem;
      if (currentItem != null && currentItem.ItemId == 0)
        currentItem.ItemId = Communicator.RawItemNameToItemPayload(itemName)?.ItemId ?? 0;
      if (CachedPrices.TryGetValue(itemName, out int? value) && value > 0)
      {
        Svc.Log.Debug($"{itemName}: using cached price");
        // Cache hit skips the MB query entirely — MBHandler never fires and the
        // lane block skips (the item was lane-decided when first priced this run).
        if (currentItem != null)
        {
          currentItem.FinalPrice = value;
          currentItem.FromPriceCache = true;
        }
        return true;
      }
      else
      {
        Svc.Log.Debug($"Clicking compare prices");
        ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, true, 4);
        return true;
      }
    }

    return false;
  }

  /// <summary>Per-window wait budget in ms. Window 0 is the initial keep-open; retries escalate 3s/5s/10s.</summary>
  private static int MbWindowMs(int window) => window switch
  {
    0 => Plugin.Configuration.MarketBoardKeepOpenMS,
    1 => 3000,
    2 => 5000,
    _ => 10000,
  };

  private static bool MbResponded(PricingItem item)
    => item.FinalPrice is > 0 || item.Result != PricingResult.Pending;

  /// <summary>
  /// Polls one MB await window. Returns true to advance (response arrived, or
  /// this window's budget elapsed), false to keep waiting this window. On the
  /// first tick of a retry window it re-fires the price request; on the final
  /// window's exhaustion it sets MbTimedOut. Never returns null — a null would
  /// abort the whole task queue.
  /// </summary>
  internal bool? AwaitMarketBoardWindow(int window)
  {
    var item = Plugin.CurrentRun?.CurrentItem;
    if (item == null || item.Result == PricingResult.Skipped)
      return true;
    // THE WHOLE BOARD, not the first answer (Drift, 08-02: "I'd rather have all
    // of the data before making a decision"). A first-pass response used to end
    // the wait at the first valid batch - the page boundary - which is how 949
    // of 1,744 receipts decided at depth exactly 10 while pages for the same
    // board were still milliseconds out. Now the wait also holds until the
    // captured board matches the proxy's total; an unknown total reads as
    // complete, so the pre-proxy behavior is the fallback, never a stall.
    if (MbResponded(item) && _mbHandler.CurrentBoardComplete)
      return true;

    var now = DateTime.UtcNow;
    if (item.MbAwaitDeadline == DateTime.MinValue)
    {
      // Arm this window. Two different moves, matched to what is missing
      // (proved live 08-02 in every wrong configuration first):
      //  - No response at all: re-fire the compare click, the original
      //    silence insurance (best effort — if the sell addon isn't ready it
      //    no-ops and we still wait).
      //  - Responded but incomplete: READ, never request. The offerings
      //    packet carries only page 1 and every re-request we tried just
      //    ordered another copy; the game's own proxy holds the board's full
      //    listing array, so the missing rows - if the client has them - are
      //    already in memory. Zero server traffic (Drift: "I want a decision
      //    based on full data" + "horrified that we've been making nonsense
      //    page 1 requests").
      if (window > 0 && !MbResponded(item))
        ClickComparePrice();
      else if (MbResponded(item) && !_mbHandler.CurrentBoardComplete)
        _mbHandler.TryCompleteFromProxy(item.ItemId);
      item.MbDepthAtArm = _mbHandler.CurrentBoardDepth;
      // Clamp under the task manager's TimeLimitMS so a jittered window never
      // trips AbortOnTimeout.
      var ms = Math.Min(_applyJitter(MbWindowMs(window)), 9500);
      item.MbAwaitDeadline = now.AddMilliseconds(ms);
      return false;
    }

    if (now < item.MbAwaitDeadline)
      return false;

    // Window elapsed. A window that made PROGRESS (pages landed since it was
    // armed) re-arms itself instead of advancing - a 45-row board needs four
    // page requests, and burning a fixed window per page would exhaust the
    // budget mid-board. Only a silent window spends its slot.
    if (_mbHandler.CurrentBoardDepth > item.MbDepthAtArm && !_mbHandler.CurrentBoardComplete)
    {
      item.MbAwaitDeadline = DateTime.MinValue;
      return false;
    }

    // Disarm for the next window.
    item.MbAwaitDeadline = DateTime.MinValue;
    item.MbAttempts = window + 1;
    if (window >= MbLastWindow)
    {
      // Exhaustion splits two honest fates: never answered = the hold path
      // (MbTimedOut, as ever); answered-but-incomplete = decide on what we
      // have, censored exactly as every pre-proxy decision was - and say so.
      if (!MbResponded(item))
        item.MbTimedOut = true;
      else if (!_mbHandler.CurrentBoardComplete)
        Svc.Log.Debug($"[Board] x-of-y: deciding censored - windows exhausted before the full board arrived");
    }
    return true;
  }
}
