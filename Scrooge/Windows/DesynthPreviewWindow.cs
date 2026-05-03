using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Generic;

namespace Scrooge.Windows;

/// <summary>
/// Floating Scrooge ImGui window that previews the items currently visible in
/// AgentSalvage.ItemList. Lets the player tweak selection before handing off
/// to <see cref="DesynthOrchestrator"/>.
///
/// Mirrors HawkWindow's lifecycle: opened on demand by DesynthLauncher, closes
/// itself when the user clicks Run or when the SalvageItemSelector addon closes.
/// </summary>
internal sealed class DesynthPreviewWindow : Window
{
  private List<DesynthItem> _items = [];

  public DesynthPreviewWindow()
    : base("Desynth Preview###DesynthPreview", ImGuiWindowFlags.None)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new System.Numerics.Vector2(520, 320),
      MaximumSize = new System.Numerics.Vector2(900, 900),
    };
  }

  /// <summary>
  /// Re-scans AgentSalvage state and opens the window. Idempotent — calling
  /// while open just refreshes the item list.
  /// </summary>
  public void OpenAndScan()
  {
    _items = DesynthInventoryScanner.Scan();
    IsOpen = true;
  }

  public override void Draw()
  {
    // Auto-close if the salvage addon went away.
    unsafe
    {
      if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out _))
      {
        IsOpen = false;
        return;
      }
    }

    if (_items.Count == 0)
    {
      ImGui.TextWrapped("No items visible in the desynthesis selector.");
      ImGui.TextWrapped("Switch the in-game filter (e.g. \"Inventory: Equipment/Items\") and reopen.");
      return;
    }

    DrawTable();
  }

  private void DrawTable()
  {
    if (!ImGui.BeginTable("DesynthItems", 6,
        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      return;

    ImGui.TableSetupScrollFreeze(0, 1);
    ImGui.TableSetupColumn("",      ImGuiTableColumnFlags.WidthFixed, 28);
    ImGui.TableSetupColumn("Item",  ImGuiTableColumnFlags.WidthStretch);
    ImGui.TableSetupColumn("Qty",   ImGuiTableColumnFlags.WidthFixed, 38);
    ImGui.TableSetupColumn("Class", ImGuiTableColumnFlags.WidthFixed, 50);
    ImGui.TableSetupColumn("Skill", ImGuiTableColumnFlags.WidthFixed, 50);
    ImGui.TableSetupColumn("Flags", ImGuiTableColumnFlags.WidthFixed, 80);
    ImGui.TableHeadersRow();

    for (int i = 0; i < _items.Count; i++)
    {
      var item = _items[i];
      ImGui.TableNextRow();

      ImGui.TableNextColumn();
      var sel = item.Selected;
      if (ImGui.Checkbox($"##sel{i}", ref sel))
        item.Selected = sel;

      ImGui.TableNextColumn();
      ImGui.Text(item.IsHq ? $"{item.Name} " : item.Name);

      ImGui.TableNextColumn();
      ImGui.Text(item.Quantity.ToString());

      ImGui.TableNextColumn();
      ImGui.Text(item.ClassAbbrev);

      ImGui.TableNextColumn();
      // Color tag — Task 7 expands this.
      ImGui.Text(item.Color.ToString());

      ImGui.TableNextColumn();
      // Flags — Task 7 expands this.
      ImGui.Text(item.IsProtected ? "P" : "");
    }

    ImGui.EndTable();
  }
}
