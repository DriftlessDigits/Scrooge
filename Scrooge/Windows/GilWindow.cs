using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Text;
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

      if (ImGui.BeginTabItem("Retainers"))
      {
        DrawRetainersTab();
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
          ImGui.TableNextColumn(); ImGui.Text(sale.ItemName + (sale.IsHQ ? " " + (char)SeIconChar.HighQuality : ""));
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
    var tree = GilStorage.GetCategoryTree(thirtyDaysAgo);

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
      if (ImGui.BeginTable("CatMacro", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.None, 150);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.None, 60);
        ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.None, 100);
        ImGui.TableHeadersRow();

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
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.None, 150);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.None, 60);
        ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.None, 100);
        ImGui.TableHeadersRow();

        foreach (var g in mainGroups)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(g.Key);
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
        ImGui.TableSetupColumn("Item Category", ImGuiTableColumnFlags.None, 150);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.None, 60);
        ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.None, 100);
        ImGui.TableHeadersRow();

        foreach (var row in tree.OrderByDescending(r => r.Gil))
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(row.Category);
          ImGui.TableNextColumn(); ImGui.Text($"{row.Count}");
          ImGui.TableNextColumn(); ImGui.Text($"{row.Gil:N0}");
        }

        ImGui.EndTable();
      }
    }
  }

  private static void DrawRetainersTab()
  {
    ImGui.TextDisabled("Last 30 days");
    ImGui.Spacing();

    var thirtyDaysAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (30 * 24 * 3600L);
    var retainers = GilStorage.GetRetainerSummary(thirtyDaysAgo);

    if (retainers.Count == 0)
    {
      ImGui.TextDisabled("No retainer data yet.");
      return;
    }

    if (ImGui.BeginTable("Retainers", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
    {
      ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.None, 120);
      ImGui.TableSetupColumn("Last Sale", ImGuiTableColumnFlags.None, 70);
      ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.None, 50);
      ImGui.TableSetupColumn("Gil Earned", ImGuiTableColumnFlags.None, 100);
      ImGui.TableSetupColumn("Avg Listing Age", ImGuiTableColumnFlags.None, 90);
      ImGui.TableHeadersRow();

      foreach (var r in retainers)
      {
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); ImGui.Text(r.RetainerName);
        ImGui.TableNextColumn(); ImGui.Text(r.LastSaleTimestamp > 0 ? FormatAge(r.LastSaleTimestamp) : "—");
        ImGui.TableNextColumn(); ImGui.Text($"{r.SaleCount}");
        ImGui.TableNextColumn(); ImGui.Text($"{r.TotalGil:N0}");
        ImGui.TableNextColumn(); ImGui.Text(r.AvgListingAgeDays > 0 ? $"{r.AvgListingAgeDays:F1}d" : "—");
      }

      ImGui.EndTable();
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
          ImGui.TableNextColumn(); ImGui.Text(item.ItemName + (item.IsHQ ? " " + (char)SeIconChar.HighQuality : ""));
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
