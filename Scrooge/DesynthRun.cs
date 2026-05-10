using System;

namespace Scrooge;

/// <summary>
/// One row in desynth_runs. Lifecycle: created at StartRun, updated at
/// EndRun (or Abort with aborted_reason set).
/// </summary>
internal sealed class DesynthRun
{
  public long Id { get; init; }
  public DateTimeOffset StartedAt { get; init; }
  public DateTimeOffset? EndedAt { get; set; }
  public string Mode { get; init; } = ""; // "Skillup" or "Burn"
  public int TotalItems { get; init; }
  public string? AbortedReason { get; set; }

  public bool IsComplete => EndedAt.HasValue;
  public bool WasAborted => !string.IsNullOrEmpty(AbortedReason);
}
