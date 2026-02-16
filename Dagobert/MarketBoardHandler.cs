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
  ///   -2   = undercut price is below vendor sell price
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

      // Guard: no matching listing found, or we already processed this batch
      if (i >= currentOfferings.ItemListings.Count || currentOfferings.RequestId == _lastRequestId)
      {
        NewPrice = -1;
        return;
      }
      else
      {
        int price;

        // Calculate price based on the selected undercut mode
        if (!Plugin.Configuration.UndercutSelf && IsOwnRetainer(currentOfferings.ItemListings[i].RetainerId))
          price = (int)currentOfferings.ItemListings[i].PricePerUnit;  // own listing — keep as-is
        else if (Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount)
          price = Math.Max((int)currentOfferings.ItemListings[i].PricePerUnit - Plugin.Configuration.UndercutAmount, 1);
        else if (Plugin.Configuration.UndercutMode == UndercutMode.Percentage)
          price = Math.Max((100 - Plugin.Configuration.UndercutAmount) * (int)currentOfferings.ItemListings[i].PricePerUnit / 100, 1);
        else
          price = (int)currentOfferings.ItemListings[i].PricePerUnit;  // GentlemansMatch — copy price exactly

        // Vendor floor safety check: don't list below what a vendor would pay
        if (Plugin.Configuration.VendorPriceFloor)
        {
          var vendorPrice = (int)_items.GetRow(currentOfferings.ItemListings[0].ItemId).PriceLow;
          if (vendorPrice > 0 && price < vendorPrice)
          {
            price = -2; // sentinel: below vendor price floor
          }
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