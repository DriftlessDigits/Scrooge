using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// One source item's realized melt record, as the almanac already knows it:
/// how many attempts, and what those attempts' yields were worth at the same
/// mat prices the per-item melt value is computed from.
/// </summary>
/// <param name="Ilvl">The SOURCE item's item level, read from the game sheet.</param>
internal readonly record struct MeltObservation(int Ilvl, int Attempts, long YieldValue);

/// <summary>
/// A melt value we did not measure for THIS item - the band it belongs to did.
/// It travels with its own provenance because a prior that renders like item
/// history is a lie the player can act on.
/// </summary>
internal readonly record struct MeltPrior(
  long ValuePerAttempt,
  int BandFloor,
  int BandCeiling,
  int Attempts,
  int SourceItems,
  bool Widened)
{
  /// <summary>How the band names itself in narration - "ilvl 650-699".</summary>
  internal string BandLabel => $"ilvl {BandFloor}-{BandCeiling}";
}

/// <summary>
/// THE MELT PRIOR (Drift, 07-25): <i>"we should have enough melt data to give a
/// generic value potential, yea? ... there is likely some correlation to item
/// level too."</i>
///
/// <para><b>The forfeit this kills.</b> Gear with no melt history scored melt as
/// null, so the turn-in exit won every comparison it entered - not because seals
/// were worth more, but because the other side of the scale was empty. The
/// 07-25 lap proved it: 25 verdicts carried the discounted seal rate and 17 were
/// still hand-overridden to melt. Drift melts that gear whether or not the plugin
/// has ever seen it melted. So above nothing at all, melt gets a real number.</para>
///
/// <para><b>The estimator.</b> Bands of <see cref="BandWidth"/> ilvl. The value
/// is the band's realized gil-per-attempt: total yield value over total attempts,
/// pooled across every source item in the band. Pooling by attempt (not averaging
/// the per-item averages) is deliberate - a band's answer should be dominated by
/// the items actually melted in volume, not by one lucky single-attempt outlier
/// carrying equal weight.</para>
///
/// <para><b>Never invented.</b> A band below <see cref="MinBandAttempts"/> is not
/// a band, it is an anecdote. It widens to its two neighbours once and re-tests;
/// if the widened window is still short, there is NO prior and melt stays null -
/// the turn-in wins by forfeit, honestly, because we genuinely do not know.</para>
///
/// <para>Quality-blind on purpose: a melt does not care whether the gear was HQ,
/// and splitting the bands by quality would halve every count for a distinction
/// the yields do not show.</para>
///
/// <para>Pure and Dalamud-free (linked into the test project). The shell supplies
/// the observations - the same <c>ReadSourceSummary</c> rollup the per-item melt
/// value is built from, with ilvl attached from the sheet - and this answers what
/// a band is worth and whether it may say so.</para>
/// </summary>
internal sealed class MeltPriorTable
{
  /// <summary>
  /// 50 ilvl. The realized data has a real gradient across the range (sub-250 gear
  /// returns a few hundred gil an attempt, 400+ returns four figures) but it is
  /// nowhere near monotonic, so the band has to be narrow enough to track the
  /// slope and wide enough to pool. 50 is roughly one gear tier cluster.
  /// </summary>
  internal const int BandWidth = 50;

  /// <summary>
  /// The floor is on ATTEMPTS, not on source items, because an attempt is the unit
  /// the value is denominated in - ten items melted once each is the same evidence
  /// as one item melted ten times. Five is not a confidence bar; it is the line
  /// under which a "band average" is one player's afternoon.
  /// </summary>
  internal const int MinBandAttempts = 5;

  private readonly Dictionary<int, (int Attempts, long Value, int Items)> _bands;

  private MeltPriorTable(Dictionary<int, (int, long, int)> bands) => _bands = bands;

  /// <summary>The band an ilvl falls in, named by its floor.</summary>
  internal static int BandOf(int ilvl) => ilvl <= 0 ? 0 : ilvl / BandWidth * BandWidth;

  /// <summary>
  /// Rolls the almanac up into bands. Observations with no ilvl (0 or less - mats,
  /// tokens, anything the sheet has no gear level for) are dropped rather than
  /// piled into the lowest band, where they would drag every low-level gear
  /// estimate toward a number that was never about gear.
  /// </summary>
  internal static MeltPriorTable Build(IEnumerable<MeltObservation> observations)
  {
    var bands = new Dictionary<int, (int Attempts, long Value, int Items)>();
    foreach (var o in observations)
    {
      if (o.Ilvl <= 0 || o.Attempts <= 0) continue;
      var band = BandOf(o.Ilvl);
      bands.TryGetValue(band, out var acc);
      bands[band] = (acc.Attempts + o.Attempts, acc.Value + o.YieldValue, acc.Items + 1);
    }
    return new MeltPriorTable(bands);
  }

  /// <summary>How many bands cleared the floor on their own - the log line's operand.</summary>
  internal int SolidBandCount
  {
    get
    {
      var n = 0;
      foreach (var b in _bands.Values) if (b.Attempts >= MinBandAttempts) n++;
      return n;
    }
  }

  /// <summary>
  /// What an item of this ilvl can be expected to melt for, or null when the
  /// evidence does not reach. A zero-value band returns null too: yields we could
  /// not price are not yields worth nothing, and "expected 0 gil" would lose every
  /// comparison exactly as loudly as the forfeit this exists to end.
  /// </summary>
  internal MeltPrior? For(int ilvl)
  {
    if (ilvl <= 0) return null;
    var band = BandOf(ilvl);

    if (_bands.TryGetValue(band, out var own) && own.Attempts >= MinBandAttempts)
      return Made(band, band + BandWidth - 1, own, widened: false);

    // Widen ONCE to the neighbours. A band that still cannot clear the floor with
    // 150 ilvl of company has no business naming a number.
    var attempts = 0; long value = 0; var items = 0;
    for (var b = band - BandWidth; b <= band + BandWidth; b += BandWidth)
      if (b >= 0 && _bands.TryGetValue(b, out var acc))
        { attempts += acc.Attempts; value += acc.Value; items += acc.Items; }

    if (attempts < MinBandAttempts) return null;
    var floor = band - BandWidth < 0 ? 0 : band - BandWidth;
    return Made(floor, band + BandWidth * 2 - 1, (attempts, value, items), widened: true);
  }

  private static MeltPrior? Made(int floor, int ceiling, (int Attempts, long Value, int Items) acc, bool widened)
  {
    var per = acc.Value / acc.Attempts;
    return per <= 0 ? null : new MeltPrior(per, floor, ceiling, acc.Attempts, acc.Items, widened);
  }
}
