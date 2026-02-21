using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Scrooge.Windows;
using ECommons;

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
  }

  public void Dispose()
  {
    WindowSystem.RemoveAllWindows();
    AutoPinch.Dispose();
    CommandManager.RemoveHandler("/scrooge");
    ECommonsMain.Dispose();
  }

  private void OnScroogeCommand(string command, string args)
  {
    // in response to the slash command, just toggle the display status of our main ui
    ToggleConfigUI();
  }

  private void DrawUI()
  {
    WindowSystem.Draw();
  }

  public void ToggleConfigUI() => ConfigWindow.Toggle();
}