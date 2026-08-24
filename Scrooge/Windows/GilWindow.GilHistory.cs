using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImPlot;
using ECommons.DalamudServices;

namespace Scrooge.Windows;

/// <summary>The GIL HISTORY tab - the snapshot series plotted, with its own refresh clock.</summary>
internal sealed partial class GilWindow
{
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
  /// <summary>
  /// How many plotted retainer points are CARRIED, not measured (V14). A snapshot
  /// with no retainer read reuses the last one that had one, so the line stays
  /// continuous - but a carried point is a repeat of an older measurement drawn at a
  /// newer timestamp, and the footer says how many of them are in the picture. The
  /// number was already computed for the log; it just never reached a reader.
  /// </summary>
  private int _historyCarried;
  private bool _historyTabWasOpen;
  private bool _historyShowBreakdown;
  private const int HistoryTickCount = 6;

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
    // The carried count is omitted when it is zero: "0 carried" is a sentence about
    // nothing, and every point on a fully-measured chart is what it looks like.
    var carried = _historyCarried > 0
      ? $" ({_historyCarried} retainer point{(_historyCarried == 1 ? "" : "s")} carried from prior reads)"
      : "";
    ImGui.TextDisabled($"{_historyX.Length} snapshots plotted{carried}. Last refresh: {refreshLabel}");
  }

  private void DrawSinglePlot(string id, string title, double[] y, long[] raw, string yFormat, float height)
  {
    // Next-plot setup: must precede BeginPlot (ImPlot asserts otherwise, and
    // the fit request would leak to whatever plot begins next).
    ImPlot.SetNextAxesToFit();

    if (ImPlot.BeginPlot(id, new Vector2(-1, height), ImPlotFlags.NoMouseText))
    {
      ImPlot.SetupAxis(ImAxis.X1, "");
      ImPlot.SetupAxis(ImAxis.Y1, "", ImPlotAxisFlags.NoGridLines);
      ImPlot.SetupAxisFormat(ImAxis.Y1, yFormat);
      if (_historyTickPositions != null && _historyTickLabels != null && _historyTickPositions.Length > 0)
        ImPlot.SetupAxisTicks(ImAxis.X1, ref _historyTickPositions[0], _historyTickPositions.Length, _historyTickLabels);
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

    _historyCarried = carried;
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
}
