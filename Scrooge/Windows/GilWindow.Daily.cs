using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The DAILY tab - the day-by-day total and its change, newest first.</summary>
internal sealed partial class GilWindow
{
  private readonly TabCache<List<(string Date, long TotalGil, long Delta)>> _daily =
    new(GilStorage.GetDailyChanges, TabCache.Tab);

  private void DrawDailyChangeTab()
  {
    var daily = _daily.Get();
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
      ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.WidthStretch, 100);
      ImGui.TableSetupColumn("Total Gil", ImGuiTableColumnFlags.WidthStretch, 120);
      ImGui.TableSetupColumn("Change", ImGuiTableColumnFlags.WidthStretch, 100);
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
          ImGui.TextColored(ScroogeColors.ForDelta(delta), Format.SignedGil(delta));
        }
      }

      ImGui.EndTable();
    }
  }
}
