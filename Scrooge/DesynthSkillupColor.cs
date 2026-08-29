namespace Scrooge;

/// <summary>
/// Visual classification of a desynth target relative to the player's current
/// desynthesis skill on the item's class. Mirrors SimpleTweaks'
/// ExtendedDesynthesisWindow color logic, cap branch included.
/// </summary>
internal enum DesynthSkillupColor
{
  /// <summary>Player skill is at or below the item level. Will skillup, high fail rate.</summary>
  Red,
  /// <summary>Player skill is above item level but within +50. Skillup possible.</summary>
  Yellow,
  /// <summary>No skillup: skill is capped at the ladder's top, or 50+ above the item.</summary>
  Green,
}

/// <summary>
/// Pure logic for computing the SimpleTweaks-style skillup color for a desynth
/// target. The PlayerState skill read lives in GameSafe.GetDesynthLevel and the
/// ladder's top in GameSafe.MaxDesynthLevel — this file stays game-free so the
/// test project can link it.
/// </summary>
internal static class DesynthSkillup
{
  /// <summary>
  /// Computes the skillup color for an item given the player's RAW desynthesis
  /// skill on the item's class (fractional — the game grants skill in
  /// hundredths), the item's level, and the ladder's top (the highest LevelItem
  /// among desynthable items; 0 when the sheet read failed, which disables the
  /// cap branch rather than painting everything Green).
  ///
  /// Cascade, first match wins — the reference's own order:
  /// - playerLevel at/past the ladder top       → Green (no skillup exists)
  /// - playerLevel &lt;= itemLevel              → Red (strictly greater leaves Red)
  /// - playerLevel &lt; itemLevel + 50          → Yellow
  /// - otherwise                                → Green
  /// </summary>
  internal static DesynthSkillupColor Classify(float playerLevel, int itemLevel, int maxLevel)
  {
    if (maxLevel > 0 && playerLevel >= maxLevel)
      return DesynthSkillupColor.Green;
    if (playerLevel <= itemLevel)
      return DesynthSkillupColor.Red;
    if (playerLevel < itemLevel + 50)
      return DesynthSkillupColor.Yellow;
    return DesynthSkillupColor.Green;
  }

  /// <summary>True if the color would still grant skillup (red or yellow).</summary>
  internal static bool IsSkillupEligible(DesynthSkillupColor color)
    => color == DesynthSkillupColor.Red || color == DesynthSkillupColor.Yellow;
}
