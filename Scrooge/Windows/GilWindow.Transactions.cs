using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The TRANSACTIONS tab - the filtered, paged ledger of every gil movement.</summary>
internal sealed partial class GilWindow
{
  private readonly TabCache<(int TotalCount, List<TransactionRecord> Rows)> _transactions;
  // Transactions tab
  private List<string>? _cachedSources;
  private int _txnDirectionFilter; // 0=All, 1=Earned, 2=Spent
  private int _txnSourceFilter;    // 0=All, 1+=source index
  private int _prevDirectionFilter = -1;
  private int _prevSourceFilter = -1;
  private int _txnPage;
  private int _txnTimeFilter; // 0=7d, 1=30d, 2=90d, 3=All
  private int _prevTimeFilter = -1;
  private const int TxnPageSize = 25;

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

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var since = SinceFor(_txnTimeFilter, now);

    // Reset page on filter change
    if (_txnDirectionFilter != _prevDirectionFilter
        || _txnSourceFilter != _prevSourceFilter
        || _txnTimeFilter != _prevTimeFilter)
    {
      _txnPage = 0;
      _prevDirectionFilter = _txnDirectionFilter;
      _prevSourceFilter = _txnSourceFilter;
      _prevTimeFilter = _txnTimeFilter;
      _transactions.Invalidate();
    }

    // Refresh on filter change or page change
    var (txnTotalCount, transactions) = _transactions.Get();

    if (transactions.Count == 0)
    {
      ImGui.TextDisabled("No transactions found.");
      return;
    }

    var culture = System.Globalization.CultureInfo.CurrentCulture;
    var earliest = _earliestTransaction.Get();
    var showDisclaimer = HasDataRangeDisclaimer(since, earliest);
    var reservedHeight = showDisclaimer ? 50f : 30f;
    var tableHeight = ImGui.GetContentRegionAvail().Y - reservedHeight;
    if (ImGui.BeginTable("Transactions", 5,
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY,
        new Vector2(-1, tableHeight)))
    {
      ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.WidthStretch, 110);
      ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 100);
      ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 150);
      ImGui.TableSetupColumn("Amount", ImGuiTableColumnFlags.WidthStretch, 100);
      ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 40);
      ImGui.TableSetupScrollFreeze(0, 1);
      ImGui.TableHeadersRow();

      var (col, asc) = TableSort.Spec();
      var sorted = col switch
      {
        1 => TableSort.Order(transactions, t => t.Source, asc),
        2 => TableSort.Order(transactions, t => t.ItemName, asc),
        3 => TableSort.Order(transactions, t => t.Amount, asc),
        4 => TableSort.Order(transactions, t => t.Quantity, asc),
        _ => TableSort.Order(transactions, t => t.Timestamp, asc),
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
        DrawCategoryChainTooltipIfHovered(txn.Category);
        ImGui.TableNextColumn();
        var signedAmount = txn.Direction == "earned" ? txn.Amount : -txn.Amount;
        ImGui.TextColored(ScroogeColors.ForDelta(signedAmount), Format.SignedGil(signedAmount));
        ImGui.TableNextColumn();
        ImGui.Text(txn.Quantity > 1 ? $"x{txn.Quantity}" : "");
      }

      ImGui.EndTable();
    }

    DrawDataRangeDisclaimer(since, earliest);

    if (Pager("Txn", ref _txnPage, txnTotalCount, TxnPageSize))
      _transactions.Invalidate();
  }
}
