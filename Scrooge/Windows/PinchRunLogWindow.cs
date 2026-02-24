using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System.Linq;

namespace Scrooge.Windows
{

  /// <summary>Log entry severity for the pinch run log window.</summary>
  public enum LogSeverity
  {
    Debug,
    Info,
    Warning,
    Error
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
  public record LogEntry (LogSeverity Severity, string RetainerName, string ItemName, string Message) : ILogItem;

  /// <summary>A run-level entry (start/end markers, summary stats).</summary>
  public record RunEntry (RunEvent EventType, string Message) : ILogItem;

  internal class PinchRunLogWindow : Window
  {

    private readonly List<ILogItem> _entries = [];
    private string _currentRetainer = string.Empty;
    private bool _autoScroll = true;
    private int _itemsAdjusted = 0;
    private int _outliersDetected = 0;
    private long _totalListingGil = 0;

    /// <summary>
    /// Clears the log and opens the window. Called at the start of each pinch run.
    /// </summary>
    public void StartNewRun()
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      _entries.Clear();
      _currentRetainer = string.Empty;
      _itemsAdjusted = 0;
      _outliersDetected = 0;
      _totalListingGil = 0;
      _entries.Add(new RunEntry(RunEvent.Start, $"Run Started — {DateTime.Now:h:mm tt}"));
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
    /// Adds an entry to the log. Guards on EnablePinchRunLog internally.
    /// </summary>
    public void AddEntry(LogSeverity severity, string itemName, string message)
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      _entries.Add(new LogEntry(severity, _currentRetainer, itemName, message));
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
      var skipped = _entries.OfType<LogEntry>().Count(e => e.Severity == LogSeverity.Error);

      _entries.Add(new RunEntry(RunEvent.End, $"Run Complete — {DateTime.Now:h:mm tt}"));
      _entries.Add(new RunEntry(RunEvent.Summary, $"{_itemsAdjusted} adjusted"));
      _entries.Add(new RunEntry(RunEvent.Summary, $"{skipped} skipped"));

      if (_outliersDetected > 0)
        _entries.Add(new RunEntry(RunEvent.Summary, $"{_outliersDetected} outliers"));

      _entries.Add(new RunEntry(RunEvent.Summary, $"{_totalListingGil:N0} gil on market"));
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

      foreach (var item in _entries)
      {
        switch (item)
        {
          case RunEntry run:
            {
              // Visual separator for Start/End, indented summary lines
              if (run.EventType == RunEvent.Start || run.EventType == RunEvent.End)
              {
                ImGui.Spacing();
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.5f, 0.5f, 0.5f, 1f)); // gray
                ImGui.TextWrapped($"--- {run.Message} ---");
                ImGui.PopStyleColor();
                ImGui.Spacing();
              }
              else if (run.EventType == RunEvent.Summary)
              {
                ImGui.Indent(16);
                ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.6f, 0.8f, 1f, 1f)); // light blue
                ImGui.TextWrapped(run.Message);
                ImGui.PopStyleColor();
                ImGui.Unindent(16);
              }
              break;
            }

          case LogEntry entry:
            {
              // Color by severity
              var color = entry.Severity switch
              {
                LogSeverity.Error => new System.Numerics.Vector4(1f, 0.4f, 0.4f, 1f),   // red
                LogSeverity.Warning => new System.Numerics.Vector4(1f, 0.8f, 0.2f, 1f), // yellow
                LogSeverity.Info => new System.Numerics.Vector4(0.6f, 0.8f, 1f, 1f),    // light blue
                LogSeverity.Debug => new System.Numerics.Vector4(0.6f, 0.6f, 0.6f, 1f), // gray
                _ => new System.Numerics.Vector4(1f, 1f, 1f, 1f)                        // white fallback
              };

              var icon = entry.Severity switch
              {
                LogSeverity.Error => "Error: ",
                LogSeverity.Warning => "Warning: ",
                LogSeverity.Info => "Info: ",
                LogSeverity.Debug => "Debug: ",
                _ => "·"
              };

              ImGui.PushStyleColor(ImGuiCol.Text, color);
              var header = string.IsNullOrEmpty(entry.RetainerName)
                ? $"{icon} {entry.ItemName}"
                : $"{icon} [{entry.RetainerName}] {entry.ItemName}";
              ImGui.TextWrapped(header);
              ImGui.PopStyleColor();

              ImGui.Indent(16);
              ImGui.TextWrapped(entry.Message);
              ImGui.Unindent(16);

              ImGui.Spacing();
              break;
            }
        }
      }

      // Auto-scroll to bottom when new entries appear
      if (_autoScroll && ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 20)
        ImGui.SetScrollHereY(1.0f);

      ImGui.EndChild();

      // Bottom bar: Clear button + entry count
      ImGui.Separator();
      if (ImGui.Button("Clear"))
      {
        _entries.Clear();
        _itemsAdjusted = 0;
        _outliersDetected = 0;
        _totalListingGil = 0;

      }
      ImGui.SameLine();
      var logEntryCount = _entries.OfType<LogEntry>().Count();
      ImGui.Text($"{logEntryCount} {(logEntryCount == 1 ? "entry" : "entries")}");
      var copyButtonWidth = ImGui.CalcTextSize("Copy All").X + ImGui.GetStyle().FramePadding.X * 2;
      ImGui.SameLine(ImGui.GetWindowWidth() - copyButtonWidth - ImGui.GetStyle().WindowPadding.X);
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

            case LogEntry entry:
              var header = string.IsNullOrEmpty(entry.RetainerName)
                ? $"{entry.Severity}: {entry.ItemName}"
                : $"{entry.Severity}: [{entry.RetainerName}] {entry.ItemName}";
              sb.AppendLine(header);
              sb.Append("  ").AppendLine(entry.Message);
              break;
          }
        }
        ImGui.SetClipboardText(sb.ToString());
      }
    }
  }
}
