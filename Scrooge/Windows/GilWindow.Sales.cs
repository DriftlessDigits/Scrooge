using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The SALES tab - the recent settled sales, pending ones marked.</summary>
internal sealed partial class GilWindow
{
  // One of the four tabs that used to re-query SQLite on every single ImGui frame.
  private readonly TabCache<(int PendingCount, List<SaleRecord> Sales)> _sales =
    new(() => (GilStorage.GetPendingSaleCount(), GilStorage.GetRecentSales(20)), TabCache.Tab);

  private void DrawSalesTab()
  {
    var (pendingCount, recentSales) = _sales.Get();
    if (pendingCount > 0)
    {
      ImGui.TextDisabled($"{pendingCount} pending, marked * below — the sale is banked, the retainer and buyer are not. Visit a summoning bell to confirm them.");
      ImGui.Spacing();
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    if (recentSales.Count > 0)
    {
      if (ImGui.BeginTable("RecentSales", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable))
      {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 200);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 30);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthStretch, 80);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthStretch, 80);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.WidthStretch, 100);
        ImGui.TableHeadersRow();

        var (col, asc) = TableSort.Spec();
        recentSales = col switch
        {
          1 => TableSort.Order(recentSales, s => s.Quantity, asc),
          2 => TableSort.Order(recentSales, s => s.UnitPrice, asc),
          3 => TableSort.Order(recentSales, s => s.TotalGil, asc),
          4 => TableSort.Order(recentSales, s => s.SaleTimestamp, asc),
          _ => TableSort.Order(recentSales, s => s.ItemName, asc),
        };

        foreach (var sale in recentSales)
        {
          ImGui.TableNextRow();
          var itemLabel = Format.Hq(sale.ItemName, sale.IsHQ);
          if (sale.IsPending) itemLabel += " *";

          void RowText(string s) { if (sale.IsPending) ImGui.TextDisabled(s); else ImGui.Text(s); }

          ImGui.TableNextColumn(); RowText(itemLabel);
          // THE ASTERISK'S KEY IS THE LINE ABOVE (08-22). The mark had no legend
          // anywhere on the tab - a symbol the table invents and never explains. The
          // key rides the pending count rather than this cell's hover, because the
          // cell's hover is already spoken for by the category chain and two
          // tooltips on one item is one of them silently losing.
          DrawCategoryChainTooltipIfHovered(sale.Category);
          ImGui.TableNextColumn(); RowText($"x{sale.Quantity}");
          ImGui.TableNextColumn(); RowText($"{sale.UnitPrice:N0}");
          ImGui.TableNextColumn(); RowText($"{sale.TotalGil:N0}");
          ImGui.TableNextColumn(); RowText(OnMarket.RelativeAt(sale.SaleTimestamp, now));
        }

        ImGui.EndTable();
      }
    }
    else
    {
      ImGui.TextDisabled("No sales recorded yet.");
    }
  }
}
