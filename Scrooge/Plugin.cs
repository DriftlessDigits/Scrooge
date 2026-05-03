using System;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Inventory;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Scrooge.Windows;

namespace Scrooge;

/// <summary>
/// Plugin entry point. Dalamud instantiates this class and injects services
/// via [PluginService]. Registers the /dagobert command and sets up the UI.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
  // Dalamud injects these via [PluginService] attribute — available globally as Plugin.ServiceName
  [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
  [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
  [PluginService] public static IMarketBoard MarketBoard { get; private set; } = null!;
  [PluginService] public static IKeyState KeyState { get; private set; } = null!;
  [PluginService] public static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
  [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
  [PluginService] public static IContextMenu ContextMenu { get; private set; } = null!;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
  public static Configuration Configuration { get; private set; } // will never be null
  public static DalamudLinkPayload ConfigLinkPayload { get; private set; } = null!;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

  internal static AutoPinch AutoPinch { get; private set; } = null!;

  /// <summary>Active run data. Null when no run is in progress.</summary>
  internal static RunData? CurrentRun { get; set; }

  internal static PinchRunLogWindow PinchRunLog { get; private set; } = null!;

  internal static GilWindow GilDashboard { get; private set; } = null!;

  internal static HawkWindow HawkWindow { get; private set; } = null!;

  internal static TriageWindow TriageWindow { get; private set; } = null!;

  internal static TriageOrchestrator TriageOrchestrator { get; private set; } = null!;

  internal static DesynthPreviewWindow DesynthPreview { get; private set; } = null!;

  private RetainerHistoryHook? _retainerHistoryHook;
  private GilTrackEventListener? _gilTrackListener;
  private ExchangeTracker? _exchangeTracker;
  private SpecialExchangeTracker? _specialExchangeTracker;
  private ChatCatchallTracker? _chatCatchallTracker;

  public readonly WindowSystem WindowSystem = new("Scrooge");
  private ConfigWindow ConfigWindow { get; init; }

  public Plugin()
  {
    Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
    ConfigWindow = new ConfigWindow();
    WindowSystem.AddWindow(ConfigWindow);

    CommandManager.AddHandler("/scrooge", new CommandInfo(OnScroogeCommand)
    {
      HelpMessage = "Opens the Scrooge configuration window"
    });

    // Register chat link handler for clickable config link
    ConfigLinkPayload = ChatGui.AddChatLinkHandler(0, (id, _) => ToggleConfigUI());

    PluginInterface.UiBuilder.Draw += DrawUI;
    PluginInterface.UiBuilder.OpenMainUi += ToggleConfigUI;
    PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

    ECommonsMain.Init(PluginInterface, this);
    AutoPinch = new AutoPinch();
    WindowSystem.AddWindow(AutoPinch);

    PinchRunLog = new PinchRunLogWindow();
    WindowSystem.AddWindow(PinchRunLog);

    // Gil tracking
    try
    {
      GilStorage.Initialize();
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, "[GilTrack] Failed to initialize database — gil tracking disabled");
    }

    try
    {
      _retainerHistoryHook = new RetainerHistoryHook();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "RetainerHistory hook failed — gil tracking will not capture sale history");
    }

    try
    {
      _gilTrackListener = new GilTrackEventListener();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "GilTrackEventListener failed — passive snapshots disabled");
    }

    try
    {
      _exchangeTracker = new ExchangeTracker();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "ExchangeTracker failed — exchange-addon gil tracking disabled");
    }

    try
    {
      _specialExchangeTracker = new SpecialExchangeTracker();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "SpecialExchangeTracker failed — custom delivery / wondrous tails gil tracking disabled");
    }

    // Instantiate last so its chat subscription runs after the specific
    // parsers in GilTrackEventListener — the listener's NotifyHandled()
    // calls need to fire before the catch-all's debounce is scheduled.
    try
    {
      _chatCatchallTracker = new ChatCatchallTracker();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "ChatCatchallTracker failed — catch-all gil tracking disabled");
    }

    GilDashboard = new GilWindow();
    WindowSystem.AddWindow(GilDashboard);

    HawkWindow = new HawkWindow();
    WindowSystem.AddWindow(HawkWindow);

    TriageWindow = new TriageWindow();
    WindowSystem.AddWindow(TriageWindow);

    TriageOrchestrator = new TriageOrchestrator();

    DesynthPreview = new DesynthPreviewWindow();
    WindowSystem.AddWindow(DesynthPreview);

    ContextMenu.OnMenuOpened += OnContextMenuOpened;

    CommandManager.AddHandler("/giltrack", new CommandInfo(OnGilTrackCommand)
    {
      HelpMessage = "Opens the Scrooge gil dashboard"
    });

    CommandManager.AddHandler("/desynthpreview", new CommandInfo(OnDesynthPreviewCommand)
    {
      HelpMessage = "DEBUG: opens the Scrooge desynth preview window"
    });
  }

  public void Dispose()
  {
    ContextMenu.OnMenuOpened -= OnContextMenuOpened;
    TriageOrchestrator.Dispose();
    WindowSystem.RemoveAllWindows();
    AutoPinch.Dispose();
    CommandManager.RemoveHandler("/scrooge");
    _chatCatchallTracker?.Dispose();
    _specialExchangeTracker?.Dispose();
    _exchangeTracker?.Dispose();
    _gilTrackListener?.Dispose();
    _retainerHistoryHook?.Dispose();
    GilStorage.Dispose();
    CommandManager.RemoveHandler("/giltrack");
    CommandManager.RemoveHandler("/desynthpreview");
    ECommonsMain.Dispose();
  }

  private void OnScroogeCommand(string command, string args)
  {
    // in response to the slash command, just toggle the display status of our main ui
    ToggleConfigUI();
  }

  private void OnGilTrackCommand(string command, string args) => GilDashboard.Toggle();

  private void OnDesynthPreviewCommand(string command, string args)
  {
    DesynthPreview.OpenAndScan();
  }

  /// <summary>
  /// Adds Scrooge context menu options:
  /// - Retainer sell list: Ban/Unban (available any time)
  /// - Inventory items: Full Hawk menu (when HawkWindow is open)
  /// </summary>
  private unsafe void OnContextMenuOpened(IMenuOpenedArgs args)
  {
    // --- Retainer Sell List: Ban/Unban (always available) ---
    // Retainer sell list context menus fire as ContextMenuType.Default, not Inventory.
    // SelectedItemIndex returns -1 on this addon, so we use GameGui.HoveredItem instead.
    if (args.MenuType == ContextMenuType.Default
        && GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var sellListAddon)
        && GenericHelpers.IsAddonReady(sellListAddon))
    {
      var hoveredItem = Svc.GameGui.HoveredItem;
      if (hoveredItem == 0)
        return;

      var isHq = hoveredItem >= 1_000_000;
      var sellListItemId = isHq ? (uint)(hoveredItem - 1_000_000) : (uint)hoveredItem;
      var banId = isHq ? sellListItemId + 1_000_000 : sellListItemId;

      var isBanned = Configuration.BannedItemIds.Contains(banId);
      if (isBanned)
      {
        args.AddMenuItem(new MenuItem
        {
          Name = "Remove Scrooge Ban",
          PrefixChar = 'S',
          PrefixColor = 539,
          OnClicked = _ =>
          {
            Configuration.BannedItemIds.Remove(banId);
            Configuration.Save();
          },
        });
      }
      else
      {
        args.AddMenuItem(new MenuItem
        {
          Name = "Ban from Scrooge",
          PrefixChar = 'S',
          PrefixColor = 17, // red
          OnClicked = _ =>
          {
            Configuration.BannedItemIds.Add(banId);
            Configuration.AlwaysVendorItemIds.Remove(banId); // mutual exclusivity
            Configuration.Save();
          },
        });
      }
      return;
    }

    if (args.MenuType != ContextMenuType.Inventory)
      return;

    if (args.Target is not MenuTargetInventory target || target.TargetItem == null)
      return;

    var item = target.TargetItem.Value;
    var itemId = item.ItemId;
    // NOTE: Do NOT strip HQ offset. itemId includes +1M for HQ items,
    // allowing HQ and NQ to be banned/always-vendored independently.
    if (itemId == 0)
      return;

    // (Retainer sell list handled above for ContextMenuType.Default)
    if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out _))
    {
      var isBanned = Configuration.BannedItemIds.Contains(itemId);
      if (isBanned)
      {
        args.AddMenuItem(new MenuItem
        {
          Name = "Remove Scrooge Ban",
          PrefixChar = 'S',
          PrefixColor = 539,
          OnClicked = _ =>
          {
            Configuration.BannedItemIds.Remove(itemId);
            Configuration.Save();
          },
        });
      }
      else
      {
        args.AddMenuItem(new MenuItem
        {
          Name = "Ban from Scrooge",
          PrefixChar = 'S',
          PrefixColor = 17, // red
          OnClicked = _ =>
          {
            Configuration.BannedItemIds.Add(itemId);
            Configuration.AlwaysVendorItemIds.Remove(itemId); // mutual exclusivity
            Configuration.Save();
          },
        });
      }
      return;
    }

    // --- Hawk Window: Full inventory context menu ---
    if (!HawkWindow.IsOpen)
      return;

    // Base ID for Lumina lookups (vendor price, etc.) and HawkWindow selection
    var baseItemId = item.IsHq && itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    var isBannedHawk = Configuration.BannedItemIds.Contains(itemId);
    var isAlwaysVendor = Configuration.AlwaysVendorItemIds.Contains(itemId);
    var isSelected = HawkWindow.IsItemSelected(baseItemId, item.IsHq);

    // --- State: Banned ---
    if (isBannedHawk)
    {
      args.AddMenuItem(new MenuItem
      {
        Name = "Remove Scrooge Ban",
        PrefixChar = 'S',
        PrefixColor = 539,
        OnClicked = _ =>
        {
          Configuration.BannedItemIds.Remove(itemId);
          Configuration.Save();
          HawkWindow.RefreshInventory();
        },
      });
      return;
    }

    // --- State: Always Vendor ---
    if (isAlwaysVendor)
    {
      args.AddMenuItem(new MenuItem
      {
        Name = "Remove Always Vendor",
        PrefixChar = 'S',
        PrefixColor = 539,
        OnClicked = _ =>
        {
          Configuration.AlwaysVendorItemIds.Remove(itemId);
          Configuration.Save();
          HawkWindow.RefreshInventory();
        },
      });
      return;
    }

    // --- State: Normal (not banned, not always-vendor) ---

    // Select / Deselect
    if (isSelected)
    {
      args.AddMenuItem(new MenuItem
      {
        Name = "Remove from Sale",
        PrefixChar = 'S',
        PrefixColor = 539,
        OnClicked = _ => HawkWindow.SetItemSelected(baseItemId, item.IsHq, false),
      });
    }
    else
    {
      args.AddMenuItem(new MenuItem
      {
        Name = "Select for Sale",
        PrefixChar = 'S',
        PrefixColor = 45, // green
        OnClicked = _ => HawkWindow.SetItemSelected(baseItemId, item.IsHq, true),
      });
    }

    // Always Vendor — only show for vendorable items
    var vendorPrice = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(baseItemId).PriceLow;
    if (vendorPrice > 0)
    {
      args.AddMenuItem(new MenuItem
      {
        Name = "Always Vendor",
        PrefixChar = 'S',
        PrefixColor = 520, // orange/yellow — verified in-game
        OnClicked = _ =>
        {
          Configuration.AlwaysVendorItemIds.Add(itemId);
          Configuration.BannedItemIds.Remove(itemId); // mutual exclusivity
          Configuration.Save();
          HawkWindow.RefreshInventory();
        },
      });
    }

    // Ban from Scrooge (red)
    args.AddMenuItem(new MenuItem
    {
      Name = "Ban from Scrooge",
      PrefixChar = 'S',
      PrefixColor = 17, // red
      OnClicked = _ =>
      {
        Configuration.BannedItemIds.Add(itemId);
        Configuration.AlwaysVendorItemIds.Remove(itemId); // mutual exclusivity
        Configuration.Save();
        HawkWindow.RefreshInventory();
      },
    });
  }

  private void DrawUI()
  {
    WindowSystem.Draw();
  }

  public void ToggleConfigUI() => ConfigWindow.Toggle();
}