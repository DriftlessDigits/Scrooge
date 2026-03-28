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

  internal static PinchRunLogWindow PinchRunLog { get; private set; } = null!;

  internal static GilWindow GilDashboard { get; private set; } = null!;

  internal static HawkWindow HawkWindow { get; private set; } = null!;

  private RetainerHistoryHook? _retainerHistoryHook;

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

    GilDashboard = new GilWindow();
    WindowSystem.AddWindow(GilDashboard);

    HawkWindow = new HawkWindow();
    WindowSystem.AddWindow(HawkWindow);

    ContextMenu.OnMenuOpened += OnContextMenuOpened;

    CommandManager.AddHandler("/giltrack", new CommandInfo(OnGilTrackCommand)
    {
      HelpMessage = "Opens the Scrooge gil dashboard"
    });
  }

  public void Dispose()
  {
    ContextMenu.OnMenuOpened -= OnContextMenuOpened;
    WindowSystem.RemoveAllWindows();
    AutoPinch.Dispose();
    CommandManager.RemoveHandler("/scrooge");
    _retainerHistoryHook?.Dispose();
    GilStorage.Dispose();
    CommandManager.RemoveHandler("/giltrack");
    ECommonsMain.Dispose();
  }

  private void OnScroogeCommand(string command, string args)
  {
    // in response to the slash command, just toggle the display status of our main ui
    ToggleConfigUI();
  }

  private void OnGilTrackCommand(string command, string args) => GilDashboard.Toggle();

  /// <summary>
  /// Adds Hawk Run context menu options to inventory items when the HawkWindow is open.
  /// </summary>
  private void OnContextMenuOpened(IMenuOpenedArgs args)
  {
    if (!HawkWindow.IsOpen)
      return;

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

    // Base ID for Lumina lookups (vendor price, etc.) and HawkWindow selection
    var baseItemId = item.IsHq && itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    var isBanned = Configuration.BannedItemIds.Contains(itemId);
    var isAlwaysVendor = Configuration.AlwaysVendorItemIds.Contains(itemId);
    var isSelected = HawkWindow.IsItemSelected(baseItemId, item.IsHq);

    // --- State: Banned ---
    if (isBanned)
    {
      args.AddMenuItem(new MenuItem
      {
        Name = "Remove Hawk Ban",
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

    // Ban from Hawk (red)
    args.AddMenuItem(new MenuItem
    {
      Name = "Ban from Hawk",
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