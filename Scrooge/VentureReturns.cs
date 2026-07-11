using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge;

/// <summary>
/// Venture-return tracking: makes the GC exit numerically honest. Captures
/// each collected quick-venture result off the RetainerTaskResult dialog,
/// then derives gil-per-venture and the empirical seals-to-gil rate the
/// routing engine has been running on a placeholder for.
///
/// The AtkValue layout of RetainerTaskResult is UNMAPPED in this codebase.
/// The parser tries a best-guess offset first, falls back to a validated
/// scan, and when both fail it dumps every value to the log tagged
/// "[Ventures] map needed" - one live venture collection gives the real
/// offsets. Capture is evidence-only: unparseable dialogs record nothing.
/// </summary>
internal sealed class VentureReturnTracker : IDisposable
{
  // Dedup: collecting fires PostSetup once, but a re-shown dialog (or a
  // PostSetup/PostUpdate double) must not double-count the same venture.
  private (string Retainer, uint ItemId, int Qty, long Minute)? _lastCapture;

  public VentureReturnTracker()
  {
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerTaskResult", OnTaskResult);
  }

  public void Dispose()
  {
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerTaskResult", OnTaskResult);
  }

  private unsafe void OnTaskResult(AddonEvent type, AddonArgs args)
  {
    try
    {
      var addon = (AtkUnitBase*)(nint)args.Addon;
      if (addon == null || !addon->IsVisible) return;

      var retainer = GameSafe.ActiveRetainerName() ?? "";
      if (retainer.Length == 0) return; // no attribution, no row

      if (ParseReward(addon) is not { } reward)
        return;

      var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      var key = (retainer, reward.ItemId, reward.Quantity, now / 60);
      if (_lastCapture == key) return;
      _lastCapture = key;

      GilStorage.InsertVentureReturn(now, retainer, reward.ItemId, reward.Quantity, reward.IsHq);
      VentureReturns.InvalidateCache();
      Svc.Log.Debug($"[Ventures] {retainer}: {reward.Quantity}x {reward.ItemId}{(reward.IsHq ? " HQ" : "")}");
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Ventures] capture failed: {ex.Message}");
    }
  }

  /// <summary>
  /// Best-guess offsets first (VERIFY in-game), then a validated scan:
  /// first UInt/Int that is a real Item row (with the +1M HQ convention),
  /// quantity from the next small positive int. Null = unparseable.
  /// </summary>
  private static unsafe (uint ItemId, int Quantity, bool IsHq)? ParseReward(AtkUnitBase* addon)
  {
    var sheet = Svc.Data.GetExcelSheet<Item>();

    // Guess: value[2] = item id, value[3] = quantity (VERIFY - unmapped).
    if (addon->AtkValuesCount > 3
        && TryAsItem(addon->AtkValues[2], sheet) is { } guessed
        && TryAsQuantity(addon->AtkValues[3]) is int guessedQty)
      return (guessed.ItemId, guessedQty, guessed.IsHq);

    // Scan fallback: adjacent (item id, quantity) pair anywhere.
    for (var i = 0; i + 1 < addon->AtkValuesCount; i++)
      if (TryAsItem(addon->AtkValues[i], sheet) is { } item
          && TryAsQuantity(addon->AtkValues[i + 1]) is int qty)
        return (item.ItemId, qty, item.IsHq);

    // Nothing parseable - dump for the live mapping pass.
    var dump = new System.Text.StringBuilder("[Ventures] map needed - RetainerTaskResult values: ");
    for (var i = 0; i < Math.Min((int)addon->AtkValuesCount, 30); i++)
      dump.Append($"[{i}]={addon->AtkValues[i].GetValueAsString()} ");
    Svc.Log.Info(dump.ToString());
    return null;
  }

  private static (uint ItemId, bool IsHq)? TryAsItem(AtkValue v, Lumina.Excel.ExcelSheet<Item> sheet)
  {
    var raw = v.Type switch
    {
      AtkValueType.UInt => v.UInt,
      AtkValueType.Int when v.Int > 0 => (uint)v.Int,
      _ => 0u,
    };
    if (raw == 0) return null;
    var isHq = raw >= 1_000_000u;
    var id = isHq ? raw - 1_000_000u : raw;
    // Real, obtainable item - excludes flags/indices that happen to be small ints.
    return id > 19 && sheet.TryGetRow(id, out var row) && row.Name.ByteLength > 0
      ? (id, isHq)
      : null;
  }

  private static int? TryAsQuantity(AtkValue v)
  {
    var q = v.Type switch
    {
      AtkValueType.UInt => (int)v.UInt,
      AtkValueType.Int => v.Int,
      _ => -1,
    };
    return q is >= 1 and <= 9999 ? q : null;
  }
}

/// <summary>
/// Derived venture economics over a rolling window. Values returns with the
/// player's own quality-split sale prices, vendor floor as the fallback -
/// local evidence only, same rules as everything else in the era.
/// </summary>
internal static class VentureReturns
{
  internal const int WindowDays = 30;
  /// <summary>Seals per venture token at the GC quartermaster.</summary>
  internal const int SealsPerToken = 200;

  private static (long GilPerVenture, int Ventures, long TotalValue)? _cached;
  private static DateTime _cacheAt = DateTime.MinValue;

  internal static void InvalidateCache() => _cacheAt = DateTime.MinValue;

  /// <summary>Rolling-window stats, cached 5 minutes. Null when storage is unavailable.</summary>
  internal static (long GilPerVenture, int Ventures, long TotalValue)? Stats()
  {
    if (DateTime.UtcNow - _cacheAt < TimeSpan.FromMinutes(5))
      return _cached;
    _cacheAt = DateTime.UtcNow;

    try
    {
      var rows = GilStorage.GetVentureReturns(WindowDays);
      if (rows.Count == 0)
        return _cached = (0, 0, 0);

      var sheet = Svc.Data.GetExcelSheet<Item>();
      long total = 0;
      foreach (var r in rows)
        total += (long)ValuePerUnit(r.ItemId, r.IsHq, sheet) * r.Quantity;

      // One capture row per collected venture (the dialog shows one reward).
      var ventures = rows.Count;
      return _cached = (total / ventures, ventures, total);
    }
    catch
    {
      return _cached = null;
    }
  }

  /// <summary>Own last sale for the variant, else vendor price - never a guess.</summary>
  internal static int ValuePerUnit(uint itemId, bool isHq, Lumina.Excel.ExcelSheet<Item> sheet)
  {
    try
    {
      if (GilStorage.GetLastSalePrice(itemId, isHq) is int sale && sale > 0)
        return sale;
    }
    catch { /* storage unavailable - vendor floor below */ }
    return sheet.TryGetRow(itemId, out var row) ? (int)row.PriceLow : 0;
  }

  /// <summary>
  /// The empirical seals-to-gil rate: gil-per-venture spread over the seal
  /// cost of the tokens a quick venture consumes. Null until enough data
  /// exists (10+ ventures in the window) - callers fall back to the config
  /// placeholder. THE number the GC exit was waiting for.
  /// </summary>
  internal static int? EmpiricalSealToGilRate()
  {
    if (Stats() is not { Ventures: >= 10 } s)
      return null;
    var sealsPerVenture = SealsPerToken * Plugin.Configuration.VentureTokensPerVenture;
    if (sealsPerVenture <= 0) return null;
    var rate = (int)(s.GilPerVenture / sealsPerVenture);
    return rate > 0 ? rate : null;
  }

  /// <summary>Token stock delta per day over the window (negative = burning), or null.</summary>
  internal static double? BurnPerDay()
  {
    try
    {
      if (GilStorage.GetVentureTokenSpan(WindowDays) is not { } span)
        return null;
      var days = (span.Last.Ts - span.First.Ts) / 86400.0;
      return days >= 1 ? (span.Last.Tokens - span.First.Tokens) / days : null;
    }
    catch { return null; }
  }
}
