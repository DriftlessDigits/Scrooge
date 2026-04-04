using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scrooge;

/// <summary>
/// Subscribes to game events for passive gil tracking outside of pinch/hawk runs.
/// Captures balance snapshots on zone change, summoning bell visits, and logout.
/// Owned by Plugin, disposed on unload.
/// </summary>
internal sealed class GilTrackEventListener : IDisposable
{
  internal GilTrackEventListener()
  {
    Svc.ClientState.TerritoryChanged += OnTerritoryChanged;
    Svc.ClientState.Logout += OnLogout;
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerList", OnRetainerListSetup);
  }

  public void Dispose()
  {
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
}
