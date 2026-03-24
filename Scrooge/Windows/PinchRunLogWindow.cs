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
    Skipped,  // red — rule blocked, no price set
    NoData,   // yellow — no competition, player decides
    Outlier   // normal — system handled it, got a price
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

    private readonly List<ILogItem> _entries = [];
    private string _currentRetainer = string.Empty;
    private string _lastRetainerHeader = string.Empty;
    private bool _autoScroll = true;
    private int _itemsAdjusted = 0;
    private int _outliersDetected = 0;
    private long _totalListingGil = 0;
    private readonly Stopwatch _runStopwatch = new Stopwatch();
    private readonly Stopwatch _etaStopwatch = new Stopwatch();
    private bool _runComplete = false;
    private bool _isHawkRun = false;
    private int _totalItems = 0;
    private int _itemsProcessed = 0;
    private float _etaCountdownMs = 0f;

    /// <summary>
    /// Clears the log and opens the window. Called at the start of each pinch run.
    /// </summary>
    public void StartNewRun(bool isHawkRun = false)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      _entries.Clear();
      _currentRetainer = string.Empty;
      _lastRetainerHeader = string.Empty;
      _itemsAdjusted = 0;
      _outliersDetected = 0;
      _totalListingGil = 0;
      _runComplete = false;
      _isHawkRun = isHawkRun;
      _totalItems = 0;
      _itemsProcessed = 0;
      _etaCountdownMs = 0f;
      _runStopwatch.Restart();
      _etaStopwatch.Restart();
      var label = isHawkRun ? "Hawk Run" : "Run";
      WindowName = isHawkRun ? "Scrooge - Hawk Run Log" : "Scrooge - Pinch Run Log";
      _entries.Add(new RunEntry(RunEvent.Start, $"{label} Started — {DateTime.Now:h:mm tt}"));
      IsOpen = true;
    }

    /// <summary>
    /// Updates the current retainer name. Subsequent log entries will be tagged with this name.
    /// </summary>
    public void SetCurrentRetainer(string retainerName)
    {
      _currentRetainer = retainerName;
    }

    /// <summary>
    /// Adds an entry to the log. Inserts a retainer header automatically
    /// on the first entry for each retainer. Guards on EnablePinchRunLog.
    /// </summary>
    public void AddEntry(ItemOutcome outcome, string itemName, string message)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      // Insert retainer header on first entry for this retainer
      if (_currentRetainer != _lastRetainerHeader && !string.IsNullOrEmpty(_currentRetainer))
      {
        _entries.Add(new RetainerHeader(_currentRetainer));
        _lastRetainerHeader = _currentRetainer;
      }

      _entries.Add(new LogEntry(outcome, _currentRetainer, itemName, message));
    }

    /// <summary>Adds an outlier entry with bait/used prices for colored rendering.</summary>
    public void AddOutlierEntry(string itemName, int baitPrice, int usedPrice)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      if (_currentRetainer != _lastRetainerHeader && !string.IsNullOrEmpty(_currentRetainer))
      {
        _entries.Add(new RetainerHeader(_currentRetainer));
        _lastRetainerHeader = _currentRetainer;
      }

      _entries.Add(new LogEntry(ItemOutcome.Outlier, _currentRetainer, itemName, "")
      {
        BaitPrice = baitPrice,
        UsedPrice = usedPrice
      });
    }

    /// <summary>
    /// Increments the successful adjustment counter. Called from Communicator.PrintPriceUpdate.
    /// </summary>
    public void IncrementAdjusted()
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      _itemsAdjusted++;
    }

    /// <summary>
    /// Increments the outlier detection counter. Called from Communicator.PrintOutlierDetected.
    /// </summary>
    public void IncrementOutliers()
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      _outliersDetected++;
    }

    /// <summary>
    /// Adds to the running total of gil currently listed on the market board.
    /// Called from AutoPinch.SetNewPrice for every item — adjusted or skipped.
    /// </summary>
    public void AddListingValue(int gil)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      _totalListingGil += gil;
    }

    /// <summary>
    /// Adds end marker and summary lines to the log. Called from AutoPinch when all tasks finish.
    /// </summary>
    public void EndRun()
    {
      var skipped = _entries.OfType<LogEntry>().Count(e => e.Outcome == ItemOutcome.Skipped);
      var noData = _entries.OfType<LogEntry>().Count(e => e.Outcome == ItemOutcome.NoData);
      var label = _isHawkRun ? "Hawk Run" : "Run";

      _entries.Add(new RunEntry(RunEvent.End, $"{label} Complete — {DateTime.Now:h:mm tt}"));
      _entries.Add(new RunEntry(RunEvent.Summary, _isHawkRun
        ? $"{_itemsAdjusted} listed"
        : $"{_itemsAdjusted} adjusted"));
      if (skipped > 0)
        _entries.Add(new RunEntry(RunEvent.Summary, $"{skipped} skipped"));
      if (noData > 0)
        _entries.Add(new RunEntry(RunEvent.Summary, $"{noData} no data"));

      if (_outliersDetected > 0)
        _entries.Add(new RunEntry(RunEvent.Summary, $"{_outliersDetected} outliers"));

      _entries.Add(new RunEntry(RunEvent.Summary, _isHawkRun
        ? $"{_totalListingGil:N0} gil put on market"
        : $"{_totalListingGil:N0} gil on market"));

      // Stop timer and mark run complete
      _runComplete = true;
      _runStopwatch.Stop();
      _etaStopwatch.Stop();

      // Update the rolling per-item average
      if (_itemsProcessed > 0)
      {
        var currentAvg = (float)_runStopwatch.ElapsedMilliseconds / _itemsProcessed;
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
      _totalItems = total;

      var storedAvg = Plugin.Configuration.AvgMsPerItem;
      if (storedAvg > 0f)
      {
        _etaCountdownMs = storedAvg * total;
        _etaStopwatch.Restart();
      }
    }

    /// <summary>
    /// Marks one item as processed (success or skip). Called from AutoPinch.SetNewPrice.
    /// </summary>
    public void IncrementProcessed()
    {
      _itemsProcessed++;

      var storedAvg = Plugin.Configuration.AvgMsPerItem;
      if (storedAvg > 0f)
      {
        var remaining = _totalItems - _itemsProcessed;
        _etaCountdownMs = storedAvg * remaining;
        _etaStopwatch.Restart();
      }
    }

    /// <summary>
    /// Cancels the current run and stops the timers
    /// </summary>
    public void CancelRun()
    {
      _runComplete = true;
      _runStopwatch.Stop(); 
      _etaStopwatch.Stop();
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

      // Scrollable child region for log entries
      // Reserve space at the bottom for the status bar
      var footerHeight = ImGui.GetFrameHeightWithSpacing() + 4;
      ImGui.BeginChild("##logEntries", new System.Numerics.Vector2(0, -footerHeight), false);

      bool treeOpen = false;

      foreach (var item in _entries)
      {
        switch (item)
        {
          case RunEntry run:
          {
            if (treeOpen) { ImGui.TreePop(); treeOpen = false; }

            if (run.EventType == RunEvent.Start || run.EventType == RunEvent.End)
            {
              ImGui.Spacing();
              ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1f));
              ImGui.TextWrapped($"--- {run.Message} ---");
              ImGui.PopStyleColor();
              ImGui.Spacing();
            }
            else if (run.EventType == RunEvent.Summary)
            {
              ImGui.Indent(16);
              ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.8f, 1f, 1f));
              ImGui.TextWrapped(run.Message);
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

      // Auto-scroll to bottom when new entries appear
      if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
        ImGui.SetScrollHereY(1.0f);

      ImGui.EndChild();

      // Bottom bar: Clear button + entry count + progress + timer + copy all
      ImGui.Separator();
      if (ImGui.Button("Clear"))
      {
        _entries.Clear();
        _currentRetainer = string.Empty;
        _lastRetainerHeader = string.Empty;
        _itemsAdjusted = 0;
        _outliersDetected = 0;
        _totalListingGil = 0;
        _runComplete = false;
        _runStopwatch.Reset();
        _etaStopwatch.Reset();
        _totalItems = 0;
        _itemsProcessed = 0;
        _etaCountdownMs = 0;
      }
      ImGui.SameLine();
      var logEntryCount = _entries.OfType<LogEntry>().Count();
      ImGui.Text($"{logEntryCount} {(logEntryCount == 1 ? "entry" : "entries")}");

      // Progress + Timer display
      if (_totalItems > 0)
      {
        ImGui.SameLine();
        ImGui.TextDisabled(" | ");
        ImGui.SameLine();
        ImGui.Text($"{_itemsProcessed}/{_totalItems}");
      }

      // ETA / final time display
      if (_runComplete)
      {
        // Run finished — show final elapsed time
        ImGui.SameLine();
        ImGui.TextDisabled(" | ");
        ImGui.SameLine();

        var elapsed = _runStopwatch.Elapsed;
        var elapsedStr = elapsed.TotalMinutes >= 1
          ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s"
          : $"{elapsed.Seconds}s";

        ImGui.Text(elapsedStr);
      }
      else if (_runStopwatch.IsRunning && _totalItems > 0)
      {
        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        if (_etaCountdownMs > 0f)
        {
          // Countdown: etaCountdownMs was set at last item completion (or initial SetTotalItems),
          // etaStopwatch measures time elapsed since then
          var remainingMs = _etaCountdownMs - (float)_etaStopwatch.ElapsedMilliseconds;
          if (remainingMs < 0f) remainingMs = 0f;

          var eta = TimeSpan.FromMilliseconds(remainingMs);
          var etaStr = eta.TotalMinutes >= 1
            ? $"~{(int)eta.TotalMinutes}m {eta.Seconds:D2}s"
            : $"~{eta.Seconds}s";

          ImGui.TextDisabled($"ETA: {etaStr}");
        }
        else
        {
          // First run ever — no stored average
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
        foreach (var item in _entries)
        {
          switch (item)
          {
            case RunEntry run:
              if (run.EventType == RunEvent.Start || run.EventType == RunEvent.End)
                sb.AppendLine($"--- {run.Message} ---");
              else if (run.EventType == RunEvent.Summary)
                sb.Append("  ").AppendLine(run.Message);
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
                  _ => "Entry"
                };
                sb.Append("  ").AppendLine($"{prefix}: {entry.ItemName} — {entry.Message}");
              }
              break;
          }
        }
        ImGui.SetClipboardText(sb.ToString());
      }
    }
  }
}
