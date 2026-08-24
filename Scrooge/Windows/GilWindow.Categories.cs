using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The CATEGORIES tab - the last 30 days by macro group, category and item type.</summary>
internal sealed partial class GilWindow
{
  // One of the four tabs that used to re-query SQLite on every single ImGui frame.
  private readonly TabCache<List<(string MacroGroup, string MainGroup, string Category, int Count, long Gil)>> _categories =
    new(() => GilStorage.GetCategoryTree(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (30 * 24 * 3600L)),
        TabCache.Tab);

  private void DrawCategoriesTab()
  {
    ImGui.TextDisabled("Last 30 days");
    ImGui.Spacing();

    var tree = _categories.Get();

    if (tree.Count == 0)
    {
      ImGui.TextDisabled("No category data yet.");
      return;
    }

    // Macro level — top-line summary (Gear, Crafting, Consumables, etc.)
    var macroGroups = tree
        .Where(r => !string.IsNullOrEmpty(r.MacroGroup))
        .GroupBy(r => r.MacroGroup)
        .OrderByDescending(g => g.Sum(r => r.Gil))
        .Select(g => (Group: g.Key, Count: g.Sum(r => r.Count), Gil: g.Sum(r => r.Gil)))
        .ToList();

    // Include unmapped as "Other" if any exist
    var unmapped = tree.Where(r => string.IsNullOrEmpty(r.MacroGroup)).ToList();
    if (unmapped.Count > 0)
      macroGroups.Add(("Uncategorized", unmapped.Sum(r => r.Count), unmapped.Sum(r => r.Gil)));

    if (ImGui.CollapsingHeader("By Group", ImGuiTreeNodeFlags.DefaultOpen))
    {
      if (ImGui.BeginTable("CatMacro", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable))
      {
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.WidthStretch, 150);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthStretch, 60);
        ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthStretch, 100);
        ImGui.TableHeadersRow();

        var (col, asc) = TableSort.Spec();
        macroGroups = col switch
        {
          1 => TableSort.Order(macroGroups, g => g.Count, asc),
          2 => TableSort.Order(macroGroups, g => g.Gil, asc),
          _ => TableSort.Order(macroGroups, g => g.Group, asc),
        };

        foreach (var g in macroGroups)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(g.Group);
          ImGui.TableNextColumn(); ImGui.Text($"{g.Count}");
          ImGui.TableNextColumn(); ImGui.Text($"{g.Gil:N0}");
        }

        ImGui.EndTable();
      }
    }

    ImGui.Spacing();
    ImGui.Spacing();

    // Main level — breakdown by display group (Armor, Weapons, Tools, etc.)
    var mainGroups = tree
        .GroupBy(r => r.MainGroup)
        .OrderByDescending(g => g.Sum(r => r.Gil));

    if (ImGui.CollapsingHeader("By Category", ImGuiTreeNodeFlags.DefaultOpen))
    {
      if (ImGui.BeginTable("CatMain", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthStretch, 150);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthStretch, 60);
        ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.WidthStretch, 100);
        ImGui.TableHeadersRow();

        foreach (var g in mainGroups)
        {
          var macro = g.Select(r => r.MacroGroup).FirstOrDefault(m => !string.IsNullOrEmpty(m)) ?? "";
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(g.Key);
          DrawParentChainTooltipIfHovered(macro);
          ImGui.TableNextColumn(); ImGui.Text($"{g.Sum(r => r.Count)}");
          ImGui.TableNextColumn(); ImGui.Text($"{g.Sum(r => r.Gil):N0}");
        }

        ImGui.EndTable();
      }
    }

    ImGui.Spacing();
    ImGui.Spacing();

    // Micro level — individual item categories
    if (ImGui.CollapsingHeader("By Item Type", ImGuiTreeNodeFlags.DefaultOpen))
    {
      if (ImGui.BeginTable("CatMicro", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Item Category", ImGuiTableColumnFlags.WidthStretch, 150);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthStretch, 60);
        ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.WidthStretch, 100);
        ImGui.TableHeadersRow();

        foreach (var row in tree.OrderByDescending(r => r.Gil))
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(row.Category);
          DrawParentChainTooltipIfHovered(row.MainGroup, row.MacroGroup);
          ImGui.TableNextColumn(); ImGui.Text($"{row.Count}");
          ImGui.TableNextColumn(); ImGui.Text($"{row.Gil:N0}");
        }

        ImGui.EndTable();
      }
    }
  }
}
