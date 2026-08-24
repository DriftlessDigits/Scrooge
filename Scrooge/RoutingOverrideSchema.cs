using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The <c>routing_overrides</c> widenings, extracted Dalamud-free so they are
/// linked-source testable (the RoutingReceiptSchema / MarketMemorySchema model -
/// one table, one schema file). The table's BIRTH is still V14's, inside
/// GilStorageBootstrap, where it was written before this convention existed;
/// nothing is moved, because moving a birth rung rewrites nobody's database and
/// costs the ladder its diffability.
/// </summary>
internal static class RoutingOverrideSchema
{
  private const string OverridesTable = "routing_overrides";

  /// <summary>
  /// V46: THE LINK from a ruling to the receipt it ruled against.
  ///
  /// <para>Both halves of a ruling were already banked - routing_overrides says
  /// what the player decided, routing_receipts says what the router had scored
  /// when he decided it - and there was no edge between them. The only join
  /// available was heuristic: same item, same quality, nearest timestamp. That
  /// is precisely the join the 4.0 MeltOverVendorFactor derivation cannot afford
  /// to make, because Desynth&lt;-&gt;Vendor rulings are exempt from tier demotion
  /// on purpose (BoardConfidence.CountsTowardDemotion names knob-tuning as their
  /// intended use), so the melt_score / vendor_score PAIR the player ruled
  /// against is the whole evidence, and a pair fetched off the wrong receipt is
  /// worse than no pair at all.</para>
  ///
  /// <para><b>The id is written, not inferred.</b> The write side resolves it the
  /// same way <c>MarkRoutingReceiptOverridden</c> picks the row it flags - the
  /// newest receipt for the variant - and bounds it to the receipt dedupe window,
  /// so a link only lands when a receipt for THIS standing decision exists. A
  /// ruling on a lane with no live receipt (a listed row the bag scan never
  /// evaluated) banks NULL, which reads as "no receipt was standing", never as a
  /// pointer to a stale one.</para>
  ///
  /// <para>Column-guarded ALTER, nullable, nothing back-filled: pre-V46 rows keep
  /// the heuristic join as their only option, which is an honest statement about
  /// what was recorded rather than a manufactured edge.</para>
  /// </summary>
  internal static void ApplyV46(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, OverridesTable, "receipt_id INTEGER");
}
