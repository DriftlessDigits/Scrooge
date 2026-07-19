using System;

namespace Scrooge;

/// <summary>
/// Pure core for the Ledger's ripeness line (v3.x stretch: SENSORS, not gates).
/// Reports how stale the board read is - age of the last full pinch scan and
/// how many market events other runs have observed since - so the player can
/// judge whether the Ledger is speaking from a fresh board or last night's.
/// Deliberately no thresholds and no colors: cadence GATES are 4.0 work, tuned
/// from receipts, not guessed here (the fence lesson - a number nobody measured
/// reads like a rule).
/// </summary>
internal static class RipenessSensors
{
  /// <summary>
  /// Human-honest age text: minutes under 90m, decimal hours under 36h, decimal
  /// days past that. Never negative (clock skew reads as "0m").
  /// </summary>
  internal static string AgeText(long seconds)
  {
    var s = Math.Max(0, seconds);
    if (s < 90 * 60) return $"{s / 60}m";
    if (s < 36 * 3600) return $"{s / 3600.0:0.#}h";
    return $"{s / 86400.0:0.#}d";
  }

  /// <summary>
  /// The header ripeness line. No scan on record is said plainly - "never
  /// scanned" is a real state on a fresh install, not a zero-age board.
  /// Events are only mentioned when there are any: "0 events since" is noise,
  /// but a count is the "the board moved under you" signal.
  /// </summary>
  internal static string HeaderLine(long lastScanAt, long now, int eventsSince)
  {
    if (lastScanAt <= 0) return "no board scan yet";
    var age = AgeText(now - lastScanAt);
    return eventsSince > 0
      ? $"board read {age} old, {eventsSince:N0} market event{(eventsSince == 1 ? "" : "s")} since"
      : $"board read {age} old";
  }
}
