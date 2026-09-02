using System.Globalization;
using System.Text;

namespace Scrooge;

/// <summary>
/// THE STANCE THE HAND WAS HOLDING when a price was written, as one banked
/// string (V45, decision_receipts.undercut_posture).
///
/// <para>The 4.0 scoreboard's undercut report card is the consumer, and its
/// question is a revealed-value question: "in reality, you base decisions on
/// value x". It cannot be asked of a corpus that records only outcomes. Two
/// receipts with the same decided_price and the same queue_position are the same
/// row today whether one ran with UndercutSelf on and a 50% climb cap armed and
/// the other ran with neither - so the pegs those decisions were actually made
/// under have to ride ON the decision.</para>
///
/// <para><b>Capture only.</b> Nothing in the plugin reads this back; it is
/// written and left alone, exactly like the other write-only corpus columns.
/// This class exists so the corpus is whole the day 4.0 opens rather than
/// starting from the day someone remembers.</para>
///
/// <para><b>The grammar is the contract.</b> Semicolon-separated
/// <c>key=value</c> pairs, fixed key order, invariant-culture numbers,
/// booleans as 0/1: a parser written a year from now reads a shape, not a
/// sentence. Keys are frozen for the same reason
/// <see cref="DeferPlan.DeadHeatKey"/> is - a renamed key silently splits a
/// tally. New knobs APPEND new keys; nothing is ever renamed or reordered, and a
/// reader that meets a key it does not know ignores it rather than guessing.</para>
///
/// <para>Dalamud-free by construction (values in, string out - it never reads
/// Configuration itself), so the grammar is pinned in the test project. That is
/// the whole reason it is not a private helper inside the pipeline.</para>
/// </summary>
internal static class UndercutPosture
{
  /// <summary>The frozen key order. Never rename, never reorder; append only.</summary>
  internal const string ModeKey = "mode";
  internal const string AmountKey = "amt";
  internal const string SelfKey = "self";
  /// <summary>
  /// RETIRED KEY (3.1 sweep) - the crasher-guard's threshold, banked while the
  /// knob existed. Rows written before the sweep carry it (append-only corpus
  /// contract: their meaning shifted skip-&gt;warn on 2026-08-21 and the 4.0
  /// report card has to know which era a row is from); new rows omit the pair -
  /// a stance can't move on a knob that no longer exists.
  /// </summary>
  internal const string MaxCutKey = "maxcut";
  internal const string IncreaseCapKey = "inccap";
  internal const string IncreasePctKey = "incpct";
  internal const string CeilingKey = "ceil";

  /// <summary>
  /// The posture as it will be banked. Every peg the undercut report card was
  /// ruled over is here, plus the write style and step that spend them.
  ///
  /// <para><paramref name="increaseCapEnabled"/> and
  /// <paramref name="maxIncreasePct"/> are BOTH written, because they are two
  /// different facts: a disarmed cap set to 50 and an armed one set to 50 lead
  /// to opposite prices, and folding the flag into the number ("off" as a
  /// value) would throw away the knob the player actually has set.</para>
  ///
  /// <para>Percentages ride as configured - percent, not ratios - so a banked
  /// row and the config window agree on what the number means. Trailing zeroes
  /// are trimmed so 100.0f and 100f bank identically; a posture that changes
  /// spelling without changing stance would read as a stance that moved.</para>
  /// </summary>
  internal static string Compose(UndercutMode mode, int amount, bool undercutSelf,
    bool increaseCapEnabled, float maxIncreasePct, float ceilingMult)
  {
    var sb = new StringBuilder();
    Pair(sb, ModeKey, LegacyUndercutMode.Fold(mode).ToString());
    Pair(sb, AmountKey, amount.ToString(CultureInfo.InvariantCulture));
    Pair(sb, SelfKey, undercutSelf ? "1" : "0");
    Pair(sb, IncreaseCapKey, increaseCapEnabled ? "1" : "0");
    Pair(sb, IncreasePctKey, Number(maxIncreasePct));
    Pair(sb, CeilingKey, Number(ceilingMult));
    return sb.ToString();
  }

  private static void Pair(StringBuilder sb, string key, string value)
  {
    if (sb.Length > 0) sb.Append(';');
    sb.Append(key).Append('=').Append(value);
  }

  /// <summary>
  /// A knob's value, written the one way. Two decimals is the resolution the
  /// config window offers (it rounds to one); invariant culture because a
  /// comma-decimal machine must not bank a row a period-decimal parser reads as
  /// two fields.
  /// </summary>
  private static string Number(float value)
    => value.ToString("0.##", CultureInfo.InvariantCulture);
}
