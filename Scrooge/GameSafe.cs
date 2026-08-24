using System;
using System.Collections.Generic;
using ECommons;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Scrooge;

/// <summary>
/// Null-guarded wrappers around ClientStructs singletons and addon node walks.
/// These read native memory — a null anywhere in the chain is an uncatchable
/// access violation (hard game crash), so every read here fails soft instead.
/// Callers decide the fallback: skip the capture, error out, or use a default.
/// </summary>
internal static unsafe class GameSafe
{
  /// <summary>
  /// Player's desynthesis skill for the given DoH class job (standard FFXIV
  /// ids, e.g. 8 = CRP ... 15 = CUL). 0 when PlayerState is unavailable —
  /// callers classify against 0, which reads as Red (skillup) and never
  /// gates anything. Moved from DesynthSkillup so that file stays pure.
  /// </summary>
  internal static int GetDesynthLevel(byte classJobId)
  {
    var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
    if (ps == null) return 0;
    return (int)ps->GetDesynthesisLevel(classJobId);
  }

  /// <summary>Player gil, or null when InventoryManager isn't available (zoning/startup).</summary>
  internal static long? PlayerGil()
  {
    var im = InventoryManager.Instance();
    return im == null ? null : (long)im->GetGil();
  }

  /// <summary>Active retainer's name, or null when no retainer is active or the manager is unavailable.</summary>
  internal static string? ActiveRetainerName()
  {
    var rm = RetainerManager.Instance();
    if (rm == null) return null;
    var retainer = rm->GetActiveRetainer();
    if (retainer == null) return null;
    var name = retainer->NameString;
    return string.IsNullOrEmpty(name) ? null : name;
  }

  /// <summary>
  /// The active retainer's current RetainerTask row id — WHICH venture is out,
  /// so a collected return can stamp its own token cost off the sheet instead of
  /// deferring to a config guess (Drift, 08-15: we don't need a mod knob for a thing
  /// we can directly measure in game).
  ///
  /// <para>Null when the manager or the retainer isn't readable. <b>0 is a real
  /// read, not a failure</b> — it means the retainer has no venture — and callers
  /// distinguish the two: a null is "we couldn't look", a 0 is "we looked and there
  /// is none". Either way the capture still happens; the row just goes unstamped.</para>
  /// </summary>
  internal static uint? ActiveRetainerVentureId()
  {
    var rm = RetainerManager.Instance();
    if (rm == null) return null;
    var retainer = rm->GetActiveRetainer();
    if (retainer == null) return null;
    return retainer->VentureId;
  }

  /// <summary>
  /// Every available retainer's OUTSTANDING venture id, by name - the stamp bank's
  /// feed (live receipt 08-15: every collect logged "unstamped" because the game
  /// clears VentureId before the result dialog's PostSetup ever fires, so even the
  /// arm-time read was too late; the spec's fallback - read BEFORE the dialog, bank
  /// per-retainer - is the only read that works). Zero ids are skipped: a retainer
  /// with no venture out has nothing worth banking, and a transitional zero must
  /// never overwrite a banked real id.
  /// </summary>
  internal static List<(string Name, uint VentureId)> RetainerVentures()
  {
    var list = new List<(string, uint)>();
    var rm = RetainerManager.Instance();
    if (rm == null) return list;
    for (uint i = 0; i < rm->GetRetainerCount(); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      if (retainer == null || !retainer->Available || retainer->VentureId == 0) continue;
      var name = retainer->NameString;
      if (!string.IsNullOrEmpty(name)) list.Add((name, retainer->VentureId));
    }
    return list;
  }

  /// <summary>
  /// Seconds until the SOONEST venture completes across the player's retainers -
  /// the return clock the fit check races the pinch estimate against
  /// (<see cref="FitCheck"/>). Null when no venture clock is readable (manager
  /// unavailable, or no retainer has a venture out); 0 when a venture is already
  /// complete (the haul is waiting).
  ///
  /// Source is ClientStructs RetainerManager (<c>VentureComplete</c>, a unix
  /// timestamp), NOT the RetainerList addon's "Complete in Xm" string. The addon
  /// text is the only pre-existing venture read documented for the roster, but it
  /// is a localized string that must be regex-parsed and is only present while the
  /// roster addon is open; the struct carries the raw timestamp on every retainer
  /// whether or not any window is open. The struct read is the honest clock.
  /// </summary>
  internal static long? SoonestVentureReturnSeconds()
  {
    var rm = RetainerManager.Instance();
    if (rm == null) return null;

    long? soonest = null;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    for (uint i = 0; i < rm->GetRetainerCount(); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      if (retainer == null || !retainer->Available) continue;
      if (retainer->VentureId == 0) continue; // no venture assigned - nothing to wait on

      var complete = (long)retainer->VentureComplete; // unix seconds
      if (complete <= 0) continue;

      var remaining = complete - now;
      var secs = remaining > 0 ? remaining : 0; // already done reads as "waiting" (0)
      if (soonest is null || secs < soonest) soonest = secs;
    }
    return soonest;
  }

  /// <summary>Per-retainer (name, gil) balances; empty when the manager is unavailable.</summary>
  internal static List<(string Name, long Gil)> RetainerBalances()
  {
    var balances = new List<(string, long)>();
    var rm = RetainerManager.Instance();
    if (rm == null) return balances;

    for (uint i = 0; i < rm->GetRetainerCount(); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      if (retainer == null) continue;
      var name = retainer->NameString;
      if (!string.IsNullOrEmpty(name))
        balances.Add((name, retainer->Gil));
    }

    return balances;
  }

  /// <summary>
  /// Checks if a listing belongs to one of the player's own retainers.
  /// Used to avoid undercutting yourself when UndercutSelf is disabled.
  /// </summary>
  internal static bool IsOwnRetainer(ulong retainerId)
  {
    var rm = RetainerManager.Instance();
    if (rm == null) return false;

    for (uint i = 0; i < rm->GetRetainerCount(); i++)
    {
      var retainer = rm->GetRetainerBySortedIndex(i);
      if (retainer != null && retainer->RetainerId == retainerId)
        return true;
    }

    return false;
  }

  /// <summary>
  /// Venture token item id. VERIFY in-game (flagged in the venture-returns
  /// design doc) — believed 21072; a wrong id reads as 0 stock, which the
  /// rules engine must treat as "unknown", never "panic mode".
  /// </summary>
  private const uint VentureTokenItemId = 21072;

  /// <summary>
  /// Venture token count, or null when the inventory is unavailable.
  /// GetInventoryItemCount resolves containers itself — the previous manual
  /// Currency-container walk read 0 against a real stock of 1300 (shake-out
  /// finding 8; ventures don't live where that walk looked). 0 stays
  /// ambiguous for callers: prefer "no tilt" over "hard override" on 0.
  /// </summary>
  internal static int? VentureTokenCount()
  {
    var im = InventoryManager.Instance();
    if (im == null) return null;

    return im->GetInventoryItemCount(VentureTokenItemId);
  }

  /// <summary>
  /// Bag count of an arbitrary item, or null when the inventory is unavailable
  /// (zoning, login, teardown). The null matters here: <see cref="CofferPullWatcher"/>
  /// polls this every tick and treats null as "no baseline", never as zero - a count
  /// that reads unavailable mid-zone must not look like a box being opened.
  /// </summary>
  internal static int? InventoryItemCount(uint itemId)
  {
    var im = InventoryManager.Instance();
    if (im == null) return null;

    return im->GetInventoryItemCount(itemId);
  }

  /// <summary>
  /// Visible sell-list row index of the first listing matching (itemId, isHq)
  /// on the OPEN retainer, plus its stack quantity - or null when the item is
  /// no longer listed or the addon isn't ready. Reads the RetainerSellList
  /// addon's own rows (count at AtkValues[9], base 10, stride 13 - the same
  /// map SnapshotListings uses), so the index is the DISPLAY index the row
  /// click callback expects. The RetainerMarket container stores slots in a
  /// different order than the sell list displays (proven 2026-07-12,
  /// finding #16) - never target rows from the container.
  /// </summary>
  internal static (int RowIndex, int Quantity)? SellListRow(uint itemId, bool isHq)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return null;

    var itemCount = addon->AtkValues[9].Int;
    for (int i = 0; i < itemCount; i++)
    {
      var baseIdx = 10 + (i * 13);
      var iconId = addon->AtkValues[baseIdx].Int;
      var itemName = addon->AtkValues[baseIdx + 1].GetValueAsString();
      var payload = Communicator.RawItemNameToItemPayload(itemName);
      if (payload == null || payload.ItemId != itemId) continue;

      var rowHq = iconId >= 1_000_000;
      if (rowHq != isHq) continue;

      var quantity = addon->AtkValues[baseIdx + 2].Int;
      return (i, quantity);
    }
    return null;
  }

  /// <summary>
  /// Row count of the RetainerSellList's list component, or null when the addon
  /// isn't open/ready or the node walk (NodeList[10] → list component) fails.
  /// </summary>
  internal static int? RetainerSellListLength()
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) || !GenericHelpers.IsAddonReady(addon))
      return null;
    if (addon->UldManager.NodeListCount <= 10) return null;

    var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
    if (listNode == null) return null;
    var listComponent = (AtkComponentList*)listNode->Component;
    if (listComponent == null) return null;

    return listComponent->ListLength;
  }

  /// <summary>
  /// The player's GC seal wallet: current, max for their rank, and the GC id.
  /// Null when PlayerState/InventoryManager are unavailable or the player
  /// has no Grand Company.
  /// </summary>
  internal static (uint Current, uint Max, byte GcId)? CompanySeals()
  {
    var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
    if (ps == null) return null;
    var gc = ps->GrandCompany;
    if (gc == 0) return null;

    var im = InventoryManager.Instance();
    if (im == null) return null;

    return (im->GetCompanySeals(gc), im->GetMaxCompanySeals(gc), gc);
  }

  // ==========================================================================
  // The world (2026-07-26): reaching for the bell
  // ==========================================================================

  /// <summary>
  /// The EObjName sheet row for the retainer bell (2000401), which is what makes the
  /// name match LOCALIZED rather than English-only: the sheet is served in the client's
  /// own language, so a DE/FR/JP/CN/KR install reads its own literal without a table
  /// here. Matched on NAME rather than on a data id list because the bells are many
  /// objects, not one - every city plaza, inn, housing plot and apartment lobby carries
  /// its own, and an id list would be a maintenance debt that silently stops working in
  /// a zone nobody tested.
  ///
  /// <para><b>Provenance:</b> the row id and the JP fallback literal below are
  /// AutoRetainer's <c>Lang.BellName</c>. Reference, not integration - Scrooge does not
  /// talk to AutoRetainer (standing rule); it simply stops guessing at semantics that
  /// plugin has field-proven across its userbase.</para>
  /// </summary>
  private const uint SummoningBellEObjNameRow = 2000401;

  /// <summary>The JP literal AutoRetainer carries alongside the sheet read, kept for the same reason.</summary>
  private const string SummoningBellNameJp = "リテイナーベル";

  /// <summary>
  /// The bell's name in the client's language, resolved once. A sheet miss falls back
  /// to the EN literal rather than matching nothing - a degraded reach is still better
  /// than a reach that silently never fires.
  /// </summary>
  private static string? _bellName;

  private static string BellName => _bellName ??= ReadBellName();

  private static string ReadBellName()
  {
    try
    {
      if (ECommons.DalamudServices.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.EObjName>()
            .TryGetRow(SummoningBellEObjNameRow, out var row))
      {
        var name = row.Singular.ToString();
        if (!string.IsNullOrWhiteSpace(name)) return name;
      }
    }
    catch { /* sheet unavailable - fall through to the literal */ }
    return "Summoning Bell";
  }

  /// <summary>
  /// A bell the plugin can see, and how far away it measured - the object, the raw
  /// centre-to-centre distance, and the reach the game will actually honour there.
  /// </summary>
  internal readonly record struct BellSighting(
    Dalamud.Game.ClientState.Objects.Types.IGameObject Object, float Yalms, float ValidRange)
  {
    /// <summary>The distance said the way a receipt says it: one decimal, "3.2y".</summary>
    internal string Text => $"{(Yalms < 0f ? 0f : Yalms):0.0}y";

    /// <summary>Close enough that the game will accept the interact.</summary>
    internal bool InRange => Yalms < ValidRange;

    /// <summary>The reach this bell was measured against, for the decline log.</summary>
    internal string RangeText => $"{ValidRange:0.0}y";
  }

  /// <summary>
  /// The nearest Summoning Bell to the player at ANY distance, or null when there is
  /// none in the object table (or the player/table is unreadable).
  ///
  /// <para><b>The semantics are AutoRetainer's, not ours</b> (its
  /// <c>GetReachableRetainerBell</c> / <c>GetValidInteractionDistance</c> - reference
  /// prior art, never an integration). Three of our guesses were wrong and are
  /// corrected here:</para>
  /// <list type="bullet">
  ///   <item>Distance is RAW centre-to-centre, with NO hitbox subtraction. We had been
  ///     subtracting <c>HitboxRadius</c> on the theory that the game measures to the
  ///     hitbox; AR's field-proven check does not, and it is the one of us with a
  ///     userbase's worth of evidence.</item>
  ///   <item>The valid range is 4.6y normally and 6.5y for a HousingEventObject. (AR
  ///     also carries 4.75y for inns off a territory list; 4.6 everywhere non-housing
  ///     is marginally conservative there, and one less list to keep alive.)</item>
  ///   <item>Housing bells are <c>HousingEventObject</c>, not <c>EventObj</c> - we were
  ///     missing every bell inside a house or apartment outright.</item>
  /// </list>
  ///
  /// <para><c>IsTargetable</c> is required too: an object the game will not let you
  /// target is one the interact would bounce off.</para>
  ///
  /// <para><b>Unbounded on purpose.</b> The range test belongs to the caller, so every
  /// reach can narrate the distance it acted on and every DECLINE can log what it stood
  /// off at - which turns a live round into free calibration for a constant nobody in
  /// this stack has actually measured.</para>
  /// </summary>
  internal static BellSighting? NearestSummoningBell()
  {
    if (!ECommons.GameHelpers.Player.Available
        || ECommons.GameHelpers.Player.Object is not { } me)
      return null;

    var name = BellName;
    BellSighting? best = null;
    var bestDistance = float.MaxValue;

    foreach (var obj in ECommons.DalamudServices.Svc.Objects)
    {
      if (obj is null || !obj.IsValid()) continue;
      if (obj.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj
          or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.HousingEventObject)) continue;
      if (!obj.IsTargetable) continue;
      var objName = obj.Name.TextValue;
      if (!string.Equals(objName, name, StringComparison.OrdinalIgnoreCase)
          && !string.Equals(objName, SummoningBellNameJp, StringComparison.OrdinalIgnoreCase)) continue;

      var distance = System.Numerics.Vector3.Distance(me.Position, obj.Position);
      if (distance >= bestDistance) continue;
      best = new BellSighting(obj, distance, BellReach.ValidRangeFor(
        obj.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.HousingEventObject));
      bestDistance = distance;
    }

    return best;
  }

  /// <summary>The nearest bell, but only if the game would honour an interact with it.</summary>
  internal static BellSighting? NearestReachableSummoningBell()
    => NearestSummoningBell() is { InRange: true } s ? s : null;

  /// <summary>
  /// Targets a world object and interacts with it ONCE - the click the player would
  /// make. Returns false when the target system is unreadable, which callers treat as
  /// "the reach did not happen" (the cooldown still burns, so a broken read cannot
  /// become a spin).
  ///
  /// <para>This is the plugin's first write into the world rather than into a window,
  /// which is why it lives here: GameSafe is the designated seam for native touches,
  /// and every one of them null-guards its way down the chain instead of trusting a
  /// singleton to exist.</para>
  /// </summary>
  internal static bool InteractWith(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
  {
    var targets = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
    if (targets == null) return false;

    var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
    if (native == null) return false;

    // Target first, then interact: the game's own flow, and it leaves the bell under
    // the player's cursor so a failed interact is obvious rather than invisible.
    targets->Target = native;
    targets->InteractWithObject(native, false);
    return true;
  }
}
