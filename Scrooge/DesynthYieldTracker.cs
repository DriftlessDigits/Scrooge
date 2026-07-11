using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Scrooge;

/// <summary>
/// Subscribes to chat during desynth runs and writes desynth_yields rows for
/// every observed obtain/synthesize event. Locale-bias is mitigated by gating
/// on an active Scrooge desynth run plus an ItemPayload presence — text-side
/// matching is just for the verb prefix and the quantity number.
/// </summary>
internal sealed class DesynthYieldTracker : IDisposable
{
  private static readonly Regex QuantityPattern = new(@"\b(\d+)\b", RegexOptions.Compiled);

  internal DesynthYieldTracker()
  {
    Svc.Chat.ChatMessage += OnChatMessage;
  }

  public void Dispose()
  {
    Svc.Chat.ChatMessage -= OnChatMessage;
  }

  private void OnChatMessage(IHandleableChatMessage chatMessage)
  {
    // Gate 1: active Scrooge desynth run with current item + run id set.
    if (Plugin.CurrentRun is not RunData run) return;
    if (run.Mode != RunMode.Desynth) return;
    if (run.DesynthRunId is not long runId) return;
    if (run.CurrentItem is not PricingItem currentItem) return;

    var typeId = (int)chatMessage.LogKind;
    var message = chatMessage.Message;
    var text = message.TextValue;

    // Verb gate — desynth output is always one of these phrasings in EN. For
    // other locales we'd extend this list (or drop it and rely solely on the
    // active-run + ItemPayload gate).
    if (!text.StartsWith("You obtain", StringComparison.OrdinalIgnoreCase)
        && !text.StartsWith("You synthesize", StringComparison.OrdinalIgnoreCase))
    {
      // Diagnostic during smoke — log unmatched chat during a desynth run so
      // we can spot any verb variants we missed. Cheap because it only runs
      // while a desynth run is in flight.
      Svc.Log.Debug($"[YieldTracker] skipped (no verb match): type={typeId} text={text}");
      return;
    }

    var itemPayload = message.Payloads.OfType<ItemPayload>().FirstOrDefault();
    if (itemPayload == null)
    {
      Svc.Log.Debug($"[YieldTracker] skipped (no ItemPayload): type={typeId} text={text}");
      return;
    }

    // Quantity: first integer in the text; absent (e.g. "You obtain a foo") → 1.
    var qtyMatch = QuantityPattern.Match(text);
    var quantity = qtyMatch.Success && int.TryParse(qtyMatch.Groups[1].Value, out var n) && n > 0 ? n : 1;

    var yield = new DesynthYield
    {
      RunId = runId,
      AttemptSeq = Plugin.DesynthOrchestrator.CurrentAttemptSeq,
      SourceItemId = currentItem.ItemId,
      SourceIsHq = currentItem.IsHq,
      YieldItemId = itemPayload.ItemId,
      YieldQty = quantity,
      YieldIsHq = itemPayload.IsHQ,
      CapturedAt = DateTimeOffset.UtcNow,
    };

    try
    {
      Plugin.DesynthYieldStore?.InsertYield(yield);
      Plugin.DesynthYieldStore?.PublishYieldCaptured(yield);
      Svc.Log.Debug($"[YieldTracker] captured: type={typeId} src={currentItem.ItemId} yield={itemPayload.ItemId}x{quantity} seq={yield.AttemptSeq}");
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, $"[Scrooge] Failed to insert desynth yield (run={runId}, seq={yield.AttemptSeq})");
    }
  }
}
