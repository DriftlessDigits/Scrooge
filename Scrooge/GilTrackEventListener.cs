using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

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

  // Tier 1 tracker state
  private long _questSnapshotGil;
  private long _dutySnapshotGil;
  private bool _inDuty;
  private string _dutyName = "";
  private long _fateSnapshotGil;

  private static readonly HashSet<TerritoryIntendedUseEnum> DutyExclusions =
  [
    TerritoryIntendedUseEnum.Eureka,
    TerritoryIntendedUseEnum.Bozja,
    TerritoryIntendedUseEnum.Occult_Crescent,
  ];

  internal GilTrackEventListener()
  {
    Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
    Svc.ClientState.Logout += OnLogout;
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListSetup);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearch", OnMarketBoardOpen);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "ItemSearch", OnMarketBoardClose);
    Svc.Chat.ChatMessage += OnChatMessage;

    // Tier 1 capture points
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "JournalResult", OnQuestRewardSetup);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, "JournalResult", OnQuestRewardFinalize);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PreSetup, "FateReward", OnFateRewardPreSetup);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "FateReward", OnFateRewardPostSetup);
  }

  public void Dispose()
  {
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "FateReward", OnFateRewardPostSetup);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreSetup, "FateReward", OnFateRewardPreSetup);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "JournalResult", OnQuestRewardFinalize);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "JournalResult", OnQuestRewardSetup);
    Svc.Chat.ChatMessage -= OnChatMessage;
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "ItemSearch", OnMarketBoardClose);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearch", OnMarketBoardOpen);
    Svc.ClientState.TerritoryChanged -= OnTerritoryChanged;
    Svc.ClientState.Logout -= OnLogout;
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListSetup);
  }

  private unsafe void OnTerritoryChanged(uint territoryId)
  {
    GilTracker.TakeBalanceSnapshot("zone_change");

    // Debug: log untracked gap between the last two snapshots
    var gap = GilStorage.GetLatestSnapshotGap();
    if (gap.HasValue && gap.Value.Untracked != 0)
    {
      Svc.Log.Debug(
        $"[GilTrack] Untracked change: {gap.Value.Untracked:N0}g " +
        $"(snapshot diff: {gap.Value.SnapshotDiff:N0}g, tracked net: {gap.Value.TrackedNet:N0}g)");
    }

    if (!Plugin.Configuration.EnableGilTracking) return;

    var cfcId = Content.ContentFinderConditionRowId;
    var intendedUse = Content.TerritoryIntendedUse;
    var isExcluded = intendedUse.HasValue && DutyExclusions.Contains(intendedUse.Value);

    if (cfcId is > 0 && !isExcluded && !_inDuty)
    {
      // Entering a duty — snapshot
      _inDuty = true;
      _dutySnapshotGil = (long)InventoryManager.Instance()->GetGil();
      _dutyName = Content.ContentName ?? $"Duty {cfcId}";
      GilTrackingState.Block();
      Svc.Log.Debug($"[GilTrack] Entered duty: {_dutyName} (CFC {cfcId}), snapshot {_dutySnapshotGil:N0}g");
    }
    else if (_inDuty && (cfcId is null or 0 || isExcluded))
    {
      // Leaving a duty — diff and record
      var currentGil = (long)InventoryManager.Instance()->GetGil();
      var diff = currentGil - _dutySnapshotGil;
      if (diff > 0)
        RecordGilTransaction("earned", "duty_reward", diff, _dutyName);
      else if (diff < 0)
        RecordGilTransaction("spent", "duty_reward", -diff, _dutyName);

      Svc.Log.Debug($"[GilTrack] Left duty: {_dutyName}, diff {diff:N0}g");
      _inDuty = false;
      _dutyName = "";
      GilTrackingState.Unblock();
    }
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
    GilTrackingState.Block();
  }

  private unsafe void OnMarketBoardClose(AddonEvent type, AddonArgs args)
  {
    try
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
    finally
    {
      GilTrackingState.Unblock();
    }
  }

  // --- Chat message transaction capture ---

  private void OnChatMessage(IHandleableChatMessage chatMessage)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;

    var typeId = (int)chatMessage.LogKind;
    var message = chatMessage.Message;

    // Retainer sale notifications. Pattern match is the gate — chat type still TBD,
    // but the "sold for X gil (after fees)" phrasing is unique enough to avoid
    // false positives across all chat channels. Parser logs the observed chat type
    // so we can tighten the filter later if needed.
    if (ParseRetainerSale(message, typeId)) return;

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
    GilTrackingState.NotifyHandled();

    Svc.Log.Debug($"[GilTrack] {source}: {itemName} x{quantity} = {amount:N0}g");
  }

  private static readonly Regex RetainerSalePattern =
    new(@"sold for ([\d,]+) gil \(after fees\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

  private static readonly Regex RetainerSaleQuantityPattern =
    new(@"^The\s+(\d+)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

  /// <summary>
  /// Parses a retainer sale notification from chat into a pending retainer_sale transaction.
  /// Chat message format: "The [N ][item] you put up for sale in the [city] markets
  /// ha(s|ve) sold for X gil (after fees)."
  ///
  /// Pending rows get enriched (retainer_name, buyer_name, real server timestamp) when
  /// the RetainerHistoryHook later reconciles them via ProcessSaleHistory.
  ///
  /// Returns true if this message was a retainer sale (whether or not it parsed cleanly),
  /// so the caller knows to skip other chat handlers.
  /// </summary>
  private bool ParseRetainerSale(SeString message, int typeId)
  {
    var text = message.TextValue;
    var gilMatch = RetainerSalePattern.Match(text);
    if (!gilMatch.Success) return false;

    // Log observed chat type on every match until we've confirmed it's stable.
    // Tighten to a typeId filter once we're sure the game only uses one channel.
    Svc.Log.Info($"[GilTrack] retainer_sale chat type={typeId}: {text}");

    var totalGil = int.Parse(gilMatch.Groups[1].Value.Replace(",", ""));
    if (totalGil <= 0) return true;

    var itemPayload = message.Payloads.OfType<ItemPayload>().FirstOrDefault();
    if (itemPayload == null)
    {
      Svc.Log.Warning($"[GilTrack] retainer_sale: no ItemPayload in chat message — skipping. Text: {text}");
      return true;
    }

    var itemId = itemPayload.ItemId;
    var isHq = itemPayload.IsHQ;
    var itemName = GilTracker.GetItemName(itemId);
    var category = GilTracker.GetItemCategory(itemId);

    var qtyMatch = RetainerSaleQuantityPattern.Match(text);
    var quantity = qtyMatch.Success ? int.Parse(qtyMatch.Groups[1].Value) : 1;
    var unitPrice = quantity > 0 ? totalGil / quantity : totalGil;

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    GilStorage.InsertTransaction(now, "earned", "retainer_sale", totalGil,
      itemId, itemName, category, quantity, unitPrice, isHq, "", "",
      transaction: null, isPending: true);
    // Retainer sale doesn't move player gil, but refresh the catch-all
    // baseline for parity with every other chat-driven parser.
    GilTrackingState.NotifyHandled();

    Svc.Log.Debug($"[GilTrack] retainer_sale (pending): {itemName} x{quantity} = {totalGil:N0}g");
    return true;
  }

  // --- Tier 1: Quest Rewards (JournalResult addon) ---

  private unsafe void OnQuestRewardSetup(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;
    _questSnapshotGil = (long)InventoryManager.Instance()->GetGil();
    GilTrackingState.Block();
    Svc.Log.Debug($"[GilTrack] Quest reward window opened, snapshot {_questSnapshotGil:N0}g");
  }

  private unsafe void OnQuestRewardFinalize(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;

    var questName = "";
    try
    {
      var addon = (AtkUnitBase*)args.Addon.Address;
      if (addon->AtkValues[1].Type != 0)
        questName = addon->AtkValues[1].GetValueAsString();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[GilTrack] Failed to read quest name: {ex.Message}");
    }

    var currentGil = (long)InventoryManager.Instance()->GetGil();
    var diff = currentGil - _questSnapshotGil;
    if (diff > 0)
      RecordGilTransaction("earned", "quest_reward", diff, questName);

    Svc.Log.Debug($"[GilTrack] Quest reward finalized: '{questName}', diff {diff:N0}g");
    GilTrackingState.Unblock();
  }

  // --- Tier 1: FATE Rewards (FateReward addon) ---

  private unsafe void OnFateRewardPreSetup(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;
    _fateSnapshotGil = (long)InventoryManager.Instance()->GetGil();
    GilTrackingState.Block();
    Svc.Log.Debug($"[GilTrack] FATE reward window opening, snapshot {_fateSnapshotGil:N0}g");
  }

  private unsafe void OnFateRewardPostSetup(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;

    var fateName = "";
    try
    {
      var addon = (AtkUnitBase*)args.Addon.Address;
      if (GenericHelpers.IsAddonReady(addon))
      {
        var textNode = addon->GetTextNodeById(6);
        if (textNode != null)
          fateName = textNode->NodeText.ToString();
      }
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[GilTrack] Failed to read FATE name: {ex.Message}");
    }

    var currentGil = (long)InventoryManager.Instance()->GetGil();
    var diff = currentGil - _fateSnapshotGil;
    if (diff > 0)
      RecordGilTransaction("earned", "fate_reward", diff, fateName);

    Svc.Log.Debug($"[GilTrack] FATE reward: '{fateName}', diff {diff:N0}g");
    GilTrackingState.Unblock();
  }

  // --- Shared helpers ---

  private static void RecordGilTransaction(string direction, string source, long amount, string name)
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    GilStorage.InsertTransaction(now, direction, source, amount,
      0, name, "", 1, (int)amount, false, "", "");
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
    GilTrackingState.NotifyHandled();

    Svc.Log.Debug($"[GilTrack] teleport: {amount:N0}g");
  }
}
