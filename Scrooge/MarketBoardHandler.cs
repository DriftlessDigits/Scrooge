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
/// Fires <see cref="NewPriceReceived"/> with the result, which the pricing pipeline consumes.
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
  // Retainer + quantity ride alongside price so market memory (M4) can diff on
  // soft identity (retainer, qty, HQ); lane pricing still reads price/own/hq only.
  private readonly List<(long Price, bool IsOwn, bool IsHq, string Retainer, int Quantity)> _board = [];
  private readonly HashSet<int> _boardRequestIds = [];
  private readonly HashSet<ulong> _boardListingIds = [];

  /// <summary>Item ID the captured board belongs to.</summary>
  internal uint BoardItemId { get; private set; }

  /// <summary>Item ID from the last HistoryReceived event. Used to validate history is for the correct item.</summary>
  internal uint HistoryItemId { get; private set; }

  /// <summary>The calculated undercut price BEFORE floor/min sentinel conversion. Used by triage UI.</summary>
  internal int LastCheckedPrice { get; private set; }

  /// <summary>
  /// Setting NewPrice fires the event — this is the bridge to the pricing pipeline.
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
    BankSaleHistory(history.ItemId, _lastHistory);
  }

  /// <summary>
  /// Banks the history window into sale_history (V23, the tape) right here where
  /// the packet is in hand — this event is the ONLY place the settled-sales data
  /// exists before _lastHistory is replaced or nulled. Best-effort but never
  /// silent: a storage failure must not break the pinch, so it downgrades to a
  /// Warning — with the exception, because a tape that misses windows without
  /// saying so is the standing book's 07-25 silent-flush lesson all over again.
  /// </summary>
  private void BankSaleHistory(uint itemId, List<IMarketBoardHistoryListing> listings)
  {
    if (listings.Count == 0)
      return;

    try
    {
      var seenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      var rows = listings.Select(h => new SaleHistorySchema.SaleRow(
        itemId, h.IsHq, (long)h.SalePrice, (int)h.Quantity, h.BuyerName ?? "",
        ((DateTimeOffset)h.PurchaseTime.ToUniversalTime()).ToUnixTimeSeconds(), seenAt)).ToList();

      var banked = GilStorage.BankSaleHistory(rows);
      if (banked > 0)
        Svc.Log.Debug($"[SaleHistory] Banked {banked} new of {rows.Count} window entries for item {itemId}");
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, $"[SaleHistory] Banking failed for item {itemId} — pinch unaffected, this window's tape is missed");
    }
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

      // THE ONE FLOOR LAW (ruled 2026-08-21). Two checks with two sentinels became
      // one: the honest ask either clears max(minimum, mode floor) or no legal
      // listing exists. Same calculation the lane guard, the cached-post re-check and
      // the Ledger's relist preview run — see PriceFloor.Effective.
      var itemId = currentOfferings.ItemListings[0].ItemId;
      var vendorPrice = (long)_items.GetRow(itemId).PriceLow;
      var floor = PriceFloor.Effective(
        Plugin.Configuration.PriceFloorMode, vendorPrice, Plugin.Configuration.MinimumListingPrice);

      if (price > 0 && floor.Refuses(price))
        price = -2; // sentinel: no legal ask - the honest price is under the floor

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
  /// <param name="crossQuality">
  /// True = the anchor is a row of the BETTER quality, handed over by the
  /// cross-quality rail. Matching it is not a market position, it is a corpse, so
  /// the answer is forced strictly under it however the mode landed. This method
  /// still knows nothing about HQ - only that the anchor crossed qualities.
  /// </param>
  internal int ApplyUndercutMode(int listingPrice, bool isOwnListing, bool crossQuality = false)
  {
    var price = UndercutByMode(listingPrice, isOwnListing);
    return crossQuality
      ? (int)LanePricing.StrictlyUnder(price, listingPrice)
      : price;
  }

  /// <summary>The mode arithmetic itself, quality-blind by design.</summary>
  private int UndercutByMode(int listingPrice, bool isOwnListing)
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
  ///
  /// <para>A board is BORN only by an armed query (_newRequest - the compare
  /// window the run or the player just opened). The game serves offerings in
  /// pages of 10 and continues from its last offset on a quick re-request, so a
  /// batch that arrives AFTER the pricing door flushed this item is the queue's
  /// TAIL wearing a fresh timestamp. Founding a board on one poisoned the banked
  /// snapshot and minted ~280 phantom appear/disappear events in one afternoon -
  /// the Stuffed Alpha's rows 11-20 read as "the whole board turned over"
  /// (08-02). Orphan batches are dropped; enrichment of an in-flight board
  /// (same item) stays unconditional, which is what late batches are for.</para>
  /// </summary>
  private void CaptureBoard(IMarketBoardCurrentOfferings offerings)
  {
    if (offerings.ItemListings.Count == 0)
      return;

    var itemId = offerings.ItemListings[0].ItemId;
    if (itemId != BoardItemId)
    {
      if (!_newRequest)
      {
        Svc.Log.Debug($"[Board] Orphan offerings batch for item {itemId} dropped - no armed query, a late page of a flushed board");
        return;
      }
      _board.Clear();
      _boardRequestIds.Clear();
      _boardListingIds.Clear();
      BoardItemId = itemId;
      BoardTotal = null; // the last item's total must never vouch for this board
    }

    if (!_boardRequestIds.Add(offerings.RequestId))
      return; // batch already captured

    // Row-level dedup: a genuine no-answer retry can cross a first request that
    // finally answers - the game restarts the board under a fresh request ID,
    // so page 1 arrives twice (live 08-02: "seen 20 of 12"). Request-id dedup
    // can't see it; the listing's own identity can. Re-sent rows become no-ops
    // and the count stays honest, so completeness only trips on the real tail.
    foreach (var listing in offerings.ItemListings)
    {
      if (!_boardListingIds.Add(listing.ListingId))
        continue;
      _board.Add(((long)listing.PricePerUnit, GameSafe.IsOwnRetainer(listing.RetainerId),
        listing.IsHq, listing.RetainerName ?? "", (int)listing.ItemQuantity));
    }

    // The board's TOTAL, from the game's own search proxy - the "y" the
    // ItemSearchResult window displays, live during the retainer compare flow
    // (proved by the x-of-y instrument, 08-02: "seen 10 of 42" while pages
    // streamed). Re-read on every batch: the freshest claim wins, and a proxy
    // hiccup on one batch never zeroes a total an earlier batch banked.
    if (ProxyListingTotal() is int total && total > 0)
      BoardTotal = total;
    Svc.Log.Debug($"[Board] x-of-y: item {itemId} seen {_board.Count} of {BoardTotal?.ToString() ?? "?"} (proxy total)");
  }

  /// <summary>The proxy's total listing count for the captured board, or null when the proxy never said.</summary>
  internal int? BoardTotal { get; private set; }

  /// <summary>
  /// Whether the captured board holds every row the game says exists (Drift,
  /// 08-02: "I'd rather have all of the data before making a decision"). An
  /// unknown total reads as complete - the pre-proxy behavior, never a stall.
  /// Callers gate on a first-pass response first, which guarantees the
  /// captured board is the item under pricing.
  /// </summary>
  internal bool CurrentBoardComplete
    => BoardTotal is not int total || _board.Count >= total;

  /// <summary>How many rows the captured board holds right now - the completeness gauge.</summary>
  internal int CurrentBoardDepth => _board.Count;

  /// <summary>
  /// Completes the board from the game's OWN copy - zero requests sent (Drift,
  /// 08-02: "I want a decision based on full data", and on request spam: "I'm
  /// a bit horrified that we've been making nonsense page 1 requests").
  ///
  /// <para>The compare-price flow's offerings PACKET carries only page 1, and
  /// every re-request we ever fired just ordered another page 1 - proved live
  /// in all three configurations. But the proxy behind the ItemSearchResult
  /// window holds a 100-slot listing array of its own, filled by the AddPage
  /// packet path that Dalamud's offerings event does not relay. If the game
  /// already holds rows 11+, they are HERE. This reads them - same item
  /// verified, deduped by ListingId like every captured batch - and logs what
  /// it found either way, because whether the proxy fills in this flow IS the
  /// experiment.</para>
  /// </summary>
  internal unsafe void TryCompleteFromProxy(uint expectedItemId)
  {
    try
    {
      var module = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoModule.Instance();
      if (module == null) return;
      var proxy = (FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch*)
        module->GetInfoProxyById(FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyId.ItemSearch);
      if (proxy == null) return;
      if (proxy->SearchItemId != expectedItemId)
      {
        Svc.Log.Debug($"[Board] proxy read: proxy holds item {proxy->SearchItemId}, pricing {expectedItemId} - not ours, skipped");
        return;
      }

      // The offerings PACKET can go missing entirely while the proxy still got
      // the data (live 08-02: item 22428 - proxy held its listings, no packet
      // ever founded a board, and the old board-id guard refused to read them).
      // Identity here is verified against the item UNDER PRICING - the
      // pipeline's own expectation, not packet timing - so founding the board
      // from the proxy is safe where founding from an orphan batch was not.
      //
      // Known imprecision, ruled acceptable (Drift, 08-02): with two listings of
      // the SAME variant back to back, this cannot tell whether the proxy's
      // rows came from this listing's query or the previous one's - at most a
      // few seconds stale, own rows never compete anyway, and "the price is
      // the price and the board is the board" for a given item. The
      // alternative on this branch is deciding on no board at all.
      if (BoardItemId != expectedItemId)
      {
        _board.Clear();
        _boardRequestIds.Clear();
        _boardListingIds.Clear();
        BoardItemId = expectedItemId;
        BoardTotal = null;
      }

      var held = (int)proxy->ListingCount;
      var before = _board.Count;
      var listings = proxy->Listings;
      for (var i = 0; i < held && i < listings.Length; i++)
      {
        ref var l = ref listings[i];
        if (l.ItemId != BoardItemId) continue;
        if (!_boardListingIds.Add(l.ListingId)) continue;
        _board.Add(((long)l.UnitPrice, GameSafe.IsOwnRetainer(l.RetainerId),
          l.IsHqItem, l.CharacterName.ToString(), (int)l.Quantity));
      }
      // A proxy-founded board has no packet-banked total; the proxy's own count
      // is the freshest claim there is (it reads the full total before pages
      // finish streaming - proved by every "seen 10 of 20" line).
      if (BoardTotal is null && held > 0)
        BoardTotal = held;
      Svc.Log.Debug($"[Board] proxy read: item {BoardItemId} - proxy holds {held}, " +
        $"captured {before} -> {_board.Count} of {BoardTotal?.ToString() ?? "?"}");
    }
    catch (Exception ex)
    {
      Svc.Log.Debug($"[Board] proxy read failed: {ex.Message}");
    }
  }

  /// <summary>
  /// The total listing count the game's own search proxy holds for the current
  /// query - the "y" behind the ItemSearchResult window's count, available (if
  /// populated) before all pages have streamed. Null when the proxy is absent.
  /// </summary>
  private static unsafe int? ProxyListingTotal()
  {
    try
    {
      var module = FFXIVClientStructs.FFXIV.Client.UI.Info.InfoModule.Instance();
      if (module == null) return null;
      var proxy = (FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyItemSearch*)
        module->GetInfoProxyById(FFXIVClientStructs.FFXIV.Client.UI.Info.InfoProxyId.ItemSearch);
      return proxy == null ? null : (int)proxy->ListingCount;
    }
    catch
    {
      return null;
    }
  }

  /// <summary>
  /// The captured board for the lane decision. HQ pricing competes with HQ
  /// listings only; NQ pricing walks the COMBINED physical queue with each
  /// row's quality flagged (A12) - the walk classifies cross-quality rows
  /// itself. Empty when the board belongs to a different item.
  /// </summary>
  internal List<LaneListing> GetBoard(uint itemId, bool hqOnly)
  {
    if (itemId != BoardItemId)
      return [];

    return _board
      .Where(l => !hqOnly || l.IsHq)
      .Select(l => new LaneListing(l.Price, l.IsOwn, l.IsHq))
      .ToList();
  }

  /// <summary>
  /// The full-identity captured board for market memory (M4): every listing (both
  /// qualities - quality is part of the soft-identity key) with its retainer, stack
  /// size, price and own-ness. Empty when the captured board belongs to another item.
  /// Read at the pricing door to diff against the stored snapshot.
  /// </summary>
  internal List<MarketEvents.BoardListing> GetBoardSnapshot(uint itemId)
  {
    if (itemId != BoardItemId)
      return [];

    return _board
      .Select(l => new MarketEvents.BoardListing(l.Retainer, l.Quantity, l.IsHq, l.Price, l.IsOwn))
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
    _boardListingIds.Clear();
    BoardItemId = 0;
    // A flushed board's total must never vouch for the next item: a stale
    // total made genuinely EMPTY boards read "0 of 47", burn every await
    // window, and log a false "deciding censored" (live 08-02, first round).
    BoardTotal = null;
  }

  /// <summary>
  /// Populates 14-day sale history stats on the given PricingItem.
  /// Called for every item in SetNewPrice so triage has full context.
  ///
  /// <para>THE ACCUSER READS BY THE SCORER'S RULES (F6, ruled 08-22). These
  /// counts feed the contradiction instrument ("settled sales contradict the
  /// vendor verdict"), and a sale the floor would refuse to list at can never
  /// change the action - so it cannot testify. The 39-gil Cotton Cloth sales
  /// accusing a Vendor verdict on a 75-floor book were evidence about a listing
  /// the player would never write; the existing minimum IS the de minimis
  /// ruling, applied to the witness stand.</para>
  /// </summary>
  internal void PopulateHistoryStats(PricingItem item)
  {
    if (_lastHistory == null || _lastHistory.Count == 0 || HistoryItemId != item.ItemId)
      return;

    var floor = PriceFloor.Effective(
      Plugin.Configuration.PriceFloorMode, item.VendorPrice,
      Plugin.Configuration.MinimumListingPrice);
    var cutoff = DateTime.UtcNow.AddDays(-14);
    var recent = _lastHistory
      .Where(h => h.PurchaseTime >= cutoff)
      .Where(h => !_useHq || !_itemHq || h.IsHq)
      .Select(h => (int)h.SalePrice)
      .Where(p => !floor.Refuses(p))
      .ToList();

    item.HistorySaleCount = recent.Count;
    if (recent.Count == 0)
      return;

    recent.Sort();
    item.HistoryMedianPrice = recent[recent.Count / 2];
  }
}
