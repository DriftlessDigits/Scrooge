using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>
/// Gil Dashboard window showing portfolio summary, recent sales,
/// category breakdown, and slow movers.
/// </summary>
internal sealed class GilWindow: Window
{
  public GilWindow() : base("Scrooge - Gil Dashboard", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(400, 300),
      MaximumSize = new Vector2(800, 1000)
    };
  }

  public override void Draw()
  {
    var data = GilStorage.Data;

    // --- Portfolio Summary ---
    ImGui.Text("PORTFOLIO");
    ImGui.Separator();

    var latestGil = data.GilHistory.LastOrDefault();
    if (latestGil != null )
    {
      ImGui.Text($"  Player Gil:     {latestGil.PlayerGil:N0}");
      ImGui.Text($"  Retainer Gil:   {latestGil.RetainerGil.Values.Sum():N0}");
      ImGui.Separator();
      ImGui.Text($"  Total Gil:      {latestGil.TotalGil:N0}");
    }
    else
    {
      ImGui.TextDisabled("No data yet — run a pinch to start tracking.");
    }

    ImGui.Spacing();
    ImGui.Spacing();

    // --- Recent Sales ---
    ImGui.Text("RECENT SALES");
    ImGui.Separator();

    var recentSales = data.Sales
    .OrderByDescending(s => s.SaleTimestamp)
    .Take(20)
    .ToList();

    if (recentSales.Count > 0)
    {
      if (ImGui.BeginTable("RecentSales", 5,
          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.None, 200);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.None, 30);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.None, 80);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.None, 80);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.None, 100);
        ImGui.TableHeadersRow();

        foreach (var sale in recentSales)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(sale.ItemName + (sale.IsHQ ? " \u2726" : ""));
          ImGui.TableNextColumn(); ImGui.Text($"x{sale.Quantity}");
          ImGui.TableNextColumn(); ImGui.Text($"{sale.UnitPrice:N0}");
          ImGui.TableNextColumn(); ImGui.Text($"{sale.TotalGil:N0}");
          ImGui.TableNextColumn(); ImGui.Text(FormatAge(sale.SaleTimestamp));
        }

        ImGui.EndTable();
      }
    }
    else
    {
      ImGui.TextDisabled("No sales recorded yet.");
    }

    ImGui.Spacing();
    ImGui.Spacing();

    // --- Sales by Category ---
    ImGui.Text("SALES BY CATEGORY (last 30 days)");
    ImGui.Separator();

    var thirtyDaysAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (30 * 24 * 3600L);
    var byCategory = data.Sales
        .Where(s => s.SaleTimestamp > thirtyDaysAgo)
        .GroupBy(s => s.Category)
        .Select(g => new { Category = g.Key, Count = g.Count(), Gil = g.Sum(s => s.TotalGil) })
        .OrderByDescending(g => g.Gil)
        .ToList();

    if (byCategory.Count > 0)
    {
      if (ImGui.BeginTable("Categories", 3,
          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.None, 150);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.None, 60);
        ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.None, 100);
        ImGui.TableHeadersRow();

        foreach (var cat in byCategory)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(cat.Category);
          ImGui.TableNextColumn(); ImGui.Text($"{cat.Count}");
          ImGui.TableNextColumn(); ImGui.Text($"{cat.Gil:N0}");
        }

        ImGui.EndTable();
      }
    }
    else
    {
      ImGui.TextDisabled("No category data yet.");
    }

    ImGui.Spacing();
    ImGui.Spacing();

    // --- Slow Movers ---
    ImGui.Text("SLOW MOVERS (listed 7+ days)");
    ImGui.Separator();

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var sevenDays = 7 * 24 * 3600L;
    var slowMovers = data.CurrentListings
        .Where(l => now - l.FirstSeenTimestamp > sevenDays)
        .OrderByDescending(l => now - l.FirstSeenTimestamp)
        .ToList();

    if (slowMovers.Count > 0)
    {
      if (ImGui.BeginTable("SlowMovers", 4,
          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.None, 200);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.None, 80);
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.None, 100);
        ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.None, 60);
        ImGui.TableHeadersRow();

        foreach (var item in slowMovers)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(item.ItemName + (item.IsHQ ? " \u2726" : ""));
          ImGui.TableNextColumn(); ImGui.Text($"{item.UnitPrice:N0}");
          ImGui.TableNextColumn(); ImGui.Text(item.Category);
          ImGui.TableNextColumn(); ImGui.Text(FormatAge(item.FirstSeenTimestamp));
        }

        ImGui.EndTable();
      }
    }
    else
    {
      ImGui.TextDisabled("No slow movers — everything is moving!");
    }
  }

  private static string FormatAge(long unixTimestamp)
  {
    var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixTimestamp;
    if (age < 3600) return $"{age / 60}m ago";
    if (age < 86400) return $"{age / 3600}h ago";
    return $"{age / 86400}d ago";
  }
}
