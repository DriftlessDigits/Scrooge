namespace Scrooge.Windows;

/// <summary>The ON MARKET tab - what the player has standing on the board right now.</summary>
internal sealed partial class GilWindow
{
  /// <summary>
  /// THE STANDING ASKS (Movement 3): what you have up on the market, readable any
  /// time. Drawn by the Accountant for the same reason the strip is - it owns the
  /// receipt read, the lane scores and the staged verbs - and hosted here because the
  /// asks are status of the world, which is what this window is for.
  ///
  /// <para><b>Not gated on the routing brain</b>, unlike the strip above: the rows are
  /// banked receipts, and a player with no router still has things on the market. The
  /// score columns are the brain's, and they draw dashes without it - which is what a
  /// column nobody scored is supposed to look like.</para>
  /// </summary>
  private static void DrawOnMarketTab() => Plugin.Accountant.DrawOnMarketPanel();
}
