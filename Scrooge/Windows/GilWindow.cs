using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImPlot;
using ECommons.DalamudServices;

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
      MaximumSize = new Vector2(1200, 1800)
    };
  }

  private GilSnapshot? _cachedSnapshot;
  private DateTime _lastRefresh = DateTime.MinValue;
  private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

  // Gil History (ImPlot validation spike)
  private double[]? _historyX;
  private long[]? _totalRaw;
  private long[]? _playerRaw;
  private long[]? _retainerRaw;
  private double[]? _totalY;
  private double[]? _playerY;
  private double[]? _retainerY;
  private string _totalFormat = "";
  private string _playerFormat = "";
  private string _retainerFormat = "";
  private double[]? _historyTickPositions;
  private string[]? _historyTickLabels;
  private DateTime _historyLastRefresh = DateTime.MinValue;
  private bool _historyTabWasOpen;
  private bool _historyShowBreakdown;
  private const int HistoryTickCount = 6;

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

      if (ImGui.BeginTabItem("Gil History"))
      {
        DrawGilHistoryTab();
        ImGui.EndTabItem();
      }

      ImGui.EndTabBar();
    }
  }

  private void DrawGilHistoryTab()
  {
    // Refresh on first open or every 30s thereafter
    if (!_historyTabWasOpen || DateTime.Now - _historyLastRefresh > TimeSpan.FromSeconds(30))
      RefreshGilHistory();

    ImGui.Checkbox("Show player + retainer breakdown", ref _historyShowBreakdown);

    if (_historyX == null || _totalY == null || _historyX.Length < 2)
    {
      ImGui.TextDisabled($"Not enough snapshots yet to draw a chart (have {_historyX?.Length ?? 0}, need 2+).");
      return;
    }

    if (_historyShowBreakdown
        && _playerY != null && _retainerY != null
        && _totalRaw != null && _playerRaw != null && _retainerRaw != null)
    {
      DrawSinglePlot("##GilHistoryTotal",    "Total Gil",    _totalY,    _totalRaw,    _totalFormat,    300);
      DrawSinglePlot("##GilHistoryPlayer",   "Player Gil",   _playerY,   _playerRaw,   _playerFormat,   300);
      DrawSinglePlot("##GilHistoryRetainer", "Retainer Gil", _retainerY, _retainerRaw, _retainerFormat, 300);
    }
    else if (_totalRaw != null)
    {
      DrawSinglePlot("##GilHistoryTotal", "Total Gil", _totalY, _totalRaw, _totalFormat, 300);
    }

    var refreshLabel = _historyLastRefresh.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
    ImGui.TextDisabled($"{_historyX.Length} snapshots plotted. Last refresh: {refreshLabel}");
  }

  private void DrawSinglePlot(string id, string title, double[] y, long[] raw, string yFormat, float height)
  {
    if (ImPlot.BeginPlot(id, new Vector2(-1, height), ImPlotFlags.NoMouseText))
    {
      ImPlot.SetupAxis(ImAxis.X1, "");
      ImPlot.SetupAxis(ImAxis.Y1, "", ImPlotAxisFlags.NoGridLines);
      ImPlot.SetupAxisFormat(ImAxis.Y1, yFormat);
      if (_historyTickPositions != null && _historyTickLabels != null && _historyTickPositions.Length > 0)
        ImPlot.SetupAxisTicks(ImAxis.X1, ref _historyTickPositions[0], _historyTickPositions.Length, _historyTickLabels);
      ImPlot.SetNextAxesToFit();
      ImPlot.PlotLine(title, ref _historyX![0], ref y[0], _historyX.Length);

      if (ImPlot.IsPlotHovered())
      {
        var mouse = ImPlot.GetPlotMousePos();
        var nearest = FindNearestIndex(_historyX, mouse.X);
        if (nearest >= 0)
        {
          var ts = (long)_historyX[nearest];
          var dt = DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime;
          ImGui.BeginTooltip();
          ImGui.Text(dt.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
          ImGui.Separator();
          ImGui.Text($"{title}: {raw[nearest]:N0} gil");
          ImGui.EndTooltip();
        }
      }

      ImPlot.EndPlot();
    }
    else
    {
      Svc.Log.Warning($"[GilHistory] ImPlot.BeginPlot returned false for {id}");
    }
  }

  private void RefreshGilHistory()
  {
    Svc.Log.Debug("[GilHistory] Refreshing history data");
    var rows = GilStorage.GetTotalGilHistory();

    // Find first snapshot with retainer data — that's our baseline for carry-forward.
    int firstWithRetainer = -1;
    for (int i = 0; i < rows.Count; i++)
    {
      if (rows[i].RetainerGil.HasValue) { firstWithRetainer = i; break; }
    }

    int carried = 0;
    if (firstWithRetainer >= 0)
    {
      var count = rows.Count - firstWithRetainer;
      _historyX = new double[count];
      _totalRaw = new long[count];
      _playerRaw = new long[count];
      _retainerRaw = new long[count];

      long lastRetainer = rows[firstWithRetainer].RetainerGil!.Value;
      for (int i = 0; i < count; i++)
      {
        var r = rows[firstWithRetainer + i];
        long retainer;
        if (r.RetainerGil.HasValue)
          retainer = r.RetainerGil.Value;
        else { retainer = lastRetainer; carried++; }
        lastRetainer = retainer;

        _historyX[i] = r.Timestamp;
        _playerRaw[i] = r.PlayerGil;
        _retainerRaw[i] = retainer;
        _totalRaw[i] = r.PlayerGil + retainer;
      }

      // Each line gets its own scale so player gil (~10M) and retainer gil (~140M)
      // both look right when plotted independently.
      BuildScaled(_totalRaw,    out _totalY,    out _totalFormat);
      BuildScaled(_playerRaw,   out _playerY,   out _playerFormat);
      BuildScaled(_retainerRaw, out _retainerY, out _retainerFormat);
    }
    else
    {
      _historyX = Array.Empty<double>();
      _totalRaw = _playerRaw = _retainerRaw = Array.Empty<long>();
      _totalY = _playerY = _retainerY = Array.Empty<double>();
      _totalFormat = _playerFormat = _retainerFormat = "%.0f";
    }

    BuildXAxisTicks();

    _historyLastRefresh = DateTime.Now;
    _historyTabWasOpen = true;
    Svc.Log.Information(
      $"[GilHistory] Loaded {rows.Count} rows; first-with-retainer={firstWithRetainer}; " +
      $"carried-forward={carried}; plotted={_historyX.Length}; " +
      $"formats: total={_totalFormat}, player={_playerFormat}, retainer={_retainerFormat}");
  }

  private static void BuildScaled(long[] raw, out double[] scaled, out string format)
  {
    long max = 0, min = long.MaxValue;
    foreach (var v in raw) { if (v > max) max = v; if (v < min) min = v; }
    var (factor, suffix) = PickScale(max);
    scaled = new double[raw.Length];
    for (int i = 0; i < raw.Length; i++) scaled[i] = raw[i] / factor;

    // Narrow range needs more precision so tick labels differentiate.
    // e.g. 10.4M→11.1M (range 0.7) needs .1f; 126M→140M (range 14) doesn't.
    var scaledRange = (max - min) / factor;
    var decimals = scaledRange < 5 ? 1 : 0;
    format = suffix switch
    {
      "B" => $"%.{decimals + 1}fB",
      "M" => $"%.{decimals}fM",
      "K" => $"%.{decimals}fK",
      _   => $"%.{decimals}f",
    };
  }

  private static int FindNearestIndex(double[] xs, double target)
  {
    if (xs.Length == 0) return -1;
    // Binary search for insertion point, then compare neighbors
    int lo = 0, hi = xs.Length - 1;
    while (lo < hi)
    {
      int mid = (lo + hi) / 2;
      if (xs[mid] < target) lo = mid + 1; else hi = mid;
    }
    if (lo > 0 && Math.Abs(xs[lo - 1] - target) < Math.Abs(xs[lo] - target))
      return lo - 1;
    return lo;
  }

  private static (double Factor, string Suffix) PickScale(long maxValue)
  {
    if (maxValue >= 1_000_000_000) return (1_000_000_000.0, "B");
    if (maxValue >= 1_000_000)     return (1_000_000.0,     "M");
    if (maxValue >= 1_000)         return (1_000.0,         "K");
    return (1.0, "");
  }

  private void BuildXAxisTicks()
  {
    if (_historyX == null || _historyX.Length < 2)
    {
      _historyTickPositions = null;
      _historyTickLabels = null;
      return;
    }

    var first = _historyX[0];
    var last = _historyX[_historyX.Length - 1];
    var span = last - first;
    var culture = System.Globalization.CultureInfo.CurrentCulture;
    // Use date-only for spans > 2 days, otherwise include time. "t" respects 12/24h per culture.
    var dateOnly = span > 2 * 24 * 3600;

    // Inset ticks 4% from each edge so labels don't clip against the plot border
    var inset = span * 0.04;
    var tickFirst = first + inset;
    var tickLast = last - inset;

    _historyTickPositions = new double[HistoryTickCount];
    _historyTickLabels = new string[HistoryTickCount];
    for (int i = 0; i < HistoryTickCount; i++)
    {
      var ts = tickFirst + (tickLast - tickFirst) * i / (HistoryTickCount - 1);
      _historyTickPositions[i] = ts;
      var dt = DateTimeOffset.FromUnixTimeSeconds((long)ts).LocalDateTime;
      _historyTickLabels[i] = dateOnly
        ? dt.ToString("M/d", culture)
        : dt.ToString("M/d ", culture) + dt.ToString("t", culture);
    }
  }

  private static void DrawSalesTab()
  {
    var recentSales = GilStorage.GetRecentSales(20);

    if (recentSales.Count > 0)
    {
      if (ImGui.BeginTable("RecentSales", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable))
      {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.None, 200);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.None, 30);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.None, 80);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.None, 80);
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 100);
        ImGui.TableHeadersRow();

        var (col, asc) = GetSortSpec();
        recentSales = col switch
        {
          1 => Order(recentSales, s => s.Quantity, asc),
          2 => Order(recentSales, s => s.UnitPrice, asc),
          3 => Order(recentSales, s => s.TotalGil, asc),
          4 => Order(recentSales, s => s.SaleTimestamp, asc),
          _ => Order(recentSales, s => s.ItemName, asc),
        };

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
      if (ImGui.BeginTable("CatMacro", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable))
      {
        ImGui.TableSetupColumn("Group", ImGuiTableColumnFlags.None, 150);
        ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.None, 60);
        ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.DefaultSort, 100);
        ImGui.TableHeadersRow();

        var (col, asc) = GetSortSpec();
        macroGroups = col switch
        {
          1 => Order(macroGroups, g => g.Count, asc),
          2 => Order(macroGroups, g => g.Gil, asc),
          _ => Order(macroGroups, g => g.Group, asc),
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

    if (ImGui.BeginTable("Retainers", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable))
    {
      ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.DefaultSort, 120);
      ImGui.TableSetupColumn("Last Sale", ImGuiTableColumnFlags.None, 70);
      ImGui.TableSetupColumn("Sales", ImGuiTableColumnFlags.None, 50);
      ImGui.TableSetupColumn("Gil Earned", ImGuiTableColumnFlags.None, 100);
      ImGui.TableSetupColumn("Avg Listing Age", ImGuiTableColumnFlags.None, 90);
      ImGui.TableHeadersRow();

      var (col, asc) = GetSortSpec();
      retainers = col switch
      {
        1 => Order(retainers, r => r.LastSaleTimestamp, asc),
        2 => Order(retainers, r => r.SaleCount, asc),
        3 => Order(retainers, r => r.TotalGil, asc),
        4 => Order(retainers, r => r.AvgListingAgeDays, asc),
        _ => Order(retainers, r => r.RetainerName, asc),
      };

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
      if (ImGui.BeginTable("SlowMovers", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable))
      {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.None, 200);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.None, 80);
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.None, 100);
        ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.DefaultSort, 60);
        ImGui.TableHeadersRow();

        var (col, asc) = GetSortSpec();
        slowMovers = col switch
        {
          1 => Order(slowMovers, m => m.UnitPrice, asc),
          2 => Order(slowMovers, m => m.Category, asc),
          3 => Order(slowMovers, m => m.FirstSeenTimestamp, asc),
          _ => Order(slowMovers, m => m.ItemName, asc),
        };

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

  private static (int Column, bool Ascending) GetSortSpec()
  {
    var specs = ImGui.TableGetSortSpecs();
    if (specs.SpecsCount > 0)
    {
      var spec = specs.Specs;
      return (spec.ColumnIndex, spec.SortDirection == ImGuiSortDirection.Ascending);
    }
    return (0, true);
  }

  private static List<T> Order<T, TKey>(List<T> list, Func<T, TKey> keySelector, bool ascending)
  {
    return ascending
        ? list.OrderBy(keySelector).ToList()
        : list.OrderByDescending(keySelector).ToList();
  }
}
