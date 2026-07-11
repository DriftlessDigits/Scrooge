using System.Collections.Generic;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Scrooge;

/// <summary>
/// Null-guarded wrappers around ClientStructs singletons and addon node walks.
/// These read native memory — a null anywhere in the chain is an uncatchable
/// access violation (hard game crash), so every read here fails soft instead.
/// Callers decide the fallback: skip the capture, error out, or use a default.
/// </summary>
internal static unsafe class GameSafe
{
  /// <summary>Player gil, or null when InventoryManager isn't available (zoning/startup).</summary>
  internal static long? PlayerGil()
  {
    var im = InventoryManager.Instance();
    return im == null ? null : (long)im->GetGil();
  }

  /// <summary>Active retainer's name, or null when no retainer is active or the manager is unavailable.</summary>
  internal static string? ActiveRetainerName()
  {
    var rm = RetainerManager.Instance();
    if (rm == null) return null;
    var retainer = rm->GetActiveRetainer();
    if (retainer == null) return null;
    var name = retainer->NameString;
    return string.IsNullOrEmpty(name) ? null : name;
  }

  /// <summary>Per-retainer (name, gil) balances; empty when the manager is unavailable.</summary>
  internal static List<(string Name, long Gil)> RetainerBalances()
  {
    var balances = new List<(string, long)>();
    var rm = RetainerManager.Instance();
    if (rm == null) return balances;

    for (uint i = 0; i < rm->GetRetainerCount(); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      if (retainer == null) continue;
      var name = retainer->NameString;
      if (!string.IsNullOrEmpty(name))
        balances.Add((name, retainer->Gil));
    }

    return balances;
  }

  /// <summary>
  /// Checks if a listing belongs to one of the player's own retainers.
  /// Used to avoid undercutting yourself when UndercutSelf is disabled.
  /// </summary>
  internal static bool IsOwnRetainer(ulong retainerId)
  {
    var rm = RetainerManager.Instance();
    if (rm == null) return false;

    for (uint i = 0; i < rm->GetRetainerCount(); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      if (retainer != null && retainer->RetainerId == retainerId)
        return true;
    }

    return false;
  }

  /// <summary>
  /// Venture token item id. VERIFY in-game (flagged in the venture-returns
  /// design doc) — believed 21072; a wrong id reads as 0 stock, which the
  /// rules engine must treat as "unknown", never "panic mode".
  /// </summary>
  private const uint VentureTokenItemId = 21072;

  /// <summary>
  /// Venture token count from the currency container, or null when the
  /// inventory is unavailable. 0 is ambiguous until the item id is verified
  /// in-game — callers should prefer "no tilt" over "hard override" on 0.
  /// </summary>
  internal static int? VentureTokenCount()
  {
    var im = InventoryManager.Instance();
    if (im == null) return null;

    var currency = im->GetInventoryContainer(InventoryType.Currency);
    if (currency == null) return null;

    var count = 0;
    for (int i = 0; i < currency->Size; i++)
    {
      var slot = currency->GetInventorySlot(i);
      if (slot != null && slot->ItemId == VentureTokenItemId)
        count += (int)slot->Quantity;
    }
    return count;
  }

  /// <summary>
  /// Row count of the RetainerSellList's list component, or null when the addon
  /// isn't open/ready or the node walk (NodeList[10] → list component) fails.
  /// </summary>
  internal static int? RetainerSellListLength()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !GenericHelpers.IsAddonReady(addon))
      return null;
    if (addon->UldManager.NodeListCount <= 10) return null;

    var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
    if (listNode == null) return null;
    var listComponent = (AtkComponentList*)listNode->Component;
    if (listComponent == null) return null;

    return listComponent->ListLength;
  }
}
