using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ECommons;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;

namespace Scrooge;

/// <summary>
/// Floating ImGui overlay that anchors a "Desynth Preview" button to the
/// SalvageItemSelector addon. Mirrors AutoPinch's render-when-context-is-right
/// pattern.
/// </summary>
internal sealed class DesynthLauncher : Window, IDisposable
{
  public DesynthLauncher()
    : base("ScroogeDesynthLauncher",
        ImGuiWindowFlags.NoDecoration |
        ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.AlwaysUseWindowPadding |
        ImGuiWindowFlags.AlwaysAutoResize, true)
  {
    Position = new System.Numerics.Vector2(0, 0);
    IsOpen = true;
    ShowCloseButton = false;
    RespectCloseHotkey = false;
    DisableWindowSounds = true;
    SizeConstraints = new WindowSizeConstraints
    {
      MaximumSize = new System.Numerics.Vector2(0, 0),
    };
  }

  public void Dispose() { }

  public override void Draw()
  {
    if (!Plugin.Configuration.EnableDesynthPreview) return;
    if (Plugin.DesynthOrchestrator?.IsRunning == true) return; // hide while running

    unsafe
    {
      if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("SalvageItemSelector", out var addon)
          || !GenericHelpers.IsAddonReady(addon))
        return;

      // Anchor to RootNode (the addon's top-left frame). If the visual anchor
      // drifts after a patch, swap to a named header child via in-game node
      // inspection.
      var node = addon->RootNode;
      if (node == null) return;

      var oldSize = AutoPinchOverlay.ImGuiSetup(node);
      try { DrawButton(); }
      finally { AutoPinchOverlay.ImGuiPostSetup(oldSize); }
    }
  }

  private static void DrawButton()
  {
    if (ImGui.Button("Desynth Preview"))
      Plugin.DesynthPreview.OpenAndScan();
  }
}
