using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Scrooge;

/// <summary>
/// The Dalamud half of the spine: reads "the state that IS" into the pure
/// <see cref="FacetReading"/> the evaluator consumes. Every reader here wraps a
/// sensor that ALREADY EXISTS in the codebase - no new game-state readers were
/// invented for the spine:
/// <list type="bullet">
///   <item>occupancy -> <see cref="DesynthOrchestrator.PlayerOccupied"/>
///     (v2.20's occupied pre-flight, generalized)</item>
///   <item>view/place addon presence -> ECommons
///     <c>TryGetAddonByName</c> + <c>IsAddonReady</c> (the same read every
///     executor's inline guard already used)</item>
///   <item>the GC Expert Delivery tab ->
///     <see cref="GcTurnInOrchestrator.AtExpertDelivery"/></item>
/// </list>
/// Kept out of the linked test set on purpose: it touches game memory, so the
/// pure <see cref="Spine"/>/<see cref="SpineEvaluator"/> can stay Dalamud-free.
/// </summary>
internal static class SpineSensors
{
  /// <summary>
  /// Occupancy reading. The game refuses commands like Desynthesize while the
  /// player is occupied (open bell, NPC talk); <c>why</c> becomes the "but ..."
  /// half of the refusal ("the retainer bell is open").
  /// </summary>
  internal static FacetReading Unoccupied()
  {
    var occupied = DesynthOrchestrator.PlayerOccupied(out var why);
    return new FacetReading(!occupied, occupied ? why : "you're free to act");
  }

  /// <summary>
  /// Whether a named addon is open AND ready. <paramref name="absent"/> is the
  /// "but ..." half of the refusal when it is not (phrased per call site).
  /// </summary>
  internal static unsafe FacetReading AddonReady(string addonName, string absent)
  {
    var ready = GenericHelpers.TryGetAddonByName<AtkUnitBase>(addonName, out var addon)
      && GenericHelpers.IsAddonReady(addon);
    return new FacetReading(ready, ready ? "it's open" : absent);
  }

  /// <summary>
  /// Whether ANY of <paramref name="addonNames"/> is open AND ready - the reading
  /// is met on the first that is. Used when one place is evidenced by more than one
  /// addon: the retainer bell shows the roster (<c>RetainerList</c>) when idle, but
  /// the game CLOSES that roster once a retainer is summoned and shows the sell view
  /// (<c>RetainerSellList</c>) instead (see AutoPinch.cs: "AddonMaster will be
  /// disposed once the RetainerList addon is closed"). Either open still means the
  /// player is standing at the bell. <paramref name="absent"/> is the "but ..." half
  /// of the refusal when none are ready.
  /// </summary>
  internal static unsafe FacetReading AnyAddonReady(string[] addonNames, string absent)
  {
    foreach (var name in addonNames)
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var addon)
        && GenericHelpers.IsAddonReady(addon))
        return new FacetReading(true, "it's open");
    }
    return new FacetReading(false, absent);
  }

  /// <summary>
  /// Whether the player is at their GC's Expert Delivery tab (list open AND the
  /// agent's SelectedTab == Expert Delivery) - the exact check the GC turn-in
  /// run and the deck already gate on.
  /// </summary>
  internal static FacetReading AtExpertDelivery()
  {
    var at = GcTurnInOrchestrator.AtExpertDelivery();
    return new FacetReading(at, at ? "it's open" : "the Expert Delivery window isn't open");
  }
}
