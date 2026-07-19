using System;
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
internal static class Sitrep
{
  internal static string Build()
  {
    var sb = new StringBuilder(1024);
    var now = DateTimeOffset.UtcNow;
    sb.AppendLine($"=== Scrooge sitrep {now.ToLocalTime():yyyy-MM-dd HH:mm} ===");

    Section(sb, "build", () =>
      $"v{typeof(Plugin).Assembly.GetName().Version} - {BuildStamp.Line}");

    Section(sb, "config", () =>
    {
      var c = Plugin.Configuration;
      return $"routing brain {(c.EnableRoutingBrain ? "ON" : "OFF")}, gil tracking {(c.EnableGilTracking ? "ON" : "OFF")}, "
           + $"pinch log {(c.EnablePinchRunLog ? "ON" : "OFF")}, skillup worth {c.SkillupWorthYellow:N0}/{c.SkillupWorthRed:N0}, "
           + $"desynth base {c.DesynthPerActionBaseMs}ms, server ceiling {c.ServerRoundTripCeilingMs}ms";
    });

    Section(sb, "board", () =>
    {
      var lastScan = GilStorage.GetLastFullScanTime();
      var ripeness = RipenessSensors.HeaderLine(lastScan, now.ToUnixTimeSeconds(),
        lastScan > 0 ? GilStorage.CountMarketEventsSince(lastScan) : 0);
      var listings = GilStorage.SitrepScalar("SELECT COUNT(*) FROM listings");
      var atAsk = GilStorage.SitrepScalar("SELECT SUM(unit_price * MAX(quantity, 1)) FROM listings");
      return $"{ripeness}; {listings} own listings, {atAsk:N0} gil at ask";
    });

    Section(sb, "venture", () =>
    {
      var tokens = GilStorage.SitrepScalar(
        "SELECT venture_tokens FROM gil_snapshots WHERE venture_tokens IS NOT NULL ORDER BY timestamp DESC LIMIT 1");
      var burn = GilStorage.MeasureWeeklyVentureBurn();
      return burn is int wb
        ? $"{tokens:N0} tokens, burn {wb:N0}/wk (~{tokens - wb:N0} in 7d)"
        : $"{tokens:N0} tokens, burn unmeasured (needs 6.5d of snapshots)";
    });

    Section(sb, "universalis", () =>
      $"{UniversalisStats.PendingCount} stats fetches pending");

    Section(sb, "runs", () =>
      $"desynth {(Plugin.DesynthOrchestrator.IsRunning ? "RUNNING" : "idle")}, "
      + $"gc turn-in {(Plugin.GcTurnIn.IsRunning ? "RUNNING" : "idle")}, "
      + $"triage {(Plugin.TriageOrchestrator.IsRunning ? "RUNNING" : "idle")}");

    Section(sb, "db", () =>
    {
      var receipts = GilStorage.SitrepScalar("SELECT COUNT(*) FROM routing_receipts");
      var executed = GilStorage.SitrepScalar("SELECT COUNT(*) FROM routing_receipts WHERE executed_action IS NOT NULL");
      var overridden = GilStorage.SitrepScalar("SELECT COUNT(*) FROM routing_receipts WHERE player_overrode = 1");
      var overrides = GilStorage.SitrepScalar("SELECT COUNT(*) FROM routing_overrides");
      var flags = GilStorage.SitrepScalar("SELECT COUNT(*) FROM triage_flags WHERE status = 'open'");
      var desynthRuns = GilStorage.SitrepScalar("SELECT COUNT(*) FROM desynth_runs");
      var events = GilStorage.SitrepScalar("SELECT COUNT(*) FROM market_events");
      return $"routing receipts {receipts} ({executed} executed, {overridden} overridden), "
           + $"overrides {overrides}, open flags {flags}, desynth runs {desynthRuns}, market events {events}";
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
