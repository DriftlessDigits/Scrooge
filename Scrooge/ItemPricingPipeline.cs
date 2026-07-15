using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;
using System;
using System.Collections.Generic;
using System.Linq;
using static ECommons.UIHelpers.AtkReaderImplementations.ReaderContextMenu;

namespace Scrooge;

/// <summary>
/// Handles per-item pricing: context menu interaction, MB price lookup,
/// price validation, and price application. Per-item state lives on
/// PricingItem (sole source of truth). Price cache lives on RunData.
/// </summary>
internal sealed class ItemPricingPipeline : IDisposable
{
  private readonly TaskManager _taskManager;
  private readonly MarketBoardHandler _mbHandler;
  private readonly Func<int, int> _applyJitter;

  // Fallback for PostPinch hotkey path (no CurrentRun/CurrentItem)
  private int? _hotKeyPrice;

  // Per-run cache lives on RunData — accessor for convenience
  // Outside a run (e.g., PostPinch hotkey), cache writes are silently discarded
  private Dictionary<string, int?> _cachedPrices => Plugin.CurrentRun?.CachedPrices ?? _noRunCacheStub;
  private readonly Dictionary<string, int?> _noRunCacheStub = [];

  /// <summary>Current run mode. Reads from Plugin.CurrentRun.</summary>
  private bool IsPinchRun => Plugin.CurrentRun?.Mode == RunMode.Pinch;

  /// <summary>Current run mode. Reads from Plugin.CurrentRun.</summary>
  private bool IsHawkRun => Plugin.CurrentRun?.Mode == RunMode.Hawk;

  internal ItemPricingPipeline(TaskManager taskManager, Func<int, int> applyJitter)
  {
    _taskManager = taskManager;
    _applyJitter = applyJitter;
    _mbHandler = new MarketBoardHandler();
    _mbHandler.NewPriceReceived += OnNewPriceReceived;
  }

  public void Dispose()
  {
    _mbHandler.NewPriceReceived -= OnNewPriceReceived;
    _mbHandler.Dispose();
  }

  /// <summary>Clears per-item and per-run state. Called at the start of each run.</summary>
  internal void ClearState()
  {
    _hotKeyPrice = null;
    SlowMoverPressure.ResetRun();
  }

  /// <summary>Clears the cached price lookup table. Called when price floor settings change.</summary>
  internal void ClearCachedPrices() => Plugin.CurrentRun?.CachedPrices.Clear();

  // --- Pinch run item interaction ---

  /// <summary>Clicks "Adjust Price" in the retainer sell list context menu. Detects mannequin items.</summary>
  internal unsafe bool? ClickAdjustPrice()
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      var reader = new ReaderContextMenu(addon);
      if (!GameNavigation.IsItemMannequin(reader.Entries))
      {
        Svc.Log.Debug($"Clicking adjust price");
        ECommons.Automation.Callback.Fire(addon, true, 0, 0, 0, 0, 0); // click adjust price
      }
      else
      {
        Svc.Log.Debug("Current item is a mannequin item and will be skipped");
        var currentItem = Plugin.CurrentRun?.CurrentItem;
        if (currentItem != null) currentItem.Result = PricingResult.Skipped;
        addon->Close(true);
      }

      return true;
    }

    return false;
  }

  // --- Hawk run item interaction ---

  /// <summary>
  /// Right-clicks an item in the player's inventory to open the context menu.
  /// Uses AgentInventoryContext to open the context menu for a specific slot.
  /// </summary>
  internal unsafe bool? ClickInventoryItem(HawkWindow.HawkItem hawkItem)
  {
    // Safety check: verify the item is still in the expected slot
    var im = InventoryManager.Instance();
    var container = im == null ? null : im->GetInventoryContainer(hawkItem.Container);
    if (container == null) return true;

    var slot = container->GetInventorySlot(hawkItem.SlotIndex);
    if (slot == null || slot->ItemId != hawkItem.ItemId)
    {
      Svc.Log.Warning($"[HawkRun] {hawkItem.Name} no longer at expected slot — skipping");
      var currentItem = Plugin.CurrentRun?.CurrentItem;
      if (currentItem != null) currentItem.Result = PricingResult.Skipped;
      return true;
    }

    var agent = AgentInventoryContext.Instance();
    var addonId = AgentInventory.Instance()->OpenAddonId;
    agent->OpenForItemSlot(hawkItem.Container, hawkItem.SlotIndex, 0, addonId);

    return true;
  }

  /// <summary>
  /// Clicks "Put Up for Sale" in the inventory context menu.
  /// If the option is missing, the sell list may be full or the item is bound.
  /// </summary>
  internal unsafe bool? ClickPutUpForSale()
  {
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return true;

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      var reader = new ReaderContextMenu(addon);

      for (int i = 0; i < reader.Entries.Count; i++)
      {
        var name = reader.Entries[i].Name;
        if (name.Equals("Put Up for Sale", StringComparison.OrdinalIgnoreCase))
        {
          ECommons.Automation.Callback.Fire(addon, true, 0, i, 0, 0, 0);
          return true;
        }
      }

      // "Put Up for Sale" not found — sell list full or item is bound
      Svc.Log.Warning("[HawkRun] 'Put Up for Sale' not in context menu — sell list may be full or item is bound");
      var currentItem = Plugin.CurrentRun?.CurrentItem;
      if (currentItem != null) currentItem.Result = PricingResult.Skipped;
      addon->Close(true);
      return true;
    }
    return false;
  }

  /// <summary>
  /// Clicks "Have Retainer Sell Items" in the inventory context menu.
  /// Vendor-sells the item through the retainer at vendor price.
  /// </summary>
  internal unsafe bool? ClickHaveRetainerSellItems()
  {
    if (Plugin.CurrentRun?.CurrentItem?.Result == PricingResult.Skipped)
      return true;

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon)
        && GenericHelpers.IsAddonReady(addon))
    {
      var reader = new ReaderContextMenu(addon);

      for (int i = 0; i < reader.Entries.Count; i++)
      {
        var name = reader.Entries[i].Name;
        // NOTE: English-only text match. Non-English clients will not match
        // and the item will be skipped. Acceptable for now — Scrooge targets EN.
        if (name.Equals("Have Retainer Sell Items", StringComparison.OrdinalIgnoreCase))
        {
          ECommons.Automation.Callback.Fire(addon, true, 0, i, 0, 0, 0);
          return true;
        }
      }

      // Option not found — item may not be vendorable
      Svc.Log.Warning("[HawkRun] 'Have Retainer Sell Items' not in context menu");
      var currentItem = Plugin.CurrentRun?.CurrentItem;
      if (currentItem != null) currentItem.Result = PricingResult.Skipped;
      addon->Close(true);
      return true;
    }
    return false;
  }

  // --- Market board & pricing ---

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
      if (!_cachedPrices.TryGetValue(itemName, out int? value) || value <= 0)
      {
        Svc.Log.Debug($"{itemName} has no cached price (or that price was <= 0), delaying next mb open");
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
      if (_cachedPrices.TryGetValue(itemName, out int? value) && value > 0)
      {
        Svc.Log.Debug($"{itemName}: using cached price");
        // Cache hit skips the MB query entirely — MBHandler never fires and the
        // lane block skips (the item was lane-decided when first priced this run).
        var currentItem = Plugin.CurrentRun?.CurrentItem;
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

  // --- Market-board await + retry ---
  // The MB response lands via an async offerings event that fills the item's
  // first-pass price/result. Instead of one flat keep-open delay (which lands
  // in Held/lane_held the instant the board never arrives), we poll for the
  // response across escalating windows, re-firing the request between them.
  // Each window is its own enqueued task so no single poll exceeds the task
  // manager's per-task TimeLimitMS; a window early-outs the moment data lands,
  // so a responsive item pays ~zero extra time. Exhaustion (no response after
  // all windows) marks MbTimedOut so SetNewPrice holds honestly.

  /// <summary>Last retry window index (0 = initial + 3 retries = windows 0..3).</summary>
  internal const int MbLastWindow = 3;

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
    if (MbResponded(item))
      return true;

    var now = DateTime.UtcNow;
    if (item.MbAwaitDeadline == DateTime.MinValue)
    {
      // Arm this window. Window 0's request was already fired by
      // ClickComparePrice; retry windows re-fire it (best effort — if the
      // sell addon isn't ready ClickComparePrice no-ops and we still wait).
      if (window > 0)
        ClickComparePrice();
      // Clamp under the task manager's TimeLimitMS so a jittered window never
      // trips AbortOnTimeout.
      var ms = Math.Min(_applyJitter(MbWindowMs(window)), 9500);
      item.MbAwaitDeadline = now.AddMilliseconds(ms);
      return false;
    }

    if (now < item.MbAwaitDeadline)
      return false;

    // Window elapsed with no response. Disarm for the next window.
    item.MbAwaitDeadline = DateTime.MinValue;
    item.MbAttempts = window + 1;
    if (window >= MbLastWindow)
      item.MbTimedOut = true;
    return true;
  }

  /// <summary>
  /// Final step: applies the calculated price to the listing.
  /// Orchestrates addon reading, price evaluation, and confirm/cancel.
  /// Business logic lives in ApplyPriceDecision.
  /// </summary>
  internal unsafe bool? SetNewPrice()
  {
    var currentItem = Plugin.CurrentRun?.CurrentItem;
    ItemPayload? itemPayload = null;
    var listingQuantity = 1;

    try
    {
      if (currentItem?.Result == PricingResult.Skipped)
        return true;

      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var searchAddon))
        searchAddon->Close(true);

      if (!GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var retainerSell)
          || !GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
        return false;

      var itemName = PopulateItemFromAddon(retainerSell, currentItem, out itemPayload, out listingQuantity);

      // Always populate 14-day history stats for triage context
      // Must run after PopulateItemFromAddon which sets ItemId
      if (currentItem != null)
        _mbHandler.PopulateHistoryStats(currentItem);

      // Ban check — item goes through MB lookup but price is never changed
      if (itemPayload != null)
      {
        var banId = itemPayload.IsHQ ? itemPayload.ItemId + 1_000_000u : itemPayload.ItemId;
        if (Plugin.Configuration.BannedItemIds.Contains(banId))
        {
          var cleanName = Communicator.CleanItemName(itemName, out _);
          var listed = currentItem?.CurrentListingPrice;
          var mb = currentItem?.MbPrice;
          var detail = mb.HasValue
            ? $"Listed at {listed:N0}, MB at {mb:N0}"
            : $"Listed at {listed:N0}, no MB data";
          Plugin.PinchRunLog?.AddEntry(ItemOutcome.Banned, cleanName, detail);
          if (currentItem != null) currentItem.Result = PricingResult.Banned;

          // Confirm without changing (keeps original price)
          ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 0);
          retainerSell->AtkUnitBase.Close(true);
          return true;
        }
      }

      // --- Lane decision: the pricing spine ---
      // "Listings are what people want; sales are what people paid." The lane
      // (recency-weighted clearing price from the MB history packet) is the
      // model; the board is positioning only. ONE decision function, both
      // pricing paths — safety references are lane-relative, so the fresh
      // Hawk listing door gets the same protection as the reprice door (the
      // fence was written through the door with different rules).
      // Runs for triage-reprice bypass items too: bypass skips HOLDS and caps,
      // never the anchor choice — a bypassed reprice must not price off bait
      // or a wall (the old triage-Reprc-off-the-fence disease).
      var laneBoardCount = -1; // -1 = lane block didn't run
      if (currentItem != null
          && currentItem.ItemId != 0
          && !currentItem.FromPriceCache)
      {
        var laneCfg = new LaneConfig
        {
          FloorPct = Plugin.Configuration.LaneFloorPct,
          CeilingMult = Plugin.Configuration.UpwardRepriceMultiplier,
          OwnedMult = Plugin.Configuration.LaneOwnedMultiplier,
          MinHistorySamples = Plugin.Configuration.LaneMinHistorySamples,
          HalfLifeDays = LaneHalfLife.Resolve(currentItem.ItemId),
        };
        var hqPricing = Plugin.Configuration.HQ && currentItem.IsHq;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sales = _mbHandler.GetLaneSales(currentItem.ItemId);
        var lane = LanePricing.BuildLane(sales, hqPricing, laneCfg, now);

        // Community fallback (lane design call #6): when the LOCAL lane is too
        // thin to price, consult Universalis DC-scope SALE history as a labeled
        // community lane before holding. Never blocks the pinch — a cache hit
        // upgrades the lane this pass; a miss queues the fetch (warm next pass)
        // and we hold-and-flag. Stale/absent community data = silent (TryGet
        // returns null). Foreign listings never price anything, ever.
        if (lane == null || lane.SampleCount < laneCfg.MinHistorySamples)
        {
          var community = UniversalisHistory.TryGet(currentItem.ItemId);
          if (community != null)
          {
            var communityLane = LanePricing.BuildLane(community, hqPricing, laneCfg, now, LaneSource.Community);
            if (communityLane != null && communityLane.SampleCount >= laneCfg.MinHistorySamples)
              lane = communityLane;
          }
          else
          {
            currentItem.CommunityQueued = true; // miss queued a fetch; warm next pinch
          }
        }

        var board = _mbHandler.GetBoard(currentItem.ItemId, hqPricing);
        laneBoardCount = board.Count;
        var velocity = _mbHandler.GetPacketVelocityPerDay(currentItem.ItemId)
          ?? UniversalisStats.TryGet(currentItem.ItemId, currentItem.IsHq)?.Velocity;

        var decision = LanePricing.Decide(board, lane, velocity, laneCfg,
          currentItem.CurrentListingPrice);
        currentItem.Lane = decision;

        if (decision.Outcome == LaneOutcome.HeldThinHistory)
        {
          if (currentItem.BypassPriceGuards)
          {
            // Sam ordered this reprice; with no lane to consult, his call
            // rides the first-pass anchor. Bypass beats the hold, not the lane.
          }
          else
          {
            // No lane anywhere — never act on a guess wearing numbers. The
            // own-sales fallback below may still price a fully SILENT market;
            // otherwise the item holds and flags for Sam.
            currentItem.FinalPrice = null;
            currentItem.Result = PricingResult.LaneHeld;
          }
        }
        else if (decision.Anchor is long anchor)
        {
          var anchorInt = (int)Math.Min(anchor, int.MaxValue);
          // The lane never classifies our own listings as competition, so a
          // listing anchor is always a foreign price to undercut — an own
          // listing can no longer BE the anchor. UndercutSelf keeps its meaning
          // only in the first-pass offerings path, not lane classification.
          var price = decision.AnchorIsListing
            ? _mbHandler.ApplyUndercutMode(anchorInt, isOwnListing: false)
            : anchorInt;

          // Existing floors stay as downstream backstops (design: guards
          // survive, the lane replaces only the anchor choice).
          var floorPrice = Plugin.Configuration.PriceFloorMode == PriceFloorMode.None ? 0
            : Plugin.Configuration.PriceFloorMode == PriceFloorMode.DomanEnclave
              ? currentItem.VendorPrice * 2 : currentItem.VendorPrice;
          if (floorPrice > 0 && price < floorPrice)
          {
            currentItem.FinalPrice = null;
            currentItem.Result = PricingResult.BelowFloor;
          }
          else if (Plugin.Configuration.MinimumListingPrice > 0
                   && price < Plugin.Configuration.MinimumListingPrice)
          {
            currentItem.FinalPrice = null;
            currentItem.Result = PricingResult.BelowMinimum;
          }
          else
          {
            currentItem.FinalPrice = price;
            currentItem.Result = PricingResult.Pending;
          }
        }
      }

      // Own-sales fallback — when the market is fully silent (no listings AND
      // no usable history), the player's own last sale prices the item.
      // Staleness-gated by StalePriceDays, never discounted (sole seller =
      // premium position), always labeled in chat. Locked spec 2026-07-10.
      // A lane hold with listings PRESENT does not fall back — pricing a
      // thin-history item off one stale own sale is the convicted baseline.
      if (currentItem != null
          && currentItem.ItemId != 0
          && currentItem.Result != PricingResult.BelowMinimum
          && currentItem.Result != PricingResult.BelowFloor
          && (currentItem.Result != PricingResult.LaneHeld || laneBoardCount == 0)
          && (currentItem.FinalPrice == null || currentItem.FinalPrice <= 0))
      {
        (int Price, long Timestamp)? ownSale = null;
        try { ownSale = GilStorage.GetLastSalePriceWithTime(currentItem.ItemId, currentItem.IsHq); } catch { /* storage unavailable */ }

        if (ownSale is (int salePrice, long saleTs) && salePrice > 0)
        {
          var ageDays = (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - saleTs) / 86400);
          if (Plugin.Configuration.StalePriceDays <= 0 || ageDays <= Plugin.Configuration.StalePriceDays)
          {
            currentItem.FinalPrice = salePrice;
            currentItem.Result = PricingResult.Pending;
            Communicator.PrintOwnSalesFallback(itemName, salePrice, ageDays);
          }
        }
      }

      // Hotkey path (no CurrentItem) rides _hotKeyPrice. With an item in hand,
      // FinalPrice is authoritative — a lane hold nulls it, and falling back to
      // the first-pass _hotKeyPrice would price the held item anyway.
      var newPrice = currentItem != null ? currentItem.FinalPrice : _hotKeyPrice;
      var confirmed = ApplyPriceDecision(retainerSell, currentItem, itemName, newPrice);

      ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, confirmed ? 0 : 1);
      retainerSell->AtkUnitBase.Close(true);
      return true;
    }
    finally
    {
      var result = currentItem?.Result ?? PricingResult.Pending;

      // Track listing value for run summary. Held results (upward hold, cap
      // block...) keep the OLD price on the market — count that, never the
      // rejected reprice target (the 58M troll-wall inflation).
      var listingValue = ListingAccounting.ListedUnitValue(
        result, currentItem?.FinalPrice, currentItem?.CurrentListingPrice);
      if (listingValue > 0)
      {
        Plugin.PinchRunLog.AddListingValue(listingValue * listingQuantity);
        if (!IsHawkRun && Plugin.Configuration.EnableGilTracking && itemPayload != null)
          GilTracker.RecordFinalPrice(itemPayload.ItemId, listingValue, listingQuantity);
      }

      _hotKeyPrice = null;
      _mbHandler.ClearHistory(); // history + captured board are per-item

      // Triage collection — save skipped items for post-run review
      if (currentItem != null && RunData.IsTriageResult(result))
        Plugin.CurrentRun?.TriageItems.Add(currentItem);

      if (result != PricingResult.VendorSell)
        Plugin.PinchRunLog.IncrementProcessed();
    }
  }



  /// <summary>
  /// Reads the RetainerSell addon and populates PricingItem with identity + prices.
  /// Returns the raw item name (needed for chat messages with SeString control chars).
  /// </summary>
  private unsafe string PopulateItemFromAddon(
    AddonRetainerSell* retainerSell, PricingItem? currentItem,
    out ItemPayload? itemPayload, out int listingQuantity)
  {
    var itemName = retainerSell->ItemName->NodeText.ToString();
    var cleanName = Communicator.CleanItemName(itemName, out var isHq);
    itemPayload = Communicator.RawItemNameToItemPayload(itemName);
    if (itemPayload != null)
      GilTracker.GetItemCategory(itemPayload.ItemId);
    listingQuantity = retainerSell->AtkValues[8].Int;

    if (currentItem != null)
    {
      currentItem.ItemName = cleanName;
      currentItem.IsHq = isHq;
      currentItem.ItemId = itemPayload?.ItemId ?? 0;
      currentItem.Quantity = listingQuantity;
      currentItem.CurrentListingPrice = retainerSell->AskingPrice->Value;
      currentItem.RetainerName = Plugin.CurrentRun?.CurrentRetainer ?? "";
      if (itemPayload != null)
        currentItem.VendorPrice = (int)Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemPayload.ItemId).PriceLow;
    }

    return itemName;
  }

  /// <summary>
  /// Evaluates the price and applies it to the RetainerSell addon.
  /// Handles valid price (undercut/cap checks), error routing (floor/min/nodata),
  /// and hawk-run vendor-sell. All chat messages and log entries happen here.
  /// Returns true = confirm (price applied or kept), false = cancel (error/skip).
  /// </summary>
  /// <remarks>
  /// CapBlocked and UndercutTooDeep return true (confirm) because the addon value
  /// was never changed — confirming just keeps the old price (no-op).
  /// </remarks>
  private unsafe bool ApplyPriceDecision(
    AddonRetainerSell* retainerSell, PricingItem? currentItem,
    string itemName, int? newPrice)
  {
    var cleanName = Communicator.CleanItemName(itemName, out _);

    // --- Valid price path ---
    if (newPrice.HasValue && newPrice > 0)
    {
      if (IsHawkRun)
      {
        Svc.Log.Debug($"Setting new listing price");
        retainerSell->AskingPrice->SetValue(newPrice.Value);
        Communicator.PrintPriceUpdate(itemName, currentItem?.CurrentListingPrice, newPrice.Value, 0f);
        Plugin.PinchRunLog?.IncrementAdjusted();
        if (currentItem != null) currentItem.Result = PricingResult.Listed;
        LogLaneOutcome(currentItem, cleanName);
        // Warn-and-list died with the lane (2026-07-13): thin-history items
        // now HOLD instead of listing at an unguarded anchor, and walls are
        // ignored by construction — nothing suspicious gets listed to warn about.
      }
      else
      {
        // Slow-mover pressure (routing brain): deepen the cut for items that
        // sat listed with a live market; flag dead-market sitters to triage.
        // Runs before the cut/cap checks so guards see the final price.
        if (IsPinchRun && Plugin.Configuration.EnableRoutingBrain
            && Plugin.Configuration.SlowMoverPressureOptIn
            && currentItem != null && currentItem.ItemId != 0)
          newPrice = SlowMoverPressure.Apply(currentItem, newPrice.Value);

        var oldPrice = currentItem?.CurrentListingPrice ?? retainerSell->AskingPrice->Value;
        var cutPercentage = oldPrice > 0 ? ((float)newPrice.Value - oldPrice) / oldPrice * 100f : 0f;

        // The upward-hold guard died with the lane (2026-07-13): its
        // own-last-sale baseline was one stale data point (~half of live holds
        // were false positives), and it blocked downward deepens too. The lane
        // ceiling carries the 3x discipline with an absolute reference now.

        if (cutPercentage >= -Plugin.Configuration.MaxUndercutPercentage
            || currentItem?.BypassPriceGuards == true)
        {
          if (IsPinchRun && Plugin.Configuration.EnableMaxPriceIncreaseCap
              && cutPercentage > Plugin.Configuration.MaxPriceIncreasePercentage
              && currentItem?.BypassPriceGuards != true)
          {
            Communicator.PrintAboveMaxIncreaseError(itemName, cutPercentage);
            Plugin.PinchRunLog?.AddEntry(ItemOutcome.Skipped, cleanName,
              LaneNote.Line(cleanName, currentItem?.IsHq ?? false, "unchanged", "skip",
                $"price increase over the {Plugin.Configuration.MaxPriceIncreasePercentage}% cap."));
            if (currentItem != null) { currentItem.Result = PricingResult.CapBlocked; currentItem.PriceChangePercent = cutPercentage; }
          }
          else
          {
            Svc.Log.Debug($"Setting new price");
            _cachedPrices.TryAdd(itemName, newPrice);
            retainerSell->AskingPrice->SetValue(newPrice.Value);
            Communicator.PrintPriceUpdate(itemName, oldPrice, newPrice.Value, cutPercentage);
            Plugin.PinchRunLog?.IncrementAdjusted();
            if (currentItem != null) currentItem.Result = PricingResult.Applied;
            LogLaneOutcome(currentItem, cleanName);
          }
        }
        else
        {
          Communicator.PrintAboveMaxCutError(itemName);
          Plugin.PinchRunLog?.AddEntry(ItemOutcome.Skipped, cleanName,
            LaneNote.Line(cleanName, currentItem?.IsHq ?? false, "unchanged", "skip",
              $"undercut over the {Plugin.Configuration.MaxUndercutPercentage}% limit."));
          if (currentItem != null) { currentItem.Result = PricingResult.UndercutTooDeep; currentItem.PriceChangePercent = cutPercentage; }
        }
      }

      return true; // confirm — price was applied, or kept unchanged (cap/undercut)
    }

    // --- Error path: no valid price ---
    var result = currentItem?.Result ?? PricingResult.NoData;

    switch (result)
    {
      case PricingResult.LaneHeld:
      {
        // "Held (thin history)" — verdicts unify, execution stays local:
        // keep-price-and-flag at the pinch, don't-auto-price in a Hawk run.
        // A genuine MB no-response (all retry windows elapsed) reads
        // identically to thin history at this point; MbTimedOut distinguishes
        // it so the flag says "didn't respond" instead of "too thin".
        var timedOut = currentItem?.MbTimedOut == true;
        var thin = currentItem?.Lane?.Evidence ?? "history too thin to build a lane";
        var evidence = timedOut
          ? $"market board didn't respond ({currentItem!.MbAttempts} attempts) — held; will retry next pinch."
          : currentItem?.CommunityQueued == true
            ? $"{thin} Checking community sales history — will retry next pinch."
            : thin;
        var heldLine = LaneNote.Line(cleanName, currentItem?.IsHq ?? false,
          LaneNote.Transition(currentItem?.CurrentListingPrice, null), "thin", evidence);
        Plugin.PinchRunLog?.AddEntry(ItemOutcome.LaneHeld, cleanName, heldLine);
        Communicator.PrintLaneHeld(itemName, evidence);
        if (currentItem != null && currentItem.ItemId != 0)
        {
          try
          {
            GilStorage.UpsertTriageFlag(currentItem.ItemId, currentItem.IsHq,
              currentItem.RetainerName, currentItem.SlotIndex, "lane_held",
              timedOut ? $"Held (MB timeout) — {evidence}" : $"Held (thin history) — {evidence}",
              currentItem.CurrentListingPrice ?? 0, 0);
          }
          catch (Exception ex)
          {
            Svc.Log.Warning($"[Triage] Failed to persist lane-held flag: {ex.Message}");
          }
        }
        // Pinch: confirm keeps the old price (no-op). Hawk: cancel — never
        // list at an unguarded anchor (warn-and-list's replacement).
        return !IsHawkRun;
      }

      case PricingResult.BelowMinimum:
      case PricingResult.BelowFloor:
        if (IsHawkRun && Plugin.Configuration.AutoVendorSellOnPriceCheckFail)
        {
          if (currentItem != null) currentItem.Result = PricingResult.VendorSell;
          Svc.Log.Debug($"[HawkRun] Price check failed — will vendor-sell");
        }
        else if (result == PricingResult.BelowMinimum)
        {
          Communicator.PrintBelowMinimumListingPriceError(itemName);
          Plugin.PinchRunLog?.AddEntry(ItemOutcome.Skipped, cleanName,
            LaneNote.Line(cleanName, currentItem?.IsHq ?? false, "unchanged", "skip",
              $"below the minimum listing price ({Plugin.Configuration.MinimumListingPrice:N0} gil)."));
        }
        else
        {
          Communicator.PrintBelowPriceFloorError(itemName);
          var floorLabel = Plugin.Configuration.PriceFloorMode == PriceFloorMode.Vendor
            ? "vendor price" : "Doman Enclave price (2x vendor)";
          Plugin.PinchRunLog?.AddEntry(ItemOutcome.Skipped, cleanName,
            LaneNote.Line(cleanName, currentItem?.IsHq ?? false, "unchanged", "skip", $"below {floorLabel}."));
        }
        break;

      default:
        Svc.Log.Warning("SetNewPrice: No price to set");
        Communicator.PrintNoPriceToSetError(itemName);
        Plugin.PinchRunLog?.AddEntry(ItemOutcome.NoData, cleanName, "No market board data");
        if (currentItem != null) currentItem.Result = PricingResult.NoData;
        break;
    }

    return false; // cancel
  }

  /// <summary>
  /// One run-log line per non-routine lane outcome, named with its evidence —
  /// never a generic costume (disguised lines make features look unbuilt, and
  /// these lines double as calibration data). InLane stays quiet: that is the
  /// ordinary adjusted path the run log already counts.
  /// </summary>
  private static void LogLaneOutcome(PricingItem? item, string cleanName)
  {
    if (item?.Lane is not { } lane)
      return;

    var outcome = lane.Outcome switch
    {
      LaneOutcome.WallIgnored => ItemOutcome.WallIgnored,
      LaneOutcome.BaitIgnored => ItemOutcome.BaitIgnored,
      LaneOutcome.LaneOwned => ItemOutcome.LaneOwned,
      LaneOutcome.RaceDeclined => ItemOutcome.RaceDeclined,
      _ => (ItemOutcome?)null,
    };
    if (outcome is { } o)
    {
      // Line assembled here, where the final applied price is known: the
      // outcome is what actually happened to the listing, not the mid-decision
      // fact (a wall being ignored is folded into the reason, not the verdict).
      var transition = LaneNote.Transition(item.CurrentListingPrice, item.FinalPrice);
      var line = LaneNote.Line(cleanName, item.IsHq, transition, LaneNote.Tag(lane.Outcome), lane.Evidence);
      Plugin.PinchRunLog?.AddEntry(o, cleanName, line);
    }
  }

  private void OnNewPriceReceived(object? sender, NewPriceEventArgs e)
  {
    Svc.Log.Debug($"New price received: {e.NewPrice}");

    // Always capture for hotkey path (no CurrentRun/CurrentItem)
    _hotKeyPrice = e.NewPrice > 0 ? e.NewPrice : null;

    var item = Plugin.CurrentRun?.CurrentItem;
    if (item != null)
    {
      item.MbPrice = _mbHandler.LastCheckedPrice > 0 ? _mbHandler.LastCheckedPrice : null;

      if (e.NewPrice > 0)
      {
        item.FinalPrice = e.NewPrice;
      }
      else if (e.NewPrice == -2)
        item.Result = PricingResult.BelowFloor;
      else if (e.NewPrice == -3)
        item.Result = PricingResult.BelowMinimum;
      else
        item.Result = PricingResult.NoData;
    }
  }
}
