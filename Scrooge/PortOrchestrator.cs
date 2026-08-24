using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Scrooge;

/// <summary>
/// PORT + ARRIVAL (WALK unit 7) - the Dalamud half. Thin by design: every
/// decision lives in <see cref="PortPlan"/>, and this file only reads the four
/// facts that decision needs and, on a human click, casts Teleport.
///
/// <para>It OFFERS NOTHING itself. The deck and the run log's stage rail ask
/// <c>AccountantWindow.TurnInPort()</c> for one decision and draw it; this class has
/// no idea a round exists. <see cref="FirePort"/> is the only thing here that
/// touches the world, and the only caller is a button's click handler.</para>
///
/// <para>Nothing here is a new sensor class. The teleport gate is
/// <c>ActionManager.GetActionStatus</c> on the Teleport action itself, which is
/// the game's own answer about combat, occupancy, duty-bound and everything else
/// it will grow later - a precondition slotted into the spine's vocabulary, not a
/// second opinion we have to keep in sync.</para>
/// </summary>
internal static class PortOrchestrator
{
  /// <summary>Action id 5 = Teleport, the shared "cast it" action every aetheryte port uses.</summary>
  private const uint TeleportActionId = 5;

  /// <summary>Where the GC counter is, as the game sheets name it.</summary>
  internal readonly record struct PortDestination(uint AetheryteId, uint TerritoryId, string Name);

  /// <summary>
  /// The player's GC HQ aetheryte, or null when they have no Grand Company (or
  /// the sheet read fails). The id comes from <see cref="PortPlan.AetheryteFor"/>;
  /// the NAME and the TERRITORY come straight back out of the Aetheryte sheet, so
  /// the button never says a place the sheet does not agree with.
  /// </summary>
  internal static unsafe PortDestination? Destination()
  {
    var ps = PlayerState.Instance();
    if (ps == null) return null;
    if (PortPlan.AetheryteFor(ps->GrandCompany) is not uint aetheryteId) return null;

    if (!Svc.Data.GetExcelSheet<Aetheryte>().TryGetRow(aetheryteId, out var row)) return null;
    var name = row.PlaceName.ValueNullable?.Name.ToString();
    if (string.IsNullOrEmpty(name)) return null;

    return new PortDestination(aetheryteId, row.Territory.RowId, name);
  }

  /// <summary>
  /// Whether the game will let Teleport be cast right now. Status 0 = castable;
  /// anything else is the game's own refusal (in combat, occupied, bound by duty,
  /// still casting). <paramref name="why"/> is the "but ..." half of the refusal.
  /// </summary>
  internal static unsafe bool Castable(out string why)
  {
    var am = ActionManager.Instance();
    if (am == null)
    {
      why = "the game isn't answering about your actions";
      return false;
    }

    var status = am->GetActionStatus(ActionType.Action, TeleportActionId);
    if (status == 0)
    {
      why = "Teleport is ready";
      return true;
    }

    why = $"the game won't let you cast Teleport right now (it answered {status})";
    return false;
  }

  /// <summary>The player is standing in the destination aetheryte's own zone.</summary>
  internal static bool InTerritory(uint territoryId)
    => territoryId != 0 && Svc.ClientState.TerritoryType == territoryId;

  /// <summary>
  /// The player is standing anywhere in the destination's CITY - same
  /// PlaceNameZone in the TerritoryType sheet, so Upper Decks counts as Limsa
  /// (live 08-02: the deck offered a gil teleport to a plaza Drift could see from
  /// the bridge, because the exact-territory test doesn't know a city has more
  /// than one zone). Sheet-driven like everything else here: the grouping comes
  /// back out of the game's own data, never a hardcoded cluster list.
  /// </summary>
  internal static bool InSameCity(uint territoryId)
  {
    if (territoryId == 0) return false;
    var current = Svc.ClientState.TerritoryType;
    if (current == territoryId) return true;

    var sheet = Svc.Data.GetExcelSheet<TerritoryType>();
    if (!sheet.TryGetRow(current, out var here) || !sheet.TryGetRow(territoryId, out var there))
      return false;
    var zone = there.PlaceNameZone.RowId;
    return zone != 0 && here.PlaceNameZone.RowId == zone;
  }

  /// <summary>
  /// Casts the teleport. THE ONLY CALLER IS A BUTTON CLICK - nothing in this
  /// plugin may call it on a timer, a completion event, or a stage transition.
  ///
  /// <para>UpdateAetheryteList first, deliberately. Telepo's TeleportList is the
  /// list Teleport() looks the destination up in, and it is populated by
  /// UpdateAetheryteList - the same call the game's own Teleport window makes when
  /// it opens. A session that has never opened that window can therefore hold an
  /// empty list, and Teleport() on an empty list fails silently. The refresh is
  /// idempotent and free, so it runs every time rather than being guessed at.</para>
  ///
  /// <para>Fails LOUD and NAMED on every path: an unattuned destination and a
  /// refused cast are different sentences, because they need different things from
  /// the player. Returns false without moving him in both cases, and the deck's
  /// walk line stays exactly where it was.</para>
  /// </summary>
  internal static unsafe bool FirePort(PortDestination dest)
  {
    var telepo = Telepo.Instance();
    if (telepo == null)
    {
      Svc.Chat.PrintError("[Scrooge] Can't port - the game's teleport system isn't answering.");
      return false;
    }

    telepo->UpdateAetheryteList();

    var attuned = false;
    foreach (var entry in telepo->TeleportList)
    {
      if (entry.AetheryteId != dest.AetheryteId) continue;
      attuned = true;
      break;
    }

    if (!attuned)
    {
      Svc.Chat.PrintError(
        $"[Scrooge] Can't port - expected {dest.Name} in your teleport list, but it isn't attuned. Walk it this once.");
      return false;
    }

    if (!telepo->Teleport(dest.AetheryteId, 0))
    {
      Svc.Chat.PrintError($"[Scrooge] The teleport to {dest.Name} was refused. Walk it, or try again.");
      return false;
    }

    Svc.Chat.Print($"[Scrooge] Porting to {dest.Name} - the turn-in arms when you reach the counter.");
    return true;
  }
}
