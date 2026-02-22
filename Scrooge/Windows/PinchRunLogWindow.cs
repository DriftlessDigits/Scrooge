using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

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

  /// <summary>A single entry in the pinch run log.</summary>
  public record LogEntry (LogSeverity Severity, string RetainerName, string ItemName, string Message);

  internal class PinchRunLogWindow : Window
  {

    private readonly List<LogEntry> _entries = [];
    private string _currentRetainer = string.Empty;
    private bool _autoScroll = true;

    /// <summary>
    /// Clears the log and opens the window. Called at the start of each pinch run.
    /// </summary>
    public void StartNewRun()
    {
      if (!Plugin.Configuration.EnablePinchRunLog)
        return;

      _entries.Clear();
      _currentRetainer = string.Empty;
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

    public PinchRunLogWindow() : base("Scrooge - Pinch Run Log")
    {
      SizeConstraints = new WindowSizeConstraints
      {
        MinimumSize = new( 300, 200 ),
        MaximumSize = new( 800, 600 )
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

      foreach (var entry in _entries)
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
        // Line 1: icon + [retainer] + item name
        var header = string.IsNullOrEmpty(entry.RetainerName)
          ? $"{icon} {entry.ItemName}"
          : $"{icon} [{entry.RetainerName}] {entry.ItemName}";
        ImGui.TextWrapped(header);
        ImGui.PopStyleColor();

        // Line 2: message (indented, default color)
        ImGui.Indent(16);
        ImGui.TextWrapped(entry.Message);
        ImGui.Unindent(16);

        ImGui.Spacing();
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
      }
      ImGui.SameLine();
      ImGui.Text($"{_entries.Count} {(_entries.Count == 1 ? "entry" : "entries")}");
      var copyButtonWidth = ImGui.CalcTextSize("Copy All").X + ImGui.GetStyle().FramePadding.X * 2;
      ImGui.SameLine(ImGui.GetWindowWidth() - copyButtonWidth - ImGui.GetStyle().WindowPadding.X);
      if (ImGui.Button("Copy All"))
      {
        var sb = new StringBuilder();
        foreach (var entry in _entries)
        {
          var header = string.IsNullOrEmpty(entry.RetainerName)
            ? $"{entry.Severity}: {entry.ItemName}"
            : $"{entry.Severity}: [{entry.RetainerName}] {entry.ItemName}";
          sb.AppendLine(header);
          sb.Append("  ").AppendLine(entry.Message);
        }
        ImGui.SetClipboardText(sb.ToString());
      }
    }
  }
}
