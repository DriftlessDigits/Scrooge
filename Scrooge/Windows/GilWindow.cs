using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>
/// Gil Dashboard window showing portfolio summary, recent sales,
/// category breakdown, and slow movers.
///
/// <para>THE SHELL. This file owns the frame and the overview - lifecycle, the
/// money line, the round strip and the tab-bar dispatch - and nothing else. Each
/// tab owns itself in <c>GilWindow.&lt;Tab&gt;.cs</c>, where its own caches, filter
/// and page state sit at the top of its own file; the grammar more than one tab
/// speaks lives in <c>GilWindow.Shared.cs</c>.</para>
/// </summary>
internal sealed partial class GilWindow: Window
{
  public GilWindow() : base("Scrooge - Gil Dashboard")
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(400, 300),
      MaximumSize = new Vector2(1200, 1800)
    };

    // The dashboard is the front door; settings live behind the cog.
    TitleBarButtons.Add(new TitleBarButton
    {
      Icon = Dalamud.Interface.FontAwesomeIcon.Cog,
      IconOffset = new Vector2(2, 1),
      Click = _ => Plugin.ConfigWindow.Toggle(),
      ShowTooltip = () =>
      {
        ImGui.BeginTooltip();
        ImGui.Text("Scrooge settings");
        ImGui.EndTooltip();
      },
    });

    // The three caches whose query depends on live filter/page state. They read
    // those fields at load time, which is why they are built here rather than at
    // the field - and why the tabs Invalidate() on every filter or page change
    // instead of comparing cached inputs. Their fields are declared with the tabs
    // that own them (Desynth/Transactions/EarnedVsSpent); a field initializer
    // cannot touch instance state, so the readonly assignment has to be here.
    _desynth = new TabCache<DesynthView>(() =>
    {
      var store = Plugin.DesynthYieldStore!;
      var since = SinceForDesynth(_desynthTimeFilter, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      return new DesynthView(
        store.ReadSourceSummary(since),
        store.ReadRecent(DesynthPageSize, _desynthPage * DesynthPageSize),
        store.Count(),
        GilStorage.GetLastSalePrices());
    }, TabCache.Tab);

    // Ventures paid the full per-frame SQLite bill until 08-23 - the returns list
    // AND both token-flow reads ran inside Draw. Same idiom as the rest now.
    _ventures = new TabCache<VenturesView>(() =>
    {
      List<(long CapturedAt, string Retainer, uint ItemId, int Quantity, bool IsHq,
        uint? VentureId, int? VentureCost, string? VentureCategory)> rows;
      try { rows = GilStorage.GetVentureReturns(VentureReturns.WindowDays); }
      catch { rows = []; }
      return new VenturesView(rows, GilStorage.MeasureWeeklyVentureBurn(), VentureReturns.BurnPerDay());
    }, TabCache.Tab);

    _transactions = new TabCache<(int, List<TransactionRecord>)>(() =>
    {
      var dir = _txnDirectionFilter switch { 1 => "earned", 2 => "spent", _ => null };
      var src = _txnSourceFilter > 0 ? _cachedSources![_txnSourceFilter - 1] : null;
      var since = SinceFor(_txnTimeFilter, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      return (GilStorage.GetTransactionCount(dir, src, since),
              GilStorage.GetTransactions(dir, src, since, TxnPageSize, _txnPage * TxnPageSize));
    }, TabCache.OnDemand);

    _earnedVsSpent = new TabCache<List<(string Direction, string Source, long Total, int Count)>>(() =>
    {
      var since = SinceFor(_evsTimeFilter, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
      var rows = GilStorage.GetEarnedVsSpent(since);

      // Append "Other" rows from untracked deltas
      var (untrackedEarned, untrackedSpent) = GilStorage.GetUntrackedDeltas(since);
      if (untrackedEarned > 0) rows.Add(("earned", "other", untrackedEarned, 0));
      if (untrackedSpent > 0) rows.Add(("spent", "other", untrackedSpent, 0));
      return rows;
    }, TabCache.OnDemand);
  }

  // Every tab's SQL runs through a TabCache — the headline on a 5s clock, the tab
  // bodies on 30s, and the paged/filtered ones on the player's own invalidation.
  // Before this, half the tabs re-queried SQLite on every single ImGui frame.
  private readonly TabCache<(GilSnapshot? Snapshot, int PendingCount)> _headline =
    new(() => (GilStorage.GetLatestSnapshot(), GilStorage.GetPendingSaleCount()), TabCache.Headline);

  // Today-delta walks the full snapshot history — its own, slower cadence.
  private readonly TabCache<long?> _todayDelta = new(ComputeTodayDelta, TabCache.Tab);

  private GilSnapshot? _cachedSnapshot;
  private int _cachedPendingCount;
  private long? _cachedTodayDelta;

  // The oldest row in the ledger — read by two tabs for the same disclaimer, and it
  // only moves when a prune runs.
  private readonly TabCache<long?> _earliestTransaction =
    new(GilStorage.GetEarliestTransactionTimestamp, TabCache.Tab);

  public override void Draw()
  {
    // Storage failed closed at startup — every query below would throw, once per
    // frame. Say so instead.
    if (!GilStorage.StorageAvailable)
    {
      ImGui.TextColored(ScroogeColors.Warning,
        "Gil storage is unavailable this session — the database failed to open or migrate.");
      ImGui.TextDisabled("See the Dalamud log for the failure, then /xlplugins reload Scrooge.");
      return;
    }

    (_cachedSnapshot, _cachedPendingCount) = _headline.Get();
    _cachedTodayDelta = _todayDelta.Get();

    DrawMoneyLine();

    DrawRoundStrip();

    ImGui.Spacing();
    if (ImGui.BeginTabBar("##GilTabs"))
    {
      // ON MARKET LEADS (Movement 3, ruled 08-13). What you have standing on the
      // market is the first thing a player opens this window to see, and until now
      // reading it meant starting a Round to get at the tab it lived on. It is status
      // of the WORLD, so it lives where world status lives - and it leads, because the
      // dashboard's other ten tabs are history and this one is now.
      if (ImGui.BeginTabItem("On Market"))
      {
        DrawOnMarketTab();
        ImGui.EndTabItem();
      }

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

      if (ImGui.BeginTabItem("Desynth"))
      {
        DrawDesynthTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Ventures"))
      {
        DrawVenturesTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Goals"))
      {
        DrawGoalsTab();
        ImGui.EndTabItem();
      }

      ImGui.EndTabBar();
    }
  }

  /// <summary>
  /// THE ROUND STRIP (Rounds unit 5, ruled Q4; narrowed by Movement 3). The readouts
  /// that used to head the standalone judgment desk live here - every line of them is
  /// status about the world rather than a decision about a row.
  ///
  /// <para><b>The launch is no longer part of it.</b> Unit 5 seated "Make the Rounds"
  /// here because the dashboard was the only front door; Movement 3 gave the Round its
  /// own screen and the strip keeps a door to it. Nothing on this window starts an
  /// errand any more.</para>
  ///
  /// <para><b>Drawn by the Accountant, hosted here.</b> It owns the cached scans, the
  /// DeckState and the round cursor, and a second derivation on this side is how two
  /// windows come to quote different numbers for one night. This window supplies a
  /// seat and a separator.</para>
  /// </summary>
  private static void DrawRoundStrip()
  {
    ImGui.Spacing();
    ImGui.Separator();
    Plugin.Accountant.DrawDashboardStrip();
    ImGui.Separator();
  }

  /// <summary>
  /// The money line — total worth headlined in gold, today's movement,
  /// pending sales, the player/retainer split, and goal progress bars.
  /// </summary>
  private void DrawMoneyLine()
  {
    var snap = _cachedSnapshot;
    if (snap == null)
    {
      // The recorder is always-on (3.1: "you installed Scrooge, you get
      // Scrooge"), so an empty dashboard has exactly one honest cure. The
      // tracking-off sentences (08-22/08-23) retired with the toggle.
      ImGui.TextDisabled("No data yet — run a pinch to start tracking.");
      return;
    }

    // Headline: total worth, gold, slightly larger
    ImGui.SetWindowFontScale(1.3f);
    ImGui.TextColored(ScroogeColors.Gold, Format.GilIcon(snap.TotalGil));
    ImGui.SetWindowFontScale(1f);

    // Movement row: today's delta + pending sales
    // TWO DIFFERENT FACTS, TWO DIFFERENT SENTENCES (08-22). "Today: —" covered both
    // "the rollup has no row for today" and "it has one and it reads zero" - a
    // missing measurement and a measured zero, which is the difference between "the
    // instrument is not reading" and "the needle has not moved."
    if (_cachedTodayDelta is not long delta)
      ImGui.TextDisabled("Today: no snapshot yet");
    else if (delta == 0)
      ImGui.TextDisabled("Today: no movement");
    else
      ImGui.TextColored(ScroogeColors.ForDelta(delta), $"Today: {Format.SignedGil(delta)}");
    ImGui.SameLine(0, 24);
    if (_cachedPendingCount > 0)
    {
      ImGui.TextColored(ScroogeColors.Warning, $"Pending: {_cachedPendingCount} {(_cachedPendingCount == 1 ? "sale" : "sales")}");
      if (ImGui.IsItemHovered())
        ImGui.SetTooltip("Visit a summoning bell to confirm retainer and buyer details.");
    }
    else
      ImGui.TextDisabled("Pending: 0");

    // Split row: player vs retainers, per-retainer balances on hover
    var retainerTotal = snap.RetainerGil.Values.Sum();
    ImGui.TextDisabled($"Player {Format.Gil(snap.PlayerGil)}   |   Retainers {Format.Gil(retainerTotal)}");
    if (ImGui.IsItemHovered() && snap.RetainerGil.Count > 0)
    {
      ImGui.BeginTooltip();
      var goal = Plugin.Configuration.GoalPerRetainer;
      foreach (var (name, gil) in snap.RetainerGil.OrderByDescending(r => r.Value))
      {
        if (goal > 0 && gil >= goal)
          ImGui.TextColored(ScroogeColors.Earned, $"{name}: {gil:N0}");
        else
          ImGui.Text($"{name}: {gil:N0}");
      }
      if (goal > 0)
      {
        ImGui.Separator();
        ImGui.TextDisabled($"Green = at the {goal:N0} bank mark");
      }
      ImGui.EndTooltip();
    }

    // Goal progress — label + subtle bar per active goal
    var goals = GilGoals.GetProgress(snap);
    if (goals.Count > 0)
    {
      ImGui.Spacing();
      foreach (var g in goals)
      {
        var labelColor = g.Achieved ? ScroogeColors.Earned : ScroogeColors.Muted;
        ImGui.TextColored(labelColor, $"{g.Label} — {g.Fraction * 100:0}%");
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, g.Achieved ? ScroogeColors.Earned : ScroogeColors.Gold);
        ImGui.ProgressBar(g.Fraction, new Vector2(-1, 5), "");
        ImGui.PopStyleColor();
      }
    }
  }

  /// <summary>
  /// Today's total-gil movement from the daily rollup, or null when the
  /// newest rollup row isn't from today.
  /// </summary>
  internal static long? ComputeTodayDelta()
  {
    var daily = GilStorage.GetDailyChanges();
    if (daily.Count < 2) return null;

    var last = daily[^1];
    var lastDate = DateTime.Parse(last.Date).Date;
    if (lastDate != DateTime.Now.Date && lastDate != DateTime.UtcNow.Date)
      return null;

    return last.Delta;
  }
}
