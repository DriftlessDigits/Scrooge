using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scrooge;

/// <summary>
/// Tracks gil changes inside exchange-style addons (shops, repair vendors,
/// materia, etc.) by snapshotting gil on PostSetup and diffing on PreFinalize.
/// Mirrors the CurrencyTracker "Exchange" component pattern.
///
/// Scope is intentionally small for the first pass: only the Repair addon.
/// The WindowAddons / NormalAddons split, target-name capture, and the full
/// ~20-addon list are follow-ups on this same branch.
///
/// While active, raises <see cref="GilTrackingState"/> so the future chat
/// catch-all doesn't double-count the same spend.
/// </summary>
internal sealed class ExchangeTracker : IDisposable
{
  /// <summary>
  /// Maps each hooked addon to its granular source string. Data is logged
  /// verbatim — any grouping (e.g. "appearance" = glamour + dye) is a UI
  /// concern layered on top, never baked into the stored source.
  /// Extend one addon at a time; no generic fallback.
  /// </summary>
  private static readonly Dictionary<string, string> AddonSources = new()
  {
    { "Repair", "repair" },
    { "FreeCompanyChest", "fc_chest" },
  };

  private bool _active;
  private long _openGil;
  private string _counterparty = "";
  private string _source = "";

  internal ExchangeTracker()
  {
    foreach (var addonName in AddonSources.Keys)
    {
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, OnOpen);
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, OnClose);
    }
  }

  public void Dispose()
  {
    foreach (var addonName in AddonSources.Keys)
    {
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, OnOpen);
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, OnClose);
    }
  }

  private unsafe void OnOpen(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;
    if (_active) return; // ignore nested openings — one snapshot per session
    if (!AddonSources.TryGetValue(args.AddonName, out var source)) return;

    _openGil = (long)InventoryManager.Instance()->GetGil();
    _counterparty = args.AddonName; // window titles come later for addons that need them
    _source = source;
    _active = true;
    GilTrackingState.Block();

    Svc.Log.Debug($"[GilTrack] {source} opened: snapshot {_openGil:N0}g");
  }

  private unsafe void OnClose(AddonEvent type, AddonArgs args)
  {
    if (!_active) return;

    var currentGil = (long)InventoryManager.Instance()->GetGil();
    var diff = currentGil - _openGil;
    if (diff > 0)
      RecordExchange("earned", diff, _source, _counterparty);
    else if (diff < 0)
      RecordExchange("spent", -diff, _source, _counterparty);

    Svc.Log.Debug($"[GilTrack] {_source} closed: diff {diff:N0}g");

    _active = false;
    _counterparty = "";
    _source = "";
    GilTrackingState.Unblock();
  }

  private static void RecordExchange(string direction, long amount, string source, string counterparty)
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    GilStorage.InsertTransaction(now, direction, source, amount,
      0, counterparty, "", 1, (int)amount, false, "", counterparty);
  }
}
