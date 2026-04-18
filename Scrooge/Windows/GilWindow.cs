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

  // Transactions tab
  private List<TransactionRecord>? _cachedTransactions;
  private List<string>? _cachedSources;
  private int _txnDirectionFilter; // 0=All, 1=Earned, 2=Spent
  private int _txnSourceFilter;    // 0=All, 1+=source index
  private int _prevDirectionFilter = -1;
  private int _prevSourceFilter = -1;
  private int _txnPage;
  private int _txnTotalCount;
  private int _txnTimeFilter; // 0=7d, 1=30d, 2=90d, 3=All
  private int _prevTimeFilter = -1;
  private const int TxnPageSize = 25;

  // Earned vs Spent
  private List<(string Direction, string Source, long Total, int Count)>? _cachedEarnedVsSpent;
  private int _evsTimeFilter;  // 0=7d, 1=30d, 2=90d, 3=All
  private int _prevEvsTimeFilter = -1;

  // Daily Change
  private List<(string Date, long TotalGil, long Delta)>? _cachedDaily;
  private DateTime _dailyLastRefresh = DateTime.MinValue;

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

      if (ImGui.BeginTabItem("Daily"))
      {
        DrawDailyChangeTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Transactions"))
      {
        DrawTransactionsTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Earned vs Spent"))
      {
        DrawEarnedVsSpentTab();
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

  private void DrawDailyChangeTab()
  {
    if (_cachedDaily == null || DateTime.Now - _dailyLastRefresh > TimeSpan.FromSeconds(30))
    {
      _cachedDaily = GilStorage.GetDailyChanges();
      _dailyLastRefresh = DateTime.Now;
    }
    var daily = _cachedDaily;
    if (daily.Count == 0)
    {
      ImGui.TextDisabled("Not enough data for daily view.");
      return;
    }

    var culture = System.Globalization.CultureInfo.CurrentCulture;
    var tableHeight = ImGui.GetContentRegionAvail().Y;
    if (ImGui.BeginTable("DailyChange", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
        new Vector2(-1, tableHeight)))
    {
      ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.None, 100);
      ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.None, 120);
      ImGui.TableSetupColumn("Change", ImGuiTableColumnFlags.None, 100);
      ImGui.TableSetupScrollFreeze(0, 1);
      ImGui.TableHeadersRow();

      for (int i = daily.Count - 1; i >= 0; i--)
      {
        var (date, total, delta) = daily[i];
        var dt = DateTime.Parse(date);
        ImGui.TableNextRow();
        ImGui.TableNextColumn(); ImGui.Text(dt.ToString("d", culture));
        ImGui.TableNextColumn(); ImGui.Text($"{total:N0}");
        ImGui.TableNextColumn();
        if (i == 0)
        {
          ImGui.TextDisabled("—");
        }
        else
        {
          var sign = delta >= 0 ? "+" : "";
          var color = delta >= 0
            ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f)
            : new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
          ImGui.TextColored(color, $"{sign}{delta:N0}");
        }
      }

      ImGui.EndTable();
    }
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
    var pendingCount = GilStorage.GetPendingSaleCount();
    if (pendingCount > 0)
    {
      ImGui.TextDisabled($"{pendingCount} pending — visit a summoning bell to confirm retainer/buyer details.");
      ImGui.Spacing();
    }

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
          var itemLabel = sale.ItemName + (sale.IsHQ ? " " + (char)SeIconChar.HighQuality : "");
          if (sale.IsPending) itemLabel += " *";

          void RowText(string s) { if (sale.IsPending) ImGui.TextDisabled(s); else ImGui.Text(s); }

          ImGui.TableNextColumn(); RowText(itemLabel);
          ImGui.TableNextColumn(); RowText($"x{sale.Quantity}");
          ImGui.TableNextColumn(); RowText($"{sale.UnitPrice:N0}");
          ImGui.TableNextColumn(); RowText($"{sale.TotalGil:N0}");
          ImGui.TableNextColumn(); RowText(FormatAge(sale.SaleTimestamp));
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

  private void DrawTransactionsTab()
  {
    // Source list — refresh once
    _cachedSources ??= GilStorage.GetDistinctSources();

    // Filters
    var directionLabels = new[] { "All", "Earned", "Spent" };
    ImGui.SetNextItemWidth(100);
    ImGui.Combo("##Direction", ref _txnDirectionFilter, directionLabels, directionLabels.Length);
    ImGui.SameLine();

    var sourceLabels = new string[_cachedSources.Count + 1];
    sourceLabels[0] = "All Sources";
    for (int i = 0; i < _cachedSources.Count; i++)
      sourceLabels[i + 1] = FormatSourceLabel(_cachedSources[i]);
    ImGui.SetNextItemWidth(150);
    ImGui.Combo("##Source", ref _txnSourceFilter, sourceLabels, sourceLabels.Length);
    ImGui.SameLine();

    var timeLabels = new[] { "7 days", "30 days", "90 days", "All time" };
    ImGui.SetNextItemWidth(100);
    ImGui.Combo("##Time", ref _txnTimeFilter, timeLabels, timeLabels.Length);

    // Reset page on filter change
    if (_txnDirectionFilter != _prevDirectionFilter
        || _txnSourceFilter != _prevSourceFilter
        || _txnTimeFilter != _prevTimeFilter)
    {
      _txnPage = 0;
      _cachedTransactions = null;
    }

    // Refresh on filter change or page change
    if (_cachedTransactions == null)
    {
      var dir = _txnDirectionFilter switch { 1 => "earned", 2 => "spent", _ => null };
      var src = _txnSourceFilter > 0 ? _cachedSources[_txnSourceFilter - 1] : null;
      long? since = _txnTimeFilter switch
      {
        0 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400L,
        1 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 * 86400L,
        2 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 90 * 86400L,
        _ => null,
      };
      _txnTotalCount = GilStorage.GetTransactionCount(dir, src, since);
      _cachedTransactions = GilStorage.GetTransactions(dir, src, since, TxnPageSize, _txnPage * TxnPageSize);
      _prevDirectionFilter = _txnDirectionFilter;
      _prevSourceFilter = _txnSourceFilter;
      _prevTimeFilter = _txnTimeFilter;
    }

    if (_cachedTransactions.Count == 0)
    {
      ImGui.TextDisabled("No transactions found.");
      return;
    }

    var culture = System.Globalization.CultureInfo.CurrentCulture;
    long? sinceForDisclaimer = _txnTimeFilter switch
    {
      0 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400L,
      1 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 * 86400L,
      2 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 90 * 86400L,
      _ => null,
    };
    var earliest = GilStorage.GetEarliestTransactionTimestamp();
    var showDisclaimer = HasDataRangeDisclaimer(sinceForDisclaimer, earliest);
    var reservedHeight = showDisclaimer ? 50f : 30f;
    var tableHeight = ImGui.GetContentRegionAvail().Y - reservedHeight;
    if (ImGui.BeginTable("Transactions", 5,
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY,
        new Vector2(-1, tableHeight)))
    {
      ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending, 110);
      ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.None, 100);
      ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.None, 150);
      ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.None, 100);
      ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.None, 40);
      ImGui.TableSetupScrollFreeze(0, 1);
      ImGui.TableHeadersRow();

      var (col, asc) = GetSortSpec();
      var sorted = col switch
      {
        1 => Order(_cachedTransactions, t => t.Source, asc),
        2 => Order(_cachedTransactions, t => t.ItemName, asc),
        3 => Order(_cachedTransactions, t => t.Amount, asc),
        4 => Order(_cachedTransactions, t => t.Quantity, asc),
        _ => Order(_cachedTransactions, t => t.Timestamp, asc),
      };

      foreach (var txn in sorted)
      {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(txn.LocalTime.ToString("g", culture));
        ImGui.TableNextColumn();
        ImGui.Text(FormatSourceLabel(txn.Source));
        ImGui.TableNextColumn();
        var name = string.IsNullOrEmpty(txn.ItemName) ? "—" : txn.ItemName;
        ImGui.Text(name);
        ImGui.TableNextColumn();
        var sign = txn.Direction == "earned" ? "+" : "-";
        var color = txn.Direction == "earned"
          ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f)
          : new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
        ImGui.TextColored(color, $"{sign}{txn.Amount:N0}");
        ImGui.TableNextColumn();
        ImGui.Text(txn.Quantity > 1 ? $"x{txn.Quantity}" : "");
      }

      ImGui.EndTable();
    }

    DrawDataRangeDisclaimer(sinceForDisclaimer, earliest);

    var totalPages = Math.Max(1, (_txnTotalCount + TxnPageSize - 1) / TxnPageSize);
    ImGui.BeginDisabled(_txnPage <= 0);
    if (ImGui.ArrowButton("##TxnPrev", ImGuiDir.Left))
    { _txnPage--; _cachedTransactions = null; }
    ImGui.EndDisabled();
    ImGui.SameLine();
    ImGui.Text($"Page {_txnPage + 1} of {totalPages}");
    ImGui.SameLine();
    ImGui.BeginDisabled(_txnPage >= totalPages - 1);
    if (ImGui.ArrowButton("##TxnNext", ImGuiDir.Right))
    { _txnPage++; _cachedTransactions = null; }
    ImGui.EndDisabled();
    ImGui.SameLine();
    ImGui.TextDisabled($"({_txnTotalCount:N0} total)");
  }

  private void DrawEarnedVsSpentTab()
  {
    var timeLabels = new[] { "7 days", "30 days", "90 days", "All time" };
    ImGui.SetNextItemWidth(120);
    ImGui.Combo("##EvsTime", ref _evsTimeFilter, timeLabels, timeLabels.Length);

    if (_evsTimeFilter != _prevEvsTimeFilter || _cachedEarnedVsSpent == null)
    {
      long? since = _evsTimeFilter switch
      {
        0 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400L,
        1 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 * 86400L,
        2 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 90 * 86400L,
        _ => null,
      };
      _cachedEarnedVsSpent = GilStorage.GetEarnedVsSpent(since);

      // Append "Other" rows from untracked deltas
      var (untrackedEarned, untrackedSpent) = GilStorage.GetUntrackedDeltas(since);
      if (untrackedEarned > 0)
        _cachedEarnedVsSpent.Add(("earned", "other", untrackedEarned, 0));
      if (untrackedSpent > 0)
        _cachedEarnedVsSpent.Add(("spent", "other", untrackedSpent, 0));

      _prevEvsTimeFilter = _evsTimeFilter;
    }

    DrawEarnedVsSpentContent(_cachedEarnedVsSpent);
    ImGui.Spacing();
    long? sinceForDisclaimer = _evsTimeFilter switch
    {
      0 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400L,
      1 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 * 86400L,
      2 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 90 * 86400L,
      _ => null,
    };
    DrawDataRangeDisclaimer(sinceForDisclaimer, GilStorage.GetEarliestTransactionTimestamp());
  }

  private void DrawEarnedVsSpentSection()
  {
    long? since = _evsTimeFilter switch
    {
      0 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400L,
      1 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 * 86400L,
      2 => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 90 * 86400L,
      _ => null,
    };
    var data = GilStorage.GetEarnedVsSpent(since);
    DrawEarnedVsSpentContent(data);
  }

  private static void DrawEarnedVsSpentContent(List<(string Direction, string Source, long Total, int Count)> data)
  {
    if (data.Count == 0)
    {
      ImGui.TextDisabled("No transaction data yet.");
      return;
    }

    var earned = data.Where(d => d.Direction == "earned").ToList();
    var spent = data.Where(d => d.Direction == "spent").ToList();
    var totalEarned = earned.Sum(e => e.Total);
    var totalSpent = spent.Sum(s => s.Total);
    var net = totalEarned - totalSpent;

    var netColor = net >= 0
      ? new Vector4(0.4f, 1.0f, 0.4f, 1.0f)
      : new Vector4(1.0f, 0.4f, 0.4f, 1.0f);
    var netSign = net >= 0 ? "+" : "";

    ImGui.Spacing();
    ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"Earned: {totalEarned:N0}");
    ImGui.SameLine(200);
    ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"Spent: {totalSpent:N0}");
    ImGui.SameLine(400);
    ImGui.TextColored(netColor, $"Net: {netSign}{net:N0}");
    ImGui.Separator();

    if (earned.Count > 0)
    {
      ImGui.Spacing();
      ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "Income");
      if (ImGui.BeginTable("EvsEarned", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.None, 150);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.None, 120);
        ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.None, 60);
        ImGui.TableHeadersRow();

        foreach (var row in earned)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(FormatSourceLabel(row.Source));
          ImGui.TableNextColumn(); ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), $"+{row.Total:N0}");
          ImGui.TableNextColumn(); ImGui.Text($"{row.Count}");
        }
        ImGui.EndTable();
      }
    }

    if (spent.Count > 0)
    {
      ImGui.Spacing();
      ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), "Expenses");
      if (ImGui.BeginTable("EvsSpent", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.None, 150);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.None, 120);
        ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.None, 60);
        ImGui.TableHeadersRow();

        foreach (var row in spent)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(FormatSourceLabel(row.Source));
          ImGui.TableNextColumn(); ImGui.TextColored(new Vector4(1.0f, 0.4f, 0.4f, 1.0f), $"-{row.Total:N0}");
          ImGui.TableNextColumn(); ImGui.Text($"{row.Count}");
        }
        ImGui.EndTable();
      }
    }
  }

  private static string FormatSourceLabel(string source)
  {
    return source switch
    {
      "retainer_sale" => "Retainer Sale",
      "vendor_sale" => "Vendor Sale",
      "npc_purchase" => "NPC Purchase",
      "npc_sale" => "NPC Sale",
      "npc_buyback" => "NPC Buyback",
      "mb_purchase" => "MB Purchase",
      "teleport" => "Teleport",
      "quest_reward" => "Quest Reward",
      "duty_reward" => "Duty Reward",
      "fate_reward" => "FATE Reward",
      "repair" => "Repair",
      "other" => "Other (untracked)",
      _ => source,
    };
  }

  private static bool HasDataRangeDisclaimer(long? since, long? earliest)
    => earliest.HasValue && (since == null || since.Value < earliest.Value);

  private static void DrawDataRangeDisclaimer(long? since, long? earliest)
  {
    if (!HasDataRangeDisclaimer(since, earliest)) return;

    var from = DateTimeOffset.FromUnixTimeSeconds(earliest!.Value).LocalDateTime;
    var days = (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - earliest.Value) / 86400);
    ImGui.TextDisabled($"Data only available from {from.ToString("d", System.Globalization.CultureInfo.CurrentCulture)} ({days} days)");
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
