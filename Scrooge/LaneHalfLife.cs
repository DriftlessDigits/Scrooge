namespace Scrooge;

/// <summary>
/// Recency half-life resolver — the seam the lane design requires: the lane
/// math asks a resolver for the half-life, never reads a constant inline.
///
/// v0 returns the config seed (30d, from the 2026-07-13 sale-age query) for
/// every item. Later versions upgrade the resolver — derived-at-n from decision
/// receipts, per evidence class — and the lane math never changes.
/// Deliberately NOT linked into Scrooge.Tests (reads config); tests pass the
/// half-life through LaneConfig directly.
/// </summary>
internal static class LaneHalfLife
{
  public static double Resolve(uint itemId) => Plugin.Configuration.LaneHalfLifeDays;
}
