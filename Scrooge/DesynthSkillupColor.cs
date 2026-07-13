namespace Scrooge;

/// <summary>
/// Visual classification of a desynth target relative to the player's current
/// desynthesis skill on the item's class. Mirrors SimpleTweaks'
/// ExtendedDesynthesisWindow color logic.
/// </summary>
internal enum DesynthSkillupColor
{
  /// <summary>Player skill is below the item level. Will skillup, high fail rate.</summary>
  Red,
  /// <summary>Player skill is at or above item level, but within +50. Skillup possible.</summary>
  Yellow,
  /// <summary>Player skill is 50+ above item level. No skillup.</summary>
  Green,
}

/// <summary>
/// Pure logic for computing the SimpleTweaks-style skillup color for a desynth
/// target. The PlayerState skill read lives in GameSafe.GetDesynthLevel —
/// this file stays game-free so the test project can link it.
/// </summary>
internal static class DesynthSkillup
{
  /// <summary>
  /// Computes the skillup color for an item given its level and the player's
  /// desynthesis skill on that item's class.
  ///
  /// Cascade: first match wins.
  /// - playerLevel &lt; itemLevel              → Red
  /// - playerLevel &lt; itemLevel + 50         → Yellow (covers equality)
  /// - otherwise (player capped past item)   → Green
  /// </summary>
  internal static DesynthSkillupColor Classify(int playerLevel, int itemLevel)
  {
    if (playerLevel < itemLevel)
      return DesynthSkillupColor.Red;
    if (playerLevel < itemLevel + 50)
      return DesynthSkillupColor.Yellow;
    return DesynthSkillupColor.Green;
  }

  /// <summary>True if the color would still grant skillup (red or yellow).</summary>
  internal static bool IsSkillupEligible(DesynthSkillupColor color)
    => color == DesynthSkillupColor.Red || color == DesynthSkillupColor.Yellow;
}
