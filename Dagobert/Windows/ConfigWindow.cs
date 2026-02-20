using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using ECommons.UIHelpers.AddonMasterImplementations;
using static ECommons.GenericHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Dagobert.Windows;

public sealed class ConfigWindow : Window
{
  private static readonly string[] _virtualKeyStrings = Enum.GetNames<VirtualKey>();

  /// <summary>Converts PascalCase enum names to display-friendly format (e.g. "FixedAmount" → "Fixed Amount").</summary>
  /// <param name="name">The raw PascalCase enum name.</param>
  /// <returns>The name with spaces inserted before each capital letter (except the first).</returns>
  private static string FormatEnumName(string name)
  {
    var result = new System.Text.StringBuilder();

    for (int i = 0; i < name.Length; i++)
    {
      if (i > 0 && char.IsUpper(name[i]))
        result.Append(' ');

      result.Append(name[i]);
    }

    return result.ToString();
  }

  public ConfigWindow()
    : base("Dagobert Configuration")
  { }

  public override void Draw()
  {

    // --- Undercut Settings ---
    ImGui.Text("-- Undercut Settings --");
    ImGui.BeginGroup();
    ImGui.Text("Mode: ");
    ImGui.SameLine();
    var enumValues = Enum.GetNames<UndercutMode>();
    var displayNames = enumValues.Select(FormatEnumName).ToArray();
    int index = Array.IndexOf(enumValues, Plugin.Configuration.UndercutMode.ToString());
    ImGui.SetNextItemWidth(150);
    if (ImGui.Combo("##undercutModeCombo", ref index, displayNames, displayNames.Length))
    {
      var value = Enum.Parse<UndercutMode>(enumValues[index]);
      if (value == UndercutMode.Percentage && Plugin.Configuration.UndercutAmount >= 100)
        Plugin.Configuration.UndercutAmount = 1;

      Plugin.Configuration.UndercutMode = value;
      Plugin.Configuration.Save();
    }
    ImGui.EndGroup();

    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("How to calculate the listing price relative to the lowest market board offer.\n\n" +
                       "Fixed Amount: Subtract a flat gil amount from the lowest listing.\n" +
                       "Percentage: Subtract a percentage of the lowest listing's price.\n" +
                       "Gentlemans Match: Match the lowest listing exactly — no undercut.\n" +
                       "Clean Numbers: Ronds down to a clean number. Interval scales with price.");
      ImGui.EndTooltip();
    }

    if (Plugin.Configuration.UndercutMode != UndercutMode.GentlemansMatch &&
        Plugin.Configuration.UndercutMode != UndercutMode.CleanNumbers)
    {
      ImGui.BeginGroup();
      ImGui.Text("Amount:");
      ImGui.SameLine();
      int amount = Plugin.Configuration.UndercutAmount;
      if (Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount)
      {
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("##undercutAmountFixed", ref amount))
        {
          Plugin.Configuration.UndercutAmount = Math.Clamp(amount, 1, int.MaxValue);
          Plugin.Configuration.Save();
        }
      }
      else
      {
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderInt("##undercutAmountPercentage", ref amount, 1, 99))
        {
          Plugin.Configuration.UndercutAmount = amount;
          Plugin.Configuration.Save();
        }
      }
      ImGui.SameLine();
      ImGui.Text($"{(Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount ? "Gil" : "%")}");
      ImGui.EndGroup();
      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("How much to undercut the lowest listing by.\n\n" +
                         "Fixed Amount: The exact number of gil to subtract.\n" +
                         "Percentage: The percentage of the listing price to subtract.");
        ImGui.EndTooltip();
      }

      ImGui.BeginGroup();
      ImGui.Text("Max Undercut percentage:");
      ImGui.SameLine();
      float maxUndercut = Plugin.Configuration.MaxUndercutPercentage;
      ImGui.SetNextItemWidth(150);
      if (ImGui.SliderFloat("##maximumUndercutAmountPercentage", ref maxUndercut, 0.1f, 99.9f, "%.1f"))
      {
        Plugin.Configuration.MaxUndercutPercentage = MathF.Round(maxUndercut, 1);
        Plugin.Configuration.Save();
      }
      ImGui.SameLine();
      ImGui.Text($"%");
      ImGui.EndGroup();
      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("Safety cap: skip an item if undercutting it would drop the price by more than this percentage.\n\n" +
                         "Protects against accidentally tanking prices when a single low outlier listing exists.\n" +
                         "Set to 99.9% to effectively disable this check.");
        ImGui.EndTooltip();
      }
    }

    var undercutSelf = Plugin.Configuration.UndercutSelf;
    if (ImGui.Checkbox("Undercut Self", ref undercutSelf))
    {
      Plugin.Configuration.UndercutSelf = undercutSelf;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("When enabled, your own retainer listings are treated like any other seller's.\n\n" +
                       "When disabled, if your retainer already has the lowest price, the listing is left unchanged.");
      ImGui.EndTooltip();
    }

    ImGui.SameLine(0,40);
    var hq = Plugin.Configuration.HQ;
    if (ImGui.Checkbox("Use HQ price", ref hq))
    {
      Plugin.Configuration.HQ = hq;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("When enabled, compares against HQ listings only for HQ items.\n\n" +
                       "If there are no HQ listings on the market board, the item will be skipped.\n" +
                       "Disable this to always compare against the cheapest listing regardless of quality.");
      ImGui.EndTooltip();
    }

    ImGui.Separator();
    // --- Price Floor Mode dropdown ---
    ImGui.Text("-- Price Floors --");
    ImGui.BeginGroup();
    ImGui.Text("Price Floor Mode:");
    ImGui.SameLine();
    var floorEnumValues = Enum.GetNames<PriceFloorMode>();
    var floorDisplayNames = floorEnumValues.Select(FormatEnumName).ToArray();
    int floorIndex = Array.IndexOf(floorEnumValues, Plugin.Configuration.PriceFloorMode.ToString());
    ImGui.SetNextItemWidth(150);
    if (ImGui.Combo("##priceFloorModeCombo", ref floorIndex, floorDisplayNames, floorDisplayNames.Length))
    {
      Plugin.Configuration.PriceFloorMode = Enum.Parse<PriceFloorMode>(floorEnumValues[floorIndex]);
      Plugin.Configuration.Save();
      Plugin.AutoPinch.ClearCachedPrices();
    }
    ImGui.EndGroup();
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Items are skipped when the undercut price falls below the selected floor.\n\n" +
                       "None: No price floor. Items are listed at any price.\n" +
                       "Vendor: Skip if price is below what a vendor would pay.\n" +
                       "Doman Enclave: Skip if price is below 2x vendor price (Assumes max Doman Enclave donation rate).");
      ImGui.EndTooltip();
    }
    ImGui.BeginGroup();
    ImGui.Text("Minimum Listing Price:");
    ImGui.SameLine();
    int minPrice = Plugin.Configuration.MinimumListingPrice;
    ImGui.SetNextItemWidth(100);
    if (ImGui.InputInt("##minimumListingPrice", ref minPrice))
    {
      Plugin.Configuration.MinimumListingPrice = Math.Max(minPrice, 0);
      Plugin.Configuration.Save();
      Plugin.AutoPinch.ClearCachedPrices();
    }
    ImGui.SameLine();
    ImGui.Text("Gil");
    ImGui.EndGroup();
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Items priced below this amount are skipped during auto-pinch.\n" +
                       "Each retainer slot is valuable — don't waste them on low-value items.\n\n" +
                       "Set to 0 to disable.");
      ImGui.EndTooltip();
    }

    ImGui.Separator();
    // --- Outlier Detection ---
    ImGui.Text("-- Outlier Detection --");
    var outlierDetection = Plugin.Configuration.OutlierDetection;
    if (ImGui.Checkbox("Outlier Detection", ref outlierDetection))
    {
      Plugin.Configuration.OutlierDetection = outlierDetection;
      Plugin.Configuration.Save();
      Plugin.AutoPinch.ClearCachedPrices();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Detect and skip abnormally low price listings on the market board.\n\n" +
                       "Compares each listing to the next — if the price jump is too large,\n" +
                       "the cheap listing is treated as an outlier and skipped.\n\n" +
                       "Only applies to NQ items. HQ items skip outlier detection.");
      ImGui.EndTooltip();
    }
    if (Plugin.Configuration.OutlierDetection)
    {
      ImGui.BeginGroup();
      ImGui.Text("Outlier Threshold:");
      ImGui.SameLine();
      float threshold = Plugin.Configuration.OutlierThresholdPercent;
      ImGui.SetNextItemWidth(150);
      if (ImGui.SliderFloat("##outlierThreshold", ref threshold, 10f, 90f, "%.0f"))
      {
        Plugin.Configuration.OutlierThresholdPercent = MathF.Round(threshold);
        Plugin.Configuration.Save();
        Plugin.AutoPinch.ClearCachedPrices();
      }
      ImGui.SameLine();
      ImGui.Text("%");
      ImGui.EndGroup();
      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("A price plunge is detected when the gap between two listings\n" +
                         "exceeds this percentage.\n\n" +
                         "Example at 50%: A listing at 40 gil is bait if the next is 100 gil\n" +
                         "(60% cheaper = plunge detected).\n\n" +
                         "Lower = catches smaller gaps, more aggressive.\n" +
                         "Higher = only catches large gaps, more tolerant.");
        ImGui.EndTooltip();
      }

      ImGui.BeginGroup();
      ImGui.Text("Search Window:");
      ImGui.SameLine();
      int searchWindow = Plugin.Configuration.OutlierSearchWindow;
      ImGui.SetNextItemWidth(150);
      if (ImGui.SliderInt("##outlierSearchWindow", ref searchWindow, 1, 9))
      {
        Plugin.Configuration.OutlierSearchWindow = searchWindow;
        Plugin.Configuration.Save();
        Plugin.AutoPinch.ClearCachedPrices();
      }
      ImGui.SameLine();
      ImGui.Text("past first");
      ImGui.EndGroup();
      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("How many listings past the cheapest to check for an outlier.\n\n" +
                         "1 = only compare the 1st and 2nd listing.\n" +
                         "9 = check all 10 listings in the batch.\n\n" +
                         "Lower = sell fast at market edge.\n" +
                         "Higher = look deeper, avoid outliers, may sell higher (eventually).");
        ImGui.EndTooltip();
      }
    }

    ImGui.Separator();
    // --- Market Board Timings ---
    float currentMBDelay = Plugin.Configuration.GetMBPricesDelayMS / 1000f;
    ImGui.Text("-- Market Board Timings --");
    ImGui.BeginGroup();
    ImGui.Text("Price Check Delay (s):");
    ImGui.SameLine();
    ImGui.SetNextItemWidth(150);
    if (ImGui.SliderFloat("###sliderMBDelay", ref currentMBDelay, 0.1f, 10f, "%.1f"))
    {
      Plugin.Configuration.GetMBPricesDelayMS = (int)(currentMBDelay * 1000);
      Plugin.Configuration.Save();
    }
    ImGui.EndGroup();
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("How long to wait before opening the market board price list.\n\n" +
                       "Too low and prices may fail to load. Too high and pinching is slow.\n" +
                       "Recommended: 3-4s. Reduce at your own risk!");
      ImGui.EndTooltip();
    }

    float currentMBKeepOpenDelay = Plugin.Configuration.MarketBoardKeepOpenMS / 1000f;
    ImGui.BeginGroup();
    ImGui.Text("Keep Open Time (s):");
    ImGui.SameLine();
    ImGui.SetNextItemWidth(150);
    if (ImGui.SliderFloat("###sliderMBKeepOpen", ref currentMBKeepOpenDelay, 0.1f, 10f, "%.1f"))
    {
      Plugin.Configuration.MarketBoardKeepOpenMS = (int)(currentMBKeepOpenDelay * 1000);
      Plugin.Configuration.Save();
    }
    ImGui.EndGroup();
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("How long to keep the market board open while fetching prices.\n\n" +
                       "Too low and price data may not fully load.\n" +
                       "Recommended: 1-2s. Reduce at your own risk!");
      ImGui.EndTooltip();
    }

    ImGui.Separator();
    // --- Chat Output ---
    ImGui.Text("-- Chat Output --");

    bool chatErrors = Plugin.Configuration.ShowErrorsInChat;
    if (ImGui.Checkbox("Show errors in chat", ref chatErrors))
    {
      Plugin.Configuration.ShowErrorsInChat = chatErrors;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Show error messages in chat when an item is skipped.\n\n" +
                       "Reasons include: price floor violations, no market board listings, or exceeding the max undercut cap.");
      ImGui.EndTooltip();
    }

    bool adjustmentsMessages = Plugin.Configuration.ShowPriceAdjustmentsMessages;
    if (ImGui.Checkbox("Show Price Adjustments", ref adjustmentsMessages))
    {
      Plugin.Configuration.ShowPriceAdjustmentsMessages = adjustmentsMessages;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Show a chat message each time an item's price is adjusted.\n\n" +
                       "Displays the old price, new price, and percentage change with a clickable item link.");
      ImGui.EndTooltip();
    }

    bool outlierMessages = Plugin.Configuration.ShowOutlierDetectionMessages;
    if (ImGui.Checkbox("Show Outlier Detection", ref outlierMessages))
    {
      Plugin.Configuration.ShowOutlierDetectionMessages = outlierMessages;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Show a chat message when a bait listing is detected and skipped.\n\n" +
                       "Displays the skipped price and the valid price being used instead.");
      ImGui.EndTooltip();
    }

    ImGui.SameLine(0, 40);

    bool retainerNames = Plugin.Configuration.ShowRetainerNames;
    if (ImGui.Checkbox("Show Retainer Names", ref retainerNames))
    {
      Plugin.Configuration.ShowRetainerNames = retainerNames;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Print the retainer's name in chat before processing their listings.\n\n" +
                       "Helpful for tracking which retainer's items are being adjusted.");
      ImGui.EndTooltip();
    }


    ImGui.Separator();
    // --- Retainers ---
    ImGui.Text("-- Retainer Selection --");
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Choose which retainers are included when using Auto Pinch.\n\n" +
                       "Unchecked retainers are skipped entirely.\n" +
                       "Open the retainer list in-game to populate this list.");
      ImGui.EndTooltip();
    }

    // Try to fetch retainer names from the RetainerList addon if available
    unsafe
    {
      string[]? retainerNameArray = null;
      bool namesUpdated = false;
      
      if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && IsAddonReady(addon))
      {
        try
        {
          var retainerList = new AddonMaster.RetainerList(addon);
          retainerNameArray = [.. retainerList.Retainers.Select(r => r.Name)];
          
          // Update stored retainer names if they changed
          var currentNames = new HashSet<string>(retainerNameArray);
          var storedNames = new HashSet<string>(Plugin.Configuration.LastKnownRetainerNames);
          
          if (!currentNames.SetEquals(storedNames))
          {
            // Names changed - update the stored list
            Plugin.Configuration.LastKnownRetainerNames = [.. retainerNameArray];
            
            // Remove enabled status for retainers that no longer exist
            Plugin.Configuration.EnabledRetainerNames.RemoveWhere(name => !currentNames.Contains(name) && name != Configuration.ALL_DISABLED_SENTINEL);
            
            Plugin.Configuration.Save();
            namesUpdated = true;
          }
        }
        catch
        {
          // Fallback if we can't read retainer names
        }
      }

      // Use fetched names if available, otherwise use stored names
      var namesToDisplay = retainerNameArray ?? [.. Plugin.Configuration.LastKnownRetainerNames];

      // Only display checkboxes if we have retainer names (either fetched or stored)
      if (namesToDisplay.Length > 0)
      {
        // Calculate column offset from longest retainer name + checkbox width + padding
        float maxNameWidth = 0;
        for (int i = 0; i < namesToDisplay.Length; i++)
          maxNameWidth = Math.Max(maxNameWidth, ImGui.CalcTextSize(namesToDisplay[i]).X);
        float columnOffset = maxNameWidth + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + 40;

        for (int i = 0; i < namesToDisplay.Length; i++)
        {
          string retainerName = namesToDisplay[i];

          // Empty set = all enabled, sentinel = all disabled, non-empty = explicit whitelist
          bool allDisabled = Plugin.Configuration.EnabledRetainerNames.Contains(Configuration.ALL_DISABLED_SENTINEL);
          bool enabled = !allDisabled && (Plugin.Configuration.EnabledRetainerNames.Count == 0 || Plugin.Configuration.EnabledRetainerNames.Contains(retainerName));

          string label = $"{retainerName}##retainer{i}";
          if (ImGui.Checkbox(label, ref enabled))
          {
            Plugin.Configuration.EnabledRetainerNames.Remove(Configuration.ALL_DISABLED_SENTINEL);

            if (enabled)
            {
              Plugin.Configuration.EnabledRetainerNames.Add(retainerName);
              // Optimize: if all retainers are enabled, clear set to use default "all enabled" mode
              if (Plugin.Configuration.EnabledRetainerNames.Count == namesToDisplay.Length)
              {
                Plugin.Configuration.EnabledRetainerNames.Clear();
              }
            }
            else
            {
              // Transition from "all enabled" (empty set) to explicit whitelist
              if (Plugin.Configuration.EnabledRetainerNames.Count == 0)
              {
                foreach (string name in namesToDisplay)
                {
                  if (name != retainerName)
                  {
                    Plugin.Configuration.EnabledRetainerNames.Add(name);
                  }
                }
              }
              else
              {
                Plugin.Configuration.EnabledRetainerNames.Remove(retainerName);
                // Use sentinel to mark "all disabled" state (empty set means "all enabled")
                if (Plugin.Configuration.EnabledRetainerNames.Count == 0)
                {
                  Plugin.Configuration.EnabledRetainerNames.Add(Configuration.ALL_DISABLED_SENTINEL);
                }
              }
            }
            Plugin.Configuration.Save();
          }

          // Place next checkbox on same line if it's an even index (0, 2, 4, 6, 8)
          if (i % 2 == 0 && i < namesToDisplay.Length - 1)
            ImGui.SameLine(columnOffset);
        }
        
        if (retainerNameArray == null && !namesUpdated)
        {
          ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1), "(Using cached retainer list - open retainer list to refresh)");
        }
      }
      else
      {
        ImGui.TextColored(new System.Numerics.Vector4(1, 1, 0, 1), "Open retainer list in-game to configure retainer selection");
      }
    }

    ImGui.Separator();
    // --- Hotkeys ---
    bool enablePostPinchKey = Plugin.Configuration.EnablePostPinchkey;
    ImGui.Text("-- Hotkeys --");
    ImGui.BeginGroup();
    if (ImGui.Checkbox("Enable Post'n'Pinch:", ref enablePostPinchKey))
    {
      Plugin.Configuration.EnablePostPinchkey = enablePostPinchKey;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Hold a key while posting an item to automatically set the undercut price.\n\n" +
                       "Saves time when listing new items — no need to manually check prices.");
      ImGui.EndTooltip();
    }
    ImGui.EndGroup();

    ImGui.BeginGroup();
    if (enablePostPinchKey)
    {
      ImGui.Text("Post'n'Pinch Key:");
      ImGui.SameLine();

      index = Array.IndexOf(_virtualKeyStrings, Plugin.Configuration.PostPinchKey.ToString());
      ImGui.SetNextItemWidth(150);
      if (ImGui.Combo("##postPinchKeyCombo", ref index, _virtualKeyStrings, _virtualKeyStrings.Length))
      {
        Plugin.Configuration.PostPinchKey = Enum.Parse<VirtualKey>(_virtualKeyStrings[index]);
        Plugin.Configuration.Save();
      }

      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("The key to hold when posting an item to trigger auto-pricing.\n\n" +
                         "Note: This key still performs its normal game function as well.");
        ImGui.EndTooltip();
      }
    }
    ImGui.EndGroup();


    bool enablePinchKey = Plugin.Configuration.EnablePinchKey;
    if (ImGui.Checkbox("Enable Pinch Hotkey", ref enablePinchKey))
    {
      Plugin.Configuration.EnablePinchKey = enablePinchKey;
      Plugin.Configuration.Save();
    }
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
    {
      ImGui.BeginTooltip();
      ImGui.SetTooltip("Press a key to start auto-pinching all items on the current retainer.\n\n" +
                       "Works from both the retainer list and individual sell list views.");
      ImGui.EndTooltip();
    }

    ImGui.BeginGroup();
    if (enablePinchKey)
    {
      ImGui.Text("Auto Pinch Key:");
      ImGui.SameLine();

      string currentKey = Plugin.Configuration.PinchKey.ToString();
      index = Array.IndexOf(_virtualKeyStrings, currentKey);
      ImGui.SetNextItemWidth(150);
      if (ImGui.Combo("##pinchKeyCombo", ref index, _virtualKeyStrings, _virtualKeyStrings.Length))
      {
        Plugin.Configuration.PinchKey = Enum.Parse<VirtualKey>(_virtualKeyStrings[index]);
        Plugin.Configuration.Save();
      }

      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("The key to press to start the auto-pinching process.\n\n" +
                         "Note: This key still performs its normal game function as well.");
        ImGui.EndTooltip();
      }
    }
    ImGui.EndGroup();


    if (!Plugin.Configuration.DontUseTTS)
    {
      ImGui.Separator();
      ImGui.Text("-- Text-To-Speech --");

      ImGui.BeginGroup();
      bool ttsall = Plugin.Configuration.TTSWhenAllDone;
      if (ImGui.Checkbox("All", ref ttsall))
      {
        Plugin.Configuration.TTSWhenAllDone = ttsall;
        Plugin.Configuration.Save();
      }
      ImGui.SameLine();
      string ttsallmsg = Plugin.Configuration.TTSWhenAllDoneMsg;
      ImGui.SetNextItemWidth(500);
      if (ImGui.InputText("##ttsallmsg", ref ttsallmsg, 256, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue))
      {
        Plugin.Configuration.TTSWhenAllDoneMsg = ttsallmsg;
        Plugin.Configuration.Save();
      }
      ImGui.EndGroup();
      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("Speak a custom phrase when all retainers have been processed.\n\n" +
                         "Great for AFK pinching — you'll hear when everything is done.");
        ImGui.EndTooltip();
      }

      ImGui.BeginGroup();
      bool ttseach = Plugin.Configuration.TTSWhenEachDone;
      if (ImGui.Checkbox("Each", ref ttseach))
      {
        Plugin.Configuration.TTSWhenEachDone = ttseach;
        Plugin.Configuration.Save();
      }
      ImGui.SameLine();
      string ttseachmsg = Plugin.Configuration.TTSWhenEachDoneMsg;
      ImGui.SetNextItemWidth(500);
      if (ImGui.InputText("##ttseachmsg", ref ttseachmsg, 256, ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue))
      {
        Plugin.Configuration.TTSWhenEachDoneMsg = ttseachmsg;
        Plugin.Configuration.Save();
      }
      ImGui.EndGroup();
      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("Speak a custom phrase after each retainer's listings are processed.\n\n" +
                         "Useful for tracking progress when pinching multiple retainers.");
        ImGui.EndTooltip();
      }

      ImGui.BeginGroup();
      ImGui.Text("TTS Volume:");
      ImGui.SameLine();
      int volume = Plugin.Configuration.TTSVolume;
      ImGui.SetNextItemWidth(150);
      if (ImGui.SliderInt("##ttsVolumeAmount", ref volume, 1, 99))
      {
        Plugin.Configuration.TTSVolume = volume;
        Plugin.Configuration.Save();
      }
      ImGui.SameLine();
      ImGui.Text("%");
      ImGui.EndGroup();
      ImGui.SameLine();
      ImGui.TextDisabled("(?)");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("Volume level for text-to-speech notifications.");
        ImGui.EndTooltip();
      }
    }
  }
}