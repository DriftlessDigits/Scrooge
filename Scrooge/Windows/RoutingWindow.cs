using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Scrooge.Windows;

/// <summary>
/// The routing brain's confirm screen: a pre-sorted pile view of the gear in
/// the player's bags. Every item shows the router's reason; ambiguous calls
/// sit in Review with both reasons. One Go executes what the current location
/// allows (List + Vendor piles via the Hawk run at a retainer bell) and
/// reminds about the rest — never zero-confirm, the piles ARE the review.
/// Non-gear keeps flowing through the Hawk window as today.
/// </summary>
internal sealed class RoutingWindow : Window
{
  /// <summary>One routed inventory item; Pile tracks player moves.</summary>
  internal sealed class RoutedItem
  {
    public uint ItemId { get; init; }
    public string Name { get; init; } = "";
    public bool IsHq { get; init; }
    public int Ilvl { get; init; }
    public int Quantity { get; init; }
    public InventoryType Container { get; init; }
    public int SlotIndex { get; init; }
    public int LastSalePrice { get; init; }
    /// <summary>The router's original call — immutable; overrides move Pile, not this.</summary>
    public RoutingVerdict Verdict { get; init; }
    /// <summary>Current pile. Starts at Verdict.Exit; the player may move it.</summary>
    public RoutingExit Pile { get; set; }
    /// <summary>Still in the Review pile (ambiguous, unresolved).</summary>
    public bool InReview { get; set; }
    public bool OverrideRecorded { get; set; }
  }

  private List<RoutedItem> _items = [];
  private int? _ventureStock;

  public RoutingWindow()
    : base("Scrooge - Router###RoutingWindow", ImGuiWindowFlags.None)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(520, 300),
      MaximumSize = new Vector2(900, 900),
    };
    Size = new Vector2(640, 500);
    SizeCondition = ImGuiCond.FirstUseEver;
    IsOpen = false;
  }

  public override void OnOpen() => Refresh();

  /// <summary>
  /// Scans the player's bags (gear only), gathers evidence once, and runs
  /// every item through the rules engine into piles.
  /// </summary>
  public void Refresh()
  {
    _items = [];
    var batch = RoutingInputService.BeginBatch();
    _ventureStock = batch.VentureStock;
    var gearsetIds = DesynthInventoryScanner.SnapshotGearsetItemIds();
    var itemSheet = Svc.Data.GetExcelSheet<Item>();

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
          if (!itemSheet.TryGetRow(itemId, out var row)) continue;

          // Gear only — the router's exits are gear exits (design: the
          // desynth/GC exits are gear exits; non-gear flows through Hawk).
          if (row.EquipSlotCategory.RowId == 0) continue;

          var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;

          // Protections — slot facts + gearset membership, same reads as
          // the desynth scanner.
          var gearsetKey = isHq ? itemId + 1_000_000u : itemId;
          var protections = new ItemProtections(
            InGearset: gearsetIds.Contains(gearsetKey),
            Spiritbond100: slot->SpiritbondOrCollectability >= 10000,
            HasMateria: HasAnyMateria(slot));

          if (RoutingInputService.Collect(batch, itemId, isHq, protections) is not { } inputs)
            continue;

          var verdict = RoutingRules.Evaluate(inputs, batch.VentureStock);
          _items.Add(new RoutedItem
          {
            ItemId = itemId,
            Name = inputs.Name,
            IsHq = isHq,
            Ilvl = inputs.Ilvl,
            Quantity = (int)slot->Quantity,
            Container = containerType,
            SlotIndex = i,
            LastSalePrice = inputs.LastSale?.Price ?? 0,
            Verdict = verdict,
            Pile = verdict.Exit,
            InReview = verdict.IsReview,
          });
        }
      }
    }

    _items = _items.OrderByDescending(i => i.Ilvl).ThenBy(i => i.Name).ToList();
  }

  private static unsafe bool HasAnyMateria(InventoryItem* slot)
  {
    for (int m = 0; m < 5; m++)
      if (slot->Materia[m] != 0) return true;
    return false;
  }

  public override void Draw()
  {
    if (!Plugin.Configuration.EnableRoutingBrain)
    {
      ImGui.TextWrapped("The routing brain is off. Enable it in the Outlier tab of the config (Routing section).");
      return;
    }

    if (_items.Count == 0)
    {
      ImGui.TextWrapped("No routable gear in your bags.");
      if (ImGui.Button("Refresh")) Refresh();
      return;
    }

    DrawHeader();
    ImGui.Separator();

    DrawPile("Review", null, ScroogeColors.Stale, defaultOpen: true,
      "Too close to call, or no evidence - your decision resolves these.");
    DrawPile("List", RoutingExit.List, ScroogeColors.Earned, defaultOpen: true,
      "Earns real gil on the market board. Executed by the Hawk run.");
    DrawPile("Melt", RoutingExit.Desynth, ScroogeColors.Amber, defaultOpen: true,
      "Skillup value or yields beat the alternatives. Run from the desynth window.");
    DrawPile("Churn", RoutingExit.Gc, ScroogeColors.Warning, defaultOpen: true,
      "Seals beat gil (or venture stock demands it). Expert Delivery at your GC.");
    DrawPile("Vendor", RoutingExit.Vendor, ScroogeColors.Muted, defaultOpen: true,
      "No better exit in evidence. Executed by the Hawk run (retainer vendor sell).");

    // Hold + Ban: count and collapse (BP4 Q3) — observed, never auto-routed.
    var held = _items.Where(i => !i.InReview && i.Pile == RoutingExit.Hold).ToList();
    if (held.Count > 0 && ImGui.CollapsingHeader($"Held: {held.Count} (protected)###pileHold"))
    {
      foreach (var item in held)
      {
        ImGui.TextDisabled($"  {Format.Hq(item.Name, item.IsHq)} - {item.Verdict.Reason}");
      }
    }

    var banned = _items.Count(i => !i.InReview && i.Pile == RoutingExit.Ban);
    if (banned > 0)
      ImGui.TextDisabled($"Banned: {banned} (untouched)");
  }

  private void DrawHeader()
  {
    var listCount = CountPile(RoutingExit.List);
    var vendorCount = CountPile(RoutingExit.Vendor);
    var meltCount = CountPile(RoutingExit.Desynth);
    var gcCount = CountPile(RoutingExit.Gc);
    var reviewCount = _items.Count(i => i.InReview);

    ImGui.Text($"{_items.Count} pieces of gear routed");
    if (_ventureStock is int stock)
    {
      ImGui.SameLine();
      ImGui.TextDisabled($"- {stock:N0} venture tokens");
    }

    if (ImGui.Button("Refresh")) Refresh();
    ImGui.SameLine();

    var atBell = AtRetainerSellView();
    var goParts = new List<string>();
    if (listCount > 0) goParts.Add($"{listCount} list");
    if (vendorCount > 0) goParts.Add($"{vendorCount} vendor");

    ImGui.BeginDisabled(goParts.Count == 0 || !atBell);
    if (ImGui.Button($"Go ({(goParts.Count > 0 ? string.Join(", ", goParts) : "nothing to run")})"))
      ExecuteHawkPiles();
    ImGui.EndDisabled();
    if (!atBell && goParts.Count > 0 && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
      ImGui.SetTooltip("Open a retainer's sell view (Hawk Wares) to execute the List and Vendor piles.");

    // Location session 2: the GC counter. Same one-confirm contract - the
    // Churn button only lights up at an open Expert Delivery window.
    if (gcCount > 0)
    {
      ImGui.SameLine();
      if (Plugin.GcTurnIn.IsRunning)
      {
        if (ImGui.Button("Cancel churn"))
          Plugin.GcTurnIn.Abort();
      }
      else
      {
        var atGc = GcTurnInOrchestrator.AtExpertDelivery();
        ImGui.BeginDisabled(!atGc);
        if (ImGui.Button($"Churn ({gcCount})"))
          ExecuteChurnPile();
        ImGui.EndDisabled();
        if (!atGc && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
          ImGui.SetTooltip("Open your GC personnel officer's Expert Delivery window to execute the Churn pile.");
      }
    }

    // The piles Go can't touch — honest, visible, waiting.
    if (meltCount > 0 || gcCount > 0 || reviewCount > 0)
    {
      var waiting = new List<string>();
      if (reviewCount > 0) waiting.Add($"{reviewCount} in review");
      if (meltCount > 0) waiting.Add($"{meltCount} to melt (desynth window)");
      if (gcCount > 0) waiting.Add($"{gcCount} to churn (Churn button lights up at Expert Delivery)");
      ImGui.TextDisabled(string.Join("  |  ", waiting));
    }
  }

  private int CountPile(RoutingExit exit) => _items.Count(i => !i.InReview && i.Pile == exit);

  /// <summary>
  /// Hands the List and Vendor piles to the Hawk run (assumes the retainer
  /// sell view is open — same contract as the Hawk window's Go). Review,
  /// Melt, and Churn items stay in the window, untouched.
  /// </summary>
  private void ExecuteHawkPiles()
  {
    var hawkItems = new List<HawkWindow.HawkItem>();
    foreach (var item in _items.Where(i => !i.InReview && i.Pile is RoutingExit.List or RoutingExit.Vendor))
    {
      hawkItems.Add(new HawkWindow.HawkItem
      {
        ItemId = item.ItemId,
        Name = item.Name,
        Quantity = item.Quantity,
        IsHq = item.IsHq,
        Selected = item.Pile == RoutingExit.List,
        IsAlwaysVendor = item.Pile == RoutingExit.Vendor,
        Container = item.Container,
        SlotIndex = item.SlotIndex,
        LastSalePrice = item.LastSalePrice,
      });
    }

    if (hawkItems.Count == 0) return;

    var meltCount = CountPile(RoutingExit.Desynth);
    var gcCount = CountPile(RoutingExit.Gc);
    if (meltCount > 0)
      Svc.Chat.Print($"[Scrooge] Melt pile: {meltCount} items - run them from the desynth window.");
    if (gcCount > 0)
      Svc.Chat.Print($"[Scrooge] Churn pile: {gcCount} items - reopen the router at an Expert Delivery counter.");

    Plugin.AutoPinch.StartHawkRun(hawkItems);
    IsOpen = false;
  }

  /// <summary>
  /// Hands the Churn pile to the GC turn-in orchestrator (Expert Delivery
  /// window open - the button gates on it). Seal values re-read from the
  /// sheet at queue time; highest seals first so a wallet-full stop wastes
  /// the least.
  /// </summary>
  private void ExecuteChurnPile()
  {
    var churnItems = _items
      .Where(i => !i.InReview && i.Pile == RoutingExit.Gc)
      .Select(i => new GcTurnInOrchestrator.GcTurnInItem(
        i.ItemId, i.IsHq, i.Name, GcSeals.For(i.ItemId) ?? 0))
      .Where(i => i.SealReward > 0)
      .OrderByDescending(i => i.SealReward)
      .ToList();
    if (churnItems.Count == 0) return;

    Plugin.GcTurnIn.StartRun(churnItems);
  }

  /// <summary>
  /// One pile as a collapsing section. Pass exit = null for the Review pile.
  /// Rows carry the reason inline and move buttons; moving an item is an
  /// override — recorded once, then the human wins.
  /// </summary>
  private void DrawPile(string title, RoutingExit? exit, Vector4 color, bool defaultOpen, string hint)
  {
    var pileItems = exit is RoutingExit e
      ? _items.Where(i => !i.InReview && i.Pile == e).ToList()
      : _items.Where(i => i.InReview).ToList();
    if (pileItems.Count == 0) return;

    ImGui.PushStyleColor(ImGuiCol.Text, color);
    var open = ImGui.CollapsingHeader($"{title}: {pileItems.Count}###pile{title}",
      defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
    ImGui.PopStyleColor();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip(hint);
    if (!open) return;

    if (ImGui.BeginTable($"pileTable{title}", 4,
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
    {
      ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.2f);
      ImGui.TableSetupColumn("ilvl", ImGuiTableColumnFlags.WidthFixed, 36);
      ImGui.TableSetupColumn("Why", ImGuiTableColumnFlags.WidthStretch, 2.0f);
      ImGui.TableSetupColumn("Move", ImGuiTableColumnFlags.WidthFixed, 150);

      foreach (var item in pileItems)
      {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(Format.Hq(item.Name, item.IsHq));
        ImGui.TableNextColumn();
        ImGui.Text(item.Ilvl.ToString());

        ImGui.TableNextColumn();
        ImGui.TextWrapped(item.Verdict.Reason);
        if (item.Verdict.RunnerUpReason.Length > 0 && ImGui.IsItemHovered())
          ImGui.SetTooltip($"Runner-up ({item.Verdict.RunnerUp}): {item.Verdict.RunnerUpReason}");

        ImGui.TableNextColumn();
        DrawMoveButtons(item);
      }
      ImGui.EndTable();
    }
  }

  /// <summary>
  /// Per-row pile toggles, triage-style. The current pile is highlighted;
  /// clicking another moves the item there (resolving Review if set) and
  /// records the override.
  /// </summary>
  private void DrawMoveButtons(RoutedItem item)
  {
    DrawMoveButton(item, RoutingExit.List, "List");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Desynth, "Melt");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Gc, "GC");
    ImGui.SameLine(0, 2);
    DrawMoveButton(item, RoutingExit.Vendor, "Vend");
  }

  private void DrawMoveButton(RoutedItem item, RoutingExit target, string label)
  {
    var isCurrent = !item.InReview && item.Pile == target;
    if (isCurrent)
      ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.45f, 0.25f, 1f));

    if (ImGui.SmallButton($"{label}##move{item.Container}_{item.SlotIndex}_{target}") && !isCurrent)
    {
      // Player overrules (or resolves) the router — record the disagreement
      // once, then respect the human. Resolving Review counts too: that
      // choice is exactly the training signal override tracking exists for.
      if (!item.OverrideRecorded)
      {
        item.OverrideRecorded = true;
        var routerVerdict = item.Verdict.IsReview ? "Review" : item.Verdict.Exit.ToString();
        try
        {
          GilStorage.InsertRoutingOverride(item.ItemId, item.IsHq, item.Ilvl,
            routerVerdict, item.Verdict.Reason, target.ToString());
        }
        catch { /* storage unavailable — the move still applies, just unrecorded */ }
      }
      item.Pile = target;
      item.InReview = false;
    }

    if (isCurrent)
      ImGui.PopStyleColor();
  }

  private static unsafe bool AtRetainerSellView()
    => GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _);
}
