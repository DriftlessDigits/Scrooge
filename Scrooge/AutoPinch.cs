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
  private readonly TaskManager _taskManager;
  private readonly Random _random = new Random();
  internal ItemPricingPipeline Pricing => _pricing;
  private readonly ItemPricingPipeline _pricing;
  private readonly HawkRunOrchestrator _hawkOrchestrator;

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
    var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
    if (listNode == null) return;
    var listComponent = (AtkComponentList*)listNode->Component;
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
        EnqueueSingleRetainer(i);
      }

      _taskManager.Enqueue(RemoveTalkAddonListeners);
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
  private void EnqueueSingleRetainer(int index)
  {
    _taskManager.Enqueue(() => GameNavigation.ClickRetainer(index), $"ClickRetainer{index}");
    _taskManager.DelayNext(100);
    _taskManager.Enqueue(GameNavigation.ClickSellItems, $"ClickSellItems{index}");
    _taskManager.DelayNext(500);

    // Gil tracking: set retainer context and snapshot all listings from the sell list
    if (Plugin.Configuration.EnableGilTracking)
    {
      _taskManager.Enqueue(() => {
        unsafe {
          var rm = RetainerManager.Instance();
          GilTracker.SetRetainer(rm->GetActiveRetainer()->NameString);
        }
        GilTracker.SnapshotListings();
        return true;
      }, $"SnapshotListings{index}");
    }

    _taskManager.Enqueue(() => EnqueueAllRetainerItems(InsertSingleItem, true), $"EnqueueAllRetainerItems{index}");
    _taskManager.DelayNext(500);
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
        unsafe
        {
          var rm = RetainerManager.Instance();
          GilTracker.SetRetainer(rm->GetActiveRetainer()->NameString);
        }
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

  private unsafe void PinchAllRetainerItems()
  {
    if (_taskManager.IsBusy)
      return;

    ClearState();
    Plugin.CurrentRun = new RunData { Mode = RunMode.Pinch };
    Plugin.PinchRunLog.StartNewRun();

    // Get total items from the sell list
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
      var listComponent = (AtkComponentList*)listNode->Component;
      Plugin.PinchRunLog.SetTotalItems(listComponent->ListLength);
    }

    // Set retainer name for log grouping (ClickRetainer doesn't fire for single-retainer runs)
    {
      var rm = RetainerManager.Instance();
      var retainerName = rm->GetActiveRetainer()->NameString;
      Plugin.PinchRunLog.SetCurrentRetainer(retainerName);

      // Gil tracking: start run, set retainer, snapshot
      if (Plugin.Configuration.EnableGilTracking)
      {
        GilTracker.StartRun(retainerName);
        GilTracker.SetRetainer(retainerName);
        _taskManager.Enqueue(() => { GilTracker.SnapshotListings(); return true; }, "SnapshotListings");
      }
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

  internal void StartHawkRun(List<HawkWindow.HawkItem> items) => _hawkOrchestrator.StartHawkRun(items);

  /// <summary>Iterates all items in the current retainer's sell list and queues them for processing.</summary>
  /// <param name="enqueueFunc">Function to queue each item (EnqueueSingleItem or InsertSingleItem).</param>
  /// <param name="reverseOrder">If true, process items bottom-to-top (needed for Insert-based queuing).</param>
  private unsafe bool? EnqueueAllRetainerItems(Action<int> enqueueFunc, bool reverseOrder)
  {
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
    {
      var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
      var listComponent = (AtkComponentList*)listNode->Component;
      int num = listComponent->ListLength;

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
    _taskManager.DelayNext(ApplyJitter(Plugin.Configuration.MarketBoardKeepOpenMS));
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
    _taskManager.InsertDelayNext(ApplyJitter(Plugin.Configuration.MarketBoardKeepOpenMS));
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

