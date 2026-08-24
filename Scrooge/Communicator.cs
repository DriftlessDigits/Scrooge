using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Scrooge.Windows;

namespace Scrooge;

/// <summary>
/// Handles all in-game chat messages for the plugin.
/// Each Print method creates an SeString with a clickable item link when possible,
/// falling back to plain text if the item name can't be resolved.
/// </summary>
public static class Communicator
{
  private static readonly ExcelSheet<Item> ItemSheet = Svc.Data.GetExcelSheet<Item>();

  /// <summary>Shows the price change details when an item is successfully pinched.</summary>
  /// <param name="itemName">Raw item name from the game addon (may contain SeString control chars).</param>
  /// <param name="oldPrice">The item's previous listing price.</param>
  /// <param name="newPrice">The new price that was set.</param>
  /// <param name="cutPercentage">Percentage change from old to new (negative = price cut).</param>
  public static void PrintPriceUpdate(string itemName, int? oldPrice, int? newPrice, float cutPercentage)
  {
    if (!Plugin.Configuration.ShowPriceAdjustmentsMessages)
      return;

    if (oldPrice == null || newPrice == null || oldPrice.Value == newPrice.Value)
      return;

    // THE ARTICLE RIDES THE NOUN. "a increase of 12%" was a hardcoded "a" in front of a
    // word chosen at runtime; carrying the article with the word it belongs to is the
    // fix that cannot come apart again, whatever nouns get added later.
    var dec = oldPrice.Value > newPrice.Value ? "a cut" : "an increase";
    var move = MathF.Abs(MathF.Round(cutPercentage, 2));
    var itemPayload = RawItemNameToItemPayload(itemName);

    if (itemPayload != null)
    {
      var seString = new SeStringBuilder()
          .AddItemLink(itemPayload.ItemId, itemPayload.IsHQ)
          .AddText($": Pinching from {oldPrice.Value:N0} to {newPrice.Value:N0} gil, {dec} of {move}%")
          .Build();

      Svc.Chat.Print(seString);
    }
    else
      Svc.Chat.Print($"{itemName}: Pinching from {oldPrice.Value:N0} to {newPrice.Value:N0}, {dec} of {move}%");
  }

  /// <summary>
  /// Converts a raw item name string (from the game's addon text nodes) into
  /// an ItemPayload for creating clickable item links in chat.
  /// Handles the messy encoding: SeString control characters, multi-payload
  /// names, and the HQ icon suffix (U+E03C).
  /// Returns null if the item can't be found in the game data.
  /// </summary>
  /// <param name="itemName">Raw item name from the game addon (may contain SeString control chars and HQ icon).</param>
  /// <returns>An ItemPayload with the resolved item ID and HQ flag, or null if lookup fails.</returns>
  /// <summary>
  /// Strips SeString control characters from a raw item name, returning clean display text.
  /// Also returns whether the item is HQ via the out parameter.
  /// </summary>
  internal static string CleanItemName(string itemName, out bool isHq)
  {
    isHq = false;

    var seString = SeString.Parse(Encoding.UTF8.GetBytes(itemName));

    var textPayloads = seString.Payloads
        .OfType<TextPayload>()
        .ToList();

    if (textPayloads.Count == 0)
      return itemName;

    string cleanedName;

    if (textPayloads.Count == 1)
    {
      cleanedName = textPayloads[0].Text?.Trim() ?? itemName;
    }
    else
    {
      // Skip the first payload (it's always just "%" with ETX)
      // Concatenate payloads starting from index 1
      var nameParts = new StringBuilder();

      for (int i = 1; i < textPayloads.Count; i++)
      {
        var text = textPayloads[i].Text;

        // First payload after the initial marker has a prefix: ANY_CHAR + ETX (U+0003)
        if (i == 1 && text?.Length >= 2 && text[1] == '\u0003')
          text = text[2..];

        nameParts.Append(text);
      }

      cleanedName = nameParts.ToString();

      // Check and clean HQ symbol at the very end
      if (cleanedName.Length >= 1 && cleanedName[^1] == '\uE03C')
      {
        isHq = true;
        cleanedName = cleanedName[..^1].TrimEnd();
      }
      else
        cleanedName = cleanedName.TrimEnd();
    }

    return cleanedName;
  }

  internal static ItemPayload? RawItemNameToItemPayload(string itemName)
  {
    var cleanedName = CleanItemName(itemName, out var isHq);

    // Search for the item
    var item = ItemSheet.FirstOrDefault(i =>
        i.Name.ToString().Equals(cleanedName, StringComparison.OrdinalIgnoreCase));

    if (item.RowId > 0)
    {
      var itemPayloadResult = new ItemPayload(item.RowId, isHq);
      return itemPayloadResult;
    }

    return null;
  }

  /// <summary>
  /// The crasher-guard's chat line. A WARNING since 2026-08-21, not a skip notice:
  /// nothing was ignored, the write is waiting on the player. The operands are printed
  /// here even though the run log speaks the same sentence - chat is where a player
  /// watching the pinch scroll by sees it first.
  /// </summary>
  public static void PrintDeepCutWarning(string itemName, int cutPct, long oldPrice, long proposed)
    => PrintItemError(itemName,
      $"Cutting {cutPct}% under the anchor - {oldPrice:N0} down to {proposed:N0}. "
      + "Competition or crasher? Confirm to follow the price.");

  /// <summary>
  /// The one floor verdict in chat. Names the binding floor and both operands - the
  /// honest ask that lost and the number it lost to - because "below the floor" with
  /// no numbers sends the player looking through three settings for which one fired.
  /// </summary>
  /// <remarks>INTERNAL because <see cref="EffectiveFloor"/> is: the one floor law made
  /// this line take the floor itself rather than re-deriving a label from config, and a
  /// public method cannot accept an internal operand. Nothing outside the plugin calls
  /// it.</remarks>
  internal static void PrintBelowPriceFloorError(string itemName, EffectiveFloor floor, long honestAsk)
  {
    // EVERY BINDING SPEAKS ITS OWN WORDING (the mechanical pile, 3b). The catch-all
    // used to be the vendor's, so a FloorBinding.None floor would have named a vendor
    // counter that never bound anything. Unreachable today - the verdict only fires
    // when EffectiveFloor.Refuses did, and that needs a floor above zero - which is
    // exactly why it is cheap insurance now and expensive the day a fifth binding
    // lands. The pin under it is what keeps it unfolded.
    var clause = floor.Binding switch
    {
      FloorBinding.PlayerMinimum => $"your {floor.Floor:N0} gil minimum",
      FloorBinding.DomanEnclave => $"the Doman Enclave's {floor.Floor:N0} gil (2x vendor)",
      FloorBinding.Vendor => $"the vendor's {floor.Floor:N0} gil",
      _ => "the floor",
    };
    PrintItemError(itemName,
      $"No legal ask - honest price {honestAsk:N0} gil/ea sits under {clause}. "
      + "List sits out; the other exits compete.");
  }

  /// <summary>Prints a chat message when an item is vendor-sold through the retainer.</summary>
  public static void PrintVendorSold(string itemName, int vendorPrice, int quantity)
  {
    var total = vendorPrice * quantity;
    var itemPayload = RawItemNameToItemPayload(itemName);
    if (itemPayload != null)
    {
      var seString = new SeStringBuilder()
          .AddItemLink(itemPayload.ItemId, itemPayload.IsHQ)
          .AddText($": Vendor-sold for {total:N0} gil")
          .Build();
      Svc.Chat.Print(seString);
    }
    else
      Svc.Chat.Print($"{itemName}: Vendor-sold for {total:N0} gil");
  }

  /// <summary>Prints the retainer name header when starting to pinch a retainer's items.</summary>
  /// <param name="name">The retainer's display name.</param>
  public static void PrintRetainerName(string name)
  {
    if (!Plugin.Configuration.ShowRetainerNames)
      return;

    var seString = new SeStringBuilder()
        .AddText("Now Pinching items of retainer: ")
        .AddUiForeground(name, 561)
        .Build();
    Svc.Chat.Print(seString);
  }

  /// <summary>
  /// THE NO-PRICE LINE, CARRYING ITS REASON (V22). It said only that nothing was
  /// written - over a row whose reason was already composed, already in the run log and
  /// already banked - so the player got the refusal in chat and had to go somewhere
  /// else to learn why. <see cref="PrintLaneHeld"/> has printed the row's own reason
  /// since Phase 3b (<see cref="PricingVoice.HeldReason"/>); this is that pattern at
  /// the other door.
  ///
  /// <para>Printed here rather than through <see cref="PrintItemError"/> because the
  /// sentence names the item INSIDE itself: that helper prefixes the link and would put
  /// the name on the line twice. Same chat gate, same link, one copy of one fact.</para>
  /// </summary>
  /// <param name="itemName">Raw item name from the game addon.</param>
  /// <param name="reason">
  /// The row's own reason. Defaults to the sentence the only caller's run-log row
  /// already speaks - the board came back with nothing - so chat and the transcript
  /// cannot carry two wordings of one refusal. Empty prints the bare verdict.
  /// </param>
  public static void PrintNoPriceToSetError(string itemName, string? reason = null)
  {
    if (!Plugin.Configuration.ShowErrorsInChat)
      return;

    var why = reason ?? RunLogVoice.Reasons.NoBoardData;
    var clause = string.IsNullOrWhiteSpace(why) ? "." : $" - {Closed(why)}";
    const string tail = " Set it manually if you want it up.";

    var itemPayload = RawItemNameToItemPayload(itemName);
    if (itemPayload != null)
    {
      var seString = new SeStringBuilder()
        .AddText("No price to set for ")
        .AddItemLink(itemPayload.ItemId, itemPayload.IsHQ)
        .AddText($"{clause}{tail}")
        .Build();
      Svc.Chat.PrintError(seString);
    }
    else
      Svc.Chat.PrintError(
        $"No price to set for {CleanItemName(itemName, out _)}{clause}{tail}");
  }

  /// <summary>One trailing period, never two. The reason sentences arrive from several
  /// composers and most of them already end in one.</summary>
  private static string Closed(string text)
  {
    var trimmed = text.Trim();
    return trimmed.Length == 0 || trimmed[^1] is '.' or '!' or '?' ? trimmed : trimmed + ".";
  }

  /// <summary>Error: user tried to auto-pinch but all retainers are disabled in config.</summary>
  public static void PrintAllRetainersDisabled()
  {
    var seString = new SeStringBuilder()
        .AddText("All retainers are disabled. Open configuration with ")
        .Add(Plugin.ConfigLinkPayload)
        .AddUiForeground("/scrooge", 31) // Bright yellow color for better visibility
        .Build();

    Svc.Chat.PrintError(seString);
  }

  /// <summary>Shared helper for printing item error messages to chat with item link.</summary>
  private static void PrintItemError(string itemName, string chatMessage)
  {
    if (!Plugin.Configuration.ShowErrorsInChat)
      return;

    var itemPayload = RawItemNameToItemPayload(itemName);
    if (itemPayload != null)
    {
      var seString = new SeStringBuilder()
          .AddItemLink(itemPayload.ItemId, itemPayload.IsHQ)
          .AddText($": {chatMessage}")
          .Build();
      Svc.Chat.PrintError(seString);
    }
    else
      Svc.Chat.PrintError($"{itemName}: {chatMessage}");
  }

  /// <summary>Chat summary after a triage run completes.</summary>
  public static void PrintStandingSummary(int vendorCount, long totalGil, int pulledCount = 0)
  {
    var parts = new List<string>();
    if (vendorCount > 0)
      parts.Add($"vendored {vendorCount} {(vendorCount == 1 ? "item" : "items")} for {totalGil:N0} gil");
    if (pulledCount > 0)
      parts.Add($"pulled {pulledCount} {(pulledCount == 1 ? "item" : "items")}");

    if (parts.Count == 0)
    {
      Svc.Chat.Print("[Scrooge] Standing listings complete — no items processed.");
      return;
    }

    Svc.Chat.Print($"[Scrooge] Standing listings complete — {string.Join(", ", parts)}.");
  }

  /// <summary>
  /// Celebrates a gil goal crossing — headline in gold, a line of counting-house
  /// flavor, and a clickable link to the dashboard. Never fires mid-run
  /// (goal evaluation only runs at snapshot writes).
  /// </summary>
  public static void PrintGoalReached(string headline, string flavor)
  {
    var seString = new SeStringBuilder()
        .AddText("[Scrooge] ")
        .AddUiForeground($"{headline}!", 31)
        .AddText($" {flavor} ")
        .Add(Plugin.ConfigLinkPayload)
        .AddUiForeground("[Dashboard]", 561)
        .Build();

    Svc.Chat.Print(seString);
  }

  /// <summary>Chat message when the player's own sale history prices a market-silent item.</summary>
  public static void PrintOwnSalesFallback(string itemName, int price, int daysAgo)
  {
    if (!Plugin.Configuration.ShowPriceAdjustmentsMessages)
      return;

    var ageLabel = OnMarket.DayAge(daysAgo);
    var itemPayload = RawItemNameToItemPayload(itemName);
    if (itemPayload != null)
    {
      var seString = new SeStringBuilder()
        .AddItemLink(itemPayload.ItemId, itemPayload.IsHQ)
        .AddText($": Market is silent — using your own sale history ({price:N0} gil, sold {ageLabel})")
        .Build();
      Svc.Chat.Print(seString);
    }
    else
      Svc.Chat.Print($"{itemName}: Market is silent — using your own sale history ({price:N0} gil, sold {ageLabel})");
  }

  /// <summary>
  /// Chat message when the lane holds an item for the player's call.
  ///
  /// <para><b>IT SPEAKS THE ROW'S OWN REASON</b> (V12, ruled B7). The prefix used to be
  /// the hardcoded "(not enough sales)" - printed over an item whose evidence might say
  /// the board never answered at all, which is a different silence and sends the player
  /// looking at the wrong thing. <see cref="PricingVoice.HeldReason"/> already answered
  /// this per row and nothing was calling it. The long-form evidence is not repeated
  /// here: the two are documented as two LENGTHS of one fact, never two versions, and
  /// printing both in one line would say the same thing twice. The numbers behind it
  /// ride the run-log row's hover, where every other evidence layer lives.</para>
  /// </summary>
  /// <param name="reason">The row's own held reason, from <see cref="PricingVoice.HeldReason"/>.</param>
  public static void PrintLaneHeld(string itemName, string reason)
  {
    if (!Plugin.Configuration.ShowPriceAdjustmentsMessages)
      return;

    var itemPayload = RawItemNameToItemPayload(itemName);
    if (itemPayload != null)
    {
      var seString = new SeStringBuilder()
        .AddItemLink(itemPayload.ItemId, itemPayload.IsHQ)
        .AddText($": Held - {reason}")
        .Build();
      Svc.Chat.Print(seString);
    }
    else
      Svc.Chat.Print($"{itemName}: Held - {reason}");
  }
}
