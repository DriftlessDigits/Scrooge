using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Scrooge.Windows
{
  /// <summary>
  /// Post-run triage window. Shows skipped items with pricing data
  /// so the user can decide what to vendor, ignore, or investigate.
  /// Reads from the most recent RunData.TriageItems.
  /// </summary>
  internal sealed class TriageWindow : Window
  {
    private List<PricingItem>? _sortedItems;

    internal TriageWindow() : base("Scrooge - Triage")
    {
      SizeConstraints = new WindowSizeConstraints
      {
        MinimumSize = new Vector2(600, 200),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
      };
      Size = new Vector2(1050, 400);
      SizeCondition = ImGuiCond.FirstUseEver;
      IsOpen = false;
    }

    public override void Draw()
    {
      var run = Plugin.CurrentRun ?? _lastRun;
      if (run == null || run.TriageItems.Count == 0)
      {
        ImGui.TextDisabled("No triage items.");
        return;
      }

      var triageItems = run.TriageItems;

      // Summary line
      var totalVendorGil = triageItems.Sum(t => t.VendorPrice * t.Quantity);
      ImGui.Text($"{triageItems.Count} {(triageItems.Count == 1 ? "item" : "items")} skipped — vendoring all would free {triageItems.Count} {(triageItems.Count == 1 ? "slot" : "slots")} and net {totalVendorGil:N0} gil");
      ImGui.Spacing();

      if (ImGui.BeginTable("TriageTable", 10,
        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
        | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp
        | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate))
      {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort, 1.0f);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("MB/ea", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Vendor/ea", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Gil In", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("vs MB", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableHeadersRow();

        // Column header tooltips (columns 2-9)
        for (int col = 2; col <= 9; col++)
        {
          ImGui.TableSetColumnIndex(col);
          if (ImGui.IsItemHovered())
          {
            var tip = col switch
            {
              2 => "Stack size of the listing",
              3 => "Your current listing price per unit",
              4 => "Current market board price per unit",
              5 => "NPC vendor sell price per unit",
              6 => "Total gil received if vendor-sold (Vendor/ea × Qty)",
              7 => "Gil difference if you vendor instead of selling on MB at the listed price",
              8 => "Why this item was skipped during the run",
              9 => "Sale history and pricing context for the skip decision",
              _ => ""
            };
            ImGui.SetTooltip(tip);
          }
        }

        // Sorting
        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsDirty || _sortedItems == null || _sortedItems.Count != triageItems.Count)
        {
          _sortedItems = new List<PricingItem>(triageItems);

          if (sortSpecs.SpecsCount > 0)
          {
            var spec = sortSpecs.Specs;
            var col = spec.ColumnIndex;
            var asc = spec.SortDirection == ImGuiSortDirection.Ascending;

            _sortedItems.Sort((a, b) =>
            {
              var cmp = col switch
              {
                0 => string.Compare(a.ItemName, b.ItemName),
                1 => string.Compare(a.RetainerName, b.RetainerName),
                2 => a.Quantity.CompareTo(b.Quantity),
                3 => (a.CurrentListingPrice ?? 0).CompareTo(b.CurrentListingPrice ?? 0),
                4 => (a.MbPrice ?? 0).CompareTo(b.MbPrice ?? 0),
                5 => a.VendorPrice.CompareTo(b.VendorPrice),
                6 => (a.VendorPrice * a.Quantity).CompareTo(b.VendorPrice * b.Quantity),
                7 => VsMb(a).CompareTo(VsMb(b)),
                8 => a.Result.CompareTo(b.Result),
                9 => a.HistorySaleCount.CompareTo(b.HistorySaleCount),
                _ => 0
              };
              return asc ? cmp : -cmp;
            });
          }

          sortSpecs.SpecsDirty = false;
        }

        foreach (var item in _sortedItems)
        {
          ImGui.TableNextRow();

          var color = item.Result switch
          {
            PricingResult.BelowFloor => new Vector4(1f, 0.3f, 0.3f, 1f),
            PricingResult.BelowMinimum => new Vector4(1f, 0.7f, 0.2f, 1f),
            PricingResult.CapBlocked => new Vector4(1f, 1f, 0.3f, 1f),
            PricingResult.UndercutTooDeep => new Vector4(1f, 1f, 0.3f, 1f),
            _ => new Vector4(0.5f, 0.5f, 0.5f, 1f),
          };

          // Item
          ImGui.TableNextColumn();
          ImGui.PushStyleColor(ImGuiCol.Text, color);
          ImGui.Text(item.ItemName);
          ImGui.PopStyleColor();

          // Retainer, Qty
          ImGui.TableNextColumn(); ImGui.Text(item.RetainerName);
          ImGui.TableNextColumn(); ImGui.Text($"{item.Quantity}");

          // Listed (current listing price)
          ImGui.TableNextColumn();
          ImGui.Text(item.CurrentListingPrice.HasValue ? $"{item.CurrentListingPrice:N0}" : "—");

          // MB/ea, Vendor/ea
          ImGui.TableNextColumn(); ImGui.Text(item.MbPrice.HasValue ? $"{item.MbPrice:N0}" : "—");
          ImGui.TableNextColumn(); ImGui.Text($"{item.VendorPrice:N0}");

          // Gil In
          ImGui.TableNextColumn();
          ImGui.Text($"{item.VendorPrice * item.Quantity:N0}");

          // vs MB
          ImGui.TableNextColumn();
          if (item.MbPrice.HasValue)
          {
            var vsMb = (item.VendorPrice - item.MbPrice.Value) * item.Quantity;
            var vsMbColor = vsMb >= 0
              ? new Vector4(0.4f, 0.9f, 0.4f, 1f)
              : new Vector4(1f, 0.4f, 0.4f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Text, vsMbColor);
            ImGui.Text($"{vsMb:+#;-#;0}");
            ImGui.PopStyleColor();
          }
          else
            ImGui.TextDisabled("—");

          // Reason
          ImGui.TableNextColumn();
          ImGui.Text(BuildReason(item));

          // Details
          ImGui.TableNextColumn();
          ImGui.Text(BuildDetails(item));
        }
        ImGui.EndTable();
      }
    }

    private static string BuildReason(PricingItem item)
    {
      return item.Result switch
      {
        PricingResult.BelowMinimum =>
          $"Below Minimum (MB/ea at {item.MbPrice:N0} gil < {Plugin.Configuration.MinimumListingPrice:N0} gil min)",
        PricingResult.BelowFloor =>
          $"Below Floor (MB/ea at {item.MbPrice:N0} gil < {item.VendorPrice:N0} gil vendor)",
        PricingResult.CapBlocked =>
          $"Cap ({item.CurrentListingPrice:N0} → {item.MbPrice:N0}, {item.PriceChangePercent:F0}%)",
        PricingResult.UndercutTooDeep =>
          $"Undercut Too Deep ({item.PriceChangePercent:F0}%)",
        PricingResult.NoData => "No Data (no listings)",
        _ => "Unknown"
      };
    }

    private static string BuildDetails(PricingItem item)
    {
      if (item.HistorySaleCount <= 0)
        return "No sales in past 14 days";

      var label = item.HistorySaleCount == 1 ? "sale" : "sales";
      return $"{item.HistorySaleCount} {label} over the past 14 days: median {item.HistoryMedianPrice:N0}, avg {item.HistoryAvgPrice:N0}";
    }

    private static int VsMb(PricingItem item) =>
      item.MbPrice.HasValue ? (item.VendorPrice - item.MbPrice.Value) * item.Quantity : 0;

    private RunData? _lastRun;

    /// <summary>Stores a reference to the run so triage data survives after CurrentRun is cleared.</summary>
    internal void SetRun(RunData run)
    {
      _lastRun = run;
      _sortedItems = null; // force re-sort on new data
    }
  }
}
