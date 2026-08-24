using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The RETAINERS tab - who is earning, and how long their asks sit.</summary>
internal sealed partial class GilWindow
{
  // One of the four tabs that used to re-query SQLite on every single ImGui frame.
  private readonly TabCache<List<RetainerSummary>> _retainers =
    new(() => GilStorage.GetRetainerSummary(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (30 * 24 * 3600L)),
        TabCache.Tab);

  private void DrawRetainersTab()
  {
    ImGui.TextDisabled("Last 30 days");
    ImGui.Spacing();

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var retainers = _retainers.Get();

    if (retainers.Count == 0)
    {
      ImGui.TextDisabled("No retainer data yet.");
      return;
    }

    if (ImGui.BeginTable("Retainers", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable))
    {
      ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthStretch, 120);
      ImGui.TableSetupColumn("Last Sale", ImGuiTableColumnFlags.WidthStretch, 70);
      ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.WidthStretch, 50);
      ImGui.TableSetupColumn("Gil Earned", ImGuiTableColumnFlags.WidthStretch, 100);
      ImGui.TableSetupColumn("Avg Listing Age", ImGuiTableColumnFlags.WidthStretch, 90);
      ImGui.TableHeadersRow();

      var (col, asc) = TableSort.Spec();
      retainers = col switch
      {
        1 => TableSort.Order(retainers, r => r.LastSaleTimestamp, asc),
        2 => TableSort.Order(retainers, r => r.SaleCount, asc),
        3 => TableSort.Order(retainers, r => r.TotalGil, asc),
        4 => TableSort.Order(retainers, r => r.AvgListingAgeDays, asc),
        _ => TableSort.Order(retainers, r => r.RetainerName, asc),
      };

      foreach (var r in retainers)
      {
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); ImGui.Text(r.RetainerName);
        ImGui.TableNextColumn(); ImGui.Text(r.LastSaleTimestamp > 0 ? OnMarket.RelativeAt(r.LastSaleTimestamp, now) : "—");
        ImGui.TableNextColumn(); ImGui.Text($"{r.SaleCount}");
        ImGui.TableNextColumn(); ImGui.Text($"{r.TotalGil:N0}");
        ImGui.TableNextColumn(); ImGui.Text(r.AvgListingAgeDays > 0 ? $"{r.AvgListingAgeDays:F1}d" : "—");
      }

      ImGui.EndTable();
    }
  }
}
