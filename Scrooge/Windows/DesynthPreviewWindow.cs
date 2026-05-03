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

  private static readonly System.Numerics.Vector4 RedTag    = new(0.95f, 0.35f, 0.35f, 1f);
  private static readonly System.Numerics.Vector4 YellowTag = new(0.95f, 0.85f, 0.30f, 1f);
  private static readonly System.Numerics.Vector4 GreenTag  = new(0.45f, 0.85f, 0.45f, 1f);
  private static readonly System.Numerics.Vector4 FlagTag   = new(0.85f, 0.65f, 0.30f, 1f);

  private static void DrawColorTag(DesynthSkillupColor color)
  {
    var (text, tint) = color switch
    {
      DesynthSkillupColor.Red    => ("Red",    RedTag),
      DesynthSkillupColor.Yellow => ("Yellow", YellowTag),
      DesynthSkillupColor.Green  => ("Green",  GreenTag),
      _                          => ("?",      GreenTag),
    };
    ImGui.PushStyleColor(ImGuiCol.Text, tint);
    ImGui.Text(text);
    ImGui.PopStyleColor();
  }

  private static void DrawFlagIcons(DesynthItem item)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, FlagTag);
    if (item.IsInGearset)
    {
      ImGui.Text("GS");
      if (ImGui.IsItemHovered()) ImGui.SetTooltip("In a saved gearset");
      ImGui.SameLine();
    }
    if (item.IsSpiritbond100)
    {
      ImGui.Text("SB");
      if (ImGui.IsItemHovered()) ImGui.SetTooltip("Spiritbond 100% — extracting yields materia");
      ImGui.SameLine();
    }
    if (item.HasMateria)
    {
      ImGui.Text("M");
      if (ImGui.IsItemHovered()) ImGui.SetTooltip("Has equipped materia — desynth destroys it");
    }
    ImGui.PopStyleColor();
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
      DrawColorTag(item.Color);

      ImGui.TableNextColumn();
      DrawFlagIcons(item);
    }

    ImGui.EndTable();
  }
}
