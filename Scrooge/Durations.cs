using System;

namespace Scrooge;

/// <summary>The whole unit a coarse age landed on. See <see cref="Durations.Coarse"/>.</summary>
internal enum AgeUnit
{
  /// <summary>Under a minute - too recent to put a number on.</summary>
  JustNow,
  Minutes,
  Hours,
  Days,
}

/// <summary>
/// EVERY RELATIVE DURATION THE PLUGIN SPEAKS, in one file (polish pass, 2026-08-16).
/// The same quantity - seconds, said to a human - was formatted in four places that
/// could not see each other: the fit check's round estimate, the ripeness sensor's
/// board age, the On Market tab's timestamps, and the run log's provenance clause.
/// Nothing made their boundaries move together, so a rung edited in one was a rung
/// silently disagreed with in three.
///
/// <para><b>There are FOUR renderings here and not one, and the count is measured
/// rather than chosen.</b> The originals genuinely disagree - the minute rung
/// ends at 90 minutes for the precise pair and at 60 for the coarse one; a span
/// rounds to the nearest minute where an age floors; the day rung arrives at 24h,
/// 36h, 48h or never depending on who is asking; and one of them counts in whole
/// days to begin with. Collapsing them would have rewritten strings a player reads,
/// so what unifies here is the FILE and the rung constants, not the grammar.
/// <c>DurationsTests</c> pins each rendering and pins the disagreements
/// themselves.</para>
///
/// <para>Pure and Dalamud-free (linked into the test project).</para>
/// </summary>
internal static class Durations
{
  private const long Minute = 60;
  private const long Hour = 60 * Minute;
  private const long Day = 24 * Hour;

  /// <summary>
  /// Where the precise pair stops counting minutes. Past an hour and a half, a
  /// minute count is more digits than the reader is using.
  /// </summary>
  private const long MinuteRungEnds = 90 * Minute;

  /// <summary>Where <see cref="Elapsed"/> stops counting hours - a day and a half.</summary>
  private const long ElapsedHourRungEnds = 36 * Hour;

  /// <summary>
  /// A duration said the way Drift says it: minutes under 90m, decimal hours past
  /// that, "&lt;1m" under a minute (never a bare "0m"). This is HOW LONG SOMETHING
  /// WILL TAKE - a round estimate, a retainer countdown, a rail row's ETA - and it
  /// has no day rung on purpose: a span that long is a bug, not a week.
  /// </summary>
  internal static string Span(long seconds)
  {
    var s = Math.Max(0, seconds);
    if (s < Minute) return "<1m";
    // NEAREST minute, not floor (ruled 08-15 shake: a 1.9-minute melt read "~1m",
    // which at small counts is a near-2x understatement wearing a tilde - the tilde
    // promises "about", and about-2 is not 1).
    if (s < MinuteRungEnds) return $"{(s + 30) / Minute}m";
    return $"{s / (double)Hour:0.#}h";
  }

  /// <summary>
  /// HOW LONG AGO, bare: minutes under 90m, decimal hours under 36h, decimal days
  /// past that. Never negative (clock skew reads as "0m"). The caller supplies the
  /// " ago" - most of them are building a longer clause around it ("last look 3.1d
  /// ago", "prices were checked 40m ago - still fresh").
  ///
  /// <para>Floors where <see cref="Span"/> rounds to nearest, and that is the
  /// difference between the two: overstating an age by half a minute claims evidence
  /// is staler than it is, while understating a span promises a round will finish
  /// before it can.</para>
  /// </summary>
  internal static string Elapsed(long seconds)
  {
    var s = Math.Max(0, seconds);
    if (s < MinuteRungEnds) return $"{s / Minute}m";
    // Floored to the tenth BEFORE formatting: "0.#" rounds to nearest, which let a
    // 1.96h-old board print "2h" beside a gate that had honestly passed it as
    // under 2h (Drift, 08-23: "guess we have a rounding disagreement"). The
    // paragraph above is the law; these two rungs now obey it like the minutes do.
    if (s < ElapsedHourRungEnds) return $"{Math.Floor(s / (double)Hour * 10) / 10:0.#}h";
    return $"{Math.Floor(s / (double)Day * 10) / 10:0.#}d";
  }

  /// <summary>
  /// "just now" / "12m ago" / "2h ago" / "3d ago" - the table grammar, whole units
  /// with the suffix baked in. Coarsens as it ages because that is how the number is
  /// used: nobody reprices on the difference between 61 and 74 days. A future stamp
  /// (clock skew, a restored DB) reads "just now" rather than a negative age - the
  /// one thing it certainly is not is old.
  /// </summary>
  internal static string Ago(long secondsAgo)
    => Coarse(secondsAgo, Day) switch
    {
      (AgeUnit.JustNow, _) => "just now",
      (AgeUnit.Minutes, var n) => $"{n}m ago",
      (AgeUnit.Hours, var n) => $"{n}h ago",
      var (_, n) => $"{n}d ago",
    };

  /// <summary>
  /// THE SAME GRAMMAR FOR EVIDENCE ALREADY COUNTED IN WHOLE DAYS - a sale book's
  /// age, a board read's age, an undercut count's age. "today" rather than "0d ago"
  /// because a day-resolution zero means the day is not over, not that no time has
  /// passed.
  ///
  /// <para>THE FOURTH RENDERING, FOLDED HOME (08-22). It lived on
  /// <see cref="OnMarket"/> - outside the file whose own doc claims every relative
  /// duration in the plugin - so the count of renderings this class measures was
  /// wrong by one and a rung edited here still could not see it. The move is a MOVE:
  /// the strings are byte-identical, <c>OnMarket.DayAge</c> stays as the name the
  /// voice layer calls through, exactly as <see cref="OnMarket.Relative"/> and
  /// <see cref="RipenessSensors.AgeText"/> already delegate here.</para>
  ///
  /// <para>It takes DAYS, not seconds, and that is why it is a fourth rendering and
  /// not a caller of <see cref="Ago"/>: its input is already whole days off a
  /// counted book, so there is nothing left to bucket and no seam where the two
  /// could drift.</para>
  /// </summary>
  internal static string DayAge(int days) => days <= 0 ? "today" : $"{days}d ago";

  /// <summary>
  /// THE COARSE BUCKETING, once: which whole unit an age belongs in and how many of
  /// them, with no wording attached. Two callers render these buckets in two
  /// deliberately different voices - the table's abbreviations
  /// (<see cref="Ago"/>) and the run log's spelled-out words
  /// (<see cref="RunLogVoice.Age"/>, whose anti-abbreviation ruling stands) - and
  /// splitting the arithmetic from the wording is what stops the boundaries drifting
  /// apart while the two voices stay apart on purpose.
  ///
  /// <para><paramref name="daysBeginAt"/> is where hours give way to days, and it is
  /// a parameter because the two callers really do differ: the table turns over at
  /// 24h, the run log carries "47 hours old" to 48h. Named here rather than spelled
  /// twice, so the divergence is a value one can read instead of a coincidence one
  /// has to notice.</para>
  ///
  /// <para>Never negative: a future stamp buckets as <see cref="AgeUnit.JustNow"/>.</para>
  /// </summary>
  internal static (AgeUnit Unit, long Value) Coarse(long seconds, long daysBeginAt)
  {
    var s = Math.Max(0, seconds);
    if (s < Minute) return (AgeUnit.JustNow, 0);
    if (s < Hour) return (AgeUnit.Minutes, s / Minute);
    if (s < daysBeginAt) return (AgeUnit.Hours, s / Hour);
    return (AgeUnit.Days, s / Day);
  }

  /// <summary>Where the run log's hour rung ends - a full two days. See <see cref="Coarse"/>.</summary>
  internal const long RunLogDaysBeginAt = 2 * Day;
}
