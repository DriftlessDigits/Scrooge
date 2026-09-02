using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE BELL'S OVERLAY: a decorationless, backgroundless Dalamud window that draws
/// buttons and veils on top of the game's own retainer UI. Auto Pinch and Round on
/// the roster, Auto Pinch on a sell list, a red stripe on banned rows, and the
/// coloured veils over the Hawk window's inventory icons.
///
/// <para>Drawing only. Every press hands straight to <see cref="PinchHost"/>, which
/// owns the runs - the two used to be one class, which is how an ImGui draw method
/// ended up as the plugin's last line of defence against a crash.</para>
///
/// <para>That defence still lives here, because this is still the draw path: the
/// catch below tears every run at this bell down. What changed is that it no longer
/// has to REMEMBER them one at a time (it got that wrong twice) - it names the gap
/// and calls <see cref="PinchHost.AbortEverything"/>.</para>
/// </summary>
internal sealed class AutoPinchOverlayWindow : Window
{
  private readonly PinchHost _host;

  public AutoPinchOverlayWindow(PinchHost host)
    : base("Scrooge", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.AlwaysAutoResize, true)
  {
    _host = host;

    Position = new System.Numerics.Vector2(0, 0);
    IsOpen = true;
    ShowCloseButton = false;
    RespectCloseHotkey = false;
    DisableWindowSounds = true;
    SizeConstraints = new WindowSizeConstraints()
    {
      MaximumSize = new System.Numerics.Vector2(0, 0),
    };
  }

  public override void Draw()
  {
    try
    {
      DrawForRetainerList();
      DrawForRetainerSellList();
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, "Error while auto pinching");
      if (Plugin.Configuration.ShowErrorsInChat)
        Svc.Chat.PrintError($"Error while auto pinching: {ex.Message}");

      // Every run at this bell comes down, and the death is reported in the words the
      // player would recognize from the line above (review ruling S20 - the draw path
      // must never be the thing that crashes the game, and a run this catch silently
      // emptied the queue for must never be left for the stall watchdog to misname).
      _host.AbortEverything("a draw error took the pass down");
    }
  }

  /// <summary>Draws the Auto Pinch button on the retainer list (all retainers view).</summary>
  private void DrawForRetainerList()
  {
    unsafe
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        // Hotkey support: start pinching if the configured key is held
        if (Plugin.Configuration.EnablePinchKey && Plugin.KeyState[Plugin.Configuration.PinchKey])
          _host.Pinch.PinchAllRetainers();

        var node = addon->UldManager.NodeList[27]; // anchor node for button positioning

        if (node == null)
          return;

        // Each overlay is wrapped in try/finally so an exception inside the
        // button draw can never orphan ImGui style pushes — a leak here
        // affects every other Dalamud window in the process.
        var position = AutoPinchOverlay.GetNodePosition(node);
        var scale = AutoPinchOverlay.GetNodeScale(node);

        // Auto Pinch button — anchored to node, stays in original position
        var oldSize = AutoPinchOverlay.ImGuiSetup(node);
        try { DrawAutoPinchButton(_host.Pinch.PinchAllRetainers); }
        finally { AutoPinchOverlay.ImGuiPostSetup(oldSize); }

        // Round button — opens the Round's own window (Movement 3). The bar is
        // two doors since the trim (08-15): Round is the pipeline's front door,
        // Auto Pinch the one-press price reset - the same PinchAllRetainers the
        // round's pinch stage runs, not a separate pricer. Hawk Wares and Tally
        // Sales came off: the bell run is the round's lister, and the pinch
        // already opens Sale History at every retainer it visits, which is all
        // Tally ever did.
        var roundPos = new Vector2(position.X - 90f * scale.X, position.Y);
        var roundOldSize = AutoPinchOverlay.ImGuiSetup(node, "###OpenRound", roundPos);
        try { DrawAccountantButton(); }
        finally { AutoPinchOverlay.ImGuiPostSetup(roundOldSize); }
      }
    }
  }

  /// <summary>Draws the Auto Pinch button on a single retainer's sell list.</summary>
  private void DrawForRetainerSellList()
  {
    unsafe
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        if (Plugin.Configuration.EnablePinchKey && Plugin.KeyState[Plugin.Configuration.PinchKey])
          _host.Pinch.PinchAllRetainerItems();

        var node = addon->UldManager.NodeList[17]; // anchor node for button positioning

        if (node == null)
          return;

        var oldSize = AutoPinchOverlay.ImGuiSetup(node);
        DrawAutoPinchButton(_host.Pinch.PinchAllRetainerItems);
        AutoPinchOverlay.ImGuiPostSetup(oldSize);

        // Draw ban indicators on sell list rows
        if (Plugin.Configuration.BannedItemIds.Count > 0)
          DrawSellListBanIndicators(addon);
      }
    }
  }

  /// <summary>
  /// Draws a red overlay stripe on RetainerSellList rows for banned items.
  /// Reads item IDs from AtkValues (stride 13) and matches against BannedItemIds.
  /// </summary>
  private unsafe void DrawSellListBanIndicators(AtkUnitBase* addon)
  {
    if (addon->UldManager.NodeListCount <= 10) return;
    var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
    if (listNode == null) return;
    var listComponent = (AtkComponentList*)listNode->Component;
    if (listComponent == null) return;
    var listLength = listComponent->ListLength;
    if (listLength == 0) return;

    // Match by icon ID — container slot order doesn't match UI display order.
    // Build a set of banned icon IDs from Lumina, then check each row's AtkValue icon.
    var itemSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
    var bannedIcons = new HashSet<int>();
    foreach (var id in Plugin.Configuration.BannedItemIds)
    {
      var baseId = id >= 1_000_000 ? id - 1_000_000 : id;
      var baseIcon = (int)itemSheet.GetRow(baseId).Icon;
      bannedIcons.Add(baseIcon);           // NQ
      bannedIcons.Add(baseIcon + 1_000_000); // HQ
    }

    if (bannedIcons.Count == 0) return;

    var drawList = ImGui.GetBackgroundDrawList();
    var banColor = ImGui.GetColorU32(new System.Numerics.Vector4(1f, 0.2f, 0.2f, 0.25f));

    for (var i = 0; i < listLength; i++)
    {
      // Icon is at AtkValues stride 13, offset 10
      var atkIdx = 10 + (i * 13);
      if (atkIdx >= addon->AtkValuesCount) break;
      var iconId = addon->AtkValues[atkIdx].Int;

      if (!bannedIcons.Contains(iconId))
        continue;

      // Get the icon node within this list row (first component node = icon)
      var renderer = listComponent->GetItemRenderer(i);
      if (renderer == null) continue;
      var ownerNode = renderer->OwnerNode;
      if (ownerNode == null || !ownerNode->AtkResNode.IsVisible()) continue;

      // Node [12] (type 1008, 44x48) is the item icon component on the left
      if (renderer->UldManager.NodeListCount <= 12) continue;
      var iconNode = renderer->UldManager.NodeList[12];
      if (iconNode == null || !iconNode->IsVisible()) continue;

      var position = AutoPinchOverlay.GetNodePosition(iconNode);
      var scale = AutoPinchOverlay.GetNodeScale(iconNode);
      var size = new System.Numerics.Vector2(iconNode->Width * scale.X, iconNode->Height * scale.Y);

      drawList.AddRectFilled(
        new System.Numerics.Vector2(position.X, position.Y),
        new System.Numerics.Vector2(position.X + size.X, position.Y + size.Y),
        banColor);
    }
  }

  // DrawInventoryOverlays is GONE with the manual Hawk view (ruled 2026-08-29):
  // its whole body keyed on HawkWindow.IsOpen, which nothing could set true
  // since the 08-15 bell-bar trim - the bag veils have been unreachable ever
  // since, so the deletion matches the reality the player has been living in.

  /// <summary>Draws Auto Pinch / Cancel button. Shows Cancel when busy.</summary>
  /// <param name="specificPinchFunction">The pinch function to call (all retainers or single retainer's items).</param>
  private void DrawAutoPinchButton(Action specificPinchFunction)
  {
    if (_host.PinchBusy)
    {
      if (ImGui.Button("Cancel"))
        _host.CancelPinchRun("you cancelled it at the button");
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("Cancels the auto pinching process");
        ImGui.EndTooltip();
      }
    }
    else
    {
      if (ImGui.Button("Auto Pinch"))
        specificPinchFunction();
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("Starts the pinch. Hands off the game while it runs - it is pressing your buttons.");
        ImGui.EndTooltip();
      }
    }
  }

  /// <summary>
  /// Draws the Round button. Never disabled - the window it opens is a reading
  /// surface, so a busy run is a reason to open it, not a reason it cannot be.
  ///
  /// <para>It OPENS, and no longer routes (Movement 3, superseding unit 5's ruling
  /// that an idle press landed on the dashboard). One press at the bell lands on
  /// Make the Rounds; the window keys its own content to the state of the errand.
  /// Drift's receipt: "I just clicked it on the retainer screen".</para>
  /// </summary>
  private void DrawAccountantButton()
  {
    // Size-match "Auto Pinch" so the two remaining overlays space uniformly.
    var buttonWidth = ImGui.CalcTextSize("Auto Pinch").X + ImGui.GetStyle().FramePadding.X * 2;
    if (ImGui.Button("Round", new Vector2(buttonWidth, 0)))
      Plugin.OpenRoundDoor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("Opens the Round: the wizard if one is underway, and Make the Rounds if not. Nothing starts until you press it there.");
  }
}
