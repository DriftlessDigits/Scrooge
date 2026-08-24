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

  internal static PinchHost PinchHost { get; private set; } = null!;

  /// <summary>Active run data. Null when no run is in progress.</summary>
  internal static RunData? CurrentRun { get; set; }

  /// <summary>
  /// THE LEDGER (Rounds unit 5): the Round's transcript, and the window it reads in.
  /// The name is REISSUED here - it used to belong to the judgment desk, which is the
  /// Accountant now. A ledger is a book of records, and this is the book: the banked
  /// <c>round_log</c> plus the live run in hand, one continuous read of the errand.
  /// </summary>
  internal static LedgerWindow Ledger { get; private set; } = null!;

  internal static GilWindow GilDashboard { get; private set; } = null!;

  internal static HawkWindow HawkWindow { get; private set; } = null!;

  /// <summary>The Accountant: the Round wizard, and the board's one host (Rounds unit 5). Was the Ledger window.</summary>
  internal static AccountantWindow Accountant { get; private set; } = null!;

  internal static GcTurnInOrchestrator GcTurnIn { get; private set; } = null!;

  /// <summary>Null when GilStorage failed to initialize — yield persistence disabled.</summary>
  internal static DesynthYieldStore? DesynthYieldStore { get; private set; }

  /// <summary>
  /// The Round's banked header + transcript (V40). Null when GilStorage failed to
  /// initialize, and every caller treats that as "this round has no banked run" -
  /// the cursor, the flow and every stage are unchanged, the transcript simply lives
  /// and dies in memory exactly as it did before unit 4.
  /// </summary>
  internal static RoundLogStore? RoundLogStore { get; private set; }

  internal static DesynthYieldTracker DesynthYieldTracker { get; private set; } = null!;

  internal static StandingOrchestrator StandingOrchestrator { get; private set; } = null!;

  internal static DesynthPreviewWindow DesynthPreview { get; private set; } = null!;

  internal static BoardMirrorWindow BoardMirror { get; private set; } = null!;

  internal static DesynthOrchestrator DesynthOrchestrator { get; private set; } = null!;

  internal static CofferOrchestrator CofferOrchestrator { get; private set; } = null!;

  internal static DesynthLauncher DesynthLauncher { get; private set; } = null!;

  private VentureReturnTracker? _ventureReturnTracker;
  private CofferPullWatcher? _cofferPullWatcher;
  private DtrToday? _dtrToday;
  private RetainerHistoryHook? _retainerHistoryHook;
  private GilTrackEventListener? _gilTrackListener;
  private ExchangeTracker? _exchangeTracker;
  private SpecialExchangeTracker? _specialExchangeTracker;
  private ChatCatchallTracker? _chatCatchallTracker;

  public readonly WindowSystem WindowSystem = new("Scrooge");
  internal static ConfigWindow ConfigWindow { get; private set; } = null!;

  public Plugin()
  {
    Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
    ConfigWindow = new ConfigWindow();
    WindowSystem.AddWindow(ConfigWindow);

    CommandManager.AddHandler("/scrooge", new CommandInfo(OnScroogeCommand)
    {
      HelpMessage = "Opens the Scrooge gil dashboard (/scrooge round = the Round, log = the Ledger transcript, config = settings, sitrep = diagnostics to clipboard)"
    });

    // Register chat link handler — clicking Scrooge's chat output opens the dashboard
    ConfigLinkPayload = ChatGui.AddChatLinkHandler(0, (id, _) => ToggleMainUi());

    PluginInterface.UiBuilder.Draw += DrawUI;
    PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

    ECommonsMain.Init(PluginInterface, this);
    Svc.Log.Information($"build {BuildStamp.Line}");
    PinchHost = new PinchHost();
    WindowSystem.AddWindow(new AutoPinchOverlayWindow(PinchHost));

    Ledger = new LedgerWindow();
    WindowSystem.AddWindow(Ledger);

    // Gil tracking
    try
    {
      GilStorage.Initialize();
      DesynthYieldStore = new DesynthYieldStore(GilStorage.Connection);
      DesynthYieldStore.YieldCaptured += Ledger.OnYieldCaptured;
      RoundLogStore = new RoundLogStore(GilStorage.Connection);
    }
    catch (Exception ex)
    {
      // Storage failed CLOSED: GilStorage nulled its connection on the way out, so
      // GilStorage.StorageAvailable is false and every read through
      // GilStorage.Connection now throws instead of quietly answering off a
      // half-migrated database. The stores above were never built, so the desynth
      // and Round-transcript features sit out the session; the rest of the plugin
      // still runs, and the call sites that reach storage without asking will
      // surface as caught exceptions rather than as wrong numbers.
      Svc.Log.Error(ex, "[GilTrack] Failed to initialize database — storage unavailable this session; " +
        "desynth yield capture and the Round transcript are off, and any feature that reads storage will fail loudly");
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

    Accountant = new AccountantWindow();
    WindowSystem.AddWindow(Accountant);

    GcTurnIn = new GcTurnInOrchestrator();

    try
    {
      _ventureReturnTracker = new VentureReturnTracker();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "VentureReturnTracker failed - venture capture disabled this session");
    }

    try
    {
      _cofferPullWatcher = new CofferPullWatcher();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "CofferPullWatcher failed - Materiel Container capture disabled this session");
    }

    try
    {
      _dtrToday = new DtrToday();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "DTR entry failed - server info bar headline disabled");
    }

    try
    {
      UniversalisStats.Initialize();
      UniversalisHistory.Initialize();
    }
    catch (Exception ex)
    {
      Svc.Log.Warning(ex, "Universalis almanac failed to start - local evidence only this session");
    }

    StandingOrchestrator = new StandingOrchestrator();

    DesynthPreview = new DesynthPreviewWindow();
    WindowSystem.AddWindow(DesynthPreview);

    BoardMirror = new BoardMirrorWindow();
    WindowSystem.AddWindow(BoardMirror);

    DesynthOrchestrator = new DesynthOrchestrator();

    CofferOrchestrator = new CofferOrchestrator();

    try
    {
      DesynthYieldTracker = new DesynthYieldTracker();
    }
    catch (Exception ex)
    {
      Svc.Log.Error(ex, "DesynthYieldTracker failed to initialize — yield capture disabled this session");
    }

    DesynthLauncher = new DesynthLauncher();
    WindowSystem.AddWindow(DesynthLauncher);

    ContextMenu.OnMenuOpened += OnContextMenuOpened;

    // The completion-event pump (WALK unit 6). Executors REPORT from wherever they
    // die; the consequences run here, once per framework tick, outside any ImGui
    // frame. Subscribed after every window and orchestrator exists so a completion
    // reported on the very first tick lands on a fully-built world.
    RunFlow.Completed += Accountant.Conductor.OnRunCompleted;
    RunFlow.Completed += StandingBookFeed.OnRunCompleted;
    Svc.Framework.Update += OnFrameworkPump;

    CommandManager.AddHandler("/giltrack", new CommandInfo(OnGilTrackCommand)
    {
      HelpMessage = "Opens the Scrooge gil dashboard"
    });

  }

  /// <summary>
  /// Once a tick, off any ImGui frame: drain the run-completion queue (RunFlow),
  /// then ask the round whether it has a stage to fire (WALK unit 9's flow). The
  /// order matters - the completions are what make the deck's counts current, and
  /// the flow decides off current counts. Both are no-ops on a quiet frame.
  /// </summary>
  private void OnFrameworkPump(IFramework framework)
  {
    RunFlow.Pump();
    // Recon's stall watchdog. The bell has none (its chain is short and the task
    // manager's own per-task timeout catches a wedge); recon runs the escalating
    // market-board retry ladder per item, so a board that stops answering entirely
    // could otherwise sit the round in a run that never ends and never dies.
    try { PinchHost.ReconTick(); }
    catch (Exception ex) { Svc.Log.Error(ex, "[Recon] The stall watchdog threw"); }
    try { Accountant.Conductor.FlowTick(); }
    catch (Exception ex) { Svc.Log.Error(ex, "[Flow] The round's flow tick threw"); }
  }

  public void Dispose()
  {
    // Stop the pump BEFORE tearing anything down: Abort() below reports a
    // completion, and a torn-down world has no consequences left to run.
    Svc.Framework.Update -= OnFrameworkPump;
    RunFlow.Completed -= Accountant.Conductor.OnRunCompleted;
    RunFlow.Completed -= StandingBookFeed.OnRunCompleted;
    RunFlow.Clear();
    OccupancyTransition.Reset(); // drops any held continuation + its framework hook
    FleetCapacity.Reset();       // a stale roster read must not advise a new session's launch
    SpineGrace.Reset();          // same shape: a grace window in flight owns a tick hook
    StandingBookFeed.Clear();
    GcTurnIn?.Abort(); // ends a live churn run + unsubscribes its watchdog
    ContextMenu.OnMenuOpened -= OnContextMenuOpened;
    StandingOrchestrator.Dispose();
    DesynthYieldTracker?.Dispose();
    DesynthOrchestrator.Dispose();
    CofferOrchestrator.Dispose();
    WindowSystem.RemoveAllWindows();
    PinchHost.Dispose();
    CommandManager.RemoveHandler("/scrooge");
    UniversalisStats.Dispose();
    UniversalisHistory.Dispose();
    _ventureReturnTracker?.Dispose();
    _cofferPullWatcher?.Dispose();
    _dtrToday?.Dispose();
    _chatCatchallTracker?.Dispose();
    _specialExchangeTracker?.Dispose();
    _exchangeTracker?.Dispose();
    _gilTrackListener?.Dispose();
    _retainerHistoryHook?.Dispose();
    GilStorage.Dispose();
    CommandManager.RemoveHandler("/giltrack");
    ECommonsMain.Dispose();
  }

  private void OnScroogeCommand(string command, string args)
  {
    // Dashboard is the front door; settings stay reachable via argument.
    // PLURALITY IS NOT AN ERROR (Drift, 2026-08-23: "don't punish me because I
    // get plurality wrong") - every word door answers to singular and plural.
    if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase)
        || args.Trim().Equals("configs", StringComparison.OrdinalIgnoreCase)
        || args.Trim().Equals("setting", StringComparison.OrdinalIgnoreCase)
        || args.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase))
      ToggleConfigUI();
    // Clean cut at v3.0 (ruling 2): /scrooge ledger REPLACES /scrooge route. The old
    // command is gone, not aliased. /scrooge triage stays as an alias - triage is now
    // a set of piles inside the Ledger, so the muscle-memory command lands there too.
    // ONE DOOR, ONE WINDOW (Movement 3, ruled 2026-08-13). These three words open the
    // Round's window, whatever state the errand is in - the window keys its own
    // content (AccountantPlan.ScreenFor). This SUPERSEDES unit 5's routing ruling,
    // which sent an idle door to the dashboard because the launch preview lived
    // there; the preview is the idle screen now, so there is nothing to route to.
    else if (args.Trim().Equals("ledger", StringComparison.OrdinalIgnoreCase)
             || args.Trim().Equals("ledgers", StringComparison.OrdinalIgnoreCase)
             || args.Trim().Equals("triage", StringComparison.OrdinalIgnoreCase)
             || args.Trim().Equals("round", StringComparison.OrdinalIgnoreCase)
             || args.Trim().Equals("rounds", StringComparison.OrdinalIgnoreCase))
      OpenRoundDoor();
    // The transcript kept a door of its own: it is a different thing from the Round
    // now (a book, not a desk), and a player who wants to re-read the last errand
    // should not have to start one.
    else if (args.Trim().Equals("log", StringComparison.OrdinalIgnoreCase)
             || args.Trim().Equals("logs", StringComparison.OrdinalIgnoreCase))
      Ledger.Toggle();
    // One-paste diagnostics: clipboard gets the block, chat confirms. The dump
    // itself never goes to chat - it's built to be pasted at Fable, not read
    // off a chat log.
    else if (args.Trim().Equals("sitrep", StringComparison.OrdinalIgnoreCase))
    {
      var dump = Sitrep.Build();
      Dalamud.Bindings.ImGui.ImGui.SetClipboardText(dump);
      Svc.Chat.Print($"[Scrooge] Sitrep copied to clipboard ({dump.Split('\n').Length} lines).");
    }
    else
      ToggleMainUi();
  }

  private void OnGilTrackCommand(string command, string args) => GilDashboard.Toggle();

  /// <summary>
  /// THE ROUND'S DOOR - the one place every "take me to the work" entry lands, and
  /// since Movement 3 it lands on ONE window. What that window shows is keyed to the
  /// state of the errand (<see cref="AccountantPlan.ScreenFor"/>): the launch preview
  /// with no Round, the wizard with one, the last one's report while it is owed.
  /// Reading the world never starts an errand, and starting one never costs a detour.
  /// </summary>
  internal static void OpenRoundDoor()
  {
    // RESTORE BEFORE THE WINDOW DRAWS (review ruling S15). The persisted round
    // rehydrates lazily, and until this call it hung off the deck's own draw - so the
    // first /scrooge round after a reload asked a cursor that had not read its state
    // yet and answered "no round" over a round that was holding. The routing that
    // mistake used to break is gone; the screen keying reads the same cursor, so the
    // restore still has to happen before anything asks.
    Accountant.EnsureRoundRestored();
    Accountant.IsOpen = true;
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

    // --- One menu, no hidden preconditions (ruled 2026-08-23) ---
    // The full menu used to demand the Hawk window be open, with a ban-only
    // fallback at the retainer - and when the 08-15 bell-bar trim took the Hawk
    // Wares button (OpenHawkView's only caller), Always Vendor became
    // UNREACHABLE: the gesture the config tab described needed a window nothing
    // could open. The item rules are config-list edits; they never needed the
    // window. Only Select for Sale stays Hawk-gated - selection is meaningless
    // without the surface it selects on.

    // Base ID for Lumina lookups (vendor price, etc.) and HawkWindow selection
    var baseItemId = item.IsHq && itemId >= 1_000_000 ? itemId - 1_000_000 : itemId;

    var isBannedHawk = Configuration.BannedItemIds.Contains(itemId);
    var isAlwaysVendor = Configuration.AlwaysVendorItemIds.Contains(itemId);

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
          if (HawkWindow.IsOpen) HawkWindow.RefreshInventory();
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
          if (HawkWindow.IsOpen) HawkWindow.RefreshInventory();
        },
      });
      return;
    }

    // --- State: Normal (not banned, not always-vendor) ---

    // Select / Deselect - the one Hawk-gated pair (see the ruling above)
    if (HawkWindow.IsOpen)
    {
      if (HawkWindow.IsItemSelected(baseItemId, item.IsHq))
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
          if (HawkWindow.IsOpen) HawkWindow.RefreshInventory();
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
        if (HawkWindow.IsOpen) HawkWindow.RefreshInventory();
      },
    });
  }

  private void DrawUI()
  {
    WindowSystem.Draw();
  }

  public void ToggleConfigUI() => ConfigWindow.Toggle();

  /// <summary>Main UI = the Gil Dashboard. Config stays behind the cog / /scrooge config.</summary>
  public void ToggleMainUi() => GilDashboard.Toggle();
}