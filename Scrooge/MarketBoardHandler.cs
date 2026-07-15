using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Network.Structures;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Listens for MB price data and calculates the undercut price.
/// Fires <see cref="NewPriceReceived"/> with the result, which AutoPinch consumes.
///
/// Sentinel values for NewPrice:
///   > 0  = valid price to set
///   -1   = no MB listings found (or duplicate request)
///   -2   = undercut price is below price floor (vendor or doman enclave)
///   -3   = undercut price is below minimum listing price
///
/// Multi-batch behavior:
///   The game sends multiple batches of 10 listings per MB query, each with a
///   unique RequestId. The _newRequest flag is only cleared on a SUCCESSFUL price
///   resolution (not on -1 returns). This means if batch 1 has no valid listings
///   (e.g. no HQ matches), the handler stays armed and processes batch 2, 3, etc.
///   until one succeeds. This is especially important for HQ items where the first
///   HQ listing may be well beyond position 10 (observed at position 30+).
/// </summary>
internal unsafe sealed class MarketBoardHandler : IDisposable
{
  private readonly Lumina.Excel.ExcelSheet<Item> _items;
  private bool _newRequest;        // true when we're expecting MB data
  private bool _useHq;             // should we filter for HQ listings?
  private bool _itemHq;            // is the item we're pricing actually HQ?
  private int _lastRequestId = -1; // dedup: MB sends listings in batches of 10
  private readonly Random _random = new Random();

  // --- Sale history support (v2.4) ---
  private List<IMarketBoardHistoryListing>? _lastHistory;

  // --- Board capture (lane pricing) ---
  // The board only exists inside the offerings event; the lane decision runs
  // later in SetNewPrice where history + velocity are also in hand. Batches
  // (10 listings each, ascending) accumulate here per item.
  private readonly List<(long Price, bool IsOwn, bool IsHq)> _board = [];
  private readonly HashSet<int> _boardRequestIds = [];

  /// <summary>Item ID the captured board belongs to.</summary>
  internal uint BoardItemId { get; private set; }

  /// <summary>Item ID from the last HistoryReceived event. Used to validate history is for the correct item.</summary>
  internal uint HistoryItemId { get; private set; }

  /// <summary>The calculated undercut price BEFORE floor/min sentinel conversion. Used by triage UI.</summary>
  internal int LastCheckedPrice { get; private set; }

  /// <summary>
  /// Setting NewPrice fires the event — this is the bridge to AutoPinch.
  /// </summary>
  private int NewPrice
  {
    get => _newPrice;
    set
    {
      _newPrice = value;
      NewPriceReceived?.Invoke(this, new NewPriceEventArgs(NewPrice));
    }
  }
  private int _newPrice;

  public event EventHandler<NewPriceEventArgs>? NewPriceReceived;

  public MarketBoardHandler()
  {
    _items = Svc.Data.GetExcelSheet<Item>();

    Plugin.MarketBoard.OfferingsReceived += MarketBoardOnOfferingsReceived;
    Plugin.MarketBoard.HistoryReceived += OnHistoryReceived;

    Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", AddonRetainerSellPostSetup);
    Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
  }

  public void Dispose()
  {
    Plugin.MarketBoard.OfferingsReceived -= MarketBoardOnOfferingsReceived;
    Plugin.MarketBoard.HistoryReceived -= OnHistoryReceived;
    Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", AddonRetainerSellPostSetup);
    Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
  }

  private void OnHistoryReceived(IMarketBoardHistory history)
  {
    HistoryItemId = history.ItemId;
    _lastHistory = history.HistoryListings.ToList();
    Svc.Log.Debug($"[SaleHistory] Received {_lastHistory.Count} history entries for item {history.ItemId}");
  }

  /// <summary>
  /// Called when the game receives MB listing data. Finds the cheapest
  /// relevant listing, calculates the undercut price, and fires the event.
  /// </summary>
  /// <param name="currentOfferings">Batch of up to 10 MB listings for the queried item, sorted by price ascending.</param>
  private void MarketBoardOnOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings)
  {
    CaptureBoard(currentOfferings);

    if (!_newRequest)
      return;

    // Empty batch (nothing listed for this item) — every ItemListings[0]
    // access below would throw. Treat as "no matching listing."
    if (currentOfferings.ItemListings.Count == 0)
    {
      NewPrice = -1;
      return;
    }

    // Find the first listing that matches our HQ filter.
    // All listings in one response are for the same item, sorted by price ascending.
    var i = 0;
    if (_useHq && _items.GetRow(currentOfferings.ItemListings[0].ItemId).CanBeHq)
    {
      // Skip NQ listings to find the cheapest HQ one
      while (i < currentOfferings.ItemListings.Count && !currentOfferings.ItemListings[i].IsHq)
        i++;
    }
    // NQ path: i stays at 0 (first listing). Empty batches fall through to the guard below.
    // Gap-geometry outlier detection deleted 2026-07-13: the lane decision in
    // SetNewPrice classifies the captured board against settled sales instead.

    // Guard: no matching listing found, or we already processed this batch
    if (i >= currentOfferings.ItemListings.Count || currentOfferings.RequestId == _lastRequestId)
    {
      NewPrice = -1;
      return;
    }
    else
    {
      var listingPrice = (int)currentOfferings.ItemListings[i].PricePerUnit;
      var isOwnRetainer = !Plugin.Configuration.UndercutSelf && GameSafe.IsOwnRetainer(currentOfferings.ItemListings[i].RetainerId);
      var price = ApplyUndercutMode(listingPrice, isOwnRetainer);

      LastCheckedPrice = price; // capture before sentinel conversion

      // Price floor checks
      var itemId = currentOfferings.ItemListings[0].ItemId;

      // Check 1: Mode-based floor (Vendor or Doman Enclave)
      if (Plugin.Configuration.PriceFloorMode != PriceFloorMode.None)
      {
        var vendorPrice = (int)_items.GetRow(itemId).PriceLow;
        var floorPrice = Plugin.Configuration.PriceFloorMode == PriceFloorMode.DomanEnclave ? vendorPrice * 2 : vendorPrice;

        if (floorPrice > 0 && price < floorPrice)
        {
          price = -2; // sentinel: below price floor
        }
      }

      // Check 2: Minimum listing price
      if (price > 0 && Plugin.Configuration.MinimumListingPrice > 0 && price < Plugin.Configuration.MinimumListingPrice)
      {
        price = -3; // sentinel: below minimum listing price
      }

      NewPrice = price;
    }

    _lastRequestId = currentOfferings.RequestId;
    _newRequest = false;
  }

  /// <summary>
  /// Applies the configured undercut mode to a board listing price. Shared by
  /// the first-pass offerings path and the lane decision's anchor pricing.
  /// Own listings are matched, never undercut.
  /// </summary>
  internal int ApplyUndercutMode(int listingPrice, bool isOwnListing)
  {
    var effectiveMode = Plugin.Configuration.UndercutMode;

    if (!isOwnListing && effectiveMode == UndercutMode.Humanized)
    {
      // 1/3 Random Pinch (stays Humanized), 1/3 Gentleman's Match, 1/3 Clean Numbers
      var roll = _random.Next(3);
      if (roll == 1)
        effectiveMode = UndercutMode.GentlemansMatch;
      else if (roll == 2)
        effectiveMode = UndercutMode.CleanNumbers;
      // roll == 0: stays Humanized → random pinch branch below
    }

    if (isOwnListing)
      return listingPrice;  // own listing — keep as-is
    if (effectiveMode == UndercutMode.FixedAmount)
      return Math.Max(listingPrice - Plugin.Configuration.UndercutAmount, 1);
    if (effectiveMode == UndercutMode.Percentage)
      return Math.Max((100 - Plugin.Configuration.UndercutAmount) * listingPrice / 100, 1);
    if (effectiveMode == UndercutMode.CleanNumbers)
    {
      if (listingPrice <= 50)
        return Math.Max(listingPrice - 1, 1);

      var p = listingPrice - 1;
      if (p > 100000) p = p / 100 * 100;
      else if (p > 10000) p = p / 50 * 50;
      else if (p > 1000) p = p / 25 * 25;
      else if (p > 500) p = p / 10 * 10;
      else p = p / 5 * 5;
      return Math.Max(p, 1);
    }
    if (effectiveMode == UndercutMode.Humanized)
    {
      var pinch = _random.Next(1, Plugin.Configuration.HumanizedMaxPinch + 1);
      return Math.Max(listingPrice - pinch, 1);
    }

    return listingPrice;  // GentlemansMatch — copy price exactly
  }

  /// <summary>
  /// Accumulates board listings across offerings batches for the current item.
  /// Runs on every offerings event (even after the first-pass price resolves)
  /// so late batches still enrich the board the lane decision sees.
  /// </summary>
  private void CaptureBoard(IMarketBoardCurrentOfferings offerings)
  {
    if (offerings.ItemListings.Count == 0)
      return;

    var itemId = offerings.ItemListings[0].ItemId;
    if (itemId != BoardItemId)
    {
      _board.Clear();
      _boardRequestIds.Clear();
      BoardItemId = itemId;
    }

    if (!_boardRequestIds.Add(offerings.RequestId))
      return; // batch already captured

    foreach (var listing in offerings.ItemListings)
      _board.Add(((long)listing.PricePerUnit, GameSafe.IsOwnRetainer(listing.RetainerId), listing.IsHq));
  }

  /// <summary>
  /// The captured board for the lane decision. HQ pricing competes with HQ
  /// listings only. Empty when the board belongs to a different item.
  /// </summary>
  internal List<LaneListing> GetBoard(uint itemId, bool hqOnly)
  {
    if (itemId != BoardItemId)
      return [];

    return _board
      .Where(l => !hqOnly || l.IsHq)
      .Select(l => new LaneListing(l.Price, l.IsOwn))
      .ToList();
  }

  /// <summary>
  /// Settled sales for the lane, from the MB history packet (the last ~20
  /// board sales, however old - the lane discounts by age, never discards).
  /// Empty when history belongs to a different item or never arrived.
  /// </summary>
  internal List<LaneSale> GetLaneSales(uint itemId)
  {
    if (_lastHistory == null || HistoryItemId != itemId)
      return [];

    return _lastHistory
      .Select(h => new LaneSale((long)h.SalePrice, ((DateTimeOffset)h.PurchaseTime.ToUniversalTime()).ToUnixTimeSeconds(), h.IsHq))
      .ToList();
  }

  /// <summary>
  /// Sales/day derived from the history packet span. Null when no history.
  /// Feeds the race join/decline call in the lane decision.
  /// </summary>
  internal double? GetPacketVelocityPerDay(uint itemId)
  {
    if (_lastHistory == null || _lastHistory.Count == 0 || HistoryItemId != itemId)
      return null;

    var oldest = _lastHistory.Min(h => h.PurchaseTime.ToUniversalTime());
    var spanDays = Math.Max(1.0, (DateTime.UtcNow - oldest).TotalDays);
    return _lastHistory.Count / spanDays;
  }

  /// <summary>
  /// Triggered when the MB search results window opens — signals that
  /// the next incoming offerings batch is one we requested.
  /// </summary>
  private void ItemSearchResultPostSetup(AddonEvent type, AddonArgs args)
  {
    _newRequest = true;
    _useHq = Plugin.Configuration.HQ && _itemHq;
  }

  /// <summary>
  /// Triggered when the "Adjust Price" window opens. Captures whether the
  /// item is HQ by checking for the HQ icon (U+E03C) in the item name text.
  /// </summary>
  private unsafe void AddonRetainerSellPostSetup(AddonEvent type, AddonArgs args)
  {
    var addon = (AddonRetainerSell*)args.Addon.Address;
    if (addon == null || addon->ItemName == null) return;
    _itemHq = addon->ItemName->NodeText.ToString().Contains(Windows.Format.HqChar);
  }

  /// <summary>Clears stored history and captured board after use.</summary>
  internal void ClearHistory()
  {
    _lastHistory = null;
    HistoryItemId = 0;
    _board.Clear();
    _boardRequestIds.Clear();
    BoardItemId = 0;
  }

  /// <summary>
  /// Populates 14-day sale history stats on the given PricingItem.
  /// Called for every item in SetNewPrice so triage has full context.
  /// </summary>
  internal void PopulateHistoryStats(PricingItem item)
  {
    if (_lastHistory == null || _lastHistory.Count == 0 || HistoryItemId != item.ItemId)
      return;

    var cutoff = DateTime.UtcNow.AddDays(-14);
    var recent = _lastHistory
      .Where(h => h.PurchaseTime >= cutoff)
      .Where(h => !_useHq || !_itemHq || h.IsHq)
      .Select(h => (int)h.SalePrice)
      .ToList();

    item.HistorySaleCount = recent.Count;
    if (recent.Count == 0)
      return;

    recent.Sort();
    item.HistoryMedianPrice = recent[recent.Count / 2];
    item.HistoryAvgPrice = (int)recent.Average();
  }
}
