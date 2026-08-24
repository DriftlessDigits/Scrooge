using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The DESYNTH tab - the profitability readout and the raw yield ledger.</summary>
internal sealed partial class GilWindow
{
  private readonly TabCache<DesynthView> _desynth;
  // Desynth tab state
  private int _desynthTimeFilter = 1; // 0=30d, 1=90d, 2=All
  private int _prevDesynthTimeFilter = -1;
  private int _desynthPage;
  private int _desynthSourcePage;
  // Last frame's Recent Yields open state - the source table's height depends on
  // whether the section below it will want room, and ImGui only answers that
  // after the header draws. One frame of lag, invisible in practice.
  private bool _desynthYieldsOpen;
  private const int DesynthPageSize = 25;

  /// <summary>One page of the desynth ledger plus the prices it is valued at.</summary>
  private readonly record struct DesynthView(
    List<DesynthSourceSummary> Summary,
    List<DesynthYieldRow> Yields,
    long YieldCount,
    Dictionary<(uint ItemId, bool IsHq), (int Price, long Timestamp, int? SoldAfterDays)> SalePrices);

  /// <summary>
  /// Desynth tab — the profitability readout. Headline is what the materials
  /// are worth (vendor forfeit is meaningless — gear vendor prices are jokes);
  /// the per-source rows carry the advisor cues: yield value per attempt vs
  /// the source's own last sale price, plus GC Expert Delivery seals.
  /// </summary>
  private void DrawDesynthTab()
  {
    if (Plugin.DesynthYieldStore is not DesynthYieldStore store)
    {
      ImGui.TextDisabled("Yield storage unavailable this session.");
      return;
    }

    var timeLabels = new[] { "30 days", "90 days", "All time" };
    ImGui.SetNextItemWidth(100);
    ImGui.Combo("##DesynthTime", ref _desynthTimeFilter, timeLabels, timeLabels.Length);

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (_desynthTimeFilter != _prevDesynthTimeFilter)
    {
      _desynthPage = 0;
      _desynthSourcePage = 0;
      _prevDesynthTimeFilter = _desynthTimeFilter;
      _desynth.Invalidate();
    }

    var view = _desynth.Get();

    if (view.Summary.Count == 0)
    {
      ImGui.TextDisabled("No desynth yields recorded yet — run a desynth to start the ledger.");
      return;
    }

    // --- By source item: the advisor view ---
    if (ImGui.CollapsingHeader("By Source Item", ImGuiTreeNodeFlags.DefaultOpen))
    {
      // V8: the caption states the verdict AND its refusal. Green used to fire on a
      // yield of zero - an unknown - so an item nobody had ever measured a melt for
      // rendered its last sale in "list this instead" green off no comparison at all.
      ImGui.TextDisabled("Green = last sale beats the measured yield. No yield measured, no verdict.");
      // The scrollbar belongs to the table, not the window (Drift, 08-23): with the
      // yields section open the two split the tab; collapsed, sources take the room
      // down to the footer, the pager and the Recent Yields header line.
      var avail = ImGui.GetContentRegionAvail().Y;
      var sourceHeight = _desynthYieldsOpen
        ? Math.Max(140f, avail * 0.45f)
        : Math.Max(140f, avail - 110f);
      if (ImGui.BeginTable("DesynthSources", 5,
          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollY,
          new Vector2(-1, sourceHeight)))
      {
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 200);
        ImGui.TableSetupColumn("Desynths", ImGuiTableColumnFlags.DefaultSort | ImGuiTableColumnFlags.PreferSortDescending | ImGuiTableColumnFlags.WidthStretch, 60);
        // V9: the headers name their WITNESS. "Yield / attempt" is weighed on the
        // three-rung ladder (your own sale, then the DC's community read, floored by
        // the vendor counter); "Item last sale" only ever reads your own tape.
        ImGui.TableSetupColumn("Yield / attempt (best witness)", ImGuiTableColumnFlags.WidthStretch, 100);
        ImGui.TableSetupColumn("Item last sale (yours)", ImGuiTableColumnFlags.WidthStretch, 100);
        ImGui.TableSetupColumn("GC Seals", ImGuiTableColumnFlags.WidthStretch, 70);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var (col, asc) = TableSort.Spec();
        var summary = col switch
        {
          1 => TableSort.Order(view.Summary, s => s.Attempts, asc),
          2 => TableSort.Order(view.Summary, s => s.Attempts > 0 ? s.YieldValue / s.Attempts : 0, asc),
          _ => TableSort.Order(view.Summary, s => GilTracker.GetItemName(s.SourceItemId), asc),
        };

        // The summary is the whole aggregate in memory, so the page is a slice - no
        // cache to drop when it turns.
        var sourcePages = Math.Max(1, (summary.Count + DesynthPageSize - 1) / DesynthPageSize);
        if (_desynthSourcePage >= sourcePages) _desynthSourcePage = sourcePages - 1;

        foreach (var row in summary.Skip(_desynthSourcePage * DesynthPageSize).Take(DesynthPageSize))
        {
          var yieldPerAttempt = row.Attempts > 0 ? row.YieldValue / row.Attempts : 0;
          var sourceSale = view.SalePrices.TryGetValue((row.SourceItemId, row.SourceIsHq), out var sale) ? sale.Price : (int?)null;

          ImGui.TableNextRow();
          ImGui.TableNextColumn();
          ImGui.Text(Format.Hq(GilTracker.GetItemName(row.SourceItemId), row.SourceIsHq));
          ImGui.TableNextColumn(); ImGui.Text($"{row.Attempts}");
          ImGui.TableNextColumn();
          // THE VALUE NAMES ITS WITNESS (ruled 08-22): the blanket "~" claimed the
          // DC rung for numbers your own sale book had priced. The mark follows the
          // rung that priced most of the value; the hover names every contributor.
          ImGui.Text(yieldPerAttempt > 0
            ? DesynthWitness.Mark(yieldPerAttempt, row.OwnValue, row.CommunityValue, row.VendorValue)
            : "?");
          if (yieldPerAttempt > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(DesynthWitness.Hover(row.OwnValue, row.CommunityValue, row.VendorValue));
          ImGui.TableNextColumn();
          if (sourceSale is int salePrice)
          {
            // The graduation cue: this item is worth more whole than melted. It
            // needs a yield to beat - a zero here is UNKNOWN, not "worthless", and
            // green over an unknown is a verdict nobody reached (V8).
            if (yieldPerAttempt > 0 && salePrice > yieldPerAttempt)
            {
              ImGui.TextColored(ScroogeColors.Earned, $"{salePrice:N0}");
              if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Your own sale, and it beats the measured yield - this one may deserve the market board.");
            }
            else
            {
              ImGui.Text($"{salePrice:N0}");
              if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Your own last sale of one.");
            }
          }
          else
            ImGui.TextDisabled("—");
          ImGui.TableNextColumn();
          // FOUR STATES, FOUR CELLS (V9). One dash used to cover "we can't read it",
          // "it isn't gear", "the counter won't take it yet" and "eligible, pays
          // nothing" - four answers, one glyph, and the reader could not tell which
          // he was looking at. The law is still GcSeals'; only the reason is drawn.
          var (seals, sealState) = GcSeals.Explain(row.SourceItemId);
          if (seals is int s)
            ImGui.Text($"{s:N0}");
          else
            ImGui.TextDisabled(sealState switch
            {
              GcSealState.NotGear => "not gear",
              GcSealState.TooNew => "too new",
              GcSealState.NoSeals => "no seals",
              _ => "unreadable",
            });
        }

        ImGui.EndTable();
      }
      Pager("DesynthSrc", ref _desynthSourcePage, view.Summary.Count, DesynthPageSize);
      // V7: the footer keeps the ONE fact the ladder cannot say per value - what a
      // "?" means and that it never colours a row. The witness ladder itself moved
      // inline, to the column headers and the values' own hovers, where the reader
      // is looking at the number it is about.
      ImGui.TextDisabled("Unknown yields show ? and never color a row.");
    }

    ImGui.Spacing();

    // --- Recent yields: the raw ledger ---
    _desynthYieldsOpen = ImGui.CollapsingHeader("Recent Yields");
    if (_desynthYieldsOpen)
    {
      var yieldsHeight = Math.Max(120f, ImGui.GetContentRegionAvail().Y - 34f);
      if (ImGui.BeginTable("DesynthYields", 4,
          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY,
          new Vector2(-1, yieldsHeight)))
      {
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch, 90);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 180);
        ImGui.TableSetupColumn("Yield", ImGuiTableColumnFlags.WidthStretch, 180);
        // V9: this column reads last_sale_prices and nothing else - it is YOUR tape,
        // never the DC's read and never the vendor floor, so it says so.
        ImGui.TableSetupColumn("Est. value (your sales)", ImGuiTableColumnFlags.WidthStretch, 90);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var y in view.Yields)
        {
          var value = view.SalePrices.TryGetValue((y.YieldItemId, y.YieldIsHq), out var p) ? (long)p.Price * y.YieldQty : 0;
          ImGui.TableNextRow();
          ImGui.TableNextColumn(); ImGui.Text(OnMarket.RelativeAt(y.CapturedAt.ToUnixTimeSeconds(), now));
          ImGui.TableNextColumn(); ImGui.Text(Format.Hq(GilTracker.GetItemName(y.SourceItemId), y.SourceIsHq));
          ImGui.TableNextColumn(); ImGui.Text($"{(y.YieldQty > 1 ? $"{y.YieldQty}x " : "")}{Format.Hq(GilTracker.GetItemName(y.YieldItemId), y.YieldIsHq)}");
          ImGui.TableNextColumn();
          // BARE, not "~". On the witness ladder a tilde is the DC community rung;
          // this number is priced off your own sale of the mat, so wearing one would
          // claim a weaker witness than the cell actually has.
          if (value > 0)
          {
            ImGui.Text($"{value:N0}");
            if (ImGui.IsItemHovered())
              ImGui.SetTooltip("Priced off your own last sale of this material.");
          }
          else
          {
            ImGui.TextDisabled("—");
            if (ImGui.IsItemHovered())
              ImGui.SetTooltip("You have never sold this material - no witness of your own to price it with.");
          }
        }

        ImGui.EndTable();
      }

      if (Pager("Desynth", ref _desynthPage, view.YieldCount, DesynthPageSize))
        _desynth.Invalidate();
    }
  }
}
