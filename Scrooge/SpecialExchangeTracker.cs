using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Scrooge;

/// <summary>
/// Gil tracker for "special exchange" addons that don't bracket cleanly on
/// PreComplete (e.g. Custom Delivery, Wondrous Tails). Snapshot fires on
/// PostSetup; the end is detected by polling <see cref="ConditionFlag.OccupiedInEvent"/>
/// via Framework.Update — when the player is no longer occupied, the exchange
/// is complete and we diff.
///
/// Mirrors CurrencyTracker's SpecialExchange pattern.
/// </summary>
internal sealed class SpecialExchangeTracker : IDisposable
{
  /// <summary>
  /// Each addon maps to its granular source string. Same rules as
  /// <see cref="ExchangeTracker"/>: user-facing language, no generic fallback.
  /// </summary>
  private static readonly Dictionary<string, string> AddonSources = new()
  {
    { "SatisfactionSupply", "custom_delivery" },
    { "WeeklyBingoResult", "wondrous_tails" },
  };

  private bool _active;
  private long _openGil;
  private string _counterparty = "";
  private string _source = "";
  private IDisposable? _catchallBlock;

  internal SpecialExchangeTracker()
  {
    foreach (var addonName in AddonSources.Keys)
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, OnOpen);
  }

  public void Dispose()
  {
    Svc.Framework.Update -= OnFrameworkUpdate;
    foreach (var addonName in AddonSources.Keys)
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, OnOpen);
    _catchallBlock?.Dispose();
    _catchallBlock = null;
  }

  private void OnOpen(AddonEvent type, AddonArgs args)
  {
    if (!Plugin.Configuration.EnableGilTracking) return;
    if (_active) return; // ignore nested openings
    if (!AddonSources.TryGetValue(args.AddonName, out var source)) return;
    if (GameSafe.PlayerGil() is not long openGil) return; // no baseline — skip this session

    _openGil = openGil;
    _counterparty = args.AddonName;
    _source = source;
    _active = true;
    _catchallBlock?.Dispose();
    _catchallBlock = GilTrackingState.Block($"special_exchange:{source}");
    Svc.Framework.Update += OnFrameworkUpdate;

    Svc.Log.Debug($"[GilTrack] {source} opened: snapshot {_openGil:N0}g");
  }

  /// <summary>
  /// Polled every game frame while an exchange is active. Completes once
  /// the player is no longer occupied by the NPC interaction.
  /// </summary>
  private void OnFrameworkUpdate(IFramework framework)
  {
    if (!_active) return;
    if (Svc.Condition[ConditionFlag.OccupiedInEvent]) return;
    if (Svc.Condition[ConditionFlag.OccupiedInQuestEvent]) return;
    Complete();
  }

  private void Complete()
  {
    Svc.Framework.Update -= OnFrameworkUpdate;

    try
    {
      if (GameSafe.PlayerGil() is not long currentGil) return;

      var diff = currentGil - _openGil;
      if (diff > 0)
        Record("earned", diff, _source, _counterparty);
      else if (diff < 0)
        Record("spent", -diff, _source, _counterparty);

      Svc.Log.Debug($"[GilTrack] {_source} closed: diff {diff:N0}g");
    }
    finally
    {
      _active = false;
      _counterparty = "";
      _source = "";
      _catchallBlock?.Dispose();
      _catchallBlock = null;
    }
  }

  private static void Record(string direction, long amount, string source, string counterparty)
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    GilStorage.InsertTransaction(now, direction, source, amount,
      0, counterparty, "", 1, (int)amount, false, "", counterparty);
  }
}
