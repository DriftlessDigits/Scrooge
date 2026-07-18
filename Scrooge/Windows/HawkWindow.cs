using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge.Windows;

/// <summary>
/// Item selection window for hawk runs. Reads the player's inventory,
/// filters for MB-listable items, and lets the user check items to list.
/// </summary>
internal sealed class HawkWindow : Window
{
  private readonly Lumina.Excel.ExcelSheet<Item> _items;
  private List<HawkItem> _inventory = [];
  private int _availableSlots = 20;

  /// <summary>An item from the player's inventory eligible for listing.</summary>
  internal sealed class HawkItem
  {
    public uint ItemId { get; init; }
    public string Name { get; init; } = "";
    public int Quantity { get; init; }
    public bool IsHq { get; init; }
    public bool Selected { get; set; }
    public InventoryType Container { get; init; }
    public int SlotIndex { get; init; }
    public int LastSalePrice { get; init; }
    public bool LastSaleStale { get; init; }
    public bool IsAlwaysVendor { get; init; }
    /// <summary>Listing-gate verdict (routing brain Increment 0). Verdict.None when the gate is off.</summary>
    public ListingGate.Result Gate { get; init; }
    /// <summary>True once an override for this item was recorded this window session — write once, not per click.</summary>
    public bool OverrideRecorded { get; set; }
  }

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

  /// <summary>
  /// Scans the player's 4 inventory pages and populates the item list.
  /// Filters out non-MB-listable items and banned items.
  /// Call before opening the window.
  /// </summary>
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

  public void RefreshInventory()
  {
    _inventory.Clear();

    // Listing gate evidence doubles as the Last Sale column — one DB pass.
    var gateOn = Plugin.Configuration.EnableRoutingBrain;
    var batch = gateOn ? RoutingInputService.BeginBatch() : null;
    var lastSales = batch?.LastSales ?? GilStorage.GetLastSalePrices();
    var staleCutoff = Plugin.Configuration.StalePriceDays > 0
        ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (Plugin.Configuration.StalePriceDays * 24L * 3600)
        : 0L;

    unsafe
    {
      var im = InventoryManager.Instance();
      if (im == null) return;

      var containers = new[]
      {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
      };

      foreach (var containerType in containers)
      {
        var container = im->GetInventoryContainer(containerType);
        if (container == null) continue;

        for (int i = 0; i < container->Size; i++)
        {
          var slot = container->GetInventorySlot(i);
          if (slot == null || slot->ItemId == 0) continue;

          var itemId = slot->ItemId;
          var item = _items.GetRow(itemId);

          // Must have a market board search category
          if (item.ItemSearchCategory.RowId == 0) continue;

          // Must not be inherently untradeable
          if (item.IsUntradable) continue;

          // HQ-aware ID for ban/vendor list checks
          var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
          var fullId = isHq ? itemId + 1_000_000u : itemId;

          // Must not be on the ban list
          if (Plugin.Configuration.BannedItemIds.Contains(fullId)) continue;

          var hasLastSale = lastSales.TryGetValue((itemId, isHq), out var lastSale);

          _inventory.Add(new HawkItem
          {
            ItemId = itemId,
            Name = item.Name.ToString(),
            Quantity = (int)slot->Quantity,
            IsHq = isHq,
            Selected = false,
            IsAlwaysVendor = Plugin.Configuration.AlwaysVendorItemIds.Contains(fullId),
            Container = containerType,
            SlotIndex = i,
            LastSalePrice = hasLastSale ? lastSale.Price : 0,
            LastSaleStale = hasLastSale && staleCutoff > 0 && lastSale.Timestamp < staleCutoff,
            Gate = batch != null
                && RoutingInputService.Collect(batch, itemId, isHq) is { } inputs
              ? ListingGate.Evaluate(inputs, batch)
              : new ListingGate.Result(ListingGate.Verdict.None, ""),
          });
        }
      }
    }

    _inventory = _inventory.OrderBy(i => i.Name).ToList();
  }

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

    // Select All honors the listing gate: gated items (better exit is
    // desynth/GC) stay unchecked. Checking one by hand is the override.
    if (ImGui.Button("Select All"))
      foreach (var item in _inventory)
        if (!item.IsAlwaysVendor && !item.Gate.IsGated) item.Selected = true;
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
      Plugin.AutoPinch.StartHawkRun(combined);
      IsOpen = false;
    }
    ImGui.EndDisabled();

    // Route: the pile view over the same bags — verdicts for every exit,
    // not just a listing gate. Only offered when the routing brain is on.
    if (Plugin.Configuration.EnableRoutingBrain)
    {
      ImGui.SameLine();
      if (ImGui.Button("Ledger"))
      {
        Plugin.Ledger.Refresh();
        Plugin.Ledger.IsOpen = true;
      }
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Open the Ledger: one worklist grouped by action (list / reprice / pull /\nmelt / churn / watch) over your gear and listings, with reasons. Confirm\na whole pile of no-brainers at once, or work the Review pile row by row.");
    }

    ImGui.Separator();

    // --- Item table ---
    var gateOn = Plugin.Configuration.EnableRoutingBrain;
    var columns = gateOn ? 6 : 5;
    if (ImGui.BeginTable("HawkItems", columns,
        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
    {
      ImGui.TableSetupScrollFreeze(0, 1);
      ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 38);      // checkbox / sell
      ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
      if (gateOn)
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

            // Checking a gated item overrules the router — record the
            // disagreement (once per window session), then respect the human.
            if (selected && item.Gate.IsGated && !item.OverrideRecorded)
            {
              item.OverrideRecorded = true;
              var ilvl = (int)_items.GetRow(item.ItemId).LevelItem.RowId;
              try
              {
                GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq, ilvl,
                  item.Gate.Verdict.ToString(), item.Gate.Reason, "List");
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

        // Route column — the gate's verdict tag; hover for the reason
        if (gateOn)
        {
          ImGui.TableNextColumn();
          DrawGateTag(item.Gate);
        }

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
  /// One-word verdict tag for the Route column. Gated verdicts get caution
  /// colors; Pass/Unknown/BelowFloor render quiet — advice, not alarm.
  /// </summary>
  private static void DrawGateTag(ListingGate.Result gate)
  {
    var (label, color) = gate.Verdict switch
    {
      ListingGate.Verdict.Pass        => ("list", ScroogeColors.Earned),
      ListingGate.Verdict.GateDesynth => ("desynth", ScroogeColors.Amber),
      ListingGate.Verdict.GateGc      => ("turn-in", ScroogeColors.Warning),
      ListingGate.Verdict.BelowFloor  => ("low", ScroogeColors.Muted),
      ListingGate.Verdict.Unknown     => ("?", ScroogeColors.Muted),
      _ => ("", ScroogeColors.Muted),
    };
    if (label.Length == 0) return;

    ImGui.PushStyleColor(ImGuiCol.Text, color);
    ImGui.Text(label);
    ImGui.PopStyleColor();
    if (gate.Reason.Length > 0 && ImGui.IsItemHovered())
      ImGui.SetTooltip(gate.Reason);
  }
}
