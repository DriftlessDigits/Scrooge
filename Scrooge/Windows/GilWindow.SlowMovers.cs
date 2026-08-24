using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The SLOW MOVERS tab - asks listed 7+ days, with the next round's note.</summary>
internal sealed partial class GilWindow
{
  // One of the four tabs that used to re-query SQLite on every single ImGui frame.
  private readonly TabCache<List<ListingRecord>> _slowMovers =
    new(() => GilStorage.GetSlowMovers(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (7 * 24 * 3600L)),
        TabCache.Tab);

  private void DrawSlowMoversTab()
  {
    ImGui.TextDisabled("Listed 7+ days");
    ImGui.Spacing();

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var slowMovers = _slowMovers.Get();

    if (slowMovers.Count > 0)
    {
      if (ImGui.BeginTable("SlowMovers", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable))
      {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 200);
        ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthStretch, 80);
        ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthStretch, 100);
        ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.WidthStretch, 60);
        // The forward tense. NoSort keeps the sort indices below meaning what they
        // always meant, and there is nothing to sort on half the rows anyway - the
        // preview only exists for lanes the ledger has scored.
        ImGui.TableSetupColumn("Next round", ImGuiTableColumnFlags.NoSort | ImGuiTableColumnFlags.WidthStretch, 90);
        ImGui.TableHeadersRow();

        var (col, asc) = TableSort.Spec();
        slowMovers = col switch
        {
          1 => TableSort.Order(slowMovers, m => m.UnitPrice, asc),
          2 => TableSort.Order(slowMovers, m => m.Category, asc),
          3 => TableSort.Order(slowMovers, m => m.FirstSeenTimestamp, asc),
          _ => TableSort.Order(slowMovers, m => m.ItemName, asc),
        };

        foreach (var item in slowMovers)
        {
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(Format.Hq(item.ItemName, item.IsHQ));
          ImGui.TableNextColumn(); ImGui.Text($"{item.UnitPrice:N0}");
          ImGui.TableNextColumn(); ImGui.Text(item.Category);
          ImGui.TableNextColumn(); ImGui.Text(OnMarket.RelativeAt(item.FirstSeenTimestamp, now));

          // What the shelf audit was missing: this tab told the past, and the
          // number that answers "so what now" was already computed one window
          // over. Same value the On Market List cell renders, no second spine.
          ImGui.TableNextColumn();
          var nextRound = BoardListings.NextRoundNote(item.UnitPrice,
            Plugin.Accountant.RelistPreview(item.ItemId, item.IsHQ, item.RetainerName));
          if (nextRound.Length > 0)
          {
            ImGui.TextDisabled(nextRound);
            if (ImGui.IsItemHovered())
              ImGui.SetTooltip(nextRound == "holds"
                ? "The next round leaves this ask where it is - the seat it holds is still the seat the walk would take."
                : $"The next round reprices this ask to {nextRound} - from your last pinch's evidence, not a live read.");
          }
        }

        ImGui.EndTable();
      }
    }
    else
    {
      ImGui.TextDisabled("No slow movers — everything is moving!");
    }
  }
}
