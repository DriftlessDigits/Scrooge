using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// Where an item is sent and why: <c>routing_overrides</c>, <c>routing_receipts</c>
/// and <c>pull_intents</c>.
///
/// <para>A receipt is written when the router rules; an override is written when the
/// player disagrees with it. Keeping both means the router can be graded against the
/// person using it instead of against itself. Pull intents are the queued half — a
/// destination remembered until the item is actually pulled.</para>
/// </summary>
internal static partial class GilStorage
{
  /// <summary>
  /// THE WINDOW A ROUTING RECEIPT SPEAKS FOR. The insert dedupes on it (the same
  /// item/exit inside it is one standing decision re-observed, not a new one) and
  /// the override link below is bounded by it for exactly the same reason - a
  /// ruling can only point at a receipt that was still standing when it was made.
  /// One constant so the two can never drift into disagreeing about what "the
  /// current decision" is.
  /// </summary>
  private const long ReceiptStandingWindowSeconds = 86400;

  /// <summary>
  /// Records a routing override: the router said one thing, the player did
  /// another. Written by the Hawk window when a gated item is checked anyway,
  /// and by the Ledger's bag and standing rows. Confirmations write here too -
  /// the read side is what separates agreement from disagreement.
  ///
  /// <para>SINCE V46 the row also names the RECEIPT it ruled against
  /// (<see cref="StandingReceiptId"/>). Nothing reads the link yet; it exists so
  /// the 4.0 scoreboard can put a ruling and the four scores it was made over in
  /// one sentence without joining on item and timestamp proximity.</para>
  /// </summary>
  internal static void InsertRoutingOverride(uint itemId, bool isHq, int ilvl,
      string routerVerdict, string routerReason, string playerVerdict)
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using var cmd = new SqliteCommand(
      @"INSERT INTO routing_overrides
          (created_at, item_id, is_hq, ilvl, router_verdict, router_reason, player_verdict, receipt_id)
        VALUES (@now, @iid, @hq, @ilvl, @rv, @reason, @pv, @rid)",
      _connection);
    cmd.Parameters.AddWithValue("@now", now);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ilvl", ilvl);
    cmd.Parameters.AddWithValue("@rv", routerVerdict);
    cmd.Parameters.AddWithValue("@reason", routerReason);
    cmd.Parameters.AddWithValue("@pv", playerVerdict);
    cmd.Parameters.AddWithValue("@rid", (object?)StandingReceiptId(itemId, isHq, now) ?? DBNull.Value);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// The receipt this ruling is about: the newest one for the variant, inside
  /// <see cref="ReceiptStandingWindowSeconds"/>. Same pick
  /// <see cref="MarkRoutingReceiptOverridden"/> makes when it sets the flag, so
  /// the id and the flag can never name two different rows.
  ///
  /// <para>NULL IS A REAL ANSWER, and the window is what makes it one. Routing
  /// receipts are written by the bag scan only, so a ruling on a standing (listed)
  /// lane the router never evaluated has no receipt behind it - and the newest
  /// receipt for that variant could be weeks old, from the last time the item sat
  /// in the bags. Linking to that row would bank a pointer to scores nobody was
  /// looking at. One indexed point read on a click path, never per frame.</para>
  /// </summary>
  private static long? StandingReceiptId(uint itemId, bool isHq, long now)
  {
    using var cmd = new SqliteCommand(
      @"SELECT id FROM routing_receipts
        WHERE item_id = @iid AND is_hq = @hq AND created_at >= @since
        ORDER BY created_at DESC, id DESC LIMIT 1",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@since", now - ReceiptStandingWindowSeconds);
    return cmd.ExecuteScalar() is long id ? id : null;
  }

  /// <summary>
  /// Override counts per router verdict class - the read side of the Ledger's
  /// confidence refinement (design Section 4: "manual decisions teach"). Counts
  /// only genuine DISAGREEMENTS (player_verdict != router_verdict); confirmations
  /// are recorded in the same table but do not demote a class. v0-simple by
  /// design: the tier refinement is a single override-count threshold, the
  /// maturation path (measured quantities) is post-3.0.
  ///
  /// <para>Storage failure is the caller's to swallow - an unreadable override
  /// history is no history, and the confidence tier simply goes unrefined. The
  /// Accountant's LoadOverrideCounts catches and hands back an empty map; this
  /// method itself makes no such promise, and the doc used to say it did.</para>
  /// </summary>
  internal static Dictionary<string, int> GetRoutingOverrideCounts()
  {
    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
    // DISTINCT items, not rows: one item re-ruled across evidence flips is a
    // player changing their mind, not a doctrine pattern - two rows from the
    // same hat must not demote a whole verdict class (finding 13: the Facet
    // Choker's two rulings zeroed a 64-item Churn bulk).
    // Which disagreements are doctrine evidence at all is the pure core's call
    // (BoardConfidence.CountsTowardDemotion): only market-boundary crossings
    // count; off-market value reshuffles are standing-rule applications.
    // Assent clears dissent (Drift's 07-19 ruling): a crossing only stands until
    // the player's next executed, unoverridden act of the class's own action -
    // trust demonstrated at the bell forgives the disagreements before it. The
    // fold and the standing test live in the pure core (BoardConfidence);
    // this just feeds it the executed-receipt facts.
    var assents = new List<(string Exit, string ExecutedAction, long CreatedAt)>();
    using (var assentCmd = new SqliteCommand(
      @"SELECT exit, executed_action, MAX(created_at)
        FROM routing_receipts
        WHERE player_overrode = 0 AND executed_action IS NOT NULL
        GROUP BY exit, executed_action",
      _connection))
    using (var assentReader = assentCmd.ExecuteReader())
    {
      while (assentReader.Read())
        assents.Add((assentReader.GetString(0), assentReader.GetString(1), assentReader.GetInt64(2)));
    }
    var lastAssent = BoardConfidence.LastAssentByClass(assents);

    var distinctItems = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    using var cmd = new SqliteCommand(
      @"SELECT router_verdict, player_verdict, item_id || '_' || is_hq, created_at
        FROM routing_overrides
        WHERE player_verdict <> router_verdict",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
      var routerVerdict = reader.GetString(0);
      if (!BoardConfidence.CountsTowardDemotion(routerVerdict, reader.GetString(1)))
        continue;
      if (!BoardConfidence.CrossingStands(reader.GetInt64(3), lastAssent, routerVerdict))
        continue;
      if (!distinctItems.TryGetValue(routerVerdict, out var items))
        distinctItems[routerVerdict] = items = new HashSet<string>(StringComparer.Ordinal);
      items.Add(reader.GetString(2));
    }
    foreach (var (verdict, items) in distinctItems)
      counts[verdict] = items.Count;
    return counts;
  }

  // =========================================================================
  // Routing receipts (V20) - decisions with their alternatives
  // =========================================================================

  /// <summary>
  /// Records one routing decision with the ALTERNATIVE scores on the table when
  /// it was made (the 4.0 scoreboard's counterfactual food). Deduped: the same
  /// (item, hq, exit) within 24h is the same standing decision re-observed on a
  /// refresh, not a new one - one receipt speaks for it. A changed exit (new
  /// evidence flipped the verdict) always writes; verdict stability across
  /// evidence phases is one of the reads this table exists to answer.
  ///
  /// <para>SINCE V44 the melt score arrives with its provenance
  /// (<paramref name="scores"/>'s <see cref="MeltGrade"/>) and the row's skillup
  /// standing (<paramref name="skillupColor"/>, null when the item is not
  /// skillup-eligible). Both were in hand at the call site and dropped on the floor
  /// here, which left melt_score a number with no units - the one thing the 4.0
  /// skillup-crossover derivation cannot work around.</para>
  /// </summary>
  internal static void InsertRoutingReceipt(uint itemId, bool isHq, int ilvl,
    string exit, string reason, bool isReview, string confidenceTier,
    RoutingScores? scores, int sealRate, bool sealRateEmpirical,
    double effectiveSealRate, bool sealDiscounted,
    int? ventureStock, int? weeklyBurn, string evidencePhase,
    DesynthSkillupColor? skillupColor)
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    using (var dupe = new SqliteCommand(
      @"SELECT 1 FROM routing_receipts
        WHERE item_id = @iid AND is_hq = @hq AND exit = @exit AND created_at >= @since
        LIMIT 1",
      _connection))
    {
      dupe.Parameters.AddWithValue("@iid", (long)itemId);
      dupe.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
      dupe.Parameters.AddWithValue("@exit", exit);
      dupe.Parameters.AddWithValue("@since", now - ReceiptStandingWindowSeconds);
      if (dupe.ExecuteScalar() is not null) return;
    }

    using var cmd = new SqliteCommand(
      @"INSERT INTO routing_receipts
          (created_at, item_id, is_hq, ilvl, exit, reason, is_review,
           confidence_tier, list_score, gc_score, melt_score, vendor_score,
           seal_rate, seal_rate_empirical, effective_seal_rate, seal_discounted,
           venture_stock, weekly_burn, evidence_phase, melt_grade, skillup_color)
        VALUES (@now, @iid, @hq, @ilvl, @exit, @reason, @review,
           @tier, @list, @gc, @melt, @vendor,
           @rate, @emp, @effrate, @disc, @stock, @burn, @phase, @grade, @color)",
      _connection);
    cmd.Parameters.AddWithValue("@now", now);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@ilvl", ilvl);
    cmd.Parameters.AddWithValue("@exit", exit);
    cmd.Parameters.AddWithValue("@reason", reason);
    cmd.Parameters.AddWithValue("@review", isReview ? 1 : 0);
    cmd.Parameters.AddWithValue("@tier", confidenceTier);
    cmd.Parameters.AddWithValue("@list", (object?)scores?.List ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@gc", (object?)scores?.Gc ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@melt", (object?)scores?.Melt ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@vendor", (object?)scores?.Vendor ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@rate", sealRate);
    cmd.Parameters.AddWithValue("@emp", sealRateEmpirical ? 1 : 0);
    // The rate the GC score was ACTUALLY computed at, and whether the runway
    // discount was in force - so a receipt reads back without knowing what the
    // threshold and factor knobs were set to that night.
    cmd.Parameters.AddWithValue("@effrate", effectiveSealRate);
    cmd.Parameters.AddWithValue("@disc", sealDiscounted ? 1 : 0);
    cmd.Parameters.AddWithValue("@stock", (object?)ventureStock ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@burn", (object?)weeklyBurn ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@phase", evidencePhase);
    // WHAT THE MELT NUMBER IS, beside the number (V44). An empty grade means the
    // verdict carried no scores at all - the pre-value exits (ban, protection,
    // always-vendor, venture panic), where no comparison ran - and it is written
    // rather than left to the column default so a recorded row can be told from a
    // pre-V44 one. Null colour = the item was not skillup-eligible, which on a row
    // that HAS a grade is a measurement, not a silence.
    cmd.Parameters.AddWithValue("@grade", scores is { } s ? s.MeltGrade.ToString() : "");
    cmd.Parameters.AddWithValue("@color", (object?)skillupColor?.ToString() ?? DBNull.Value);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Flags the latest receipt for an item as player-overridden (a ruling moved it
  /// off the router's exit).
  ///
  /// <para>UNBOUNDED IN TIME, unlike <see cref="StandingReceiptId"/>'s otherwise
  /// identical pick: this flag feeds the confidence tier's assent read, whose
  /// behaviour is not this batch's to change, so it keeps flagging the newest
  /// receipt however old. The consequence is worth knowing rather than hiding -
  /// on a variant whose only receipt predates the standing window, the flag lands
  /// and the override row's receipt_id is NULL. Read as what each says: the flag
  /// is "this variant has been overruled", the link is "THIS decision was".</para>
  /// </summary>
  internal static void MarkRoutingReceiptOverridden(uint itemId, bool isHq)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE routing_receipts SET player_overrode = 1
        WHERE id = (SELECT id FROM routing_receipts
                    WHERE item_id = @iid AND is_hq = @hq
                    ORDER BY created_at DESC LIMIT 1)",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// Stamps what ACTUALLY happened onto the latest open receipt for an item -
  /// the executed side of the counterfactual join. Only the newest unexecuted
  /// receipt takes the stamp; older receipts keep their null (decision made,
  /// nothing fired - itself a signal).
  /// </summary>
  internal static void MarkRoutingReceiptExecuted(uint itemId, bool isHq, string action)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE routing_receipts SET executed_action = @action
        WHERE id = (SELECT id FROM routing_receipts
                    WHERE item_id = @iid AND is_hq = @hq AND executed_action IS NULL
                    ORDER BY created_at DESC LIMIT 1)",
      _connection);
    cmd.Parameters.AddWithValue("@action", action);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.ExecuteNonQuery();
  }

  /// <summary>
  /// The player's most recent ruling per (item, HQ, router verdict) - the read side
  /// of persistent Ledger rulings. Keyed on the router's verdict so a ruling sticks
  /// exactly as long as it answers the SAME question: if the router's verdict for
  /// the item later changes, the key misses and the Ledger honestly re-asks.
  /// </summary>
  internal static Dictionary<(uint ItemId, bool IsHq, string RouterVerdict), string> GetLatestRoutingRulings()
  {
    var rulings = new Dictionary<(uint, bool, string), string>();
    using var cmd = new SqliteCommand(
      @"SELECT item_id, is_hq, router_verdict, player_verdict, MAX(created_at)
        FROM routing_overrides
        GROUP BY item_id, is_hq, router_verdict",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      rulings[((uint)reader.GetInt64(0), reader.GetInt32(1) != 0, reader.GetString(2))] = reader.GetString(3);
    return rulings;
  }

  // ==========================================================================
  // Pull intents (V31) - the ruling that survives the retainer->bag crossing
  // ==========================================================================

  /// <summary>
  /// Banks (or re-banks) a pull-for-X ruling for an item variant. Written at
  /// stage-spend time, BEFORE the retrieve - a ruling banked after a retrieve
  /// that crashed mid-chain would be a ruling lost. Re-staging overwrites:
  /// the newest verb is the standing answer.
  /// </summary>
  internal static void UpsertPullIntent(uint itemId, bool isHq, string destination)
  {
    using var cmd = new SqliteCommand(
      @"INSERT INTO pull_intents (item_id, is_hq, destination, staged_at, consumed_at)
        VALUES (@iid, @hq, @dest, @now, NULL)
        ON CONFLICT(item_id, is_hq) DO UPDATE SET
          destination = @dest, staged_at = @now, consumed_at = NULL",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@dest", destination);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    cmd.ExecuteNonQuery();
  }

  /// <summary>All unconsumed pull intents, keyed by variant. The bag scan is the one reader.</summary>
  internal static Dictionary<(uint ItemId, bool IsHq), string> GetOpenPullIntents()
  {
    var intents = new Dictionary<(uint, bool), string>();
    using var cmd = new SqliteCommand(
      "SELECT item_id, is_hq, destination FROM pull_intents WHERE consumed_at IS NULL",
      _connection);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
      intents[((uint)reader.GetInt64(0), reader.GetInt32(1) != 0)] = reader.GetString(2);
    return intents;
  }

  /// <summary>
  /// Stamps an intent consumed - the item landed in the bags and the ruling was
  /// applied. One-shot by design: the NEXT time this variant is pulled it is a
  /// new question.
  /// </summary>
  internal static void ConsumePullIntent(uint itemId, bool isHq)
  {
    using var cmd = new SqliteCommand(
      @"UPDATE pull_intents SET consumed_at = @now
        WHERE item_id = @iid AND is_hq = @hq AND consumed_at IS NULL",
      _connection);
    cmd.Parameters.AddWithValue("@iid", (long)itemId);
    cmd.Parameters.AddWithValue("@hq", isHq ? 1 : 0);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    cmd.ExecuteNonQuery();
  }
}
