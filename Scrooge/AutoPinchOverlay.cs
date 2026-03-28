using Dalamud.Interface.Utility;
using ECommons;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Static helpers for rendering ImGui overlays anchored to game UI nodes.
/// </summary>
internal static class AutoPinchOverlay
{
  /// <summary>Walks the node tree to calculate absolute screen position.</summary>
  internal static unsafe Vector2 GetNodePosition(AtkResNode* node)
  {
    var pos = new Vector2(node->X, node->Y);
    var par = node->ParentNode;
    while (par != null)
    {
      pos *= new Vector2(par->ScaleX, par->ScaleY);
      pos += new Vector2(par->X, par->Y);
      par = par->ParentNode;
    }

    return pos;
  }

  /// <summary>Walks the node tree to calculate cumulative scale factor.</summary>
  /// <param name="node">Starting node to calculate scale for.</param>
  /// <returns>Cumulative scale factor from all parent nodes. Returns (1,1) if node is null.</returns>
  internal static unsafe Vector2 GetNodeScale(AtkResNode* node)
  {
    if (node == null) return new Vector2(1, 1);
    var scale = new Vector2(node->ScaleX, node->ScaleY);
    while (node->ParentNode != null)
    {
      node = node->ParentNode;
      scale *= new Vector2(node->ScaleX, node->ScaleY);
    }

    return scale;
  }

  /// <summary>
  /// Positions and styles an invisible ImGui window to overlay a game UI node.
  /// Returns the previous font scale so it can be restored in ImGuiPostSetup.
  /// </summary>
  /// <param name="node">The game UI node to anchor the overlay to.</param>
  /// <returns>The previous font scale, to be passed to ImGuiPostSetup for cleanup.</returns>
  internal static unsafe float ImGuiSetup(AtkResNode* node, string? windowName = null, Vector2? positionOverride = null)
  {
    var position = positionOverride ?? GetNodePosition(node);
    var scale = GetNodeScale(node);
    var size = positionOverride.HasValue ? new Vector2(1, 1) : new Vector2(node->Width, node->Height) * scale;

    ImGuiHelpers.ForceNextWindowMainViewport();
    ImGuiHelpers.SetNextWindowPosRelativeMainViewport(position);

    ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
    var oldSize = ImGui.GetFont().Scale;
    ImGui.GetFont().Scale *= scale.X;
    ImGui.PushFont(ImGui.GetFont());
    ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f.Scale());
    ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3f.Scale(), 3f.Scale()));
    ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f.Scale(), 0f.Scale()));
    ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f.Scale());
    ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, size);
    ImGui.Begin(windowName ?? $"###AutoPinch{node->NodeId}", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNavFocus
        | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings);

    return oldSize;
  }

  /// <summary>Restores ImGui state after drawing the overlay.</summary>
  /// <param name="oldSize">The font scale returned by ImGuiSetup.</param>
  internal static void ImGuiPostSetup(float oldSize)
  {
    ImGui.End();
    ImGui.PopStyleVar(5);
    ImGui.GetFont().Scale = oldSize;
    ImGui.PopFont();
    ImGui.PopStyleColor();
  }

  /// <summary>
  /// Draws colored overlays on inventory grid slots matching the given icon IDs.
  /// Scans InventoryGrid0E–3E using AddonInventoryGrid.Slots to read each
  /// slot's displayed icon directly.
  /// </summary>
  /// <param name="iconIds">Set of Lumina icon IDs to highlight.</param>
  /// <param name="color">Overlay color (RGBA).</param>
  internal static unsafe void DrawIconOverlays(HashSet<int> iconIds, System.Numerics.Vector4 color)
  {
    var gridNames = new[] { "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E" };
    var drawList = ImGui.GetBackgroundDrawList();
    var colorU32 = ImGui.GetColorU32(color);

    foreach (var gridName in gridNames)
    {
      if (!GenericHelpers.TryGetAddonByName<AddonInventoryGrid>(gridName, out var grid) || !GenericHelpers.IsAddonReady(&grid->AtkUnitBase))
        continue;

      for (int i = 0; i < 35; i++)
      {
        var dragDrop = grid->Slots[i];
        if (dragDrop.Value == null) continue;

        var icon = dragDrop.Value->AtkComponentIcon;
        if (icon == null) continue;
        var iconId = (int)icon->IconId;
        if (iconId == 0 || !iconIds.Contains(iconId)) continue;

        var ownerNode = dragDrop.Value->AtkComponentBase.OwnerNode;
        if (ownerNode == null || !ownerNode->AtkResNode.IsVisible()) continue;

        var node = &ownerNode->AtkResNode;
        var position = GetNodePosition(node);
        var scale = GetNodeScale(node);
        var size = new System.Numerics.Vector2(node->Width * scale.X, node->Height * scale.Y);

        drawList.AddRectFilled(
          new System.Numerics.Vector2(position.X, position.Y),
          new System.Numerics.Vector2(position.X + size.X, position.Y + size.Y),
          colorU32);
      }
    }
  }
}
