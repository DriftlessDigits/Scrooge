using System;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The GOALS tab - the three goal buckets and the crossings ledger.</summary>
internal sealed partial class GilWindow
{

  /// <summary>
  /// Goals tab — set the three goal buckets and review the crossings ledger.
  /// Shapes are generic (per-retainer bank, walking gil, total worth); any,
  /// all, or none can be active.
  /// </summary>
  private void DrawGoalsTab()
  {
    var config = Plugin.Configuration;

    ImGui.TextColored(ScroogeColors.Header, "Name your mark. Scrooge keeps count.");
    ImGui.TextDisabled("Set a target to 0 to retire it. Crossings are celebrated in chat and recorded below.");
    ImGui.Spacing();

    var perRetainer = config.GoalPerRetainer;
    if (DrawGoalInput("Bank per retainer", ref perRetainer))
      config.GoalPerRetainer = perRetainer;
    if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();
    ImGui.SameLine();
    ImGui.TextDisabled("(?)");
    if (ImGui.IsItemHovered())
      // V21: QUOTE THE STRING THE PRODUCT PRINTS. The tooltip advertised "N of M
      // retainers at target", which no surface has ever rendered - the money line's
      // bar is labelled by GilGoals.GetProgress, and that is its grammar. A tooltip
      // that quotes a string the reader will never see teaches him to distrust the
      // ones that do. The bell line is the silent dependency underneath it
      // (GilGoals.Evaluate gates every retainer goal on snap.RetainerGil).
      ImGui.SetTooltip("Every retainer holds at least this much."
        + "\nProgress reads \"Retainer bank: 3/5 at 1,000,000\"."
        + "\nNeeds a bell visit before retainer marks can count.");

    var playerGil = config.GoalPlayerGil;
    if (DrawGoalInput("Walking-around gil", ref playerGil))
      config.GoalPlayerGil = playerGil;
    if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();

    var totalGil = config.GoalTotalGil;
    if (DrawGoalInput("Total worth", ref totalGil))
      config.GoalTotalGil = totalGil;
    if (ImGui.IsItemDeactivatedAfterEdit()) config.Save();

    ImGui.Spacing();
    ImGui.Separator();
    ImGui.Spacing();

    ImGui.TextColored(ScroogeColors.Header, "The Ledger of Marks");
    if (config.GoalHistory.Count == 0)
    {
      ImGui.TextDisabled("No marks crossed yet. The vault remembers when you do.");
      return;
    }

    if (ImGui.BeginTable("GoalHistory", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
    {
      ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.WidthStretch, 90);
      ImGui.TableSetupColumn("Goal", ImGuiTableColumnFlags.WidthStretch, 110);
      ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthStretch, 110);
      ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthStretch, 180);
      ImGui.TableHeadersRow();

      var culture = System.Globalization.CultureInfo.CurrentCulture;
      for (int i = config.GoalHistory.Count - 1; i >= 0; i--)
      {
        var rec = config.GoalHistory[i];
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(DateTimeOffset.FromUnixTimeSeconds(rec.AchievedAt).LocalDateTime.ToString("d", culture));
        ImGui.TableNextColumn();
        ImGui.Text(rec.Kind switch
        {
          "retainer" => "Retainer bank",
          "player" => "Walking gil",
          "total" => "Total worth",
          _ => rec.Kind,
        });
        ImGui.TableNextColumn();
        ImGui.TextColored(ScroogeColors.Gold, $"{rec.Target:N0}");
        ImGui.TableNextColumn();
        ImGui.Text(rec.Detail);
      }

      ImGui.EndTable();
    }
  }

  /// <summary>Digits-only gil input; returns true when the parsed value changed.</summary>
  private static bool DrawGoalInput(string label, ref long value)
  {
    var text = value > 0 ? value.ToString() : string.Empty;
    ImGui.SetNextItemWidth(160);
    if (!ImGui.InputText(label, ref text, 15, ImGuiInputTextFlags.CharsDecimal))
      return false;

    if (string.IsNullOrWhiteSpace(text))
    {
      var changed = value != 0;
      value = 0;
      return changed;
    }

    if (long.TryParse(text, out var parsed) && parsed >= 0 && parsed != value)
    {
      value = parsed;
      return true;
    }

    return false;
  }
}
