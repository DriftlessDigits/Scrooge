using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>The VENTURES tab - token stock, gil-per-venture, and the recent returns.</summary>
internal sealed partial class GilWindow
{
  private readonly TabCache<VenturesView> _ventures;
  private int _venturesPage;
  private const int VenturesPageSize = 25;

  /// <summary>The window's returns plus the two token-flow reads they are judged against.</summary>
  private readonly record struct VenturesView(
    List<(long CapturedAt, string Retainer, uint ItemId, int Quantity, bool IsHq,
      uint? VentureId, int? VentureCost, string? VentureCategory)> Rows,
    int? WeeklySpend,
    double? Drift);

  /// <summary>
  /// Venture economics: token stock (colored by the routing tilt bands),
  /// gil-per-venture and the empirical seals-to-gil rate over the rolling
  /// window, then recent returns in the Desynth tab's grammar.
  /// </summary>
  private void DrawVenturesTab()
  {
    // Headline row
    var stock = GameSafe.VentureTokenCount();
    var cfg = Plugin.Configuration;
    if (stock is int tokens)
    {
      // THE PAINT DERIVES FROM THE CURVE (ruled 2026-08-23): orange below half
      // the seal curve's midpoint, red below a quarter - so the warning colors
      // move when the curve moves, and the two paint-only band knobs
      // (VentureBandLow/Panic) died with their fossil hover. Green/amber still
      // split at the tilt band, the one that changes a decision.
      var curveMid = (cfg.SealCurveFullBelow + cfg.SealCurveZeroAbove) / 2;
      var color = tokens >= cfg.VentureBandFull ? ScroogeColors.Earned
        : tokens >= curveMid / 2 ? ScroogeColors.Amber
        : tokens >= curveMid / 4 ? ScroogeColors.Warning
        : ScroogeColors.Spent;
      ImGui.TextColored(color, $"{tokens:N0} venture tokens");
    }
    else
      ImGui.TextDisabled("Venture tokens: unreadable (id unverified)");

    var stats = VentureReturns.Stats();
    ImGui.SameLine();
    if (stats is { Ventures: > 0 } s)
    {
      ImGui.TextDisabled("|");
      ImGui.SameLine();
      ImGui.Text($"{s.Ventures} ventures / {VentureReturns.WindowDays}d");
      ImGui.SameLine();
      ImGui.TextDisabled("|");
      ImGui.SameLine();
      ImGui.Text($"~{s.GilPerVenture:N0} gil/venture");
      if (VentureReturns.EmpiricalSealToGilRate() is int rate)
      {
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();
        ImGui.TextColored(ScroogeColors.Earned, $"{rate} gil/seal (measured)");
        // V16: THE VERDICT AND ITS TWO LIVE NUMBERS. Four sentences of curve
        // mechanism used to live here - full-value anchor, zero anchor, "sliding
        // between", and a note about where the reason says so. Those anchors are
        // the SealCurve knobs' own recital; what belongs at the rate is what the
        // rate IS and what this round's stock did to it.
        if (ImGui.IsItemHovered())
        {
          // Same curve, same knobs, same measured rate the router scores with -
          // one arithmetic, so the readout and the verdict cannot disagree.
          var seal = SealRunway.Effective(rate, stock, cfg.SealCurveFullBelow, cfg.SealCurveZeroAbove);
          ImGui.SetTooltip($"Turn-in scores at the measured {rate} gil/seal"
            + (seal.Discounted ? $" - reduced to {seal.EffectiveRate:0.##} by current stock." : "."));
        }
      }
      else
      {
        ImGui.SameLine();
        ImGui.TextDisabled($"| gil/seal: placeholder {cfg.SealToGilRate} (measures at 10+ ventures)");
      }
    }
    else
      ImGui.TextDisabled("| No venture returns captured yet - collect a quick venture to start the ledger.");

    var view = _ventures.Get();
    if (view.Drift is double drift)
    {
      // TWO instruments, both named (Drift, 08-23: "how are we only acquiring 1.7
      // tokens/day?"). The old line said "Acquiring" off the stock's NET drift -
      // first snapshot vs last - which reads as a gross rate and is not one. The
      // gross spend is the sitrep's own weekly burn; "after purchases" carries the
      // how without reciting it (reworded on Drift's redline, same day).
      var driftText = $"stock {(drift >= 0 ? "+" : "")}{drift:F1}/day";
      ImGui.TextDisabled(view.WeeklySpend is int wk
        ? $"Spending ~{wk / 7.0:F0} tokens/day; {driftText} after purchases."
        : $"Stock {(drift >= 0 ? "+" : "")}{drift:F1} tokens/day (weekly spend unmeasured).");
    }

    ImGui.Spacing();
    ImGui.Separator();

    // Recent returns
    var rows = view.Rows;
    if (rows.Count == 0)
    {
      // V18: the player's own act, not the packet we listen for. "RetainerTaskResult
      // capture" named an internal seam on an empty-state line whose whole job is to
      // tell him what fills it.
      ImGui.TextDisabled("Returns land here as you collect them at the bell.");
      return;
    }

    var tableHeight = Math.Max(120f, ImGui.GetContentRegionAvail().Y - 30f);
    if (ImGui.BeginTable("VentureReturns", 5,
        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH,
        new Vector2(-1, tableHeight)))
    {
      ImGui.TableSetupScrollFreeze(0, 1);
      ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 80);
      ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 0.8f);
      ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.4f);
      ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 40);
      ImGui.TableSetupColumn("Est. value", ImGuiTableColumnFlags.WidthFixed, 80);
      ImGui.TableHeadersRow();

      var sheet = ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
      var nowS = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      // The window's rows are all in memory, so the page is a slice - nothing to
      // invalidate when it turns. Clamp survives the cache refreshing shorter.
      var pages = Math.Max(1, (rows.Count + VenturesPageSize - 1) / VenturesPageSize);
      if (_venturesPage >= pages) _venturesPage = pages - 1;
      foreach (var r in rows.Skip(_venturesPage * VenturesPageSize).Take(VenturesPageSize))
      {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(OnMarket.RelativeAt(r.CapturedAt, nowS));
        ImGui.TableNextColumn(); ImGui.Text(r.Retainer);
        ImGui.TableNextColumn(); ImGui.Text(Format.Hq(GilTracker.GetItemName(r.ItemId), r.IsHq));
        ImGui.TableNextColumn(); ImGui.Text(r.Quantity.ToString());
        ImGui.TableNextColumn();
        var value = (long)VentureReturns.ValuePerUnit(r.ItemId, r.IsHq, sheet) * r.Quantity;
        ImGui.Text($"{value:N0}");
      }
      ImGui.EndTable();
    }

    Pager("Ventures", ref _venturesPage, rows.Count, VenturesPageSize);
  }
}
