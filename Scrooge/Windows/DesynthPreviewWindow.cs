using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;

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
  private bool _confirmModalOpen;
  private List<DesynthItem> _pendingProtected = [];
  private HashSet<(uint ItemId, bool IsHq)> _meltPile = [];

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
  /// while open just refreshes the item list. Also refreshes the router so
  /// the desynth-pile banner and tags reflect the bags as they are NOW, not
  /// as of the last manual Refresh (Sam's call: launching this window IS
  /// the moment the pile data matters).
  /// </summary>
  public void OpenAndScan()
  {
    Plugin.Ledger.Refresh();
    _items = DesynthInventoryScanner.Scan();
    IsOpen = true;
  }

  /// <summary>
  /// The Ledger's one-click entry for the Melt pile: opens the game's
  /// desynthesis window if it is not already up (AgentSalvage), waits a beat
  /// for the item list to populate, then opens this preview with the router's
  /// Melt pile pre-selected. The player still fires Run here - that click is
  /// where the protection modal lives, and it stays.
  /// </summary>
  public unsafe void OpenSalvageWithPileSelected()
  {
    var alreadyOpen = GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out _);
    if (!alreadyOpen)
    {
      var agent = AgentSalvage.Instance();
      if (agent == null)
      {
        Svc.Chat.PrintError("[Scrooge] Couldn't reach the desynthesis agent.");
        return;
      }
      agent->AgentInterface.Show();
    }

    Svc.Framework.RunOnTick(() =>
    {
      unsafe
      {
        if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out _))
        {
          Svc.Chat.PrintError("[Scrooge] Desynthesis window didn't open - open it manually and retry.");
          return;
        }
      }
      OpenAndScan();
      SelectMeltPile();
    }, delay: TimeSpan.FromMilliseconds(alreadyOpen ? 50 : 600));
  }

  /// <summary>Selects exactly the router's Melt pile (protections excluded) - the same
  /// action as the "Select Desynth Pile" button, callable by the Ledger hop.</summary>
  private void SelectMeltPile()
  {
    _meltPile = Plugin.Ledger.MeltPileVariants();
    foreach (var it in _items)
      it.Selected = !it.IsProtected && _meltPile.Contains((it.ItemId, it.IsHq));
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

    // Location-session parity with the GC counter's Churn button: the
    // router's Melt pile meets its executor here — remind, mark the rows,
    // and offer one-click selection (protections still excluded).
    _meltPile = Plugin.Ledger.MeltPileVariants();
    if (_meltPile.Count > 0)
    {
      var visible = _items.Count(i => _meltPile.Contains((i.ItemId, i.IsHq)));
      ImGui.TextColored(ScroogeColors.Amber,
        $"Router desynth pile: {_meltPile.Count} {(_meltPile.Count == 1 ? "item" : "items")} routed here"
        + (visible < _meltPile.Count ? $" ({visible} visible under the current in-game filter)." : "."));
      if (visible > 0)
      {
        ImGui.SameLine();
        if (ImGui.SmallButton("Select Desynth Pile"))
          SelectMeltPile();
      }
    }

    if (_items.Count == 0)
    {
      ImGui.TextWrapped("No items visible in the desynthesis selector.");
      ImGui.TextWrapped("Switch the in-game filter (e.g. \"Inventory: Equipment/Items\") and reopen.");
      return;
    }

    DrawControlsRow();
    ImGui.Separator();
    DrawTable();
  }

  private void DrawControlsRow()
  {
    int checkedCount = 0;
    int protectedChecked = 0;
    foreach (var it in _items)
    {
      if (!it.Selected) continue;
      checkedCount++;
      if (it.IsProtected) protectedChecked++;
    }

    ImGui.Text($"{checkedCount} selected");
    if (protectedChecked > 0)
    {
      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f));
      ImGui.Text($"({protectedChecked} protected)");
      ImGui.PopStyleColor();
    }

    if (ImGui.Button("Select Skillup-Eligible"))
    {
      foreach (var it in _items)
        it.Selected = !it.IsProtected && DesynthSkillup.IsSkillupEligible(it.Color);
    }
    ImGui.SameLine();

    if (ImGui.Button("Select All"))
    {
      foreach (var it in _items)
        it.Selected = !it.IsProtected;
    }
    ImGui.SameLine();

    if (ImGui.Button("Deselect All"))
    {
      foreach (var it in _items)
        it.Selected = false;
    }

    ImGui.Separator();

    ImGui.BeginDisabled(checkedCount == 0);
    if (ImGui.Button($"Run Desynth ({checkedCount})"))
      OnRunClicked();
    ImGui.EndDisabled();

    DrawConfirmModal();
  }

  private void OnRunClicked()
  {
    var selected = _items.FindAll(i => i.Selected);
    if (selected.Count == 0) return;

    _pendingProtected = selected.FindAll(i => i.IsProtected);
    if (_pendingProtected.Count > 0)
    {
      _confirmModalOpen = true;
      ImGui.OpenPopup("Confirm protected desynth###DesynthConfirm");
      return;
    }

    StartRun(selected);
  }

  private void DrawConfirmModal()
  {
    if (!_confirmModalOpen) return;

    var center = ImGui.GetMainViewport().GetCenter();
    ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));

    if (ImGui.BeginPopupModal("Confirm protected desynth###DesynthConfirm",
        ref _confirmModalOpen, ImGuiWindowFlags.AlwaysAutoResize))
    {
      ImGui.TextWrapped($"You've checked {_pendingProtected.Count} protected items:");
      ImGui.Separator();

      foreach (var it in _pendingProtected)
      {
        string tag = it.IsInGearset ? "[gearset]"
                    : it.IsSpiritbond100 ? "[SB100]"
                    : it.HasMateria ? "[materia]"
                    : "[?]";
        ImGui.BulletText($"{tag}  {it.Name}");
      }

      ImGui.Separator();
      ImGui.TextWrapped("Confirm desynth? These will be destroyed.");

      if (ImGui.Button("Cancel"))
      {
        _confirmModalOpen = false;
        _pendingProtected.Clear();
        ImGui.CloseCurrentPopup();
      }
      ImGui.SameLine();
      if (ImGui.Button("Confirm"))
      {
        _confirmModalOpen = false;
        var selected = _items.FindAll(i => i.Selected);
        _pendingProtected.Clear();
        ImGui.CloseCurrentPopup();
        StartRun(selected);
      }

      ImGui.EndPopup();
    }
  }

  private void StartRun(List<DesynthItem> items)
  {
    // Select All intent: every non-protected item in the scan is checked.
    // Grants the orchestrator permission to auto-continue when the game's
    // truncated agent list repopulates after the queue drains.
    bool allEligibleSelected = _items.TrueForAll(i => i.IsProtected || i.Selected);
    Plugin.DesynthOrchestrator.StartRun(items, allEligibleSelected);
    IsOpen = false;
  }

  private static void DrawColorTag(DesynthSkillupColor color)
  {
    var (text, tint) = color switch
    {
      DesynthSkillupColor.Red    => ("Red",    ScroogeColors.TagRed),
      DesynthSkillupColor.Yellow => ("Yellow", ScroogeColors.TagYellow),
      DesynthSkillupColor.Green  => ("Green",  ScroogeColors.TagGreen),
      _                          => ("?",      ScroogeColors.TagGreen),
    };
    ImGui.PushStyleColor(ImGuiCol.Text, tint);
    ImGui.Text(text);
    ImGui.PopStyleColor();
  }

  private static void DrawFlagIcons(DesynthItem item)
  {
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.TagFlag);
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
      if (_meltPile.Contains((item.ItemId, item.IsHq)))
      {
        ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
        ImGui.Text("desynth");
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("In the router's desynth pile");
        ImGui.SameLine();
      }
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
