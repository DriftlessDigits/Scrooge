using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System.Linq;
using System.Diagnostics;

namespace Scrooge.Windows
{

  /// <summary>Outcome type for pinch run log entries.</summary>
  public enum ItemOutcome
  {
    Skipped,     // red — rule blocked, no price set
    NoData,      // yellow — no competition, player decides
    Outlier,     // normal — system handled it, got a price
    VendorSold,  // green — vendor-sold through retainer
    Banned       // blue — on ban list, observed but not changed
  }

  /// <summary>Run-level event type for lifecycle markers and summary lines.</summary>
  public enum RunEvent
  {
    Start,
    End,
    Summary
  }

  /// <summary>Shared interface for all log items. Enables mixed-type list with insertion-order rendering.</summary>
  public interface ILogItem { }

  /// <summary>A single entry in the pinch run log.</summary>
  public record LogEntry(ItemOutcome Outcome, string RetainerName, string ItemName, string Message) : ILogItem
  {
    public int BaitPrice { get; init; }
    public int UsedPrice { get; init; }
  }

  /// <summary>A run-level entry (start/end markers, summary stats).</summary>
  public record RunEntry (RunEvent EventType, string Message) : ILogItem;

  /// <summary>Retainer section header. Inserted lazily on first entry for each retainer.</summary>
  public record RetainerHeader(string RetainerName) : ILogItem;

  internal class PinchRunLogWindow : Window
  {

    // UI-only state (not business data)
    private bool _autoScroll = true;

    // Convenience accessor — falls back to reading RunData, keeps UI working after run ends
    private RunData? Run => Plugin.CurrentRun ?? _lastRun;
    private RunData? _lastRun;

    /// <summary>
    /// Clears the log and opens the window. Called at the start of each pinch run.
    /// </summary>
    public void StartNewRun(bool isHawkRun = false)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      var run = Plugin.CurrentRun;
      if (run == null) return;

      _lastRun = run;
      run.RunStopwatch.Restart();
      run.EtaStopwatch.Restart();
      var label = isHawkRun ? "Hawk Run" : "Run";
      WindowName = isHawkRun ? "Scrooge - Hawk Run Log" : "Scrooge - Pinch Run Log";
      run.AddRunEntry(RunEvent.Start, $"{label} Started — {DateTime.Now:h:mm tt}");
      IsOpen = true;
    }

    /// <summary>
    /// Updates the current retainer name. Subsequent log entries will be tagged with this name.
    /// </summary>
    public void SetCurrentRetainer(string retainerName)
    {
      var run = Run;
      if (run != null)
        run.CurrentRetainer = retainerName;
    }

    /// <summary>
    /// Adds an entry to the log. Inserts a retainer header automatically
    /// on the first entry for each retainer. Guards on EnablePinchRunLog.
    /// </summary>
    public void AddEntry(ItemOutcome outcome, string itemName, string message)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      Run?.AddLogEntry(outcome, itemName, message);
    }

    /// <summary>Adds an outlier entry with bait/used prices for colored rendering.</summary>
    public void AddOutlierEntry(string itemName, int baitPrice, int usedPrice)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      Run?.AddOutlierEntry(itemName, baitPrice, usedPrice);
    }

    /// <summary>
    /// Increments the successful adjustment counter. Called from Communicator.PrintPriceUpdate.
    /// </summary>
    public void IncrementAdjusted()
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      var run = Run;
      if (run != null) run.ItemsAdjusted++;
    }

    /// <summary>
    /// Increments the outlier detection counter. Called from Communicator.PrintOutlierDetected.
    /// </summary>
    public void IncrementOutliers()
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      var run = Run;
      if (run != null) run.OutliersDetected++;
    }

    /// <summary>
    /// Adds to the running total of gil currently listed on the market board.
    /// Called from AutoPinch.SetNewPrice for every item — adjusted or skipped.
    /// </summary>
    public void AddListingValue(int gil)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      var run = Run;
      if (run != null) run.TotalListingGil += gil;
    }

    /// <summary>Tracks a vendor sale for the run summary.</summary>
    public void AddVendorSale(long gil)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      var run = Run;
      if (run != null)
      {
        run.VendorSoldCount++;
        run.VendorSoldGil += gil;
      }
    }

    /// <summary>
    /// Adds end marker and summary lines to the log. Called from AutoPinch when all tasks finish.
    /// </summary>
    public void EndRun()
    {
      var run = Run;
      if (run == null) return;

      var isHawkRun = run.Mode == RunMode.Hawk;
      var label = isHawkRun ? "Hawk Run" : "Run";

      run.AddRunEntry(RunEvent.End, $"{label} Complete — {DateTime.Now:h:mm tt}");
      run.AddRunEntry(RunEvent.Summary, isHawkRun
        ? $"{run.ItemsAdjusted} listed"
        : $"{run.ItemsAdjusted} adjusted");
      if (run.SkippedCount > 0)
        run.AddRunEntry(RunEvent.Summary, $"{run.SkippedCount} skipped");
      if (run.NoDataCount > 0)
        run.AddRunEntry(RunEvent.Summary, $"{run.NoDataCount} no data");

      if (run.OutliersDetected > 0)
        run.AddRunEntry(RunEvent.Summary, $"{run.OutliersDetected} outliers");

      if (run.VendorSoldCount > 0)
        run.AddRunEntry(RunEvent.Summary, $"{run.VendorSoldCount} vendor-sold for {run.VendorSoldGil:N0} gil");

      run.AddRunEntry(RunEvent.Summary, isHawkRun
        ? $"{run.TotalListingGil:N0} gil put on market"
        : $"{run.TotalListingGil:N0} gil on market");

      // Hand off triage data to the triage window
      if (run.TriageItems.Count > 0)
        Plugin.TriageWindow.SetRun(run);

      // Stop timer and mark run complete
      run.IsComplete = true;
      run.RunStopwatch.Stop();
      run.EtaStopwatch.Stop();

      // Update the rolling per-item average
      if (run.ItemsProcessed > 0)
      {
        var currentAvg = (float)run.RunStopwatch.ElapsedMilliseconds / run.ItemsProcessed;
        var stored = Plugin.Configuration.AvgMsPerItem;

        if (stored <= 0f)
          Plugin.Configuration.AvgMsPerItem = currentAvg; // first run ever
        else
          Plugin.Configuration.AvgMsPerItem = (stored * 0.7f) + (currentAvg * 0.3f);  // weighted blend

        Plugin.Configuration.Save();
      }
    }

    /// <summary>
    /// Sets the total expected item count for the run. Called once before
    /// retainer processing begins, using pre-scanned counts from the
    /// RetainerList addon's AtkValues. Calculates initial ETA countdown.
    /// </summary>
    public void SetTotalItems(int total)
    {
      var run = Run;
      if (run == null) return;

      run.TotalItems = total;

      var storedAvg = Plugin.Configuration.AvgMsPerItem;
      if (storedAvg > 0f)
      {
        run.EtaCountdownMs = storedAvg * total;
        run.EtaStopwatch.Restart();
      }
    }

    /// <summary>
    /// Marks one item as processed (success or skip). Called from AutoPinch.SetNewPrice.
    /// </summary>
    public void IncrementProcessed()
    {
      var run = Run;
      if (run == null) return;

      run.ItemsProcessed++;

      var storedAvg = Plugin.Configuration.AvgMsPerItem;
      if (storedAvg > 0f)
      {
        var remaining = run.TotalItems - run.ItemsProcessed;
        run.EtaCountdownMs = storedAvg * remaining;
        run.EtaStopwatch.Restart();
      }
    }

    /// <summary>
    /// Cancels the current run and stops the timers
    /// </summary>
    public void CancelRun()
    {
      var run = Run;
      if (run == null) return;

      run.IsComplete = true;
      run.RunStopwatch.Stop();
      run.EtaStopwatch.Stop();
    }

    public PinchRunLogWindow() : base("Scrooge - Pinch Run Log")
    {
      SizeConstraints = new WindowSizeConstraints
      {
        MinimumSize = new(300, 200),
        MaximumSize = new(800, 600)
      };
      Size = new System.Numerics.Vector2(400, 300);
      SizeCondition = ImGuiCond.FirstUseEver;
      IsOpen = false;
    }

    public override void Draw()
    {
      var run = Run;
      if (run == null) return;

      // Scrollable child region for log entries
      // Reserve space at the bottom for the status bar
      var footerHeight = ImGui.GetFrameHeightWithSpacing() + 4;
      ImGui.BeginChild("##logEntries", new System.Numerics.Vector2(0, -footerHeight), false);

      bool treeOpen = false;

      foreach (var item in run.LogEntries)
      {
        switch (item)
        {
          case RunEntry runEntry:
          {
            if (treeOpen) { ImGui.TreePop(); treeOpen = false; }

            if (runEntry.EventType == RunEvent.Start || runEntry.EventType == RunEvent.End)
            {
              ImGui.Spacing();
              ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1f));
              ImGui.TextWrapped($"--- {runEntry.Message} ---");
              ImGui.PopStyleColor();
              ImGui.Spacing();
            }
            else if (runEntry.EventType == RunEvent.Summary)
            {
              ImGui.Indent(16);
              ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.8f, 1f, 1f));
              ImGui.TextWrapped(runEntry.Message);
              ImGui.PopStyleColor();
              ImGui.Unindent(16);
            }
            break;
          }

          case RetainerHeader header:
          {
            if (treeOpen) ImGui.TreePop();
            treeOpen = ImGui.TreeNodeEx(header.RetainerName, ImGuiTreeNodeFlags.DefaultOpen);
            break;
          }

          case LogEntry entry:
          {
            if (!treeOpen) break;

            if (entry.Outcome == ItemOutcome.Outlier)
            {
              // Normal text with colored prices — mimics in-game chat
              ImGui.Text($"{entry.ItemName} — skipping ");
              ImGui.SameLine(0, 0);
              ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f));
              ImGui.Text($"{entry.BaitPrice:N0}");
              ImGui.PopStyleColor();
              ImGui.SameLine(0, 0);
              ImGui.Text(" gil, using ");
              ImGui.SameLine(0, 0);
              ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f));
              ImGui.Text($"{entry.UsedPrice:N0}");
              ImGui.PopStyleColor();
              ImGui.SameLine(0, 0);
              ImGui.Text(" gil");
            }
            else
            {
              var color = entry.Outcome switch
              {
                ItemOutcome.Skipped => new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f),
                ItemOutcome.NoData => new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f),
                ItemOutcome.VendorSold => new System.Numerics.Vector4(0.4f, 0.9f, 0.4f, 1f),
                ItemOutcome.Banned => new System.Numerics.Vector4(0.4f, 0.6f, 1f, 1f),
                _ => new System.Numerics.Vector4(1f, 1f, 1f, 1f)
              };

              ImGui.PushStyleColor(ImGuiCol.Text, color);
              ImGui.TextWrapped($"{entry.ItemName} — {entry.Message}");
              ImGui.PopStyleColor();
            }

            break;
          }
        }
      }

      if (treeOpen) ImGui.TreePop();

      // --- Triage summary + launch button ---
      if (run.IsComplete && run.TriageItems.Count > 0)
      {
        ImGui.Spacing();
        ImGui.Indent(16);
        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f));
        ImGui.Text($"{run.TriageItems.Count} {(run.TriageItems.Count == 1 ? "item needs" : "items need")} triage");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.SmallButton("Open Triage"))
          Plugin.TriageWindow.IsOpen = true;
        ImGui.Unindent(16);
      }

      // Auto-scroll to bottom when new entries appear
      if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
        ImGui.SetScrollHereY(1.0f);

      ImGui.EndChild();

      // Bottom bar: Clear button + entry count + progress + timer + copy all
      ImGui.Separator();
      ImGui.BeginDisabled(Plugin.CurrentRun != null);
      if (ImGui.Button("Clear"))
      {
        _lastRun = null;
        ImGui.EndDisabled();
        return;
      }
      ImGui.EndDisabled();
      ImGui.SameLine();
      var logEntryCount = run.LogEntries.OfType<LogEntry>().Count();
      ImGui.Text($"{logEntryCount} {(logEntryCount == 1 ? "entry" : "entries")}");

      // Progress + Timer display
      if (run.TotalItems > 0)
      {
        ImGui.SameLine();
        ImGui.TextDisabled(" | ");
        ImGui.SameLine();
        ImGui.Text($"{run.ItemsProcessed}/{run.TotalItems}");
      }

      // ETA / final time display
      if (run.IsComplete)
      {
        // Run finished — show final elapsed time
        ImGui.SameLine();
        ImGui.TextDisabled(" | ");
        ImGui.SameLine();

        var elapsed = run.RunStopwatch.Elapsed;
        var elapsedStr = elapsed.TotalMinutes >= 1
          ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
          : $"{elapsed.Seconds}s";

        ImGui.Text(elapsedStr);
      }
      else if (run.RunStopwatch.IsRunning && run.TotalItems > 0)
      {
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        if (run.EtaCountdownMs > 0f)
        {
          var remainingMs = run.EtaCountdownMs - (float)run.EtaStopwatch.ElapsedMilliseconds;
          if (remainingMs < 0f) remainingMs = 0f;

          var eta = TimeSpan.FromMilliseconds(remainingMs);
          var etaStr = eta.TotalMinutes >= 1
            ? $"~{(int)eta.TotalMinutes}m {eta.Seconds:D2}s"
            : $"~{eta.Seconds}s";

          ImGui.TextDisabled($"ETA: {etaStr}");
        }
        else
        {
          ImGui.TextDisabled("Gathering data...");
        }
      }

      var gilBtnWidth = ImGui.CalcTextSize("Gil Dashboard").X + ImGui.GetStyle().FramePadding.X * 2;
      var copyBtnWidth = ImGui.CalcTextSize("Copy All").X + ImGui.GetStyle().FramePadding.X * 2;
      var spacing = ImGui.GetStyle().ItemSpacing.X;
      var padding = ImGui.GetStyle().WindowPadding.X;

      ImGui.SameLine(ImGui.GetWindowWidth() - gilBtnWidth - copyBtnWidth - spacing - padding);
      if (ImGui.Button("Gil Dashboard"))
        Plugin.GilDashboard.IsOpen = true;

      ImGui.SameLine();
      if (ImGui.Button("Copy All"))
      {
        var sb = new StringBuilder();
        foreach (var item in run.LogEntries)
        {
          switch (item)
          {
            case RunEntry runEntry:
              if (runEntry.EventType == RunEvent.Start || runEntry.EventType == RunEvent.End)
                sb.AppendLine($"--- {runEntry.Message} ---");
              else if (runEntry.EventType == RunEvent.Summary)
                sb.Append("  ").AppendLine(runEntry.Message);
              break;

            case RetainerHeader rh:
              sb.AppendLine($"[{rh.RetainerName}]");
              break;

            case LogEntry entry:
              if (entry.Outcome == ItemOutcome.Outlier)
                sb.Append("  ").AppendLine($"Outlier: {entry.ItemName} — skipping {entry.BaitPrice:N0} gil, using {entry.UsedPrice:N0} gil");
              else
              {
                var prefix = entry.Outcome switch
                {
                  ItemOutcome.Skipped => "Skipped",
                  ItemOutcome.NoData => "No data",
                  ItemOutcome.VendorSold => "Vendor-sold",
                  ItemOutcome.Banned => "Banned",
                  _ => "Entry"
                };
                sb.Append("  ").AppendLine($"{prefix}: {entry.ItemName} — {entry.Message}");
              }
              break;
          }
        }

        // Triage summary
        if (run.IsComplete && run.TriageItems.Count > 0)
          sb.AppendLine().AppendLine($"{run.TriageItems.Count} {(run.TriageItems.Count == 1 ? "item needs" : "items need")} triage");

        ImGui.SetClipboardText(sb.ToString());
      }
    }
  }
}
