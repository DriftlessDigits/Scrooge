using System;

namespace Scrooge;

/// <summary>
/// Pure core for the Ledger's ripeness line (v3.x stretch: SENSORS, not gates).
/// Reports how stale the reads are - the age of the last full pinch scan and of
/// recon's last banked decision - so the player can judge whether the Ledger is
/// speaking from a fresh board or last night's. The since-scan market-events tally
/// this class used to compose died 08-22: it only ever counted what our own looks
/// wrote, so it reported Scrooge's own observation as movement in the world.
/// Deliberately no thresholds and no colors: cadence GATES are 4.0 work, tuned
/// from receipts, not guessed here (the fence lesson - a number nobody measured
/// reads like a rule).
/// </summary>
internal static class RipenessSensors
{
  /// <summary>
  /// Human-honest age text - see <see cref="Durations.Elapsed"/>, which owns the
  /// grammar. Kept under this name because the ripeness vocabulary is what the
  /// header, the bell's plan and the standing book all ask for.
  /// </summary>
  internal static string AgeText(long seconds) => Durations.Elapsed(seconds);
  /// <summary>
  /// THE HEADER'S TWO CLOCKS (ruled 08-21, pen 6). What the pinch last read, and what
  /// recon last banked - the two reads every verdict on the board is scored against.
  ///
  /// <para><b>The events count is gone from here.</b> It was a market_events tally
  /// counted since the last scan, and market_events are only written when one of our
  /// own runs LOOKS - so the header was reporting Scrooge's own observation back at
  /// the player as if the market had moved under him. A number that only grows when
  /// we look is not news about the world.</para>
  ///
  /// <para>Each clock says "never" plainly rather than dating itself to the epoch: a
  /// fresh install has no reads, and a zero-age board is a different claim.</para>
  ///
  /// <para><b>THE TWINS COLLAPSE WHEN THEY READ THE SAME</b> (ruled 08-22). A pinch
  /// and the recon that follows it inside one Round land minutes apart, so both clocks
  /// render the identical age string and the header spends two clauses saying one fact.
  /// The test is the RENDERED string, not a tolerance: no threshold is invented here,
  /// and the collapse can only ever hide a difference the reader could not have seen
  /// anyway.</para>
  /// </summary>
  /// <param name="lastScanAt">Unix seconds of the last full board read, 0 for never.</param>
  /// <param name="now">Unix seconds, now.</param>
  /// <param name="reconBankedAt">Unix seconds of recon's last banked decision, 0 for never.</param>
  /// <param name="reconItems">How many variants recon holds a banked decision for.</param>
  internal static string HeaderClocks(long lastScanAt, long now, long reconBankedAt, int reconItems)
  {
    var boardAge = lastScanAt > 0 ? AgeText(now - lastScanAt) : null;
    var reconAge = reconBankedAt > 0 && reconItems > 0 ? AgeText(now - reconBankedAt) : null;
    var items = $"{reconItems:N0} item{(reconItems == 1 ? "" : "s")}";

    if (boardAge is not null && reconAge is not null
        && string.Equals(boardAge, reconAge, StringComparison.Ordinal))
      return $"board and recon both read {boardAge} old, {items}";

    var board = boardAge is not null ? $"board read {boardAge} old" : "no board scan yet";
    var recon = reconAge is not null ? $"recon {reconAge} old on {items}" : "no recon read yet";
    return $"{board}; {recon}";
  }
}
