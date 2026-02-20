using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Network.Structures;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System;
using System.Linq;

namespace Dagobert
{
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

      Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", AddonRetainerSellPostSetup);
      Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
    }

    public void Dispose()
    {
      Plugin.MarketBoard.OfferingsReceived -= MarketBoardOnOfferingsReceived;
      Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", AddonRetainerSellPostSetup);
      Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
    }

    /// <summary>
    /// Called when the game receives MB listing data. Finds the cheapest
    /// relevant listing, calculates the undercut price, and fires the event.
    /// </summary>
    /// <param name="currentOfferings">Batch of up to 10 MB listings for the queried item, sorted by price ascending.</param>
    private void MarketBoardOnOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings)
    {
      if (!_newRequest)
        return;

      // Find the first listing that matches our HQ filter.
      // All listings in one response are for the same item, sorted by price ascending.
      var i = 0;
      if (_useHq && _items.Single(j => j.RowId == currentOfferings.ItemListings[0].ItemId).CanBeHq)
      {
        // Skip NQ listings to find the cheapest HQ one
        while (i < currentOfferings.ItemListings.Count && !currentOfferings.ItemListings[i].IsHq)
          i++;
      }
      else
      {
        if (currentOfferings.ItemListings.Count > 0)
          i = 0;
        else
          i = currentOfferings.ItemListings.Count; // no listings at all
      }

      // --- Outlier detection: price-by-price gap comparison ---
      // Only applies to NQ item pricing. HQ items skip outlier detection.
      // Treats all listings (NQ and HQ) as equals for gap analysis.
      if (Plugin.Configuration.OutlierDetection && !(_useHq && _itemHq))
      {
        var searchEnd = Math.Min(currentOfferings.ItemListings.Count, (i + 1 + Plugin.Configuration.OutlierSearchWindow));

        for (var j = i; j + 1 < searchEnd; j++)
        {
          var currentPrice = currentOfferings.ItemListings[j].PricePerUnit;
          var nextPrice = currentOfferings.ItemListings[j + 1].PricePerUnit;
          var gapPercent = nextPrice > 0 ? (float)(nextPrice - currentPrice) / nextPrice * 100f : 0f;

          // If the price gap exceeds the threshold, it's a cliff
          if (gapPercent > Plugin.Configuration.OutlierThresholdPercent)
          {
            Communicator.PrintOutlierDetected(currentOfferings.ItemListings[0].ItemId, (int)currentPrice, (int)nextPrice);
            i = j + 1; // skip everything below the cliff
          }
        }

        // Re-check bounds after skipping outliers
        if (i >= currentOfferings.ItemListings.Count)
        {
          NewPrice = -1;
          return;
        }
      }

      // Guard: no matching listing found, or we already processed this batch
      if (i >= currentOfferings.ItemListings.Count || currentOfferings.RequestId == _lastRequestId)
      {
        NewPrice = -1;
        return;
      }
      else
      {
        int price;
        var listingPrice = (int)currentOfferings.ItemListings[i].PricePerUnit;
        var isOwnRetainer = !Plugin.Configuration.UndercutSelf && IsOwnRetainer(currentOfferings.ItemListings[i].RetainerId);

        // Calculate price based on the selected undercut mode
        if (isOwnRetainer)
          price = listingPrice;  // own listing — keep as-is
        else if (Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount)
          price = Math.Max(listingPrice - Plugin.Configuration.UndercutAmount, 1);
        else if (Plugin.Configuration.UndercutMode == UndercutMode.Percentage)
          price = Math.Max((100 - Plugin.Configuration.UndercutAmount) * listingPrice / 100, 1);
        else if (Plugin.Configuration.UndercutMode == UndercutMode.CleanNumbers)
        {
          if (listingPrice <= 50)
            price = Math.Max(listingPrice - 1, 1);
          else
          {
            var p = listingPrice - 1;
            if (p > 100000) p = p / 100 * 100;
            else if (p > 10000) p = p / 50 * 50;
            else if (p > 1000) p = p / 25 * 25;
            else if (p > 500) p = p / 10 * 10;
            else p = p / 5 * 5;
            price = Math.Max(p, 1);
          }
        }
        else
          price = listingPrice;  // GentlemansMatch — copy price exactly

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
      string nodeText = ((AddonRetainerSell*)args.Addon.Address)->ItemName->NodeText.ToString();
      _itemHq = nodeText.Contains('\uE03C');
    }

    /// <summary>
    /// Checks if a listing belongs to one of the player's own retainers.
    /// Used to avoid undercutting yourself when UndercutSelf is disabled.
    /// </summary>
    /// <param name="retainerId">The retainer ID from the MB listing to check.</param>
    /// <returns>True if the retainer belongs to the current player.</returns>
    private unsafe bool IsOwnRetainer(ulong retainerId)
    {
      var retainerManager = RetainerManager.Instance();
      for (uint i = 0; i < retainerManager->GetRetainerCount(); ++i)
      {
        if (retainerId == retainerManager->GetRetainerBySortedIndex(i)->RetainerId)
          return true;
      }

      return false;
    }
  }
}