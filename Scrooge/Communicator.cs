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

    var dec = oldPrice.Value > newPrice.Value ? "cut" : "increase";
    var itemPayload = RawItemNameToItemPayload(itemName);

    if (itemPayload != null)
    {
      var seString = new SeStringBuilder()
          .AddItemLink(itemPayload.ItemId, itemPayload.IsHQ)
          .AddText($": Pinching from {oldPrice.Value:N0} to {newPrice.Value:N0} gil, a {dec} of {MathF.Abs(MathF.Round(cutPercentage, 2))}%")
          .Build();

      Svc.Chat.Print(seString);
    }
    else
      Svc.Chat.Print($"{itemName}: Pinching from {oldPrice.Value:N0} to {newPrice.Value:N0}, a {dec} of {MathF.Abs(MathF.Round(cutPercentage, 2))}%");
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

  public static void PrintAboveMaxCutError(string itemName)
  {
    var pct = Plugin.Configuration.MaxUndercutPercentage;
    PrintItemError(itemName, $"Item ignored because it would cut the price by more than {pct}%");
  }

  public static void PrintAboveMaxIncreaseError(string itemName, float increasePercentage)
  {
    var actual = MathF.Abs(MathF.Round(increasePercentage, 1));
    var max = Plugin.Configuration.MaxPriceIncreasePercentage;
    PrintItemError(itemName, $"Item ignored because the price would increase by {actual}% (max {max}%)");
  }

  public static void PrintBelowPriceFloorError(string itemName)
  {
    var floorLabel = Plugin.Configuration.PriceFloorMode == PriceFloorMode.Vendor ? "Vendor price" : "Max Doman Enclave price (2x vendor)";
    PrintItemError(itemName, $"Item ignored because it would cut the price below {floorLabel}");
  }

  public static void PrintBelowMinimumListingPriceError(string itemName)
  {
    var minPrice = Plugin.Configuration.MinimumListingPrice.ToString("N0");
    PrintItemError(itemName, $"Item ignored because it would cut the price below the minimum listing price of {minPrice} gil");
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

  /// <summary>Informs the user that an outlier listing was detected and skipped.</summary>
  /// <param name="itemId">The item's row ID from the game data sheet.</param>
  /// <param name="outlierPrice">The bait listing price that was skipped.</param>
  /// <param name="nextPrice">The next valid price tier being used instead.</param>
  public static void PrintOutlierDetected(uint itemId, int outlierPrice, int nextPrice)
  {
    if (!Plugin.Configuration.ShowOutlierDetectionMessages)
      return;

    var itemPayload = new ItemPayload(itemId, false);

    var seString = new SeStringBuilder()
        .AddItemLink(itemPayload.ItemId, false)
        .AddText(": ")
        .AddUiForeground("Outlier detected", 540)
        .AddText(" — skipping ")
        .AddUiForeground($"{outlierPrice:N0}", 17)
        .AddText(" gil, using ")
        .AddUiForeground($"{nextPrice:N0}", 45)
        .AddText(" gil")
        .Build();

    Svc.Chat.Print(seString);
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

  public static void PrintNoPriceToSetError(string itemName)
  {
    PrintItemError(itemName, "No price to set, please set price manually");
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
  public static void PrintTriageSummary(int vendorCount, long totalGil, int pulledCount = 0)
  {
    var parts = new List<string>();
    if (vendorCount > 0)
      parts.Add($"vendored {vendorCount} {(vendorCount == 1 ? "item" : "items")} for {totalGil:N0} gil");
    if (pulledCount > 0)
      parts.Add($"pulled {pulledCount} {(pulledCount == 1 ? "item" : "items")}");

    if (parts.Count == 0)
    {
      Svc.Chat.Print("[Scrooge] Triage complete — no items processed.");
      return;
    }

    Svc.Chat.Print($"[Scrooge] Triage complete — {string.Join(", ", parts)}.");
  }

  /// <summary>Chat message when sale history is used instead of outlier listing.</summary>
  public static void PrintHistoryFallback(string itemName, int price, int saleCount)
  {
    if (!Plugin.Configuration.ShowPriceAdjustmentsMessages)
      return;

    var itemPayload = RawItemNameToItemPayload(itemName);
    if (itemPayload != null)
    {
      var seString = new SeStringBuilder()
        .AddItemLink(itemPayload.ItemId, itemPayload.IsHQ)
        .AddText($": Using sale history (median {price:N0} gil from {saleCount} sales)")
        .Build();
      Svc.Chat.Print(seString);
    }
    else
      Svc.Chat.Print($"{itemName}: Using sale history (median {price:N0} gil from {saleCount} sales)");
  }
}