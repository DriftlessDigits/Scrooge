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
    Skipped,      // red — rule blocked, no price set
    NoData,       // yellow — no competition, player decides
    VendorSold,   // green — vendor-sold through retainer
    TurnedIn,     // green — GC Expert Delivery turn-in, paid in seals not gil
    Banned,       // blue — on ban list, observed but not changed
    Desynthed,    // grey — item destroyed via desynthesis, no price math (Task 13 wires render branch)
    SlowMover,    // info blue — slow-mover pressure deepened the cut (price WAS applied)
    // Lane pricing outcomes — each named with its evidence, never a generic costume
    WallIgnored,  // info — anchored in-lane past above-ceiling walls
    BaitIgnored,  // info — anchored in-lane past below-floor claims
    LaneOwned,    // green — no in-lane competition, listed at the lane edge
    RaceDeclined, // yellow — all listings below lane, waiting at the lane floor
    LaneHeld,     // yellow — history too thin, held for the player's call
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
  public record LogEntry(ItemOutcome Outcome, string RetainerName, string ItemName, string Message) : ILogItem;

  /// <summary>A run-level entry (start/end markers, summary stats).</summary>
  public record RunEntry (RunEvent EventType, string Message) : ILogItem;

  /// <summary>Retainer section header. Inserted lazily on first entry for each retainer.</summary>
  public record RetainerHeader(string RetainerName) : ILogItem;

  /// <summary>
  /// A desynth yield sub-row — one material obtained, rendered indented under
  /// the desynthed item's entry. Value is the cached last-sale price (0 = unknown).
  /// </summary>
  public record YieldEntry(string YieldName, int Qty, bool IsHq, long Value) : ILogItem;

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
    public void StartNewRun(bool isHawkRun = false, bool isDesynthRun = false, bool isGcRun = false)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      var run = Plugin.CurrentRun;
      if (run == null) return;

      _lastRun = run;
      run.RunStopwatch.Restart();

      string label;
      if (isGcRun)
      {
        label = "GC Turn-In Run";
        WindowName = "Scrooge - GC Turn-In Log";
      }
      else if (isDesynthRun)
      {
        label = "Desynth Run";
        WindowName = "Scrooge - Desynth Run Log";
      }
      else if (isHawkRun)
      {
        label = "Hawk Run";
        WindowName = "Scrooge - Hawk Run Log";
      }
      else
      {
        label = "Run";
        WindowName = "Scrooge - Pinch Run Log";
      }

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

    /// <summary>
    /// Live yield sub-row during a desynth run. Subscribed to
    /// DesynthYieldStore.YieldCaptured in the Plugin constructor.
    /// </summary>
    public void OnYieldCaptured(DesynthYield yield)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      var run = Run;
      if (run == null || run.Mode != RunMode.Desynth || run.IsComplete)
        return;

      var value = (long)(GilStorage.GetLastSalePrice(yield.YieldItemId, yield.YieldIsHq) ?? 0) * yield.YieldQty;
      run.AddYieldEntry(GilTracker.GetItemName(yield.YieldItemId), yield.YieldQty, yield.YieldIsHq, value);
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
    /// Increments the slow-mover deepen counter. Called from SlowMoverPressure.Apply.
    /// </summary>
    public void IncrementSlowMovers()
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      var run = Run;
      if (run != null) run.SlowMoversDeepened++;
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
      var isDesynthRun = run.Mode == RunMode.Desynth;
      var isGcRun = run.Mode == RunMode.Gc;
      var label = isGcRun ? "GC Turn-In Run"
                : isDesynthRun ? "Desynth Run"
                : isHawkRun ? "Hawk Run"
                : "Run";

      run.AddRunEntry(RunEvent.End, $"{label} Complete — {DateTime.Now:h:mm tt}");
      run.AddRunEntry(RunEvent.Summary, isGcRun
        ? $"{run.ItemsProcessed} turned in"
        : isDesynthRun
          ? $"{run.ItemsProcessed} desynthed"
          : isHawkRun
            ? $"{run.ItemsAdjusted} listed"
            : $"{run.ItemsAdjusted} adjusted");

      // Desynth headline = what the materials are worth, not vendor forfeit —
      // vendor prices for gear are jokingly cheap, so "net vs vendor" always wins.
      if (isDesynthRun && run.MaterialsValueGil > 0)
        run.AddRunEntry(RunEvent.Summary, $"~{run.MaterialsValueGil:N0} gil in materials (cached prices)");

      // Desynth and GC runs don't carry vendor / listing / lane semantics — skip
      // those summary lines for them (GC's seals summary is added by the
      // orchestrator, which owns the seal-value RunLifecycle).
      if (!isDesynthRun && !isGcRun)
      {
        if (run.SkippedCount > 0)
          run.AddRunEntry(RunEvent.Summary, $"{run.SkippedCount} skipped");
        if (run.NoDataCount > 0)
          run.AddRunEntry(RunEvent.Summary, $"{run.NoDataCount} no data");

        // Lane outcomes — each type counted separately, per the lane design
        AddLaneSummary(run, ItemOutcome.WallIgnored, "walls ignored");
        AddLaneSummary(run, ItemOutcome.BaitIgnored, "bait ignored");
        AddLaneSummary(run, ItemOutcome.LaneOwned, "lanes owned");
        AddLaneSummary(run, ItemOutcome.RaceDeclined, "races declined");
        AddLaneSummary(run, ItemOutcome.LaneHeld, "held (thin history)");

        if (run.SlowMoversDeepened > 0)
          run.AddRunEntry(RunEvent.Summary, $"{run.SlowMoversDeepened} slow movers deepened");

        if (run.VendorSoldCount > 0)
          run.AddRunEntry(RunEvent.Summary, $"{run.VendorSoldCount} vendor-sold for {run.VendorSoldGil:N0} gil");

        run.AddRunEntry(RunEvent.Summary, isHawkRun
          ? $"{run.TotalListingGil:N0} gil put on market"
          : $"{run.TotalListingGil:N0} gil on market");
      }

      // Hand off triage data to the triage window
      if (run.TriageItems.Count > 0)
        Plugin.Ledger.SetRun(run);

      // Stop timer and mark run complete (lifecycle terminal - fail closed)
      run.MarkComplete();
      run.RunStopwatch.Stop();

      // Update the rolling per-item average. GC turn-in cadence differs from a
      // pinch/hawk pass and must not pollute the pinch ETA seed (existing desynth
      // behavior is left unchanged).
      if (run.ItemsProcessed > 0 && !isGcRun)
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
    /// True when the entry's Message is a pre-composed market-language line
    /// ("Item: transition [tag] - reason") and should render/copy verbatim.
    /// Legacy outcomes still carry a bare message rendered as "Item — message".
    /// </summary>
    private static bool IsGrammarLine(ItemOutcome outcome) => outcome switch
    {
      ItemOutcome.WallIgnored or ItemOutcome.BaitIgnored or ItemOutcome.LaneOwned
        or ItemOutcome.RaceDeclined or ItemOutcome.LaneHeld
        or ItemOutcome.SlowMover or ItemOutcome.Skipped => true,
      _ => false,
    };

    /// <summary>Adds a run-summary line for one lane outcome type, when any occurred.</summary>
    private static void AddLaneSummary(RunData run, ItemOutcome outcome, string label)
    {
      var n = run.CountOutcome(outcome);
      if (n > 0)
        run.AddRunEntry(RunEvent.Summary, $"{n} {label}");
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

      // Starts the lifecycle when idle (seeded with the persisted AvgMsPerItem);
      // revises Total in place on a live run - the lifecycle ETA self-calibrates
      // off observed pace once the first item lands.
      run.TotalItems = total;
    }

    /// <summary>
    /// Marks one item as processed (success or skip). Called from AutoPinch.SetNewPrice.
    /// </summary>
    public void IncrementProcessed()
    {
      var run = Run;
      if (run == null) return;

      run.Beat();
    }

    /// <summary>
    /// Cancels the current run and stops the timers
    /// </summary>
    public void CancelRun()
    {
      var run = Run;
      if (run == null) return;

      run.MarkCancelled();
      run.RunStopwatch.Stop();
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
              ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
              ImGui.TextWrapped($"--- {runEntry.Message} ---");
              ImGui.PopStyleColor();
              ImGui.Spacing();
            }
            else if (runEntry.EventType == RunEvent.Summary)
            {
              ImGui.Indent(16);
              ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Info);
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

            var color = entry.Outcome switch
            {
              ItemOutcome.Skipped => ScroogeColors.Spent,
              ItemOutcome.NoData => ScroogeColors.Amber,
              ItemOutcome.VendorSold => ScroogeColors.Earned,
              ItemOutcome.TurnedIn => ScroogeColors.Earned,
              ItemOutcome.Banned => ScroogeColors.Banned,
              ItemOutcome.Desynthed => ScroogeColors.Muted,
              ItemOutcome.SlowMover => ScroogeColors.Info,
              ItemOutcome.WallIgnored => ScroogeColors.Info,
              ItemOutcome.BaitIgnored => ScroogeColors.Info,
              ItemOutcome.LaneOwned => ScroogeColors.Earned,
              ItemOutcome.RaceDeclined => ScroogeColors.Amber,
              ItemOutcome.LaneHeld => ScroogeColors.Amber,
              _ => new System.Numerics.Vector4(1f, 1f, 1f, 1f)
            };

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            // Grammar lines are pre-composed ("Item: transition [tag] - reason");
            // legacy outcomes still render as "Item — message".
            ImGui.TextWrapped(IsGrammarLine(entry.Outcome)
              ? entry.Message
              : $"{entry.ItemName} — {entry.Message}");
            ImGui.PopStyleColor();

            break;
          }

          case YieldEntry yield:
          {
            if (!treeOpen) break;

            var label = Format.Hq(yield.YieldName, yield.IsHq);
            var qty = yield.Qty > 1 ? $"{yield.Qty}x " : "";
            var value = yield.Value > 0 ? $"  (~{yield.Value:N0}g)" : "";
            ImGui.Indent(16);
            ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
            ImGui.TextWrapped($"→ {qty}{label}{value}");
            ImGui.PopStyleColor();
            ImGui.Unindent(16);
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
        ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
        ImGui.Text($"{run.TriageItems.Count} {(run.TriageItems.Count == 1 ? "item needs" : "items need")} triage");
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGui.SmallButton("Open Ledger"))
          Plugin.Ledger.IsOpen = true;
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

        if (run.Lifecycle.Eta(DateTime.UtcNow) is TimeSpan eta)
        {
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
            {
              if (IsGrammarLine(entry.Outcome))
              {
                // Already self-describing: "Item: transition [tag] - reason".
                sb.Append("  ").AppendLine(entry.Message);
                break;
              }
              var prefix = entry.Outcome switch
              {
                ItemOutcome.NoData => "No data",
                ItemOutcome.VendorSold => "Vendor-sold",
                ItemOutcome.TurnedIn => "Turned in",
                ItemOutcome.Banned => "Banned",
                ItemOutcome.Desynthed => "Desynthed",
                _ => "Entry"
              };
              sb.Append("  ").AppendLine($"{prefix}: {entry.ItemName} — {entry.Message}");
              break;
            }
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
