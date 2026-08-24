using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Scrooge;

/// <summary>
/// Captures Materiel Container pulls: which 20k-seal box was opened, and what
/// came out of it. The seals' second exit, banked (V43).
///
/// <para>Ventures are one way Grand Company seals become gil; the quartermaster's
/// Materiel Container 3.0 / 4.0 is the other. The box costs 20,000 seals and returns
/// a random mount or minion, and those are marketable - the eventual sale already
/// rides the normal receipts pipeline. What was missing was the purchase side of the
/// trade: nothing recorded that a box was opened, so a sale six days later had no
/// cost to sit against.</para>
///
/// <para><b>Nothing reads these rows in 3.0.</b> Ruled 08-15 (Drift): <i>let the data
/// bake.</i> This is passive gathering only. Comparing the two exits on a handful of
/// pulls would be advising off noise, so the arithmetic waits for 3.1 and the writer
/// ships now to give it a book to read.</para>
///
/// <para><b>The trigger is bags, not an addon.</b> Unlike venture coffers - which the
/// plugin's own orchestrator opens, and can therefore report - these boxes are opened
/// by hand from the inventory: right-click, use. There is no dialog whose lifecycle
/// the plugin can hang a listener on. So the count of the watched containers is polled
/// on Framework update, and a DECREASE is the "a box was just used" signal. It arms a
/// short window; the next "You obtain" chat line with an ItemPayload inside that window
/// is the pull. One capture per arm - one box, one reward.</para>
///
/// <para><b>Scope is structural, not procedural.</b> The only thing that can arm this
/// watcher is one of the watched container ids going down in the bags. Ordinary
/// treasure coffers, venture coffers, deep dungeon chests and every other "You obtain"
/// line in the game reach a disarmed watcher and are dropped on the first line of
/// <see cref="OnChatMessage"/>. There is no filter to get wrong, because there is no
/// path from an unwatched item to a row.</para>
///
/// <para>Ids resolve by NAME off the Item sheet at construction, never hardcoded: a
/// literal from memory is a claim nobody can check, and a wrong one silently watches
/// something else forever. A name that doesn't resolve is logged once and dropped -
/// an empty watch list makes the watcher idle, which is the correct failure.</para>
/// </summary>
internal sealed class CofferPullWatcher : IDisposable
{
  /// <summary>
  /// The boxes worth watching, by their exact sheet names. 3.0 and 4.0 are the two
  /// the quartermaster sells for seals; the older tiers are not seal exits.
  /// </summary>
  private static readonly string[] WatchedNames =
    ["Materiel Container 3.0", "Materiel Container 4.0"];

  private static readonly Regex QuantityPattern = new(@"\b(\d+)\b", RegexOptions.Compiled);
  private static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(5);

  /// <summary>Resolved container ids. Empty = nothing resolved, and the watcher idles.</summary>
  private readonly uint[] _watched;

  /// <summary>
  /// Last readable bag count per watched id. A missing entry is "no baseline" -
  /// the state an unreadable poll drops back to, and the state that cannot arm.
  /// </summary>
  private readonly Dictionary<uint, int> _baseline = [];

  private DateTime _armedUntil = DateTime.MinValue;
  private uint _armedContainer;

  // Dedup: the same box+pull inside one minute is the same event re-heard, not a
  // second box (the VentureReturnTracker rule, same reason).
  private (uint Container, uint ItemId, int Qty, long Minute)? _lastCapture;

  public CofferPullWatcher()
  {
    _watched = ResolveWatchedIds();

    Svc.Framework.Update += OnFrameworkUpdate;
    Svc.Chat.ChatMessage += OnChatMessage;
  }

  public void Dispose()
  {
    Svc.Framework.Update -= OnFrameworkUpdate;
    Svc.Chat.ChatMessage -= OnChatMessage;
  }

  /// <summary>
  /// One pass over the Item sheet, banking the ids of the watched names
  /// (case-insensitive). Done once at construction - the sheet does not change
  /// under a running game, and a per-tick lookup would be a sheet walk per frame.
  /// </summary>
  private static uint[] ResolveWatchedIds()
  {
    var found = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    try
    {
      foreach (var row in Svc.Data.GetExcelSheet<Item>())
      {
        var name = row.Name.ExtractText();
        if (name.Length == 0) continue;
        foreach (var watched in WatchedNames)
          if (string.Equals(name, watched, StringComparison.OrdinalIgnoreCase))
            found.TryAdd(watched, row.RowId);
      }
    }
    catch (Exception ex)
    {
      Svc.Log.Info($"[Coffers] item sheet walk failed - coffer capture idle this session: {ex.Message}");
      return [];
    }

    foreach (var watched in WatchedNames)
      if (!found.ContainsKey(watched))
        Svc.Log.Info($"[Coffers] \"{watched}\" did not resolve on the Item sheet - not watching it");

    if (found.Count > 0)
      Svc.Log.Info($"[Coffers] watching {string.Join(", ", found.Select(kv => $"{kv.Key} ({kv.Value})"))}");

    return [.. found.Values];
  }

  /// <summary>
  /// The poll. Two GetInventoryItemCount calls a tick at most, and none at all
  /// while logged out or when no id resolved.
  /// </summary>
  private void OnFrameworkUpdate(IFramework _)
  {
    if (_watched.Length == 0) return;

    // Logged out / loading: forget every baseline without arming. The count is not
    // "0 boxes", it is "no answer", and the difference is the phantom-pull guard.
    if (!ECommons.GameHelpers.Player.Available)
    {
      _baseline.Clear();
      return;
    }

    try
    {
      foreach (var id in _watched)
        Observe(id, GameSafe.InventoryItemCount(id));
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Coffers] bag poll failed: {ex.Message}");
    }
  }

  private void Observe(uint containerId, int? reading)
  {
    switch (CofferPullPairing.Decide(_baseline.TryGetValue(containerId, out var known) ? known : null, reading))
    {
      case CofferCountMove.Forget:
        _baseline.Remove(containerId);
        return;

      case CofferCountMove.Rebaseline:
        _baseline[containerId] = reading!.Value;
        return;

      case CofferCountMove.Arm:
        _baseline[containerId] = reading!.Value;
        _armedContainer = containerId;
        _armedUntil = DateTime.UtcNow + ArmWindow;
        Svc.Log.Debug($"[Coffers] container {containerId} count fell to {reading} - armed for the pull");
        return;

      default:
        return;
    }
  }

  private void OnChatMessage(IHandleableChatMessage chatMessage)
  {
    // Disarmed is the resting state, and every unwatched coffer in the game exits here.
    if (DateTime.UtcNow > _armedUntil) return;

    try
    {
      var text = chatMessage.Message.TextValue;
      if (!text.StartsWith("You obtain", StringComparison.OrdinalIgnoreCase))
        return;

      var itemPayload = chatMessage.Message.Payloads.OfType<ItemPayload>().FirstOrDefault();
      if (itemPayload == null)
      {
        Svc.Log.Debug($"[Coffers] skipped (no ItemPayload): {text}");
        return;
      }

      var qtyMatch = QuantityPattern.Match(text);
      var quantity = qtyMatch.Success && int.TryParse(qtyMatch.Groups[1].Value, out var n) && n > 0 ? n : 1;

      var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      var key = (_armedContainer, itemPayload.ItemId, quantity, now / 60);
      if (_lastCapture == key) return;
      _lastCapture = key;

      var container = _armedContainer;
      _armedUntil = DateTime.MinValue;
      _armedContainer = 0;

      GilStorage.InsertCofferPull(now, container, itemPayload.ItemId, quantity, itemPayload.IsHQ);
      Svc.Log.Info($"[Coffers] container {container}: {quantity}x {itemPayload.ItemId}" +
        $"{(itemPayload.IsHQ ? " HQ" : "")} captured from chat");
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Coffers] capture failed: {ex.Message}");
    }
  }
}
