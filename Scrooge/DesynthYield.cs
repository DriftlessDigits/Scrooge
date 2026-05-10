using System;

namespace Scrooge;

/// <summary>
/// One row in desynth_yields. Immutable after insert.
/// </summary>
internal sealed class DesynthYield
{
  public long Id { get; init; }
  public long RunId { get; init; }
  public int AttemptSeq { get; init; }
  public uint SourceItemId { get; init; }
  public bool SourceIsHq { get; init; }
  public uint YieldItemId { get; init; }
  public int YieldQty { get; init; }
  public bool YieldIsHq { get; init; }
  public DateTimeOffset CapturedAt { get; init; }
}
