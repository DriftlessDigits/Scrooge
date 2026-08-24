using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge.Windows;

/// <summary>
/// Item selection window for hawk runs. Draws the rows
/// <see cref="ListableInventoryScanner"/> hands it and lets the user check what
/// rides.
/// </summary>
internal sealed class HawkWindow : Window
{
  private readonly Lumina.Excel.ExcelSheet<Item> _items;
  private List<ListableItem> _inventory = [];
  private int _availableSlots = 20;

  public HawkWindow()
    : base("Hawk Run###HawkWindow", ImGuiWindowFlags.None)
  {
    _items = Svc.Data.GetExcelSheet<Item>();
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new System.Numerics.Vector2(400, 300),
      MaximumSize = new System.Numerics.Vector2(600, 800),
    };
  }

  /// <summary>Sets the number of available sell slots for the active retainer.</summary>
  public void SetAvailableSlots(int slots) => _availableSlots = slots;

  /// <summary>Checks if a specific item is currently selected in the hawk list.</summary>
  public bool IsItemSelected(uint itemId, bool isHq)
    => _inventory.Any(i => i.ItemId == itemId && i.IsHq == isHq && i.Selected);

  /// <summary>Sets the selection state of a specific item by ID and HQ flag.</summary>
  public void SetItemSelected(uint itemId, bool isHq, bool selected)
  {
    foreach (var item in _inventory)
      if (item.ItemId == itemId && item.IsHq == isHq)
        item.Selected = selected;
  }

  /// <summary>Returns icon IDs for all currently selected items.</summary>
  public HashSet<int> GetSelectedIconIds()
  {
    var icons = new HashSet<int>();
    foreach (var item in _inventory)
      if (item.Selected)
      {
        var baseIcon = (int)_items.GetRow(item.ItemId).Icon;
        icons.Add(item.IsHq ? baseIcon + 1000000 : baseIcon);
      }
    return icons;
  }

  /// <summary>Returns icon IDs for all Always Vendor items.</summary>
  public HashSet<int> GetAlwaysVendorIconIds()
  {
    var icons = new HashSet<int>();
    foreach (var item in _inventory)
      if (item.IsAlwaysVendor)
      {
        var baseIcon = (int)_items.GetRow(item.ItemId).Icon;
        icons.Add(item.IsHq ? baseIcon + 1000000 : baseIcon);
      }
    return icons;
  }

  /// <summary>
  /// Repopulates the checklist from the router's answer. Call before opening the
  /// window.
  /// </summary>
  public void RefreshInventory() => _inventory = ListableInventoryScanner.Scan();

  public override void Draw()
  {
    if (_inventory.Count == 0)
    {
      ImGui.TextWrapped("No listable items found in your inventory.");
      ImGui.TextWrapped("Untradeable, non-MB, and banned items are excluded.");
      return;
    }

    // --- Controls row ---
    var checkedCount = _inventory.Count(i => i.Selected);
    var vendorCount = _inventory.Count(i => i.IsAlwaysVendor);
    var overCapacity = checkedCount > _availableSlots;

    ImGui.Text($"{checkedCount} selected");
    if (vendorCount > 0)
    {
      ImGui.SameLine();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
      ImGui.Text($"+ {vendorCount} vendor");
      ImGui.PopStyleColor();
    }
    ImGui.SameLine();
    if (overCapacity)
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Spent);
      ImGui.Text($"({_availableSlots} slots available)");
      ImGui.PopStyleColor();
    }
    else
      ImGui.Text($"({_availableSlots} slots available)");

    // Select All takes every listable row. The router's verdict rides beside it
    // as advice in the Route column - it stopped holding items back when the door
    // gates retired.
    if (ImGui.Button("Select All"))
      foreach (var item in _inventory)
        if (!item.IsAlwaysVendor) item.Selected = true;
    ImGui.SameLine();

    if (ImGui.Button("Deselect All"))
      foreach (var item in _inventory) item.Selected = false;
    ImGui.SameLine();

    ImGui.BeginDisabled(checkedCount == 0 && vendorCount == 0);
    if (ImGui.Button(overCapacity ? $"Go (first {_availableSlots})" : "Go"))
    {
      var selected = _inventory.Where(i => i.Selected).Take(_availableSlots).ToList();
      var alwaysVendor = _inventory.Where(i => i.IsAlwaysVendor).ToList();
      var combined = selected.Concat(alwaysVendor).ToList();
      // The navigating entry, not the assuming one: Go works from the sell
      // view AND from the bare roster (it summons a retainer itself). The
      // Fresh Yields hop opens this window without navigating anywhere, so
      // Go must manage its own transition (07-22: current state vs expected
      // state - the advisor owns the gap).
      Plugin.PinchHost.NavigateAndStartHawkRun(combined);
      IsOpen = false;
    }
    ImGui.EndDisabled();

    // Round: the pile view over the same bags — verdicts for every exit, not
    // just a tag beside a checkbox.
    ImGui.SameLine();
    if (ImGui.Button("Round"))
    {
      Plugin.Accountant.Refresh();
      Plugin.OpenRoundDoor();
    }
    if (ImGui.IsItemHovered())
      // V23: recon BANKS its reads and re-reads the stale ones; it does not re-read
      // the world minutes before every board. The old claim promised a freshness the
      // round never had. The window it judges "stale" by is the ReconFreshHours
      // knob's own recital, not this tooltip's.
      ImGui.SetTooltip("The Round: the wizard if one is underway, Make the Rounds if not.\nThe board - your gear and listings grouped by action, with reasons -\nis a step inside a Round: it judges against the boards recon banked; stale ones are re-read first.");

    ImGui.Separator();

    // --- Item table ---
    if (ImGui.BeginTable("HawkItems", 6,
        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
    {
      ImGui.TableSetupScrollFreeze(0, 1);
      ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 38);      // checkbox / sell
      ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
      ImGui.TableSetupColumn("Route", ImGuiTableColumnFlags.WidthFixed, 50);
      ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 40);
      ImGui.TableSetupColumn("Last Sale", ImGuiTableColumnFlags.WidthFixed, 90);
      ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 40);      // ban
      ImGui.TableHeadersRow();

      for (int i = 0; i < _inventory.Count; i++)
      {
        var item = _inventory[i];
        ImGui.TableNextRow();

        // Checkbox column — vendor indicator for Always Vendor, checkbox for normal
        ImGui.TableNextColumn();
        if (item.IsAlwaysVendor)
        {
          ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
          ImGui.Text("Sell");
          ImGui.PopStyleColor();
        }
        else
        {
          var selected = item.Selected;
          if (ImGui.Checkbox($"##check{i}", ref selected))
          {
            item.Selected = selected;

            // Listing an item the router would have sent elsewhere overrules it —
            // record the disagreement (once per window session), then respect the
            // human.
            var routerSendsElsewhere = item.RouteTag.Verdict
              is RouteTagMap.Verdict.GateDesynth or RouteTagMap.Verdict.GateGc;
            if (selected && routerSendsElsewhere && !item.OverrideRecorded)
            {
              item.OverrideRecorded = true;
              var ilvl = (int)_items.GetRow(item.ItemId).LevelItem.RowId;
              try
              {
                GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq, ilvl,
                  item.RouteTag.Verdict.ToString(), item.RouteTag.Reason, "List");
              }
              catch { /* storage unavailable — the override still applies, just unrecorded */ }
            }
          }
        }

        // Item name column — orange for Always Vendor
        ImGui.TableNextColumn();
        if (item.IsAlwaysVendor)
          ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
        ImGui.Text(Format.Hq(item.Name, item.IsHq));
        if (item.IsAlwaysVendor)
          ImGui.PopStyleColor();

        // Route column — the router's verdict tag; hover for the reason
        ImGui.TableNextColumn();
        DrawRouteTag(item.RouteTag);

        ImGui.TableNextColumn();
        ImGui.Text(item.Quantity.ToString());

        ImGui.TableNextColumn();
        if (item.LastSalePrice > 0)
        {
          if (item.LastSaleStale)
            ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Stale);
          ImGui.Text($"{item.LastSalePrice:N0}");
          if (item.LastSaleStale)
            ImGui.PopStyleColor();
        }
        else
          ImGui.TextDisabled("—");

        // Ban button — skip for Always Vendor items (managed via context menu)
        ImGui.TableNextColumn();
        if (!item.IsAlwaysVendor)
        {
          if (ImGui.SmallButton($"Ban##{i}"))
          {
            var banId = item.IsHq ? item.ItemId + 1_000_000u : item.ItemId;
            Plugin.Configuration.BannedItemIds.Add(banId);
            Plugin.Configuration.Save();
            _inventory.RemoveAt(i);
            i--;
          }
        }
      }

      ImGui.EndTable();
    }
  }

  /// <summary>
  /// One-word verdict tag for the Route column. Exits away from the market get
  /// caution colors; Pass/Unknown/BelowFloor render quiet — advice, not alarm.
  ///
  /// <para><b>The tag stays one word; the HOVER leads with the arithmetic</b> (V24,
  /// ruled 08-22). The column is scanned, so it keeps the word - but the reader who
  /// stops on it is asking "why that exit and not the other one", and that question is
  /// answered by the two numbers the router compared, not by the prose that
  /// illustrates them. So the pair goes first and the reason follows it.</para>
  /// </summary>
  private static void DrawRouteTag(RouteTagMap.Result tag)
  {
    var (label, color) = tag.Verdict switch
    {
      RouteTagMap.Verdict.Pass        => ("list", ScroogeColors.Earned),
      RouteTagMap.Verdict.GateDesynth => ("desynth", ScroogeColors.Amber),
      RouteTagMap.Verdict.GateGc      => ("turn-in", ScroogeColors.Warning),
      RouteTagMap.Verdict.GateVendor  => ("low", ScroogeColors.Muted),
      RouteTagMap.Verdict.Unknown     => ("?", ScroogeColors.Muted),
      _ => ("", ScroogeColors.Muted),
    };
    if (label.Length == 0) return;

    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.Text(label);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered() && RouteTagHint(label, tag) is { Length: > 0 } hint)
      ImGui.SetTooltip(hint);
  }

  /// <summary>
  /// The two decision numbers, then the router's reason. An uncontested win says so
  /// rather than inventing a loser, and a verdict that carries no scores at all
  /// (the pre-value early exits) falls back to the reason alone - the numbers lead
  /// only where numbers exist.
  /// </summary>
  private static string RouteTagHint(string label, RouteTagMap.Result tag)
  {
    if (tag.WinnerScore is not long winner)
      return tag.Reason;

    var head = tag.RunnerUpScore is long runnerUp
      ? $"{label} {winner:N0} gil, next best {runnerUp:N0}."
      : $"{label} {winner:N0} gil - no other exit had evidence.";
    return tag.Reason.Length > 0 ? $"{head}\n{tag.Reason}" : head;
  }
}
