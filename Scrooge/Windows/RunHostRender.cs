using System;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>
/// The ONE progress readout for every run-host executor - the standard rendering
/// that replaces the per-executor hand-rolled lines (Ledger design Section 5:
/// "if the system is automatically doing something in a run format, we should have
/// a standard UI and process for it"). This is the generalization of the GC stopgap
/// one-liner (0129f13) that read the Progress tuple inline; executors now drive a
/// <see cref="RunLifecycle"/> and this draws it the same way for all of them.
/// </summary>
internal static class RunHostRender
{
  /// <summary>The human word for a run's value unit ("gil" / "seals").</summary>
  internal static string UnitLabel(RunValueUnit unit) => unit switch
  {
    RunValueUnit.Gil => "gil",
    RunValueUnit.Seals => "seals",
    _ => "",
  };

  /// <summary>
  /// The live progress line: "{verb} {done}/{total} - {value} {unit}{eta}". ETA is
  /// omitted until the run has an honest estimate (self-calibrating; see
  /// <see cref="RunLifecycle.Eta"/>). Draws nothing if the run is not live.
  /// </summary>
  internal static void Progress(RunLifecycle run, string verb)
  {
    if (!run.IsRunning) return;

    var eta = run.Eta(DateTime.UtcNow);
    var etaText = eta is { } t
      ? t.TotalMinutes >= 1
        ? $" - ~{(int)t.TotalMinutes}m {t.Seconds}s left"
        : $" - ~{t.Seconds}s left"
      : "";

    var unit = UnitLabel(run.Unit);
    var valueText = unit.Length > 0 ? $" - {run.Value:N0} {unit}" : "";
    ImGui.TextColored(ScroogeColors.Earned, $"{verb} {run.Done}/{run.Total}{valueText}{etaText}");
  }

  /// <summary>
  /// THE BELL'S TWO-REGISTER LINE (F9, ruled 08-22): "{verb} {done}/{total} -
  /// {n} listed, ~{asked} asked · {vendored} vendored{eta}". The old line's one
  /// value slot read "0 gil" across a run that had listed a million - honest
  /// about the vendor register, silent about which register it was, dead weight
  /// on runs with no vendor rows. Each clause speaks only when nonzero, and the
  /// two currencies never sum (a would-earn ask is not earned gil - first law).
  /// </summary>
  internal static void ProgressWithRegisters(RunLifecycle run, string verb,
    int listedCount, long gilAsked)
  {
    if (!run.IsRunning) return;

    var eta = run.Eta(DateTime.UtcNow);
    var etaText = eta is { } t
      ? t.TotalMinutes >= 1
        ? $" - ~{(int)t.TotalMinutes}m {t.Seconds}s left"
        : $" - ~{t.Seconds}s left"
      : "";

    var clauses = "";
    if (listedCount > 0)
      clauses += $" - {listedCount} listed, ~{gilAsked:N0} asked";
    if (run.Value > 0)
      clauses += $"{(clauses.Length > 0 ? " · " : " - ")}{run.Value:N0} vendored";

    ImGui.TextColored(ScroogeColors.Earned, $"{verb} {run.Done}/{run.Total}{clauses}{etaText}");
  }

  /// <summary>
  /// The completion summary line for chat/log: "{done} {noun}, {value} {unit} in {duration}".
  /// Names the outcome when it was not a clean completion so a cancel or a stall reads
  /// distinctly from a finish.
  /// </summary>
  internal static string SummaryLine(in RunSummary s, string noun)
  {
    var unit = UnitLabel(s.Unit);
    var valueText = unit.Length > 0 ? $", {s.Value:N0} {unit}" : "";
    var dur = s.Duration.TotalMinutes >= 1
      ? $"{(int)s.Duration.TotalMinutes}m {s.Duration.Seconds:D2}s"
      : $"{s.Duration.Seconds}s";
    var outcome = s.Outcome switch
    {
      RunState.Cancelled => "cancelled - ",
      RunState.Stalled => "stalled - ",
      _ => "",
    };
    return $"{outcome}{s.Done} {noun}{valueText} in {dur}";
  }
}
