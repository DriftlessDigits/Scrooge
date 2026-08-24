using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE PLAN STOPS PRETENDING (2026-07-26). A round sent 52 items to Expert Delivery -
/// roughly 2,000 seals apiece, ~104,000 seals of reward - against a 90,000-seal wallet.
/// The run did the right thing: it halted at 89,105/90,000 with 7 items still on the
/// checklist, and <see cref="WalletHalt"/> got it moving again once Drift spent seals down.
/// Nothing about the STOP was dishonest.
///
/// <para>The plan was. "Churn - at your GC's Expert Delivery: 52" and "-&gt; 52 turn in"
/// both promised an errand the wallet could not hold, and the overflow was knowable
/// before the first item moved: the seal count, the cap and every row's reward are all
/// readable at compose time. This is the arithmetic nobody was doing.</para>
///
/// <para><b>It is a sensor, not a gate.</b> The run still starts, still halts, still
/// self-resumes. Refusing here would trade an honest stop for a new blocker, and the
/// stop already works. All this does is make the header and the plan line say the number
/// they already knew.</para>
///
/// <para><b>Why the pessimistic order.</b> "How many fit" depends on the order the
/// counter works, and only the counter knows that - the executor walks the DISPLAYED
/// list, which the game sorts and filters. So the estimate is taken in the order the
/// pile is HANDED OFF (richest first, <c>ExecuteChurn</c>'s own sort), which is also the
/// order that fills the wallet fastest and therefore fits the fewest. An estimate that
/// errs low is the right way to err for a warning: the surprise is a couple more items
/// getting through, never a couple fewer. The wording wears a "~" for the same reason.</para>
///
/// <para>Pure and Dalamud-free: the shell reads the wallet and resolves the rewards,
/// this answers "how much of this actually fits, and what do we say about it".</para>
/// </summary>
internal readonly record struct SealFit
{
  /// <summary>Rows the turn-in would be handed (each with a resolvable seal reward).</summary>
  internal int Ready { get; init; }

  /// <summary>What those rows would pay, all in.</summary>
  internal long ExpectedSeals { get; init; }

  /// <summary>Seals the wallet can still take before the cap eats the overflow.</summary>
  internal long Headroom { get; init; }

  /// <summary>
  /// How many of <see cref="Ready"/> the wallet holds before the run halts - the
  /// longest richest-first prefix whose rewards fit inside <see cref="Headroom"/>.
  /// </summary>
  internal int Fits { get; init; }

  /// <summary>The pile promises more than the wallet holds.</summary>
  internal bool Overflows => Fits < Ready;

  /// <summary>
  /// The honest line for the pile header, or empty when the whole pile fits. A plan
  /// that fits says nothing - the count was already true.
  /// </summary>
  // Player language (strings pass, 08-02): "seal wallet is nearly full" is the
  // fact; "wallet fits / spend down" was shop jargon only we understood.
  internal string Note => !Overflows ? string.Empty
    : $"{Ready} items ready (~{Seals(ExpectedSeals)} seals), but your seal wallet only has room for "
      + $"~{Seals(Headroom)} - about {Fits} will fit. The run stops there unless you spend some seals first.";

  /// <summary>The same fact at header width - it has to survive a collapsed pile.</summary>
  internal string Tag => !Overflows ? string.Empty : $"  [~{Fits} fit the seal wallet]";

  /// <summary>
  /// THE RIDERS PAGE'S TOP LINE (3b-6, pen 5). Decision numbers only: what the turn-ins
  /// riding this round pay, and what the wallet can still take. It speaks whether or not
  /// the pile overflows - <see cref="Note"/> is a warning and stays silent on a clean
  /// plan, but this seat is the reader's one look at the wallet before he presses
  /// Continue, and "no warning" is not the same information as the two numbers.
  ///
  /// <para>No mechanics: the cap, the curve and the richest-first estimate order are all
  /// this type's business, and none of them appears in the sentence. What appears is the
  /// pair a player would use to decide whether to go spend some seals first.</para>
  /// </summary>
  internal string Line => Ready <= 0 ? string.Empty
    : Overflows
      ? $"{Ready} turn-ins pay ~{Seals(ExpectedSeals)} seals; the wallet has room for"
        + $" ~{Seals(Headroom)} - about {Fits} fit."
      : $"{Ready} turn-ins pay ~{Seals(ExpectedSeals)} seals; the wallet has room for ~{Seals(Headroom)}.";

  /// <summary>The round plan line's honest count suffix: "52 turn in (~23 fit)".</summary>
  internal string PlanSuffix => !Overflows ? string.Empty : $" (~{Fits} fit)";

  /// <summary>
  /// The fit at compose time. <paramref name="held"/> / <paramref name="cap"/> are the
  /// seal wallet (null when unreadable - the same read the halt uses, so the plan and
  /// the stop can never disagree about the cap). <paramref name="rewards"/> is each
  /// waiting row's seal reward.
  ///
  /// <para>Returns null when there is nothing to say: no wallet reading, or no rows
  /// carrying a reward. An unreadable wallet must not produce a warning - a plan that
  /// invents a cap is worse than a plan that stays quiet.</para>
  /// </summary>
  internal static SealFit? Assess(uint? held, uint? cap, IReadOnlyList<int> rewards)
  {
    if (held is not uint current || cap is not uint max) return null;

    var sorted = new List<int>();
    foreach (var r in rewards)
      if (r > 0) sorted.Add(r);
    if (sorted.Count == 0) return null;
    sorted.Sort((a, b) => b.CompareTo(a)); // richest first - see the class note

    var headroom = current >= max ? 0L : (long)max - current;
    long total = 0;
    long running = 0;
    var fits = 0;
    var stillFitting = true;
    foreach (var r in sorted)
    {
      total += r;
      if (!stillFitting) continue;
      if (running + r <= headroom) { running += r; fits++; }
      else stillFitting = false; // the run HALTS on the first row that doesn't fit
    }

    return new SealFit
    {
      Ready = sorted.Count,
      ExpectedSeals = total,
      Headroom = headroom,
      Fits = fits,
    };
  }

  /// <summary>
  /// A seal count said the way a player reads one: thousands past ten thousand, the
  /// bare number below it. 104,000 -&gt; "104k"; 46,500 -&gt; "46.5k"; 900 -&gt; "900".
  /// </summary>
  internal static string Seals(long seals)
  {
    var s = seals < 0 ? 0 : seals;
    return s < 10_000 ? $"{s:N0}" : $"{s / 1000.0:0.#}k";
  }
}
