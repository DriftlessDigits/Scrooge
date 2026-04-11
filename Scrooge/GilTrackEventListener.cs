using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scrooge;

/// <summary>
/// Subscribes to game events for passive gil tracking outside of pinch/hawk runs.
/// Captures balance snapshots on zone change, summoning bell visits, and logout.
/// Parses chat messages for NPC vendor transactions and teleport costs.
/// Owned by Plugin, disposed on unload.
/// </summary>
internal sealed class GilTrackEventListener : IDisposable
{
  // MB purchase tracking — snapshot gil on open, diff on close
  private long _mbOpenGil;
  private int _mbPurchaseCount;

  internal GilTrackEventListener()
  {
    Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
    Svc.ClientState.Logout += OnLogout;
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListSetup);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearch", OnMarketBoardOpen);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ItemSearch", OnMarketBoardClose);
    Svc.Chat.ChatMessage += OnChatMessage;
  }

  public void Dispose()
  {
    Svc.Chat.ChatMessage -= OnChatMessage;
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ItemSearch", OnMarketBoardClose);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearch", OnMarketBoardOpen);
    Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
    Svc.ClientState.Logout -= OnLogout;
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListSetup);
  }

  private void OnTerritoryChanged(ushort territoryId)
  {
    GilTracker.TakeBalanceSnapshot("zone_change");
  }

  private void OnLogout(int type, int code)
  {
    GilTracker.TakeBalanceSnapshot("logout");
  }

  /// <summary>
  /// Summoning bell — we're at the bell so retainer data IS available.
  /// Captures full snapshot including per-retainer balances.
  /// </summary>
  private unsafe void OnRetainerListSetup(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;

    var playerGil = (long)InventoryManager.Instance()->GetGil();
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Dedup: skip if last snapshot < 60s ago and balance unchanged
    var last = GilStorage.GetLatestPlayerGilAndTimestamp();
    if (last.HasValue && (now - last.Value.Timestamp) < 60 && last.Value.Gil == playerGil)
      return;

    var snapshotId = GilStorage.InsertGilSnapshot(now, playerGil, "summoning_bell");

    // At the bell, retainer balances are populated
    var rm = RetainerManager.Instance();
    for (uint i = 0; i < rm->GetRetainerCount(); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      var name = retainer->NameString;
      if (!string.IsNullOrEmpty(name))
        GilStorage.InsertRetainerSnapshot(snapshotId, name, retainer->Gil);
    }

    Svc.Log.Debug($"[GilTrack] Bell snapshot: {playerGil:N0}g player + retainer balances");
  }

  // --- Market Board purchase tracking (gil diff approach) ---

  private unsafe void OnMarketBoardOpen(AddonEvent type, AddonArgs args)
  {
    _mbOpenGil = (long)InventoryManager.Instance()->GetGil();
    _mbPurchaseCount = 0;
  }

  private unsafe void OnMarketBoardClose(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking || _mbPurchaseCount == 0) return;

    var currentGil = (long)InventoryManager.Instance()->GetGil();
    var spent = _mbOpenGil - currentGil;
    if (spent <= 0) return;

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var label = _mbPurchaseCount == 1 ? "item" : "items";
    GilStorage.InsertTransaction(now, "spent", "mb_purchase", spent,
      0, $"{_mbPurchaseCount} {label}", "", _mbPurchaseCount, (int)(spent / _mbPurchaseCount),
      false, "", "Market Board");

    Svc.Log.Debug($"[GilTrack] mb_purchase: {spent:N0}g on {_mbPurchaseCount} {label}");
    _mbPurchaseCount = 0;
  }

  // --- Chat message transaction capture ---

  private void OnChatMessage(XivChatType type, int timestamp,
    ref SeString sender, ref SeString message, ref bool isHandled)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;

    var typeId = (int)type;

    if (typeId == 57)
      ParseSystemMessage(message);
    else if (typeId == 2105)
      ParseGilSpent(message);
  }

  /// <summary>
  /// Parses type 57 (SystemMessage) for NPC vendor buy/sell and MB purchase counting.
  /// "You purchase a [item] for X gil." → npc_purchase (spent)
  /// "You sell a [item] for X gil." → npc_sale (earned)
  /// "You purchase a [item]." (no price) → MB purchase, counted for gil diff
  /// </summary>
  private void ParseSystemMessage(SeString message)
  {
    var text = message.TextValue;

    if (text.StartsWith("You purchase", StringComparison.OrdinalIgnoreCase))
    {
      // Check if it has a price — NPC purchases have "for X gil", MB purchases don't
      var gilMatch = Regex.Match(text, @"for\s+([\d,]+)\s+gil", RegexOptions.IgnoreCase);
      if (gilMatch.Success)
      {
        // NPC purchase with price
        ParseNpcBuySell(message, text, "spent", "npc_purchase",
          int.Parse(gilMatch.Groups[1].Value.Replace(",", "")));
      }
      else
      {
        // MB purchase — no price in message, just count it for the gil diff
        _mbPurchaseCount++;
      }
    }
    else if (text.StartsWith("You sell", StringComparison.OrdinalIgnoreCase))
    {
      var gilMatch = Regex.Match(text, @"for\s+([\d,]+)\s+gil", RegexOptions.IgnoreCase);
      if (gilMatch.Success)
        ParseNpcBuySell(message, text, "earned", "npc_sale",
          int.Parse(gilMatch.Groups[1].Value.Replace(",", "")));
    }
  }

  /// <summary>Writes an NPC vendor buy/sell transaction to the DB.</summary>
  private void ParseNpcBuySell(SeString message, string text, string direction, string source, int amount)
  {
    if (amount <= 0) return;

    var itemPayload = message.Payloads.OfType<ItemPayload>().FirstOrDefault();
    if (itemPayload == null) return;

    var itemId = itemPayload.ItemId;
    var isHq = itemPayload.IsHQ;
    var itemName = GilTracker.GetItemName(itemId);
    var category = GilTracker.GetItemCategory(itemId);

    // "for X gil" is the total — check for quantity in text ("3 [item]" pattern)
    var qtyMatch = Regex.Match(text, @"(\d+)\s+" + Regex.Escape(itemName));
    var quantity = qtyMatch.Success ? int.Parse(qtyMatch.Groups[1].Value) : 1;
    var unitPrice = quantity > 0 ? amount / quantity : amount;

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    GilStorage.InsertTransaction(now, direction, source, amount,
      itemId, itemName, category, quantity, unitPrice, isHq, "", "NPC");

    Svc.Log.Debug($"[GilTrack] {source}: {itemName} x{quantity} = {amount:N0}g");
  }

  /// <summary>
  /// Parses type 2105 for "You spent X gil." — currently used for teleport costs.
  /// </summary>
  private void ParseGilSpent(SeString message)
  {
    var text = message.TextValue;

    var gilMatch = Regex.Match(text, @"spent\s+([\d,]+)\s+gil", RegexOptions.IgnoreCase);
    if (!gilMatch.Success) return;

    var amount = int.Parse(gilMatch.Groups[1].Value.Replace(",", ""));
    if (amount <= 0) return;

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    GilStorage.InsertTransaction(now, "spent", "teleport", amount,
      0, "", "", 1, amount, false, "", "");

    Svc.Log.Debug($"[GilTrack] teleport: {amount:N0}g");
  }
}
