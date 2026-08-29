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
  /// as of the last manual Refresh (Drift's call: launching this window IS
  /// the moment the pile data matters).
  /// </summary>
  public void OpenAndScan()
  {
    Plugin.Accountant.Refresh();
    _items = DesynthInventoryScanner.Scan();
    // Inside a Round the Accountant is the host and this window stays down - the scan
    // still happens, because the hosted body reads exactly this list.
    IsOpen = !(Plugin.Accountant.RoundActive || Plugin.Accountant.RoundHeld);
  }

  /// <summary>
  /// The Ledger's one-click entry for the Melt pile: opens the game's
  /// desynthesis window if it is not already up (AgentSalvage), waits a beat
  /// for the item list to populate, then opens this preview with the router's
  /// Melt pile pre-selected. The player still fires Run here - that click is
  /// where the protection modal lives, and it stays.
  ///
  /// <para>Both failure paths REPORT (WALK unit 9). The round marks a stage done at
  /// fire time, so a melt stage whose window never opened would otherwise leave the
  /// deck flowing on to the bell as though the pile had been worked. The report
  /// halts the round and names the gap; outside a round it costs one refresh.</para>
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
        RunFlow.ReportDied(RunKind.Melt, "couldn't reach the desynthesis agent", run: null);
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
          RunFlow.ReportDied(RunKind.Melt, "the desynthesis window didn't open", run: null);
          return;
        }
      }
      OpenAndScan();
      SelectMeltPile();
      // Round context: the melt step is the host, so raise THAT surface. The press
      // contract B owes is made in the wizard, under the rail that says why.
      if (Plugin.Accountant.RoundActive || Plugin.Accountant.RoundHeld)
        Plugin.Accountant.IsOpen = true;
    }, delay: TimeSpan.FromMilliseconds(alreadyOpen ? 50 : 600));
  }

  /// <summary>Selects exactly the router's Melt pile (protections excluded) - the same
  /// action as the "Select Desynth Pile" button, callable by the Ledger hop.</summary>
  private void SelectMeltPile()
  {
    _meltPile = Plugin.Accountant.MeltPileVariants();
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

    // THE STANDALONE DRAW IS THE OUT-OF-ROUND DOOR (Rounds unit 5). Inside a Round the
    // Accountant hosts this same body at its melt step (see DrawHosted) and this window
    // stands down, because two live copies of one checklist is two selections the
    // player can disagree with himself about. Outside a Round - the manual Desynth
    // Launcher press - it is still the only surface there is.
    if (Plugin.Accountant.RoundActive || Plugin.Accountant.RoundHeld)
    {
      ImGui.TextDisabled("The Round is hosting this list - see the Accountant's melt step.");
      if (ImGui.Button("Open the Accountant###toAccountant"))
        Plugin.Accountant.IsOpen = true;
      return;
    }

    DrawBody();
  }

  /// <summary>
  /// THE RE-HOSTED BODY (Rounds unit 5). Exactly what the standalone window draws,
  /// callable from the Accountant's melt step - the extraction is a MOVE, not a fork:
  /// the pile banner, the select-all controls, the protection modal and the table are
  /// one implementation with two hosts.
  ///
  /// <para>Draws nothing when the game's salvage window is shut, which is the honest
  /// answer rather than an empty table: this is a view OF that window's item list, and
  /// with the list gone there is nothing to preview. The melt step's own narration
  /// ("staged - press Run Desynth") carries the story in the meantime.</para>
  /// </summary>
  internal unsafe void DrawHosted()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out _)) return;
    DrawBody();
  }

  private void DrawBody()
  {
    // Location-session parity with the GC counter's Churn button: the
    // router's Melt pile meets its executor here — remind, mark the rows,
    // and offer one-click selection (protections still excluded).
    _meltPile = Plugin.Accountant.MeltPileVariants();
    if (_meltPile.Count > 0)
    {
      var visible = _items.Count(i => _meltPile.Contains((i.ItemId, i.IsHq)));
      // V25: the banner says WHY the pile is a pile. "Routed here" restates the
      // window the reader is already standing in; what he does not know is what the
      // router decided, and that is one clause.
      ImGui.TextColored(ScroogeColors.Amber,
        $"{_meltPile.Count} routed to melt - yields beat their other exits"
        + (visible < _meltPile.Count ? $" ({visible} visible under the current filter)." : "."));
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
      // NO QUOTED SCREEN STRINGS (08-22). This quoted an ENGLISH dropdown label that
      // a DE/JA/FR client never renders, so the one player who most needed the
      // instruction was sent hunting for text his game does not contain. The control
      // is named by where it is, which is true in every client.
      ImGui.TextWrapped("Change the item filter at the top of the game's desynthesis window and reopen this one.");
      return;
    }

    DrawControlsRow();
    ImGui.Separator();
    DrawTable();
  }

  private void DrawControlsRow()
  {
    int checkedCount = 0;
    int checkedActs = 0;
    int protectedChecked = 0;
    foreach (var it in _items)
    {
      if (!it.Selected) continue;
      checkedCount++;
      checkedActs += Math.Max(1, it.Quantity);
      if (it.IsProtected) protectedChecked++;
    }

    // Rows are what you select; acts are what runs. When a stack makes the two
    // differ, say both - "Run Desynth (3)" over one selected stack is the same
    // counter that lied to the progress bar (fast-follow, 2026-08-28).
    ImGui.Text(checkedActs != checkedCount
      ? $"{checkedCount} selected — {checkedActs} desynths"
      : $"{checkedCount} selected");
    if (protectedChecked > 0)
    {
      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Protected);
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
    if (ImGui.Button($"Run Desynth ({checkedActs})"))
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
        ImGui.BulletText($"{tag}  {Format.Hq(it.Name, it.IsHq)}");
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
      ImGui.Text(Format.Hq(item.Name, item.IsHq));

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
