using System;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
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

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
  public static Configuration Configuration { get; private set; } // will never be null
  public static DalamudLinkPayload ConfigLinkPayload { get; private set; } = null!;
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.

  internal static AutoPinch AutoPinch { get; private set; } = null!;

  internal static PinchRunLogWindow PinchRunLog { get; private set; } = null!;

  internal static GilWindow GilDashboard { get; private set; } = null!;

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

    CommandManager.AddHandler("/giltrack", new CommandInfo(OnGilTrackCommand)
    {
      HelpMessage = "Opens the Scrooge gil dashboard"
    });
  }

  public void Dispose()
  {
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

  private void DrawUI()
  {
    WindowSystem.Draw();
  }

  public void ToggleConfigUI() => ConfigWindow.Toggle();
}