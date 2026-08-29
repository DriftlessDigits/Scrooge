using System;
using System.Linq;
using System.Text;

namespace Scrooge;

/// <summary>
/// /scrooge sitrep - the one-paste diagnostic dump (v3.x stretch 3). Everything
/// a debugging session opens with (version/build, config posture, board
/// freshness, venture state, run states, DB counts) assembled into a single
/// clipboard-ready block, so "paste me a sitrep" replaces twenty questions.
/// Every section guards independently: a broken read reports itself inline and
/// the rest of the dump still arrives - a diagnostic that dies mid-diagnosis
/// is the disease it exists to cure.
/// </summary>
/// <summary>
/// The scalars the dump quotes, read in one pass by
/// <see cref="GilStorage.ReadSitrepCounts"/>.
/// </summary>
internal readonly record struct SitrepCounts(
  long Listings,
  long ListingsValue,
  long VentureTokens,
  long RoutingReceipts,
  long ReceiptsExecuted,
  long ReceiptsOverridden,
  long Overrides,
  long OpenFlags,
  long DesynthRuns,
  long MarketEvents);

internal static class Sitrep
{
  internal static string Build()
  {
    var sb = new StringBuilder(1024);
    var now = DateTimeOffset.UtcNow;

    // Lazy, not eager: every section still guards independently (a broken read
    // reports itself inline and the rest of the dump arrives), and a Lazy that
    // faulted rethrows into whichever section asks next - so the three sections
    // that need counts each say so, off ONE database pass.
    var counts = new Lazy<SitrepCounts>(GilStorage.ReadSitrepCounts);

    sb.AppendLine($"=== Scrooge sitrep {now.ToLocalTime():yyyy-MM-dd HH:mm} ===");

    Section(sb, "build", () =>
      $"v{typeof(Plugin).Assembly.GetName().Version} - {BuildStamp.Line}");

    Section(sb, "config", () =>
    {
      var c = Plugin.Configuration;
      return $"gil tracking {(c.EnableGilTracking ? "ON" : "OFF")}, "
           + $"ledger {(c.EnableLedger ? "ON" : "OFF")}, skillup worth {c.SkillupWorthYellow:N0}/{c.SkillupWorthRed:N0}, "
           + $"desynth base {c.DesynthPerActionBaseMs}ms, server ceiling {c.ServerRoundTripCeilingMs}ms";
    });

    // THE ROUTING DRIVERS (08-22). The config line above quotes timing and the
    // skill-up worth floor, which are the knobs a stuck run turns on - and NONE of
    // the knobs that decide where an item goes. Every "why did it melt that" paste
    // arrived without the numbers that answer it: what a seal is worth, where the
    // curve cheapens it, how close two exits must be to become a Review, and what
    // floor the List exit has to clear. One line, the knobs only - the live seal
    // rate rides the venture line below, where the stock it reads already is.
    Section(sb, "routing", () =>
    {
      var c = Plugin.Configuration;
      return $"seal rate {c.SealToGilRate} gil (placeholder; measured wins when it exists), "
           + $"seal curve full<={c.SealCurveFullBelow:N0} zero>={c.SealCurveZeroAbove:N0} tokens, "
           + $"review band {c.RoutingReviewBandPct}%, "
           + $"floor {c.PriceFloorMode} (minimum {c.MinimumListingPrice:N0}), "
           + $"always-vendor {c.AlwaysVendorItemIds.Count} items";
    });

    // FIRST, because it is the answer to "why is every other line broken". Storage
    // fails CLOSED now: a failed migration leaves no connection at all rather than a
    // half-climbed one, so the sections below throw honestly - and this line says why.
    Section(sb, "storage", () => GilStorage.StorageAvailable
      ? "open"
      : "UNAVAILABLE - the database failed to open or migrate this session (see the log); every read below is dead");

    Section(sb, "board", () =>
    {
      var lastScan = GilStorage.GetLastFullScanTime();
      // THE TWO CLOCKS, same composer as the dashboard (ruled 08-22). The old
      // since-scan events tally is gone from here too: it read as "the board moved
      // under you" while only ever counting what our own looks wrote, and a zero
      // meant three different things (capture dead / no looks since the scan / looks
      // found nothing). The honest capture instrument is the corpus section's total
      // row count below. What a diagnostic wants here is the pair every verdict was
      // scored against - the pinch's read and recon's.
      var banked = GilStorage.GetDecisionCacheBankTimes();
      var reconAt = banked.Count > 0 ? banked.Values.Max() : 0L;
      var ripeness = RipenessSensors.HeaderClocks(
        lastScan, now.ToUnixTimeSeconds(), reconAt, banked.Count);
      // The BOOK's claim, not the raw table's (the 9b one-value rule, applied
      // here too): the listings table is the LAST LOOK at the sell lists, and
      // the round looks BEFORE hawk lists - so a sitrep reading only the table
      // understated the board by the whole launch (08-06: said 766k, ~2.1M
      // standing). Same feed, same wording as the dashboard readouts.
      var book = StandingBookFeed.Read(counts.Value.ListingsValue, lastScan);
      var age = lastScan > 0 ? now.ToUnixTimeSeconds() - lastScan : (long?)null;
      return $"{ripeness}; {counts.Value.Listings} listings at last look, "
           + StandingBook.Headline(book.Value, book.BookKept, age);
    });

    Section(sb, "venture", () =>
    {
      var tokens = counts.Value.VentureTokens;
      var burn = GilStorage.MeasureWeeklyVentureBurn();
      return burn is int wb
        ? $"{tokens:N0} tokens, burn {wb:N0}/wk"
        : $"{tokens:N0} tokens, burn unmeasured (needs 6.5d of snapshots)";
    });

    Section(sb, "universalis", () =>
      $"{UniversalisStats.PendingCount} stats fetches pending");

    // RECON IS ON THIS LINE (the minors batch, 2026-08-12). It was the one executor
    // missing from it and the one with its own stall watchdog - which is exactly the
    // state a sitrep gets pasted about ("the round is stuck and nothing is moving").
    Section(sb, "runs", () =>
      $"desynth {(Plugin.DesynthOrchestrator.IsRunning ? "RUNNING" : "idle")}, "
      + $"gc turn-in {(Plugin.GcTurnIn.IsRunning ? "RUNNING" : "idle")}, "
      + $"standing listings {(Plugin.StandingOrchestrator.IsRunning ? "RUNNING" : "idle")}, "
      + $"recon {(Plugin.PinchHost.ReconRunning ? "RUNNING" : "idle")}");

    // The skillup ladder at a glance (fast-follow, 2026-08-28): every class's
    // raw skill beside the ladder top the Green cap branch fires at - the
    // in-game verify for the cap fix, and the CUL training readout.
    Section(sb, "desynth skill", () =>
    {
      var top = GameSafe.MaxDesynthLevel();
      var skills = string.Join(", ", DesynthInventoryScanner.ClassJobAbbrev
        .OrderBy(kv => kv.Key)
        .Select(kv => $"{kv.Value} {GameSafe.GetDesynthLevel(kv.Key):0.##}"));
      return $"ladder top {top}; {skills}";
    });

    Section(sb, "db", () =>
    {
      var c = counts.Value;
      return $"routing receipts {c.RoutingReceipts} ({c.ReceiptsExecuted} executed, {c.ReceiptsOverridden} overridden), "
           + $"overrides {c.Overrides}, open flags {c.OpenFlags}, desynth runs {c.DesynthRuns}, market events {c.MarketEvents}";
    });

    // The V47 shadow corpus at a glance (ruled 08-23): how much paired data the
    // 3.1 queue-doctrine ruling has to work with, and how often the two worlds
    // actually disagree. A diagnostic count, not a verdict - the receipts hold
    // the cases.
    Section(sb, "doctrine shadow", () =>
    {
      var (banked, divergent) = GilStorage.CountDoctrineShadow();
      return banked == 0
        ? "no shadow receipts yet"
        : $"{banked} receipts banked, {divergent} step-divergent";
    });

    return sb.ToString();
  }

  /// <summary>One guarded line: "name: value" or "name: <read failed: reason>".</summary>
  private static void Section(StringBuilder sb, string name, Func<string> read)
  {
    string value;
    try { value = read(); }
    catch (Exception ex) { value = $"<read failed: {ex.Message}>"; }
    sb.Append(name).Append(": ").AppendLine(value);
  }
}
