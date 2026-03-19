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
  public GilWindow() : base("Scrooge - Gil Dashboard")
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(400, 300),
      MaximumSize = new Vector2(800, 1000)
    };
  }

  private GilSnapshot? _cachedSnapshot;
  private DateTime _lastRefresh = DateTime.MinValue;
  private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

  public override void Draw()
  {
    // --- Portfolio Summary ---
    ImGui.Text("Portfolio");
    ImGui.Separator();

    if (DateTime.UtcNow - _lastRefresh > RefreshInterval)
    {
      _cachedSnapshot = GilStorage.GetLatestSnapshot();
      _lastRefresh = DateTime.UtcNow;
    }
    var latestGil = _cachedSnapshot;

    if (latestGil != null)
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
    if (ImGui.BeginTabBar("##GilTabs"))
    {
      if (ImGui.BeginTabItem("Sales"))
      {
        DrawSalesTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Categories"))
      {
        DrawCategoriesTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Slow Movers"))
      {
        DrawSlowMoversTab();
        ImGui.EndTabItem();
      }

      ImGui.EndTabBar();
    }
  }

  private static void DrawSalesTab()
  {
    var recentSales = GilStorage.GetRecentSales(20);

    if (recentSales.Count > 0)
    {
      if (ImGui.BeginTable("RecentSales", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
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
  }

  private static void DrawCategoriesTab()
  {
    ImGui.TextDisabled("Last 30 days");
    ImGui.Spacing();

    var thirtyDaysAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (30 * 24 * 3600L);
    var byCategory = GilStorage.GetCategorySales(thirtyDaysAgo);

    if (byCategory.Count > 0)
    {
      if (ImGui.BeginTable("Categories", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
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
  }

  private static void DrawSlowMoversTab()
  {
    ImGui.TextDisabled("Listed 7+ days");
    ImGui.Spacing();

    var sevenDaysAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (7 * 24 * 3600L);
    var slowMovers = GilStorage.GetSlowMovers(sevenDaysAgo);

    if (slowMovers.Count > 0)
    {
      if (ImGui.BeginTable("SlowMovers", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
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
