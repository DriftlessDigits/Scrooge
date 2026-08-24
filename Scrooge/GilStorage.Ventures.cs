using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// <c>venture_returns</c> and <c>coffer_pulls</c>: what the retainers brought back and
/// what fell out of the containers they brought.
///
/// <para>The two are separate tables because a coffer is a return whose real contents
/// are not known until it is opened, sometimes days later. Joining them at write time
/// would have forced a guess; keeping them apart lets the token-burn math (see
/// <see cref="MeasureWeeklyVentureBurn"/>) count what was actually spent.</para>
/// </summary>
internal static partial class GilStorage
{
  /// <summary>
  /// Records one collected venture result (V15), optionally stamped with WHICH
  /// venture produced it (V42).
  ///
  /// <para>The stamp is nullable on purpose: a capture must never be lost to a
  /// failed sheet or struct read, so an unstamped call writes NULLs and the row
  /// still counts as a venture. Token arithmetic reads stamped rows only, and pre-V42
  /// history is never imputed (ruled 08-15).</para>
  /// </summary>
  internal static void InsertVentureReturn(long capturedAt, string retainerName,
      uint itemId, int quantity, bool isHq,
      uint? ventureId = null, int? ventureCost = null, string? ventureCategory = null)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO venture_returns
        (captured_at, retainer_name, item_id, quantity, is_hq, venture_id, venture_cost, venture_category)
      VALUES (@ts, @ret, @item, @qty, @hq, @vid, @vcost, @vcat)",
      _connection);
    cmd.Parameters.AddWithValue("@ts", capturedAt);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@item", itemId);
    cmd.Parameters.AddWithValue("@qty", quantity);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@vid", (object?)ventureId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@vcost", (object?)ventureCost ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@vcat", (object?)ventureCategory ?? DBNull.Value);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Venture returns captured in the last N days, newest first. The three V42
  /// stamp columns ride along nullable — NULL is "captured before the stamp
  /// existed, or the stamp read failed", and callers must not fill it in.
  /// </summary>
  internal static List<(long CapturedAt, string Retainer, uint ItemId, int Quantity, bool IsHq,
      uint? VentureId, int? VentureCost, string? VentureCategory)>
      GetVentureReturns(int sinceDays)
  {
    var rows = new List<(long, string, uint, int, bool, uint?, int?, string?)>();
    var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sinceDays * 86400L;
    using var cmd = new SqliteCommand(
      @"SELECT captured_at, retainer_name, item_id, quantity, is_hq,
               venture_id, venture_cost, venture_category
        FROM venture_returns WHERE captured_at >= @cutoff
        ORDER BY captured_at DESC",
      _connection);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rows.Add((reader.GetInt64(0), reader.GetString(1), (uint)reader.GetInt64(2),
        reader.GetInt32(3), reader.GetInt32(4) != 0,
        reader.IsDBNull(5) ? null : (uint)reader.GetInt64(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7)));
    return rows;
  }

  /// <summary>
  /// Banks one opened Materiel Container and what it gave up (V43). Written by
  /// the passive <see cref="CofferPullWatcher"/> - the boxes are opened by hand
  /// from the bags, so there is no orchestrator to report the pull.
  /// </summary>
  internal static void InsertCofferPull(long openedAt, uint containerItemId,
      uint pulledItemId, int quantity, bool isHq)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO coffer_pulls
        (opened_at, container_item_id, pulled_item_id, quantity, is_hq)
      VALUES (@ts, @container, @item, @qty, @hq)",
      _connection);
    cmd.Parameters.AddWithValue("@ts", openedAt);
    cmd.Parameters.AddWithValue("@container", containerItemId);
    cmd.Parameters.AddWithValue("@item", pulledItemId);
    cmd.Parameters.AddWithValue("@qty", quantity);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Coffer pulls captured in the last N days, newest first (V43).
  ///
  /// <para>Deliberately unread in 3.0 - the window reader ships with the writer so
  /// the table is whole, but nothing values or advises off it until the data has
  /// baked (ruled 08-15). It exists here rather than being added later so a 3.1
  /// reader inherits a shape that was pinned the day the rows started arriving.</para>
  /// </summary>
  internal static List<(long OpenedAt, uint ContainerItemId, uint PulledItemId, int Quantity, bool IsHq)>
      GetCofferPulls(int sinceDays)
  {
    var rows = new List<(long, uint, uint, int, bool)>();
    var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sinceDays * 86400L;
    using var cmd = new SqliteCommand(
      @"SELECT opened_at, container_item_id, pulled_item_id, quantity, is_hq
        FROM coffer_pulls WHERE opened_at >= @cutoff
        ORDER BY opened_at DESC",
      _connection);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rows.Add((reader.GetInt64(0), (uint)reader.GetInt64(1), (uint)reader.GetInt64(2),
        reader.GetInt32(3), reader.GetInt32(4) != 0));
    return rows;
  }

  /// <summary>
  /// Oldest and newest venture-token stock readings in the last N days
  /// (bell-snapshot piggyback), or null when fewer than two readings exist.
  /// Burn/acquire rate = the delta over the window.
  /// </summary>
  internal static ((long Ts, int Tokens) First, (long Ts, int Tokens) Last)? GetVentureTokenSpan(int sinceDays)
  {
    var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sinceDays * 86400L;
    using var cmd = new SqliteCommand(
      @"SELECT timestamp, venture_tokens FROM gil_snapshots
        WHERE venture_tokens IS NOT NULL AND timestamp >= @cutoff
        ORDER BY timestamp ASC",
      _connection);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    using var reader = cmd.ExecuteReader();
    (long, int)? first = null, last = null;
    while (reader.Read())
    {
      var row = (reader.GetInt64(0), reader.GetInt32(1));
      first ??= row;
      last = row;
    }
    return first is { } f && last is { } l && f.Item1 != l.Item1
      ? ((f.Item1, f.Item2), (l.Item1, l.Item2))
      : null;
  }

  /// <summary>
  /// Measured venture-token burn over the trailing FULL week: the sum of downward
  /// deltas between consecutive gil_snapshots token reads (upward jumps are
  /// purchases and are ignored). Whole-week window on purpose - weekday and
  /// weekend usage differ heavily, and a 7-day window contains its own mix, so
  /// the shape cancels out of any 7-day projection. Returns null until snapshots
  /// actually cover the week (earliest read within the window must be at least
  /// 6.5 days old) - a partial week would smuggle the weekday/weekend bias right
  /// back in, so no measurement means the saturation tilt stays off.
  /// </summary>
  internal static int? MeasureWeeklyVentureBurn()
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var weekAgo = now - 7 * 86400;

    // The bracket seed: the counter at the last look AT OR BEFORE the window
    // opened. See VentureBurn - without it, a quiet morning at the window's
    // left edge read as "unmeasured" over weeks of banked history.
    int? seed = null;
    using (var seedCmd = new SqliteCommand(
      @"SELECT venture_tokens FROM gil_snapshots
        WHERE venture_tokens IS NOT NULL AND venture_tokens > 0 AND timestamp <= @cutoff
        ORDER BY timestamp DESC LIMIT 1",
      _connection))
    {
      seedCmd.Parameters.AddWithValue("@cutoff", weekAgo);
      if (seedCmd.ExecuteScalar() is long s)
        seed = (int)s;
    }

    using var cmd = new SqliteCommand(
      @"SELECT timestamp, venture_tokens FROM gil_snapshots
        WHERE venture_tokens IS NOT NULL AND venture_tokens > 0 AND timestamp >= @cutoff
        ORDER BY timestamp",
      _connection);
    cmd.Parameters.AddWithValue("@cutoff", weekAgo);
    using var reader = cmd.ExecuteReader();

    var rows = new List<(long At, int Tokens)>();
    while (reader.Read())
      rows.Add((reader.GetInt64(0), reader.GetInt32(1)));

    return VentureBurn.Measure(seed, rows, now, weekAgo);
  }
}
