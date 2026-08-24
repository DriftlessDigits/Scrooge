using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The EARNED VS SPENT tab - income and expenses by source over the window.</summary>
internal sealed partial class GilWindow
{
  private readonly TabCache<List<(string Direction, string Source, long Total, int Count)>> _earnedVsSpent;
  // Earned vs Spent
  private int _evsTimeFilter;  // 0=7d, 1=30d, 2=90d, 3=All
  private int _prevEvsTimeFilter = -1;

  private void DrawEarnedVsSpentTab()
  {
    var timeLabels = new[] { "7 days", "30 days", "90 days", "All time" };
    ImGui.SetNextItemWidth(120);
    ImGui.Combo("##EvsTime", ref _evsTimeFilter, timeLabels, timeLabels.Length);

    var since = SinceFor(_evsTimeFilter, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    if (_evsTimeFilter != _prevEvsTimeFilter)
    {
      _prevEvsTimeFilter = _evsTimeFilter;
      _earnedVsSpent.Invalidate();
    }

    DrawEarnedVsSpentContent(_earnedVsSpent.Get());
    ImGui.Spacing();
    DrawDataRangeDisclaimer(since, _earliestTransaction.Get());
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

    ImGui.Spacing();
    ImGui.TextColored(ScroogeColors.Earned, $"Earned: {Format.Gil(totalEarned)}");
    ImGui.SameLine(200);
    ImGui.TextColored(ScroogeColors.Spent, $"Spent: {Format.Gil(totalSpent)}");
    ImGui.SameLine(400);
    ImGui.TextColored(ScroogeColors.ForDelta(net), $"Net: {Format.SignedGil(net)}");
    ImGui.Separator();

    if (earned.Count > 0)
    {
      ImGui.Spacing();
      ImGui.TextColored(ScroogeColors.Earned, "Income");
      if (ImGui.BeginTable("EvsEarned", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 150);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthStretch, 120);
        ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthStretch, 60);
        ImGui.TableHeadersRow();

        foreach (var row in earned)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(FormatSourceLabel(row.Source));
          ImGui.TableNextColumn(); ImGui.TextColored(ScroogeColors.Earned, $"+{row.Total:N0}");
          ImGui.TableNextColumn(); ImGui.Text($"{row.Count}");
        }
        ImGui.EndTable();
      }
    }

    if (spent.Count > 0)
    {
      ImGui.Spacing();
      ImGui.TextColored(ScroogeColors.Spent, "Expenses");
      if (ImGui.BeginTable("EvsSpent", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
      {
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 150);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthStretch, 120);
        ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthStretch, 60);
        ImGui.TableHeadersRow();

        foreach (var row in spent)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(FormatSourceLabel(row.Source));
          ImGui.TableNextColumn(); ImGui.TextColored(ScroogeColors.Spent, $"-{row.Total:N0}");
          ImGui.TableNextColumn(); ImGui.Text($"{row.Count}");
        }
        ImGui.EndTable();
      }
    }
  }
}
