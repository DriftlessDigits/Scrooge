using System.Globalization;

namespace Scrooge;

/// <summary>
/// How gil is written, for the layers that have no window. The rule is the same
/// one <see cref="Scrooge.Windows.Format"/> speaks - separators always - and it
/// lives here because the pricing spine composes its own prose and cannot reach
/// into a Dalamud-linked namespace to borrow it.
///
/// <para>Invariant culture, explicitly. A voice test pins these strings, and a
/// number that changes its separator with the player's locale would make the
/// pinned sentence a claim about the machine that ran it.</para>
/// </summary>
internal static class MoneyText
{
  /// <summary>"1,234,567" - plain gil amount.</summary>
  internal static string Gil(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture);

  /// <summary>"+1,234" / "-1,234" / "+0" - explicit sign, separators kept.</summary>
  internal static string SignedGil(long delta) => delta >= 0 ? $"+{Gil(delta)}" : Gil(delta);
}
