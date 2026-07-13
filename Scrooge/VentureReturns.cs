using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Scrooge;

/// <summary>
/// Venture-return tracking: makes the GC exit numerically honest. Captures
/// each collected venture reward, then derives gil-per-venture and the
/// empirical seals-to-gil rate the routing engine has been running on a
/// placeholder for.
///
/// Data source is CHAT, not the addon. Live receipts 2026-07-12: the
/// RetainerTaskResult addon carries only its button bar in AtkValues
/// ([0]=2 [1]=Reassign [2]=Confirm) through PostSetup, PostRefresh AND
/// PostRequestedUpdate - the reward is never in the value array. But the
/// reward always prints to chat as "You obtain a &lt;item link&gt;" with a
/// full ItemPayload (id + HQ), whether collected by hand or clicked through
/// by AutoRetainer in a frame.
///
/// So the addon opening ARMS a short capture window (PostSetup provably
/// fires even under AutoRetainer's instant click-through), and the next
/// "You obtain" chat line with an ItemPayload inside that window is the
/// venture reward. Disarms after one capture - one dialog, one reward.
/// </summary>
internal sealed class VentureReturnTracker : IDisposable
{
  private static readonly AddonEvent[] Events =
    [AddonEvent.PostSetup, AddonEvent.PostRefresh, AddonEvent.PostRequestedUpdate];

  private static readonly Regex QuantityPattern = new(@"\b(\d+)\b", RegexOptions.Compiled);
  private static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(5);

  private DateTime _armedUntil = DateTime.MinValue;
  private string _armedRetainer = "";

  // Dedup: both reward phrasings ("is added to your inventory" / "You obtain")
  // and re-shown dialogs must never double-count the same venture.
  private (string Retainer, uint ItemId, int Qty, long Minute)? _lastCapture;

  public VentureReturnTracker()
  {
    foreach (var ev in Events)
      Svc.AddonLifecycle.RegisterListener(ev, "RetainerTaskResult", OnTaskResult);
    Svc.Chat.ChatMessage += OnChatMessage;
  }

  public void Dispose()
  {
    foreach (var ev in Events)
      Svc.AddonLifecycle.UnregisterListener(ev, "RetainerTaskResult", OnTaskResult);
    Svc.Chat.ChatMessage -= OnChatMessage;
  }

  private void OnTaskResult(AddonEvent type, AddonArgs args)
  {
    try
    {
      var retainer = GameSafe.ActiveRetainerName() ?? "";
      if (retainer.Length == 0)
      {
        // No attribution, no row - but never silently (finding 9).
        Svc.Log.Debug($"[Ventures] {type}: no active retainer name - not arming");
        return;
      }

      _armedRetainer = retainer;
      _armedUntil = DateTime.UtcNow + ArmWindow;
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Ventures] arm failed: {ex.Message}");
    }
  }

  private void OnChatMessage(IHandleableChatMessage chatMessage)
  {
    if (DateTime.UtcNow > _armedUntil) return;

    try
    {
      var text = chatMessage.Message.TextValue;

      // Verb gate - the reward line in EN. The duplicate "... is added to
      // your inventory" phrasing is deliberately NOT matched; one line per
      // reward keeps the capture single-shot.
      if (!text.StartsWith("You obtain", StringComparison.OrdinalIgnoreCase))
        return;

      var itemPayload = chatMessage.Message.Payloads.OfType<ItemPayload>().FirstOrDefault();
      if (itemPayload == null)
      {
        Svc.Log.Debug($"[Ventures] skipped (no ItemPayload): {text}");
        return;
      }

      // Quantity: leading count when present ("You obtain 2 ..."), else 1.
      var qtyMatch = QuantityPattern.Match(text);
      var quantity = qtyMatch.Success && int.TryParse(qtyMatch.Groups[1].Value, out var n) && n > 0 ? n : 1;

      var retainer = GameSafe.ActiveRetainerName() ?? _armedRetainer;
      var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      var key = (retainer, itemPayload.ItemId, quantity, now / 60);
      if (_lastCapture == key) return;
      _lastCapture = key;
      _armedUntil = DateTime.MinValue;

      GilStorage.InsertVentureReturn(now, retainer, itemPayload.ItemId, quantity, itemPayload.IsHQ);
      VentureReturns.InvalidateCache();
      Svc.Log.Info($"[Ventures] {retainer}: {quantity}x {itemPayload.ItemId}{(itemPayload.IsHQ ? " HQ" : "")} captured from chat");
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Ventures] capture failed: {ex.Message}");
    }
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
