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
  /// Addons whose counterparty label comes from the addon itself (window
  /// title or addon name), not from TargetManager. Starting with Repair.
  /// </summary>
  private static readonly HashSet<string> WindowAddons = ["Repair"];

  private bool _active;
  private long _openGil;
  private string _counterparty = "";

  internal ExchangeTracker()
  {
    foreach (var addonName in WindowAddons)
    {
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, OnOpen);
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, OnClose);
    }
  }

  public void Dispose()
  {
    foreach (var addonName in WindowAddons)
    {
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, OnOpen);
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, OnClose);
    }
  }

  private unsafe void OnOpen(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;
    if (_active) return; // ignore nested openings — one snapshot per session

    _openGil = (long)InventoryManager.Instance()->GetGil();
    _counterparty = args.AddonName; // good enough for Repair; window titles come later
    _active = true;
    GilTrackingState.Block();

    Svc.Log.Debug($"[GilTrack] Exchange opened ({args.AddonName}): snapshot {_openGil:N0}g");
  }

  private unsafe void OnClose(AddonEvent type, AddonArgs args)
  {
    if (!_active) return;

    var currentGil = (long)InventoryManager.Instance()->GetGil();
    var diff = currentGil - _openGil;
    if (diff > 0)
      RecordExchange("earned", diff, _counterparty);
    else if (diff < 0)
      RecordExchange("spent", -diff, _counterparty);

    Svc.Log.Debug($"[GilTrack] Exchange closed ({args.AddonName}): diff {diff:N0}g");

    _active = false;
    _counterparty = "";
    GilTrackingState.Unblock();
  }

  private static void RecordExchange(string direction, long amount, string counterparty)
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    GilStorage.InsertTransaction(now, direction, "exchange", amount,
      0, counterparty, "", 1, (int)amount, false, "", counterparty);
  }
}
