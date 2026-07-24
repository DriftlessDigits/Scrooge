using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility;
using Dalamud.Utility;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using Scrooge.Windows;

namespace Scrooge;

/// <summary>
/// Core automation engine. Extends Window to overlay an "Auto Pinch" button
/// on the retainer list and sell list UIs. Uses ECommons TaskManager to queue
/// sequential actions (open menu → click adjust → compare prices → set price).
///
/// Flow: PinchAllRetainers/PinchAllRetainerItems → enqueue tasks per item →
/// each item goes through: OpenContextMenu → AdjustPrice → ComparePrice → SetNewPrice
/// </summary>
internal sealed class AutoPinch : Window, IDisposable
{
  /// <summary>
  /// The pinch's declared expected state (spine): the bell roster (RetainerList)
  /// must be open. Unmet is a WalkThere gap - the sweep deck is what names the
  /// walk. The bell-roster pinch keeps its historical SILENT no-op when it is
  /// not at the roster (this routes the same check through the one evaluator and
  /// gives the executor a declared contract; it does not add a chat line).
  /// </summary>
  internal static readonly ExpectedState PinchExpected = new("pinch",
    new SpineExpectation(Spine.Facet.Place, "to be at a retainer bell", Spine.Rung.WalkThere));

  private static unsafe List<FacetReading> ReadPinchState() => new()
  {
    SpineSensors.AddonReady("RetainerList", "you're not at a retainer bell"),
  };

  private readonly TaskManager _taskManager;
  private readonly Random _random = new Random();
  internal ItemPricingPipeline Pricing => _pricing;
  private readonly ItemPricingPipeline _pricing;
  private readonly HawkRunOrchestrator _hawkOrchestrator;

  // --- Vendor rider (WALK unit 3): the unanimous Pull & Vendor rows that ride
  //     THIS pinch, grouped by the retainer whose visit drains them. Snapshotted
  //     at pinch start and woven into each retainer's own task chain - never a
  //     separate errand. Recomputed rows, not queued state: an abort just leaves
  //     the un-drained ones in the pile (a row clears only on a completed vendor). ---
  private Dictionary<string, List<PricingItem>> _riderByRetainer = new(StringComparer.Ordinal);
  private IDisposable? _riderCatchall;
  private bool _riderListenerActive;

  public AutoPinch()
    : base("Scrooge", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.AlwaysAutoResize, true)
  {

    // window
    Position = new System.Numerics.Vector2(0, 0);
    IsOpen = true;
    ShowCloseButton = false;
    RespectCloseHotkey = false;
    DisableWindowSounds = true;
    SizeConstraints = new WindowSizeConstraints()
    {
      MaximumSize = new System.Numerics.Vector2(0, 0),
    };

    _taskManager = new TaskManager
    {
      TimeLimitMS = 10000,   // per-task timeout (individual steps, not the whole run)
      AbortOnTimeout = true
    };
    _pricing = new ItemPricingPipeline(_taskManager, ApplyJitter);
    _hawkOrchestrator = new HawkRunOrchestrator(
      _taskManager, _pricing, ApplyJitter, SkipRetainerDialog, RemoveTalkAddonListeners);
    // TTS is Windows-only; detect at startup and disable gracefully on other platforms
    try
    {
      var tts = new SpeechSynthesizer();
      tts.SelectVoice(tts.Voice.Name);
      Plugin.Configuration.DontUseTTS = false;
      Plugin.Configuration.Save();
    }
    catch
    {
      Plugin.Configuration.DontUseTTS = true;
      Plugin.Configuration.Save();
    }

    Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, RetainerSellPostSetup);
  }

  public void Dispose()
  {
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, RetainerSellPostSetup);
    _pricing.Dispose();
  }

  public override void Draw()
  {
    try
    {
      DrawForRetainerList();
      DrawForRetainerSellList();
      DrawInventoryOverlays();
    }
    catch (Exception ex)
    {
      _taskManager.Abort();
      Svc.Log.Error(ex, "Error while auto pinching");
      if (Plugin.Configuration.ShowErrorsInChat)
        Svc.Chat.PrintError($"Error while auto pinching: {ex.Message}");

      Plugin.PinchRunLog.CancelRun();
      _hawkOrchestrator.Abort();
      RemoveTalkAddonListeners();
      RiderCleanup();
    }
  }

  /// <summary>Draws the Auto Pinch button on the retainer list (all retainers view).</summary>
  private void DrawForRetainerList()
  {
    unsafe
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        // Hotkey support: start pinching if the configured key is held
        if (Plugin.Configuration.EnablePinchKey && Plugin.KeyState[Plugin.Configuration.PinchKey])
          PinchAllRetainers();

        var node = addon->UldManager.NodeList[27]; // anchor node for button positioning

        if (node == null)
          return;

        // Each overlay is wrapped in try/finally so an exception inside the
        // button draw can never orphan ImGui style pushes — a leak here
        // affects every other Dalamud window in the process.
        var position = AutoPinchOverlay.GetNodePosition(node);
        var scale = AutoPinchOverlay.GetNodeScale(node);

        // Auto Pinch button — anchored to node, stays in original position
        var oldSize = AutoPinchOverlay.ImGuiSetup(node);
        try { DrawAutoPinchButton(PinchAllRetainers); }
        finally { AutoPinchOverlay.ImGuiPostSetup(oldSize); }

        // Hawk Wares button — separate overlay, positioned to the left
        var hawkPos = new Vector2(position.X - 90f * scale.X, position.Y);
        var hawkOldSize = AutoPinchOverlay.ImGuiSetup(node, "###HawkWares", hawkPos);
        try { DrawHawkButton(); }
        finally { AutoPinchOverlay.ImGuiPostSetup(hawkOldSize); }

        // Tally Sales button — further left, reconciles pending chat-captured sales
        var tallyPos = new Vector2(position.X - 180f * scale.X, position.Y);
        var tallyOldSize = AutoPinchOverlay.ImGuiSetup(node, "###TallySales", tallyPos);
        try { DrawTallySalesButton(); }
        finally { AutoPinchOverlay.ImGuiPostSetup(tallyOldSize); }
      }
    }
  }

  /// <summary>Draws the Auto Pinch button on a single retainer's sell list.</summary>
  private void DrawForRetainerSellList()
  {
    unsafe
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        if (Plugin.Configuration.EnablePinchKey && Plugin.KeyState[Plugin.Configuration.PinchKey])
          PinchAllRetainerItems();

        var node = addon->UldManager.NodeList[17]; // anchor node for button positioning

        if (node == null)
          return;

        var oldSize = AutoPinchOverlay.ImGuiSetup(node);
        DrawAutoPinchButton(PinchAllRetainerItems);
        AutoPinchOverlay.ImGuiPostSetup(oldSize);

        // Draw ban indicators on sell list rows
        if (Plugin.Configuration.BannedItemIds.Count > 0)
          DrawSellListBanIndicators(addon);
      }
    }
  }

  /// <summary>
  /// Draws a red overlay stripe on RetainerSellList rows for banned items.
  /// Reads item IDs from AtkValues (stride 13) and matches against BannedItemIds.
  /// </summary>
  private unsafe void DrawSellListBanIndicators(AtkUnitBase* addon)
  {
    if (addon->UldManager.NodeListCount <= 10) return;
    var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
    if (listNode == null) return;
    var listComponent = (AtkComponentList*)listNode->Component;
    if (listComponent == null) return;
    var listLength = listComponent->ListLength;
    if (listLength == 0) return;

    // Match by icon ID — container slot order doesn't match UI display order.
    // Build a set of banned icon IDs from Lumina, then check each row's AtkValue icon.
    var itemSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
    var bannedIcons = new HashSet<int>();
    foreach (var id in Plugin.Configuration.BannedItemIds)
    {
      var baseId = id >= 1_000_000 ? id - 1_000_000 : id;
      var baseIcon = (int)itemSheet.GetRow(baseId).Icon;
      bannedIcons.Add(baseIcon);           // NQ
      bannedIcons.Add(baseIcon + 1_000_000); // HQ
    }

    if (bannedIcons.Count == 0) return;

    var drawList = ImGui.GetBackgroundDrawList();
    var banColor = ImGui.GetColorU32(new System.Numerics.Vector4(1f, 0.2f, 0.2f, 0.25f));

    for (var i = 0; i < listLength; i++)
    {
      // Icon is at AtkValues stride 13, offset 10
      var atkIdx = 10 + (i * 13);
      if (atkIdx >= addon->AtkValuesCount) break;
      var iconId = addon->AtkValues[atkIdx].Int;

      if (!bannedIcons.Contains(iconId))
        continue;

      // Get the icon node within this list row (first component node = icon)
      var renderer = listComponent->GetItemRenderer(i);
      if (renderer == null) continue;
      var ownerNode = renderer->OwnerNode;
      if (ownerNode == null || !ownerNode->AtkResNode.IsVisible()) continue;

      // Node [12] (type 1008, 44x48) is the item icon component on the left
      if (renderer->UldManager.NodeListCount <= 12) continue;
      var iconNode = renderer->UldManager.NodeList[12];
      if (iconNode == null || !iconNode->IsVisible()) continue;

      var position = AutoPinchOverlay.GetNodePosition(iconNode);
      var scale = AutoPinchOverlay.GetNodeScale(iconNode);
      var size = new System.Numerics.Vector2(iconNode->Width * scale.X, iconNode->Height * scale.Y);

      drawList.AddRectFilled(
        new System.Numerics.Vector2(position.X, position.Y),
        new System.Numerics.Vector2(position.X + size.X, position.Y + size.Y),
        banColor);
    }
  }

  /// <summary>Draws inventory overlays when the Hawk Window is open.</summary>
  private unsafe void DrawInventoryOverlays()
  {
    if (Plugin.HawkWindow == null || !Plugin.HawkWindow.IsOpen)
      return;

    // Don't draw overlays while a context menu is open (z-order conflict)
    unsafe
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var cm) && GenericHelpers.IsAddonReady(cm))
        return;
    }

    // Always Vendor items — orange/yellow veil
    var vendorIcons = Plugin.HawkWindow.GetAlwaysVendorIconIds();
    if (vendorIcons.Count > 0)
      AutoPinchOverlay.DrawIconOverlays(vendorIcons, new System.Numerics.Vector4(1f, 0.7f, 0.2f, 0.35f));

    // Selected items — green veil
    var selectedIcons = Plugin.HawkWindow.GetSelectedIconIds();
    if (selectedIcons.Count > 0)
      AutoPinchOverlay.DrawIconOverlays(selectedIcons, new System.Numerics.Vector4(0.2f, 1f, 0.2f, 0.35f));

    // Banned items — red veil (both NQ and HQ variants)
    if (Plugin.Configuration.BannedItemIds.Count > 0)
    {
      var itemSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
      var bannedIcons = new HashSet<int>();
      foreach (var itemId in Plugin.Configuration.BannedItemIds)
      {
        var baseIcon = (int)itemSheet.GetRow(itemId).Icon;
        bannedIcons.Add(baseIcon);           // NQ
        bannedIcons.Add(baseIcon + 1000000);  // HQ
      }

      if (bannedIcons.Count > 0)
        AutoPinchOverlay.DrawIconOverlays(bannedIcons, new System.Numerics.Vector4(1f, 0.2f, 0.2f, 0.35f));
    }
  }

  /// <summary>Draws Auto Pinch / Cancel button. Shows Cancel when busy.</summary>
  /// <param name="specificPinchFunction">The pinch function to call (all retainers or single retainer's items).</param>
  private void DrawAutoPinchButton(Action specificPinchFunction)
  {
    if (_taskManager.IsBusy)
    {
      if (ImGui.Button("Cancel"))
      {
        _taskManager.Abort();
        RemoveTalkAddonListeners();
        RiderCleanup();
        Plugin.PinchRunLog.CancelRun();
        Plugin.CurrentRun = null;
      }
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("Cancels the auto pinching process");
        ImGui.EndTooltip();
      }
    }
    else
    {
      if (ImGui.Button("Auto Pinch"))
        specificPinchFunction();
      if (ImGui.IsItemHovered())
      {
        ImGui.BeginTooltip();
        ImGui.SetTooltip("Starts auto pinching\r\n" +
                         "Please do not interact with the game while this process is running");
        ImGui.EndTooltip();
      }
    }
  }

  /// <summary>Draws the Hawk Run button. Disabled when task manager is busy.</summary>
  private unsafe void DrawHawkButton()
  {
    ImGui.BeginDisabled(_taskManager.IsBusy);
    if (ImGui.Button("Hawk Wares"))
      _hawkOrchestrator.OpenHawkView();
    ImGui.EndDisabled();
    if (ImGui.IsItemHovered())
      ImGui.SetTooltip("List new items from your inventory");
  }

  /// <summary>
  /// Draws the Tally Sales button. Walks each enabled retainer to open their
  /// Sale History — fires the RetainerHistoryHook so chat-captured pending
  /// rows reconcile with authoritative server data. Also refreshes listings
  /// snapshots while we're at each retainer. Disabled when task manager is
  /// busy or there are no pending sales to reconcile.
  /// </summary>
  private unsafe void DrawTallySalesButton()
  {
    var pendingCount = GilStorage.GetPendingSaleCount();
    ImGui.BeginDisabled(_taskManager.IsBusy || pendingCount == 0);
    // Size-match "Hawk Wares" so the three overlays (Tally/Hawk/Auto) space uniformly;
    // "Tally Sales" renders narrower by default and leaves a gap at the 90px stride.
    var buttonWidth = ImGui.CalcTextSize("Hawk Wares").X + ImGui.GetStyle().FramePadding.X * 2;
    if (ImGui.Button("Tally Sales", new Vector2(buttonWidth, 0)))
      TallySalesAllRetainers();
    ImGui.EndDisabled();
    if (ImGui.IsItemHovered())
    {
      if (pendingCount == 0)
        ImGui.SetTooltip("No pending sales to reconcile.");
      else
        ImGui.SetTooltip($"Visit each retainer's Sale History to reconcile {pendingCount} pending sale(s).");
    }
  }

  /// <summary>
  /// Entry point for "pinch all retainers": iterates every enabled retainer,
  /// opens their sell list, and queues price adjustments for all their items.
  /// Registers Talk dialog listeners to auto-dismiss retainer greeting dialogs.
  /// </summary>
  private unsafe void PinchAllRetainers()
  {
    if (_taskManager.IsBusy)
      return;

    // Spine gate: the pinch expects the bell roster. Routed through the one
    // evaluator so the contract is declared and honest; the body's addon fetch
    // below is now just the pointer read (the same condition the spine checked).
    if (!SpineEvaluator.Evaluate(PinchExpected, ReadPinchState()).CanFire)
      return;

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      // Auto-dismiss the "Talk" dialog that appears when opening each retainer
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);

      // we cache the number of retainers because AddonMaster will be disposed once the RetainerList addon is closed.
      var retainerList = new AddonMaster.RetainerList(addon);
      var retainers = retainerList.Retainers;
      var num = retainers.Length;
      
      // Check if all are disabled (sentinel present)
      bool allDisabled = Plugin.Configuration.EnabledRetainerNames.Contains(Configuration.ALL_DISABLED_SENTINEL);
      
      // If all are disabled, skip all retainers and notify user
      if (allDisabled)
      {
        Communicator.PrintAllRetainersDisabled();
        return;
      }

      ClearState();
      Plugin.CurrentRun = new RunData { Mode = RunMode.Pinch };
      Plugin.PinchRunLog.StartNewRun();
      if (Plugin.Configuration.EnableGilTracking)
        GilTracker.StartRun();

      // Vendor rider (WALK unit 3): snapshot the unanimous Pull & Vendor rows now,
      // grouped by retainer, and drain each retainer's set inside its own visit
      // below. The buyback-dismiss dialog only appears once we've actually
      // vendored, so its listener is armed only when rows are riding.
      SnapshotVendorRiders();
      if (_riderByRetainer.Count > 0)
      {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
        _riderListenerActive = true;
      }

      // If no retainers are explicitly enabled, enable all by default
      bool allEnabled = Plugin.Configuration.EnabledRetainerNames.Count == 0;

      // Pre-calculate total items from RetainerList addon AtkValues
      // Layout: base offset 3, 10 values per retainer, offset 6 = "Selling X items" text
      // See: RetainerList Addon - AtkValue Map.md
      int preRunTotal = 0;
      for (int i = 0; i < num; i++)
      {
        var retainerName = retainers[i].Name;
        if (!allEnabled && !Plugin.Configuration.EnabledRetainerNames.Contains(retainerName))
          continue;

        preRunTotal += GameNavigation.GetRetainerListingCount(addon, i);
      }
      Plugin.PinchRunLog.SetTotalItems(preRunTotal);

      for (int i = 0; i < num; i++)
      {
        var retainerName = retainers[i].Name;
        
        // Skip retainers that are excluded in configuration
        if (!allEnabled && !Plugin.Configuration.EnabledRetainerNames.Contains(retainerName))
        {
          Svc.Log.Debug($"Skipping retainer '{retainerName}' (excluded by user configuration)");
          continue;
        }
        EnqueueSingleRetainer(i, retainerName);
      }

      _taskManager.Enqueue(RemoveTalkAddonListeners);
      _taskManager.Enqueue(() => { RiderCleanup(); return true; }, "RiderCleanup");
      if (Plugin.Configuration.TTSWhenAllDone)
        _taskManager.Enqueue(() => SpeakTTS(Plugin.Configuration.TTSWhenAllDoneMsg), "SpeakTTSAll");

      _taskManager.Enqueue(() => {
        Plugin.PinchRunLog.EndRun();
        Plugin.CurrentRun = null;
        if (Plugin.Configuration.EnableGilTracking)
          GilTracker.FinalizeRun();
        Util.FlashWindow();
        return true;
      }, "EndRunLog");
    }
  }

  /// <summary>
  /// Queues the full sequence for one retainer: click retainer → open sell list →
  /// process all items → close sell list → close retainer.
  /// </summary>
  /// <param name="index">Retainer index in the RetainerList addon (0-based).</param>
  /// <param name="retainerName">The retainer's name - the key its vendor riders were grouped under.</param>
  private void EnqueueSingleRetainer(int index, string retainerName)
  {
    _taskManager.Enqueue(() => GameNavigation.ClickRetainer(index), $"ClickRetainer{index}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(GameNavigation.ClickSellItems, $"ClickSellItems{index}");
    _taskManager.DelayNext(500);

    // Gil tracking: set retainer context and snapshot all listings from the sell list
    if (Plugin.Configuration.EnableGilTracking)
    {
      _taskManager.Enqueue(() => {
        var name = GameSafe.ActiveRetainerName();
        if (name == null)
        {
          Svc.Log.Warning("[GilTrack] Couldn't read active retainer — skipping listing snapshot");
          return true;
        }
        GilTracker.SetRetainer(name);
        GilTracker.SnapshotListings();
        return true;
      }, $"SnapshotListings{index}");
    }

    _taskManager.Enqueue(() => EnqueueAllRetainerItems(InsertSingleItem, true), $"EnqueueAllRetainerItems{index}");
    _taskManager.DelayNext(500);

    // Vendor rider: with the sell list still open and the reprice pass done, pull
    // and vendor this retainer's unanimous Pull & Vendor rows (WALK unit 3). Same
    // window, same retainer - no separate errand. Dispatched (not pre-built)
    // because the reprice pass inserts its steps at runtime; the dispatch inserts
    // the rider's steps ahead of the close below, so they run while the list is up.
    _taskManager.Enqueue(() => { RiderDispatch(retainerName); return true; }, $"RiderDispatch{index}");

    _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, $"CloseRetainerSellList{index}");
    _taskManager.DelayNext(100);

    // Gil tracking: view sale history to capture sales via hook
    if (Plugin.Configuration.EnableGilTracking)
    {
      _taskManager.Enqueue(GameNavigation.ClickSaleHistory, $"ClickSaleHistory{index}");
      _taskManager.DelayNext(1500); // wait for server response + hook to fire
      _taskManager.Enqueue(GameNavigation.CloseSaleHistory, $"CloseSaleHistory{index}");
      _taskManager.DelayNext(100);
    }

    _taskManager.Enqueue(GameNavigation.CloseRetainer, $"CloseRetainer{index}");
    _taskManager.DelayNext(100);
  }

  // ==========================================================================
  // Vendor rider (WALK unit 3): Pull & Vendor unanimous rows drained inside the
  // pinch's own retainer visit. The selection (which rows ride, per-retainer) is
  // the pure VendorRider core; this is the execution - the same pull-then-vendor
  // steps the manual Pull & Vendor executor runs, now woven into the pinch chain,
  // reporting into the pinch run log and stamping the routing receipt exactly as
  // the Hawk vendor path does (so assent-clears-dissent registers the act).
  // ==========================================================================

  /// <summary>
  /// Snapshots the unanimous Pull &amp; Vendor rows the rider will drain this pinch,
  /// grouped by retainer. Silent (empty map) when the rider is disabled or nothing
  /// qualifies. Marks each riding row's queued action so the per-item steps below
  /// have the Skipped() contract they share with the manual triage executor.
  /// </summary>
  private void SnapshotVendorRiders()
  {
    _riderByRetainer.Clear();
    if (!Plugin.Configuration.PinchVendorRider) return;

    var candidates = Plugin.Ledger.PullVendorRiderCandidates();
    foreach (var item in VendorRider.Riders(candidates))
      item.QueuedAction = TriageAction.Vendor;
    _riderByRetainer = VendorRider.ByRetainer(candidates, i => i.RetainerName);
  }

  /// <summary>
  /// Inserts this retainer's rider steps ahead of the queued sell-list close, so
  /// they run while the list is still open (Insert prepends - build back-to-front:
  /// last item first, each item's steps reversed, so execution is forward order).
  /// A no-op when nothing rides here.
  /// </summary>
  private void RiderDispatch(string retainerName)
  {
    if (!Plugin.Configuration.PinchVendorRider) return;
    if (!_riderByRetainer.TryGetValue(retainerName, out var riders) || riders.Count == 0) return;

    for (int r = riders.Count - 1; r >= 0; r--)
      InsertRiderItem(riders[r]);
  }

  /// <summary>
  /// The per-item pull-then-vendor chain, inserted in reverse. Mirrors the manual
  /// triage executor's vendor path: re-resolve the row by id at the open sell list
  /// (a no-longer-listed row marks itself skipped and the chain no-ops), pull it to
  /// inventory, wait for it to land, then vendor it. Bookkeeping is pinch-flavored:
  /// the run log, not a triage summary, and the routing-receipt stamp the pull path
  /// otherwise omits.
  /// </summary>
  private void InsertRiderItem(PricingItem item)
  {
    _taskManager.Insert(() => { _riderCatchall?.Dispose(); _riderCatchall = null; return true; },
      $"RiderUnblock_{item.ItemName}");
    _taskManager.Insert(() => { if (!RiderSkipped(item)) RiderBookSale(item); return true; },
      $"RiderTrack_{item.ItemName}");
    _taskManager.InsertDelayNext(500);
    _taskManager.Insert(() => RiderSkipped(item) ? true : RiderFailClosed(item, GameNavigation.ClickVendorSellItem()),
      $"RiderVendor_{item.ItemName}");
    _taskManager.Insert(() => { if (!RiderSkipped(item)) { _riderCatchall?.Dispose(); _riderCatchall = GilTrackingState.Block("pinch_vendor_rider"); } return true; },
      $"RiderBlock_{item.ItemName}");
    _taskManager.InsertDelayNext(500);

    // Wait for the pulled item to reach the bags (server round-trip, variable
    // latency); a hard deadline marks the ITEM skipped so one slow arrival costs
    // one item, never the whole pinch. The item stays in the bags either way.
    var arrivalDeadline = DateTime.MinValue;
    _taskManager.Insert(() =>
    {
      if (RiderSkipped(item)) return true;
      if (arrivalDeadline == DateTime.MinValue)
        arrivalDeadline = DateTime.UtcNow.AddMilliseconds(5000);
      if (GameNavigation.TryClickInventoryItemById(item.ItemId, item.IsHq))
        return true;
      if (DateTime.UtcNow < arrivalDeadline)
        return false; // not in bags yet - retry
      Svc.Chat.Print($"[Scrooge] {item.ItemName} never arrived in bags — left unvendored (check inventory).");
      item.QueuedAction = TriageAction.None;
      return true;
    }, $"RiderClickInv_{item.ItemName}");
    _taskManager.InsertDelayNext(1000);
    _taskManager.Insert(() => RiderSkipped(item) ? true : RiderFailClosed(item, GameNavigation.ClickReturnToInventory()),
      $"RiderReturn_{item.ItemName}");
    _taskManager.InsertDelayNext(500);
    _taskManager.Insert(() => RiderOpenRow(item), $"RiderOpenRow_{item.ItemName}");
  }

  /// <summary>An item skipped mid-chain (no longer listed, or a missing menu entry) - later steps no-op.</summary>
  private static bool RiderSkipped(PricingItem item)
    => TriageMemory.ItemSkipped(item.QueuedAction, item.Result);

  /// <summary>
  /// Translates a context-menu clicker's tri-state for the TaskManager: a null
  /// (entry missing - fail closed) becomes a per-item skip, not a queue abort.
  /// One bad menu costs one item, never the pinch.
  /// </summary>
  private static bool? RiderFailClosed(PricingItem item, bool? clickResult)
  {
    if (clickResult != null) return clickResult;
    Svc.Chat.Print($"[Scrooge] {item.ItemName} — expected menu entry missing; item skipped (see /xllog).");
    item.QueuedAction = TriageAction.None;
    return true;
  }

  /// <summary>
  /// Opens the sell-list context menu for the rider's row, re-resolving it by
  /// (item id, HQ) against the DISPLAYED rows - recorded slot indexes go stale the
  /// moment anything sells. A row that is no longer listed marks itself skipped and
  /// closes its ledger row; the chain moves on.
  /// </summary>
  private unsafe bool? RiderOpenRow(PricingItem item)
  {
    if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
        || !GenericHelpers.IsAddonReady(addon))
      return false; // sell list not ready yet - retry

    if (GameSafe.SellListRow(item.ItemId, item.IsHq) is not { } row)
    {
      Svc.Chat.Print($"[Scrooge] {item.ItemName} is no longer listed on {item.RetainerName} — skipped (likely sold).");
      item.QueuedAction = TriageAction.None;
      Plugin.Ledger.RemoveItem(item); // row is moot - close its flags
      return true;
    }

    item.Quantity = row.Quantity; // live stack size (flag rows start unknown)
    return GameNavigation.OpenItemContextMenu(row.RowIndex);
  }

  /// <summary>
  /// Books a completed rider vendor sale: the pinch run log (its end-of-run summary
  /// then reports the vendored count and gil), the gil ledger, and the routing
  /// receipt stamp the Hawk vendor path writes - assent-clears-dissent (v2.17) keys
  /// off exactly this executed, unoverridden act. The pull evicted the listing
  /// without a market sale, so its forecast receipts close never-cleared, same as
  /// the manual pull path. Finally retires the ledger row.
  /// </summary>
  private void RiderBookSale(PricingItem item)
  {
    var vendorPrice = (int)Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(item.ItemId).PriceLow;
    var qty = item.Quantity > 0 ? item.Quantity : 1;
    var totalGil = vendorPrice * qty;

    Plugin.PinchRunLog.AddVendorSale(totalGil);
    Plugin.PinchRunLog.AddEntry(ItemOutcome.VendorSold, item.ItemName,
      $"Pull & Vendor — {totalGil:N0} gil ({vendorPrice:N0} × {qty})");
    Communicator.PrintVendorSold(item.ItemName, vendorPrice, qty);

    if (Plugin.Configuration.EnableGilTracking)
    {
      var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
      GilStorage.InsertTransaction(now, "earned", "vendor_sale", totalGil,
        item.ItemId, item.ItemName, GilTracker.GetItemCategory(item.ItemId),
        qty, vendorPrice, item.IsHq, "", "NPC Vendor");
    }

    // Stamp the standing routing receipt - the item's Vendor exit executed (the
    // same stamp the Hawk vendor path writes; not a fifth unstamped path).
    try { GilStorage.MarkRoutingReceiptExecuted(item.ItemId, item.IsHq, "Vendored"); } catch { }
    // Pull evicted the listing without an MB sale - its forecast never got tested.
    if (Plugin.Configuration.EnableGilTracking && item.ItemId != 0)
      try { GilStorage.CloseReceiptsNeverCleared(item.ItemId, item.IsHq); } catch { }

    Plugin.Ledger.RemoveItem(item);
  }

  /// <summary>
  /// Auto-confirms the "unable to process item buyback requests" dialog that
  /// appears when dismissing a retainer after the rider vendor-sold items - the
  /// same guard the manual triage executor arms. Ignores unexpected dialogs.
  /// </summary>
  private unsafe void AutoConfirmVendorDismiss(AddonEvent type, AddonArgs args)
  {
    var addon = new AddonMaster.SelectYesno(args.Addon);
    if (addon.Text.Contains("unable to process item buyback requests"))
    {
      Svc.Log.Debug("[Rider] Auto-confirming vendor dismiss dialog");
      addon.Yes();
    }
  }

  /// <summary>Tears down the rider's per-run state: the buyback listener and any live catchall block.</summary>
  private void RiderCleanup()
  {
    if (_riderListenerActive)
    {
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "SelectYesno", AutoConfirmVendorDismiss);
      _riderListenerActive = false;
    }
    _riderCatchall?.Dispose();
    _riderCatchall = null;
    _riderByRetainer.Clear();
  }

  /// <summary>
  /// Entry point for "tally sales": walks every enabled retainer, opening their
  /// Sale History to fire the RetainerHistoryHook and reconcile chat-captured
  /// pending rows. Also refreshes listing snapshots. No pricing work — this is
  /// the gil-tracking subset of a pinch run, with generous delays so it doesn't
  /// look automated and tolerates lag.
  /// </summary>
  private unsafe void TallySalesAllRetainers()
  {
    if (_taskManager.IsBusy)
      return;

    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);

      var retainerList = new AddonMaster.RetainerList(addon);
      var retainers = retainerList.Retainers;
      var num = retainers.Length;

      bool allDisabled = Plugin.Configuration.EnabledRetainerNames.Contains(Configuration.ALL_DISABLED_SENTINEL);
      if (allDisabled)
      {
        Communicator.PrintAllRetainersDisabled();
        return;
      }

      bool allEnabled = Plugin.Configuration.EnabledRetainerNames.Count == 0;

      if (Plugin.Configuration.EnableGilTracking)
        GilTracker.StartRun();

      for (int i = 0; i < num; i++)
      {
        var retainerName = retainers[i].Name;
        if (!allEnabled && !Plugin.Configuration.EnabledRetainerNames.Contains(retainerName))
        {
          Svc.Log.Debug($"Skipping retainer '{retainerName}' for tally (excluded by user configuration)");
          continue;
        }
        EnqueueSingleRetainerTally(i);
      }

      _taskManager.Enqueue(RemoveTalkAddonListeners);
      _taskManager.Enqueue(() =>
      {
        if (Plugin.Configuration.EnableGilTracking)
          GilTracker.FinalizeRun();
        Util.FlashWindow();
        return true;
      }, "EndTallyRun");
    }
  }

  /// <summary>
  /// Queues the tally sequence for one retainer: click retainer → open sell list
  /// → snapshot listings → close sell list → view sale history → close history →
  /// close retainer. Delays are intentionally generous (6s+ per retainer) to
  /// tolerate server lag and avoid looking suspiciously automated.
  /// </summary>
  private void EnqueueSingleRetainerTally(int index)
  {
    _taskManager.Enqueue(() => GameNavigation.ClickRetainer(index), $"TallyClickRetainer{index}");
    _taskManager.DelayNext(800);
    _taskManager.Enqueue(GameNavigation.ClickSellItems, $"TallyClickSellItems{index}");
    _taskManager.DelayNext(800);

    if (Plugin.Configuration.EnableGilTracking)
    {
      _taskManager.Enqueue(() =>
      {
        var name = GameSafe.ActiveRetainerName();
        if (name == null)
        {
          Svc.Log.Warning("[GilTrack] Couldn't read active retainer — skipping listing snapshot");
          return true;
        }
        GilTracker.SetRetainer(name);
        GilTracker.SnapshotListings();
        return true;
      }, $"TallySnapshotListings{index}");
      _taskManager.DelayNext(500);
    }

    _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, $"TallyCloseSellList{index}");
    _taskManager.DelayNext(600);
    _taskManager.Enqueue(GameNavigation.ClickSaleHistory, $"TallyClickSaleHistory{index}");
    _taskManager.DelayNext(2000); // give hook + server response room
    _taskManager.Enqueue(GameNavigation.CloseSaleHistory, $"TallyCloseSaleHistory{index}");
    _taskManager.DelayNext(600);
    _taskManager.Enqueue(GameNavigation.CloseRetainer, $"TallyCloseRetainer{index}");
    _taskManager.DelayNext(1000);
  }

  private void PinchAllRetainerItems()
  {
    if (_taskManager.IsBusy)
      return;

    // Read before mutating any run state — fail closed if the retainer
    // can't be resolved (avoids a half-started run).
    var retainerName = GameSafe.ActiveRetainerName();
    if (retainerName == null)
    {
      Svc.Chat.PrintError("[Scrooge] Couldn't read the active retainer — try reopening the sell list.");
      return;
    }

    ClearState();
    Plugin.CurrentRun = new RunData { Mode = RunMode.Pinch };
    Plugin.PinchRunLog.StartNewRun();

    // Get total items from the sell list
    if (GameSafe.RetainerSellListLength() is int totalItems)
      Plugin.PinchRunLog.SetTotalItems(totalItems);

    // Set retainer name for log grouping (ClickRetainer doesn't fire for single-retainer runs)
    Plugin.PinchRunLog.SetCurrentRetainer(retainerName);

    // Gil tracking: start run, set retainer, snapshot
    if (Plugin.Configuration.EnableGilTracking)
    {
      GilTracker.StartRun(retainerName);
      GilTracker.SetRetainer(retainerName);
      _taskManager.Enqueue(() => { GilTracker.SnapshotListings(); return true; }, "SnapshotListings");
    }

    EnqueueAllRetainerItems(EnqueueSingleItem, false);

    // Gil tracking: close sell list → view sale history → reopen sell list
    if (Plugin.Configuration.EnableGilTracking)
    {
      _taskManager.Enqueue(GameNavigation.CloseRetainerSellList, "GilTrack_CloseSellList");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.ClickSaleHistory, "GilTrack_ClickSaleHistory");
      _taskManager.DelayNext(1500);
      _taskManager.Enqueue(GameNavigation.CloseSaleHistory, "GilTrack_CloseSaleHistory");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(GameNavigation.ClickSellItems, "GilTrack_ReopenSellList");
      _taskManager.DelayNext(100);
    }

    _taskManager.Enqueue(() => {
      Plugin.PinchRunLog.EndRun();
      Plugin.CurrentRun = null;
      if (Plugin.Configuration.EnableGilTracking)
        GilTracker.FinalizeRun();
      Util.FlashWindow();
      return true;
    }, "EndRunLog");

  }

  /// <summary>Whether the pinch task chain is mid-flight (sweep deck's busy gate).</summary>
  internal bool PinchBusy => _taskManager.IsBusy;

  /// <summary>Whether a Hawk run is mid-flight (sweep deck's busy gate).</summary>
  internal bool HawkRunning => _hawkOrchestrator.IsRunning;

  /// <summary>
  /// Sweep-deck entry to "pinch all retainers". Self-guarding: a no-op unless
  /// the RetainerList addon is open and ready and no task chain is busy - the
  /// same preconditions the overlay button enforces by only existing there.
  /// </summary>
  internal void StartPinchAllRetainers() => PinchAllRetainers();

  internal void NavigateAndStartHawkRun(List<HawkWindow.HawkItem> items) => _hawkOrchestrator.NavigateAndStartHawkRun(items);

  /// <summary>Iterates all items in the current retainer's sell list and queues them for processing.</summary>
  /// <param name="enqueueFunc">Function to queue each item (EnqueueSingleItem or InsertSingleItem).</param>
  /// <param name="reverseOrder">If true, process items bottom-to-top (needed for Insert-based queuing).</param>
  private bool? EnqueueAllRetainerItems(Action<int> enqueueFunc, bool reverseOrder)
  {
    if (GameSafe.RetainerSellListLength() is int num)
    {
      if (reverseOrder)
      {
        for (int i = num - 1; i >= 0; i--)
        {
          enqueueFunc(i);
        }
      }
      else
      {
        for (int i = 0; i < num; i++)
        {
          enqueueFunc(i);
        }
      }
      if (Plugin.Configuration.TTSWhenEachDone)
        _taskManager.Enqueue(() => SpeakTTS(Plugin.Configuration.TTSWhenEachDoneMsg), "SpeakTTSEach");

      return true;
    }
    else
      return false;
  }

  /// <summary>Queues the price adjustment steps for a single item (forward order).</summary>
  /// <param name="index">Item index in the RetainerSellList addon (0-based).</param>
  private void EnqueueSingleItem(int index)
  {
    _taskManager.Enqueue(() => { if (Plugin.CurrentRun != null) Plugin.CurrentRun.CurrentItem = new PricingItem { SlotIndex = index }; return true; }, $"InitItem{index}");
    _taskManager.Enqueue(() => GameNavigation.OpenItemContextMenu(index), $"OpenItemContextMenu{index}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(_pricing.ClickAdjustPrice, $"ClickAdjustPrice{index}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(_pricing.DelayMarketBoard, $"DelayMB{index}");
    _taskManager.Enqueue(_pricing.ClickComparePrice, $"ClickComparePrice{index}");
    // Poll for the MB response across escalating windows (initial keep-open +
    // 3 retries), re-firing the request between them, before SetNewPrice holds
    // on a genuine no-response. Each window early-outs the instant data lands.
    for (int w = 0; w <= ItemPricingPipeline.MbLastWindow; w++)
    {
      int window = w;
      _taskManager.Enqueue(() => _pricing.AwaitMarketBoardWindow(window), $"AwaitMB{index}_{window}");
    }
    _taskManager.Enqueue(_pricing.SetNewPrice, $"SetNewPrice{index}");
  }

  /// <summary>
  /// Same as EnqueueSingleItem but uses Insert (prepend) instead of Enqueue (append).
  /// Steps are added in reverse order because Insert pushes to the front of the queue.
  /// Used when processing items within the PinchAllRetainers flow.
  /// </summary>
  /// <param name="index">Item index in the RetainerSellList addon (0-based).</param>
  private void InsertSingleItem(int index)
  {
    _taskManager.Insert(_pricing.SetNewPrice, $"SetNewPrice{index}");
    // Insert prepends, so add the await windows in reverse: they execute
    // ClickComparePrice -> AwaitMB 0..3 -> SetNewPrice (see EnqueueSingleItem).
    for (int w = ItemPricingPipeline.MbLastWindow; w >= 0; w--)
    {
      int window = w;
      _taskManager.Insert(() => _pricing.AwaitMarketBoardWindow(window), $"AwaitMB{index}_{window}");
    }
    _taskManager.Insert(_pricing.ClickComparePrice, $"ClickComparePrice{index}");
    _taskManager.Insert(_pricing.DelayMarketBoard, $"DelayMB{index}");
    _taskManager.InsertDelayNext(100);
    _taskManager.Insert(_pricing.ClickAdjustPrice, $"ClickAdjustPrice{index}");
    _taskManager.InsertDelayNext(100);
    _taskManager.Insert(() => GameNavigation.OpenItemContextMenu(index), $"OpenItemContextMenu{index}");
    _taskManager.Insert(() => { if (Plugin.CurrentRun != null) Plugin.CurrentRun.CurrentItem = new PricingItem { SlotIndex = index }; return true; }, $"InitItem{index}");
  }


  private unsafe void SkipRetainerDialog(AddonEvent type, AddonArgs args)
  {
    // fallback for when something was improperly cleaned up
    if (!_taskManager.IsBusy)
      RemoveTalkAddonListeners();
    else
    {
      if (((AtkUnitBase*)args.Addon.Address)->IsVisible)
        new AddonMaster.Talk(args.Addon).Click();
    }
  }

  /// <summary>
  /// Triggered when posting a new item to the MB. If the post-pinch hotkey
  /// is held, automatically fetches the lowest price and undercuts it.
  /// </summary>
  private void RetainerSellPostSetup(AddonEvent type, AddonArgs args)
  {
    if (_taskManager.IsBusy)
      return;

    if (Plugin.Configuration.EnablePostPinchkey && Plugin.KeyState[Plugin.Configuration.PostPinchKey])
    {
      _taskManager.Enqueue(_pricing.ClickComparePrice, $"ClickComparePricePosted");
      _taskManager.DelayNext(ApplyJitter(Plugin.Configuration.MarketBoardKeepOpenMS));
      _taskManager.Enqueue(_pricing.SetNewPrice, $"SetNewPricePosted");
    }
  }
  private void RemoveTalkAddonListeners()
  {
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
    Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);
  }



  /// <summary>Speaks a message using Windows TTS. Disposes the synthesizer after playback.</summary>
  /// <param name="msg">The text to speak.</param>
  private static bool? SpeakTTS(string msg)
  {
    if (!Plugin.Configuration.DontUseTTS)
    {
      SpeechSynthesizer tts = new()
      {
        Volume = Plugin.Configuration.TTSVolume
      };
      tts.SpeakAsync(msg);
      tts.SpeakCompleted += (o, e) =>
      {
        tts.Dispose();
        Svc.Log.Verbose($"Finished message: {msg} - tts disposed");
      };
    }
    return true;
  }

  /// <summary>
  /// Clears the cached price lookup table. Called when price floor settings
  /// change so that affected items are re-queried from the market board.
  /// </summary>
  public void ClearCachedPrices() => _pricing.ClearCachedPrices();

  private void ClearState()
  {
    _pricing.ClearState();
  }

  private int ApplyJitter(int baseMS)
  {
    // Check if jitter is enabled
    if (!Plugin.Configuration.EnableJitter)
      return baseMS;

    var jitterMS = Plugin.Configuration.JitterMS;

    // Guard against weird
    if (jitterMS <= 0) 
      return baseMS;

    // Calculate offset
    var offset = (int)(((_random.NextDouble() * 2.0) - 1.0) * jitterMS);

    return Math.Max(1000, baseMS + offset);
  }
}

