using System;
using System.Collections.Generic;
using ECommons.DalamudServices;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The flag desk: <c>triage_flags</c> and <c>contest_receipts</c>.
///
/// <para>A standing flag is a lane (item + HQ + retainer + reason) held open until it
/// is acted on, dismissed, or self-heals because the condition that raised it stopped
/// being true. The lane is the identity — not the row — which is why the writes here
/// are upserts and status transitions rather than inserts.</para>
/// </summary>
internal static partial class GilStorage
{
  // =========================================================================
  // Standing-listing flags (V12; the table is still triage_flags - DB names are frozen) - persistent until acted on or dismissed
  // =========================================================================

  /// <summary>
  /// Inserts a triage flag, or refreshes the existing OPEN flag for the same
  /// (item, hq, retainer, reason) - re-flagging updates detail/prices/created_at
  /// instead of stacking duplicates.
  ///
  /// When <paramref name="evidence"/> is supplied, the flag is EVIDENCE-KEYED
  /// (decision memory): the stored snapshot is compared to the live one and the
  /// row is only touched when the world actually moved (StandingMemory.DecideUpsert).
  /// An unchanged world - or a bare manual reprice - is a silent no-op, so a
  /// stuck-thin item does not reset its "held since" clock every pinch. Passing
  /// null keeps the legacy always-refresh behavior (slow_evict and friends).
  /// </summary>
  internal static void UpsertStandingFlag(uint itemId, bool isHq, string retainerName, int slotIndex,
      string reason, string detail, int oldPrice, int flaggedPrice,
      StandingMemory.EvidenceSnapshot? evidence = null, int minHistorySamples = 0,
      string scope = "")
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // Evidence-keyed path: read the stored snapshot, let the pure core decide.
    var action = StandingMemory.FlagAction.Refresh; // legacy default: always UPDATE-then-INSERT
    var evidenceStr = "";
    if (evidence is StandingMemory.EvidenceSnapshot snap)
    {
      evidenceStr = snap.Serialize();
      action = StandingMemory.DecideUpsert(ReadOpenFlagEvidence(itemId, isHq, retainerName, reason), snap, minHistorySamples);
      Svc.Log.Debug($"[Standing] {reason} item {itemId}{(isHq ? " HQ" : "")} @ {retainerName}: {action} (ev {evidenceStr})");
      if (action == StandingMemory.FlagAction.Silent)
        return; // same unanswered question - don't churn the row or the clock
    }

    if (action != StandingMemory.FlagAction.Insert)
    {
      using var update = new SqliteCommand(
        @"UPDATE triage_flags
          SET detail = @detail, old_price = @old, flagged_price = @flagged,
              slot_index = @slot, created_at = @now, evidence = @evidence,
              scope = CASE WHEN @scope <> '' THEN @scope ELSE scope END
          WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
            AND reason = @reason AND status = 'open'",
        _connection);
      update.Parameters.AddWithValue("@detail", detail);
      update.Parameters.AddWithValue("@old", oldPrice);
      update.Parameters.AddWithValue("@flagged", flaggedPrice);
      update.Parameters.AddWithValue("@slot", slotIndex);
      update.Parameters.AddWithValue("@now", now);
      update.Parameters.AddWithValue("@evidence", evidenceStr);
      update.Parameters.AddWithValue("@scope", scope);
      update.Parameters.AddWithValue("@iid", (long)itemId);
      update.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      update.Parameters.AddWithValue("@ret", retainerName);
      update.Parameters.AddWithValue("@reason", reason);
      if (update.ExecuteNonQuery() > 0) return;
    }

    using var insert = new SqliteCommand(
      @"INSERT INTO triage_flags
          (created_at, item_id, is_hq, retainer_name, slot_index, reason, detail, old_price, flagged_price, evidence, scope)
        VALUES (@now, @iid, @hq, @ret, @slot, @reason, @detail, @old, @flagged, @evidence, @scope)",
      _connection);
    insert.Parameters.AddWithValue("@now", now);
    insert.Parameters.AddWithValue("@iid", (long)itemId);
    insert.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    insert.Parameters.AddWithValue("@ret", retainerName);
    insert.Parameters.AddWithValue("@slot", slotIndex);
    insert.Parameters.AddWithValue("@reason", reason);
    insert.Parameters.AddWithValue("@detail", detail);
    insert.Parameters.AddWithValue("@old", oldPrice);
    insert.Parameters.AddWithValue("@flagged", flaggedPrice);
    insert.Parameters.AddWithValue("@evidence", evidenceStr);
    insert.Parameters.AddWithValue("@scope", scope);
    insert.ExecuteNonQuery();
  }

  /// <summary>
  /// Item ids with an open lane_held flag — the standing thin-history set.
  /// Feeds the community-history prefetch at run start (the triage table IS
  /// the persistent list of items that will hold thin again next pinch).
  /// </summary>
  internal static List<uint> GetOpenLaneHeldItemIds()
  {
    var ids = new List<uint>();
    using var cmd = new SqliteCommand(
      "SELECT DISTINCT item_id FROM triage_flags WHERE status = 'open' AND reason = 'lane_held'",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      ids.Add((uint)reader.GetInt64(0));
    return ids;
  }

  /// <summary>
  /// The evidence snapshot stored on the open flag for a key. NULL means no
  /// open row exists; an empty string means a row exists WITHOUT a snapshot
  /// (pre-V17 legacy) — DecideUpsert adopts that row rather than duplicating
  /// it. Deterministic pick: prefer an evidenced row, then the newest, so a
  /// not-yet-deduped pair never feeds a legacy '' to the gate.
  /// </summary>
  private static string? ReadOpenFlagEvidence(uint itemId, bool isHq, string retainerName, string reason)
  {
    using var cmd = new SqliteCommand(
      @"SELECT evidence FROM triage_flags
        WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
          AND reason = @reason AND status = 'open'
        ORDER BY (evidence <> '') DESC, created_at DESC, id DESC LIMIT 1",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@reason", reason);
    var result = cmd.ExecuteScalar();
    return result == null ? null
      : result == DBNull.Value ? "" // row exists, no snapshot — adopt, don't duplicate
      : (string)result;
  }

  /// <summary>
  /// Self-heal (M2): closes every open flag on this (item, hq, retainer) whose
  /// reason did NOT re-fire this pass - the resolved live rules and the dead-
  /// producer strays (upward_held/outlier_warn) alike. StandingMemory.FlagsToClose
  /// makes the pick (pure/tested); this method just reads the rows and stamps
  /// the closes as 'resolved'. Returns the number of flags healed.
  /// </summary>
  internal static int SelfHealStandingFlags(uint itemId, bool isHq, string retainerName,
      IReadOnlySet<string> raisedThisPass)
  {
    var open = new List<(long Id, string Reason)>();
    using (var cmd = new SqliteCommand(
      @"SELECT id, reason FROM triage_flags
        WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret AND status = 'open'",
      _connection))
    {
      cmd.Parameters.AddWithValue("@iid", (long)itemId);
      cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      cmd.Parameters.AddWithValue("@ret", retainerName);
      using var reader = cmd.ExecuteReader();
      while (reader.Read())
        open.Add((reader.GetInt64(0), reader.GetString(1)));
    }

    var toClose = StandingMemory.FlagsToClose(open, raisedThisPass);
    foreach (var id in toClose)
      SetStandingFlagStatus(id, "resolved");
    return toClose.Count;
  }

  /// <summary>Open triage flags, newest first. Loaded by the Accountant's hinge alongside the current run's items.</summary>
  internal static List<StandingFlag> GetOpenStandingFlags()
  {
    var flags = new List<StandingFlag>();
    using var cmd = new SqliteCommand(
      @"SELECT id, created_at, item_id, is_hq, retainer_name, slot_index,
               reason, detail, old_price, flagged_price, status
        FROM triage_flags WHERE status = 'open'
        ORDER BY created_at DESC",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      flags.Add(new StandingFlag
      {
        Id = reader.GetInt64(0),
        CreatedAt = reader.GetInt64(1),
        ItemId = (uint)reader.GetInt64(2),
        IsHq = reader.GetInt32(3) != 0,
        RetainerName = reader.GetString(4),
        SlotIndex = reader.GetInt32(5),
        Reason = reader.GetString(6),
        Detail = reader.GetString(7),
        OldPrice = reader.GetInt32(8),
        FlaggedPrice = reader.GetInt32(9),
        Status = reader.GetString(10),
      });
    }
    return flags;
  }

  /// <summary>
  /// The ask on the newest DISMISSED flag of a class for one lane, or null when
  /// the player has never answered one. A dismissal is a verdict ("I know, and
  /// it stands"), and the raiser has to be able to read it back or it will ask
  /// the same question on the very next refresh. Null when never dismissed;
  /// the caller decides what a changed ask means.
  /// </summary>
  internal static long? LastDismissedFlagPrice(uint itemId, bool isHq, string retainerName, string reason)
  {
    using var cmd = new SqliteCommand(
      @"SELECT old_price FROM triage_flags
        WHERE item_id = @iid AND is_hq = @hq AND retainer_name = @ret
          AND reason = @reason AND status = 'dismissed'
        ORDER BY acted_at DESC, id DESC LIMIT 1",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@reason", reason);
    var result = cmd.ExecuteScalar();
    return result is null || result == DBNull.Value ? null : Convert.ToInt64(result);
  }

  /// <summary>
  /// Every OPEN flag of one class, as lane keys - what a raiser needs to close
  /// its own. Self-heal only ever sees one item at a time (it runs inside a
  /// pricing pass); a raiser that walks the whole standing book at once has to
  /// be able to ask the same question of every flag it ever raised, including
  /// the ones whose lanes have since left the board.
  /// </summary>
  internal static List<(long Id, (uint ItemId, bool IsHq, string Retainer) Lane)> GetOpenFlagLanes(string reason)
  {
    var rows = new List<(long, (uint, bool, string))>();
    using var cmd = new SqliteCommand(
      @"SELECT id, item_id, is_hq, retainer_name FROM triage_flags
        WHERE reason = @reason AND status = 'open'",
      _connection);
    cmd.Parameters.AddWithValue("@reason", reason);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rows.Add((reader.GetInt64(0),
        ((uint)reader.GetInt64(1), reader.GetInt32(2) != 0,
         reader.IsDBNull(3) ? "" : reader.GetString(3))));
    return rows;
  }

  /// <summary>Closes a flag: status = 'dismissed' or 'actioned', stamps acted_at.</summary>
  internal static void SetStandingFlagStatus(long flagId, string status)
  {
    using var cmd = new SqliteCommand(
      "UPDATE triage_flags SET status = @status, acted_at = @now WHERE id = @id",
      _connection);
    cmd.Parameters.AddWithValue("@status", status);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@id", flagId);
    cmd.ExecuteNonQuery();
  }

  // ==========================================================================
  // Contest receipts (V32) - answered contests grade the flags
  // ==========================================================================

  /// <summary>
  /// Books one answered contest against its flag class. Evidence fields are
  /// nullable and stay null where the path held no fact - a receipt never
  /// invents one. Append-only; the read surface is a later release.
  /// </summary>
  /// <param name="doubtBranch">
  /// The stable doubt-branch key the row acted through (V38), or "" when the
  /// row had no doubt on it. Empty is written as NULL, never as a string:
  /// "no branch" is an absence, and the tape's tally filters absences out
  /// rather than counting them as a branch of their own.
  /// </param>
  internal static void InsertContestReceipt(uint itemId, bool isHq, string retainerName,
    string reason, string proposal, string verdict, string answer, long raisedAt,
    long? listingPrice, long? cheapestCompetitor, int? saleCount, long? latestSaleAt,
    string doubtBranch = "")
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO contest_receipts
          (raised_at, verdict_at, item_id, is_hq, retainer_name, reason,
           proposal, verdict, answer,
           listing_price, cheapest_competitor, sale_count, latest_sale_at, doubt_branch)
        VALUES (@raised, @now, @iid, @hq, @ret, @reason,
                @proposal, @verdict, @answer,
                @price, @cheapest, @n, @latest, @branch)",
      _connection);
    cmd.Parameters.AddWithValue("@branch",
      string.IsNullOrEmpty(doubtBranch) ? DBNull.Value : doubtBranch);
    cmd.Parameters.AddWithValue("@raised", raisedAt);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ret", retainerName);
    cmd.Parameters.AddWithValue("@reason", reason);
    cmd.Parameters.AddWithValue("@proposal", proposal);
    cmd.Parameters.AddWithValue("@verdict", verdict);
    cmd.Parameters.AddWithValue("@answer", answer);
    cmd.Parameters.AddWithValue("@price", (object?)listingPrice ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@cheapest", (object?)cheapestCompetitor ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@n", (object?)saleCount ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@latest", (object?)latestSaleAt ?? DBNull.Value);
    cmd.ExecuteNonQuery();
  }

  // =========================================================================
  // Zombie lane_held flag heal (V19) — container-scoped close
  // =========================================================================

  /// <summary>
  /// The zombie round (M4): closes open lane_held flags whose item has left the
  /// container this run observed — but ONLY the container this run type can prove.
  /// A pinch passes FlagScope.Board with the sell-list item ids it saw; a Hawk run
  /// passes FlagScope.Inventory with the inventory item ids it saw. StandingMemory makes
  /// the pick (pure/tested); a flag pointing at the other container, or an Unknown-
  /// scope legacy flag, is left OPEN (fail toward open). Closes as 'item_gone' — a
  /// later GilTrack confirm may relabel it 'sold'. Returns the number closed.
  /// </summary>
  internal static int ZombieRoundLaneHeldFlags(string retainerName,
      StandingMemory.FlagScope runScope, IReadOnlySet<uint> observedItemIds)
  {
    var open = new List<StandingMemory.ZombieFlagRow>();
    using (var cmd = new SqliteCommand(
      @"SELECT id, item_id, scope FROM triage_flags
        WHERE retainer_name = @ret AND reason = 'lane_held' AND status = 'open'",
      _connection))
    {
      cmd.Parameters.AddWithValue("@ret", retainerName);
      using var reader = cmd.ExecuteReader();
      while (reader.Read())
        open.Add(new StandingMemory.ZombieFlagRow(
          reader.GetInt64(0), (uint)reader.GetInt64(1),
          StandingMemory.ParseScope(reader.IsDBNull(2) ? "" : reader.GetString(2))));
    }

    var toClose = StandingMemory.ZombieFlagsToClose(runScope, observedItemIds, open);
    foreach (var id in toClose)
      SetStandingFlagStatus(id, "item_gone");
    return toClose.Count;
  }
}
