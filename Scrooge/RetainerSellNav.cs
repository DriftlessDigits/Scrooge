using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// THE ROAD TO A RETAINER'S SELL VIEW - the one definition, now that two runs walk
/// it.
///
/// <para>The hawk owned this leg alone until Rounds unit 2, and recon needs it
/// verbatim: the spine expectations that decide fire-vs-navigate-vs-refuse, the
/// bell's two-addon Place reading, and the roster hop to the first retainer with
/// open sell slots. Recon consumes no slots and lists nothing, so it might look
/// like it should skip the with-space test - it must not. Verified in game
/// 2026-08-10: a retainer whose sell list is 20/20 REFUSES to open the sell panel
/// at all ("You cannot put any more items up for sale at this time"), so the game
/// gates the PANEL, not the posting. Recon needs exactly one free slot, and because
/// it never fills it, that one slot serves the entire pass - which is why recon has
/// no mid-run swap leg and the hawk does.</para>
///
/// <para>Extracted rather than parameterised into the hawk. The two runs share this
/// leg and nothing else: past the sell view they differ on every act (vendor
/// routing, slot accounting, retainer swaps, the standing book, the completion
/// contract), and a mode flag threaded through those would have made one class
/// bimodal in six places to save duplicating none of them. What IS identical is
/// this road, so this road is what moved. The only thing either caller may vary is
/// the sentence said when every list is full, because a hawk that cannot list and a
/// recon that cannot look owe the player different reasons.</para>
/// </summary>
internal static class RetainerSellNav
{
  /// <summary>
  /// The sell-view run's declared expected state (spine), in report order:
  /// <list type="bullet">
  ///   <item><b>View</b> - the retainer sell view. If it is closed but the
  ///     player is at the bell roster, the advisor SELF-NAVIGATES (the Hawk
  ///     Wares hop) - rung 1, the model the whole ladder is named after.</item>
  ///   <item><b>Place</b> - a retainer bell, evidenced by EITHER the bell roster
  ///     (RetainerList) OR an engaged retainer's sell view (RetainerSellList),
  ///     since the game closes the roster once a retainer is summoned. If the
  ///     player is not even at a bell there is nothing to navigate from, so this
  ///     WalkThere gap wins over the self-navigable one and the run refuses,
  ///     naming the walk.</item>
  /// </list>
  /// Truth table: sell view open -> Fire (View met, Place met via the sell view);
  /// roster open but sell view closed -> SelfNavigate (View unmet, Place met via
  /// the roster); neither -> WalkThere refusal naming the walk.
  /// </summary>
  internal static readonly ExpectedState ListExpected = new("list",
    new SpineExpectation(Spine.Facet.View, "the retainer sell view open", Spine.Rung.SelfNavigate),
    new SpineExpectation(Spine.Facet.Place, "to be at a retainer bell", Spine.Rung.WalkThere));

  /// <summary>The addons that evidence "you are standing at a bell" - see <see cref="ReadPlace"/>.</summary>
  private static readonly string[] BellAddons = { "RetainerList", "RetainerSellList" };

  /// <summary>
  /// The Place reading on its own, so the grace window can re-ask exactly the
  /// question that failed rather than a re-derived approximation of it.
  ///
  /// <para>Place is evidenced by EITHER the bell roster (RetainerList, idle) OR a
  /// summoned retainer's sell view (RetainerSellList) - the game closes the roster
  /// once a retainer is engaged, so requiring RetainerList alone would read "not at
  /// a bell" from inside the very sell view the run fires from. What it still cannot
  /// see is the moment BETWEEN them (Talk/SelectString mid-summon), which is what
  /// the grace window is for.</para>
  /// </summary>
  internal static FacetReading ReadPlace()
    => SpineSensors.AnyAddonReady(BellAddons, "you're not at a retainer bell");

  /// <summary>The full reading <see cref="ListExpected"/> is evaluated against.</summary>
  internal static List<FacetReading> ReadListState() => new()
  {
    SpineSensors.AddonReady("RetainerSellList", "the sell view isn't open"),
    ReadPlace(),
  };

  /// <summary>
  /// From the bell roster (RetainerList), navigate to the first retainer with open
  /// sell slots and open their "Sell items in your inventory" view, then invoke
  /// <paramref name="onArrived"/> with the total open slots across all retainers.
  /// Returns false WITH a chat error when it cannot - every refusal is loud.
  ///
  /// <para><paramref name="allFullMessage"/> is the caller's own words for the
  /// 20/20 wall, printed verbatim. It is the one sentence the two runs cannot
  /// share: the hawk is telling the player his shelves are full, recon is telling
  /// him the game will not let it read a board without a shelf to stand on.</para>
  /// </summary>
  internal static unsafe bool EnqueueToSellView(
    TaskManager taskManager,
    IAddonLifecycle.AddonEventDelegate skipRetainerDialog,
    Action removeTalkListeners,
    string allFullMessage,
    Action<int> onArrived)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
    {
      Svc.Chat.PrintError("[Scrooge] Retainer list isn't open (or still loading) - summon your retainers and retry.");
      return false;
    }

    var retainerList = new AddonMaster.RetainerList(addon);
    var retainers = retainerList.Retainers;

    // Count total available slots across all retainers, find first with space
    int targetIndex = -1;
    int totalAvailableSlots = 0;
    for (int i = 0; i < retainers.Length; i++)
    {
      var count = GameNavigation.GetRetainerListingCount(addon, i);
      totalAvailableSlots += (20 - count);
      if (targetIndex < 0 && count < 20)
        targetIndex = i;
    }
    // The launch strip's capacity advisory reads this observation (gate 9b) -
    // banked here because the roster is already in hand, never fetched for it.
    FleetCapacity.Observe(totalAvailableSlots, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    if (targetIndex < 0)
    {
      Svc.Chat.PrintError(allFullMessage);
      return false;
    }

    // Auto-dismiss retainer greeting dialog
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", skipRetainerDialog);
    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", skipRetainerDialog);

    // Navigate: click retainer -> sell view -> hand off to the caller
    taskManager.Enqueue(() => GameNavigation.ClickRetainer(targetIndex), "SellViewClickRetainer");
    taskManager.DelayNext(100);
    taskManager.Enqueue(GameNavigation.ClickSellItems, "SellViewClickSellItems");
    taskManager.DelayNext(500);
    taskManager.Enqueue(() => {
      removeTalkListeners();
      onArrived(totalAvailableSlots);
      return true;
    }, "SellViewArrived");
    return true;
  }

  /// <summary>
  /// THE ROAD BACK OUT. The close-up chain both sell-view runs end on, enqueued once:
  /// shut the sell list, shut the retainer, drop the Talk listeners, then the caller's
  /// own terminal task behind them.
  ///
  /// <para><b>Why the terminal task is enqueued LAST and not just called</b> (review
  /// ruling S18, 2026-08-12): it has to run after the close-up steps, and a death that
  /// lands WHILE they run does not unqueue it - the straggler arrives a moment later
  /// and would report the run DONE over a run the round had already halted, which puts
  /// a spurious run-end in the transcript and tells the cursor a stage finished that
  /// never did. Both callers' terminal bodies open on their lifecycle's own answer
  /// (<c>if (!_run.Complete(now)) return true;</c>) - the lifecycle already refuses to
  /// leave a terminal state, so its answer IS the latch and no second flag has to be
  /// kept in step with it. This comment used to be written twice.</para>
  /// </summary>
  /// <param name="onTerminal">
  /// The run's own end-of-run task, returning true when it is finished with the queue
  /// (the S18 latch's early-out returns true as well - the straggler is done, it just
  /// says nothing).
  /// </param>
  internal static void EnqueueSellViewCloseUp(
    TaskManager taskManager,
    Action removeTalkListeners,
    Func<bool> onTerminal)
  {
    taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "SellViewCloseSellList");
    taskManager.DelayNext(100);
    taskManager.Enqueue(GameNavigation.CloseRetainer, "SellViewCloseRetainer");
    taskManager.Enqueue(() => { removeTalkListeners(); return true; });
    taskManager.Enqueue(() => onTerminal(), "SellViewRunEnd");
  }

  /// <summary>The hawk's words for the 20/20 wall: it came to list and there is no shelf.</summary>
  internal const string HawkAllFull = "[Scrooge] All retainers have full sell lists (20/20).";

  /// <summary>
  /// Recon's words for the same wall, which is a different fact for it. Recon posts
  /// nothing, so a full fleet costs it no capacity at all - what it costs is the
  /// PANEL, which the game refuses to open at 20/20 (verified in game 2026-08-10).
  /// The refusal has to say that, or it reads as the advisor confusing recon with a
  /// listing run and the player goes looking for space he does not need.
  /// </summary>
  internal const string ReconAllFull =
    "[Scrooge] Recon needs one open sell slot - every sell list is 20/20. "
    + "It posts nothing; the game just won't open the sell panel on a full retainer.";
}
