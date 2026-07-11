using System.Numerics;

namespace Scrooge.Windows;

/// <summary>
/// The Scrooge palette — one semantic color per meaning, shared by every
/// window. Push these instead of inline Vector4s so "earned green" and
/// "spent red" are the same green and red everywhere.
/// </summary>
internal static class ScroogeColors
{
  // Money movement
  internal static readonly Vector4 Earned = new(0.45f, 0.85f, 0.45f, 1f); // gil in
  internal static readonly Vector4 Spent  = new(0.95f, 0.35f, 0.35f, 1f); // gil out
  internal static readonly Vector4 Gold   = new(0.98f, 0.80f, 0.35f, 1f); // headline gil, coin glyph, milestones
  internal static readonly Vector4 HqGold = new(1.00f, 0.85f, 0.45f, 1f); // HQ glyph tint

  // State
  internal static readonly Vector4 Warning = new(0.95f, 0.85f, 0.30f, 1f); // needs attention
  internal static readonly Vector4 Amber   = new(1.00f, 0.70f, 0.20f, 1f); // aging data, soft caution
  internal static readonly Vector4 Stale   = new(0.60f, 0.60f, 0.60f, 1f); // stale/expired data
  internal static readonly Vector4 Muted   = new(0.55f, 0.55f, 0.55f, 1f); // secondary text
  internal static readonly Vector4 Info    = new(0.60f, 0.80f, 1.00f, 1f); // summary/info lines
  internal static readonly Vector4 Banned  = new(0.40f, 0.60f, 1.00f, 1f); // ban-list tag
  internal static readonly Vector4 Header  = new(0.90f, 0.80f, 0.55f, 1f); // section headers — counting-house parchment

  // Desynth skill-up tags (match the game's desynth color language)
  internal static readonly Vector4 TagRed    = new(0.95f, 0.35f, 0.35f, 1f);
  internal static readonly Vector4 TagYellow = new(0.95f, 0.85f, 0.30f, 1f);
  internal static readonly Vector4 TagGreen  = new(0.45f, 0.85f, 0.45f, 1f);
  internal static readonly Vector4 TagFlag   = new(0.85f, 0.65f, 0.30f, 1f); // protected/flagged items

  /// <summary>Earned for zero-or-positive deltas, Spent for negative.</summary>
  internal static Vector4 ForDelta(long delta) => delta >= 0 ? Earned : Spent;
}
