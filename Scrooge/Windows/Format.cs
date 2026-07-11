using Dalamud.Game.Text;

namespace Scrooge.Windows;

/// <summary>
/// Shared money/text formatting so every window speaks the same grammar:
/// separators always, explicit sign on deltas, one HQ glyph, one coin glyph.
/// </summary>
internal static class Format
{
  /// <summary>The game's gil coin glyph.</summary>
  internal const char GilChar = (char)SeIconChar.Gil;

  /// <summary>The game's HQ glyph.</summary>
  internal const char HqChar = (char)SeIconChar.HighQuality;

  /// <summary>"1,234,567" — plain gil amount.</summary>
  internal static string Gil(long amount) => $"{amount:N0}";

  /// <summary>"1,234,567 [coin]" — amount with the gil glyph, for headline money.</summary>
  internal static string GilIcon(long amount) => $"{amount:N0}{GilChar}";

  /// <summary>"+1,234" / "-1,234" / "+0" — explicit sign, separators kept.</summary>
  internal static string SignedGil(long delta) => delta >= 0 ? $"+{delta:N0}" : $"{delta:N0}";

  /// <summary>Appends the HQ glyph when the item is HQ.</summary>
  internal static string Hq(string name, bool isHq) => isHq ? $"{name} {HqChar}" : name;
}
