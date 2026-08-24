using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>
/// THE SHARED GRAMMAR. The named helpers more than one tab speaks through - the
/// source labels, the time-window cutoffs, the pager strip, the data-range
/// disclaimer and the category-chain tooltips. A tab keeps its own state and its
/// own draw; anything two tabs would otherwise each spell out lives here once.
/// </summary>
internal sealed partial class GilWindow
{
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
      "fc_chest" => "FC Chest",
      "custom_delivery" => "Custom Delivery",
      "wondrous_tails" => "Wondrous Tails",
      "other" => "Other (untracked)",
      _ => source,
    };
  }

  /// <summary>
  /// The dashboard's time combo, as the cutoff a query wants: "7 days" / "30 days"
  /// / "90 days" / "All time". Null is all time - no floor at all, which is not the
  /// same as a floor of zero. Read ONCE per draw and handed to both the query and
  /// the data-range disclaimer, so the window can never filter on one window and
  /// caption another.
  /// </summary>
  private static long? SinceFor(int filterIndex, long now) => filterIndex switch
  {
    0 => now - 7 * 86400L,
    1 => now - 30 * 86400L,
    2 => now - 90 * 86400L,
    _ => null,
  };

  /// <summary>
  /// The desynth tab's shorter combo - "30 days" / "90 days" / "All time" - whose
  /// store takes a floor rather than a nullable, and reads 0 as "everything".
  /// </summary>
  private static long SinceForDesynth(int filterIndex, long now) => filterIndex switch
  {
    0 => now - 30 * 86400L,
    1 => now - 90 * 86400L,
    _ => 0L,
  };

  /// <summary>
  /// The prev / page-of / next strip every paged table wears. Returns true when the
  /// player moved, which is the caller's cue to drop its own page cache - the cache
  /// is the caller's, so the invalidation stays there.
  /// </summary>
  private static bool Pager(string id, ref int page, long totalRows, int pageSize)
  {
    var totalPages = Math.Max(1, (int)((totalRows + pageSize - 1) / pageSize));
    var moved = false;

    ImGui.BeginDisabled(page <= 0);
    if (ImGui.ArrowButton($"##{id}Prev", ImGuiDir.Left)) { page--; moved = true; }
    ImGui.EndDisabled();
    ImGui.SameLine();
    ImGui.Text($"Page {page + 1} of {totalPages}");
    ImGui.SameLine();
    ImGui.BeginDisabled(page >= totalPages - 1);
    if (ImGui.ArrowButton($"##{id}Next", ImGuiDir.Right)) { page++; moved = true; }
    ImGui.EndDisabled();
    ImGui.SameLine();
    ImGui.TextDisabled($"({totalRows:N0} total)");

    return moved;
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

  /// <summary>
  /// Renders a tooltip showing the macro → display → raw category chain for the
  /// last-drawn item when hovered. No-op if the item isn't hovered or the
  /// category string is empty (e.g. teleport, catch-all). If the category has
  /// no mapping in category_groups, just shows the raw category.
  /// </summary>
  private static void DrawCategoryChainTooltipIfHovered(string category)
  {
    if (string.IsNullOrEmpty(category)) return;
    if (!ImGui.IsItemHovered()) return;

    var group = GilStorage.GetCategoryGroup(category);
    var parts = new List<string>(3) { category };
    if (group.HasValue)
    {
      if (!string.IsNullOrEmpty(group.Value.Display) && group.Value.Display != category)
        parts.Add(group.Value.Display);
      if (!string.IsNullOrEmpty(group.Value.Macro)) parts.Add(group.Value.Macro);
    }

    ImGui.BeginTooltip();
    ImGui.TextUnformatted(string.Join(" \u2192 ", parts));
    ImGui.EndTooltip();
  }

  /// <summary>
  /// Tooltip variant for Categories-tab rows: shows the row's parent chain
  /// (upward-looking). Empty parents are dropped, so "By Category" rows can
  /// pass just the macro while "By Item Type" rows pass macro + display.
  /// No tooltip when hovered row has no non-empty parents.
  /// </summary>
  private static void DrawParentChainTooltipIfHovered(params string[] parents)
  {
    if (!ImGui.IsItemHovered()) return;

    var chain = string.Join(" \u2192 ", parents.Where(p => !string.IsNullOrEmpty(p)));
    if (string.IsNullOrEmpty(chain)) return;

    ImGui.BeginTooltip();
    ImGui.TextUnformatted(chain);
    ImGui.EndTooltip();
  }
}
