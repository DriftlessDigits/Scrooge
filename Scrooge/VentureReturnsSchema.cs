using Microsoft.Data.Sqlite;

namespace Scrooge;

/// <summary>
/// The venture_returns table's schema beyond its V15 birth, extracted
/// Dalamud-free so the migration contract is linked-source testable against a
/// real temp SQLite DB (the MarketMemorySchema model). GilStorageBootstrap owns
/// the version gate and the logging; this class owns the statements.
/// </summary>
internal static class VentureReturnsSchema
{
  /// <summary>
  /// V42: the capture learns WHICH venture it was.
  ///
  /// <para>Until now a venture row recorded only what came back - retainer, item,
  /// quantity, quality - and the seals-to-gil arithmetic covered the missing half
  /// with a config knob that said venture tokens cost "believed 2". Drift killed the
  /// knob (08-15): <b>we don't need a mod knob for a thing we can directly measure
  /// in game.</b> The RetainerTask sheet carries the token cost of every venture,
  /// and the active retainer carries the id of the one it just ran - so the row can
  /// stamp its own cost at capture time and the arithmetic can stop believing.</para>
  ///
  /// <para>venture_id is the RetainerTask row, venture_cost the sheet's token cost
  /// for it, venture_category the coarse kind (quick / field / highland / woodland /
  /// waterside / targeted) so a future readout can split returns by what was sent.</para>
  ///
  /// <para><b>NULL means pre-stamp history and is never imputed</b> (ruled 08-15).
  /// Every row captured before this migration was captured type-blind; guessing a
  /// cost for it would launder a default into a measurement. Token arithmetic reads
  /// stamped rows only, and a stamp read that fails at capture time writes NULL
  /// rather than costing us the row.</para>
  ///
  /// <para>Column-guarded ALTERs (the V22/V24/V25/V30/V34/V35/V37/V41 model):
  /// re-running is a no-op.</para>
  /// </summary>
  internal static void ApplyV42(SqliteConnection connection)
    => SchemaGuards.EnsureColumns(connection, "venture_returns",
        "venture_id INTEGER",
        "venture_cost INTEGER",
        "venture_category TEXT");
}
