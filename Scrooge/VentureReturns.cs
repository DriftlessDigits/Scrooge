using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
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
/// The capture is the collect's own chat grammar (rebuilt twice 08-16 - the
/// first mixed sweep proved the one-dialog-one-reward window wrong three ways:
/// loot lines race the dialog's arm under AutoRetainer, an exploration returns
/// several lines, and a late line captured under the NEXT retainer's session
/// mis-stamped the venture; the full-sweep transcript then proved a completion
/// -line gate wrong too, because Quick Exploration never prints one): "You
/// obtain" lines buffer while a retainer session is open, and "You pay
/// &lt;name&gt; N ventures." flushes them with the attribution and the cost in
/// the same ordered stream as the loot. No pay line, no row - a stale buffer
/// drops loudly rather than guessing an owner.
/// </summary>
internal sealed class VentureReturnTracker : IDisposable
{
  private static readonly Regex QuantityPattern = new(@"\b(\d+)\b", RegexOptions.Compiled);

  /// <summary>The collect's closing line: You pay Elwyn 2 ventures. Name and the
  /// tokens actually paid, in the same ordered stream as the loot.</summary>
  private static readonly Regex PayPattern =
    new(@"^You pay (.+?) (\d+) ventures?\.", RegexOptions.Compiled);

  /// <summary>Buffered loot awaiting its pay line.</summary>
  private readonly List<(DateTime At, uint ItemId, int Qty, bool IsHq)> _pending = [];
  private static readonly TimeSpan PendingTtl = TimeSpan.FromSeconds(30);

  // THE STAMP BANK (V42 fallback, live-proven necessary 08-15 same night): the game
  // clears the retainer's VentureId BEFORE the result dialog's PostSetup fires, so
  // the arm-time read always came back 0 and every collect logged "unstamped". The
  // only read that works is the one taken while the venture is still OUT - so the
  // outstanding ids are polled on a slow clock and banked per retainer, and the arm
  // spends the banked id. The bank self-corrects: a resend writes the new id on the
  // next poll, and an id is never overwritten by a transitional zero (GameSafe skips
  // them), so the value at arm time is the venture that just came home.
  private readonly Dictionary<string, uint> _ventureBank = new(StringComparer.Ordinal);
  private DateTime _nextBankPoll = DateTime.MinValue;
  private static readonly TimeSpan BankPollEvery = TimeSpan.FromSeconds(5);


  // Dedup: both reward phrasings ("is added to your inventory" / "You obtain")
  // and re-shown dialogs must never double-count the same venture.
  private (string Retainer, uint ItemId, int Qty, long Minute)? _lastCapture;

  public VentureReturnTracker()
  {
    Svc.Chat.ChatMessage += OnChatMessage;
    Svc.Framework.Update += OnFrameworkUpdate;
  }

  public void Dispose()
  {
    Svc.Chat.ChatMessage -= OnChatMessage;
    Svc.Framework.Update -= OnFrameworkUpdate;
  }

  /// <summary>
  /// The stamp bank's poll: every 5 seconds, every outstanding venture id by
  /// retainer. Cheap (one struct walk, no SQL, no sheets), and the only moment the
  /// id is readable at all - see the bank's field note.
  /// </summary>
  private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework _)
  {
    SweepPending();
    if (DateTime.UtcNow < _nextBankPoll) return;
    _nextBankPoll = DateTime.UtcNow + BankPollEvery;
    try
    {
      foreach (var (name, ventureId) in GameSafe.RetainerVentures())
        _ventureBank[name] = ventureId;
    }
    catch { /* unreadable this tick - the bank keeps what it had */ }
  }

  /// <summary>
  /// THE COLLECT IS A CHAT PARAGRAPH, and the pay line is its signature. The
  /// stream for one collect is ordered and self-describing:
  ///
  /// <code>
  ///   You obtain 10 Allagan silver pieces.                 &lt;- loot, one line each
  ///   You obtain 4 clumps of tumbleclaw weeds.
  ///   You assign your retainer "..."                       &lt;- (reassign, ignored)
  ///   You pay Elwyn 2 ventures.                            &lt;- closes it: NAME + COST
  /// </code>
  ///
  /// Obtain lines buffer while a retainer session is open, then flush attributed
  /// to the retainer the PAY LINE names - same ordered stream as the loot, so the
  /// AutoRetainer race that mis-stamped the first exploration structurally cannot
  /// cross a collect's rows onto another retainer. The venture id is the bank's
  /// entry for that name; the token cost is the pay line's own number - the game
  /// saying what was actually paid. Explorations print a "... is now complete."
  /// heading and quicks do not (live receipt 08-16), which is why nothing here
  /// gates on one.
  /// </summary>
  private void OnChatMessage(IHandleableChatMessage chatMessage)
  {
    try
    {
      var text = chatMessage.Message.TextValue;

      if (PayPattern.Match(text) is { Success: true } pay)
      {
        var tokens = int.TryParse(pay.Groups[2].Value, out var t) && t > 0 ? t : (int?)null;
        FlushPending(pay.Groups[1].Value, tokens, "pay line");
        return;
      }

      // The verb gate keeps the duplicate "... is added to your inventory"
      // phrasing out. THE RETAINER SESSION IS THE SCOPE, not a completion line
      // (live receipt 08-16, Drift's full sweep transcript: Quick Exploration
      // prints NO "is now complete." line, so a paragraph gated on one dropped
      // every quick's reward while banking the explorations around them). A
      // gathering or quest "You obtain" has no retainer open and buffers nowhere.
      if (!text.StartsWith("You obtain", StringComparison.OrdinalIgnoreCase)
          || GameSafe.ActiveRetainerName() is null)
        return;

      var itemPayload = chatMessage.Message.Payloads.OfType<ItemPayload>().FirstOrDefault();
      if (itemPayload == null)
      {
        Svc.Log.Debug($"[Ventures] obtain line with no ItemPayload dropped: \"{text}\"");
        return;
      }

      // Quantity: leading count when present ("You obtain 2 ..."), else 1. The raw
      // line rides the debug log (live receipt 08-16: knowing exactly which SeString
      // arrived is what cracked the first mis-stamp - keep the evidence flowing).
      var qtyMatch = QuantityPattern.Match(text);
      var quantity = qtyMatch.Success && int.TryParse(qtyMatch.Groups[1].Value, out var n) && n > 0 ? n : 1;
      Svc.Log.Debug($"[Ventures] buffered obtain line: \"{text}\" -> qty {quantity}");

      _pending.Add((DateTime.UtcNow, itemPayload.ItemId, quantity, itemPayload.IsHQ));
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Ventures] capture failed: {ex.Message}");
    }
  }

  /// <summary>
  /// Banks the buffered loot to the retainer the pay line names. Cost rides the
  /// FIRST row only (cost-once: a multi-line collect must not bank the same tokens
  /// once per reward); the pay line's own count outranks the sheet's, because it
  /// is what was actually paid.
  /// </summary>
  private void FlushPending(string payLineName, int? tokensPaid, string why)
  {
    if (_pending.Count == 0) return;
    var rows = _pending.ToList();
    _pending.Clear();

    var retainer = payLineName;

    var ventureId = _ventureBank.TryGetValue(retainer, out var banked) ? banked : (uint?)null;
    var (sheetCost, category) = ResolveVenture(ventureId);
    var cost = tokensPaid ?? sheetCost;

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    foreach (var (_, itemId, quantity, isHq) in rows)
    {
      var key = (retainer, itemId, quantity, now / 60);
      if (_lastCapture == key) continue;
      _lastCapture = key;
      GilStorage.InsertVentureReturn(now, retainer, itemId, quantity, isHq, ventureId, cost, category);
      Svc.Log.Info($"[Ventures] {retainer}: {quantity}x {itemId}{(isHq ? " HQ" : "")} " +
        $"captured from chat ({(ventureId is { } vid ? $"venture {vid} {category}{(cost is { } c ? $" {c} tokens" : "")}" : "unstamped")}, {why})");
      cost = null; // cost-once: siblings carry NULL
    }
    VentureReturns.InvalidateCache();
  }

  /// <summary>
  /// A stale buffer is loot whose pay line never came - a view-only collect, or a
  /// non-venture "You obtain" that happened near an open retainer (a bell run's
  /// pulls, say). With no pay line there is no attribution, and a guessed owner
  /// would bank somebody's bell pull as a venture return - so the stale buffer
  /// DROPS, loudly, and the log says what was lost.
  /// </summary>
  private void SweepPending()
  {
    if (_pending.Count == 0) return;
    if (DateTime.UtcNow - _pending[0].At < PendingTtl) return;
    Svc.Log.Warning($"[Ventures] {_pending.Count} buffered loot line(s) dropped - "
      + "no pay line arrived to attribute them");
    _pending.Clear();
  }

  /// <summary>
  /// The banked venture id against the RetainerTask sheet: token cost and coarse
  /// category. (null, null) whenever anything in the chain doesn't resolve - an
  /// unstamped row is a fine outcome, a lost capture is not.
  /// </summary>
  private static (int? Cost, string? Category) ResolveVenture(uint? ventureId)
  {
    if (ventureId is not { } id || id == 0) return (null, null);
    try
    {
      var sheet = Svc.Data.GetExcelSheet<RetainerTask>();
      if (!sheet.TryGetRow(id, out var task)) return (null, null);

      string? randomName = null;
      if (task.IsRandom &&
          Svc.Data.GetExcelSheet<RetainerTaskRandom>().TryGetRow(task.Task.RowId, out var random))
        randomName = random.Name.ExtractText();

      return ((int)task.VentureCost, VentureStamp.Category(task.IsRandom, randomName));
    }
    catch (Exception ex)
    {
      Svc.Log.Debug($"[Ventures] venture {ventureId} did not resolve: {ex.Message}");
      return (null, null);
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

  private static (long GilPerVenture, int Ventures, long TotalValue)? _cached;
  // Derived in the same pass as _cached so there is exactly one window read and one
  // cache clock for both numbers.
  private static int? _cachedRate;
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
      {
        _cachedRate = null;
        return _cached = (0, 0, 0);
      }

      var sheet = Svc.Data.GetExcelSheet<Item>();
      var valued = new System.Collections.Generic.List<(long Value, int? VentureCost)>(rows.Count);
      long total = 0;
      foreach (var r in rows)
      {
        var value = (long)ValuePerUnit(r.ItemId, r.IsHq, sheet) * r.Quantity;
        total += value;
        valued.Add((value, r.VentureCost));
      }

      _cachedRate = VentureStamp.StampedSealToGilRate(valued);

      // One capture row per collected venture (the dialog shows one reward).
      var ventures = rows.Count;
      return _cached = (total / ventures, ventures, total);
    }
    catch
    {
      _cachedRate = null;
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
  /// The empirical seals-to-gil rate: what the STAMPED ventures in the window
  /// returned, over the seals their tokens actually cost. Null until 10+ stamped
  /// rows exist - callers fall back to the config placeholder. THE number the GC
  /// exit was waiting for.
  ///
  /// <para>Stamped rows only (V42, 08-15). The old form divided gil-per-venture by
  /// a config knob that asserted every venture costs 2 tokens; the sheet says what
  /// each one cost and the row carries it, so nothing here is believed any more.
  /// Pre-stamp rows are excluded whole - value and cost both - because imputing
  /// their cost is the guess the knob died for. <see cref="Stats"/> still counts
  /// them: gil-per-venture is type-blind by nature and correct as it stands.</para>
  /// </summary>
  internal static int? EmpiricalSealToGilRate()
  {
    Stats(); // fills _cachedRate in the same window read
    return _cachedRate;
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
