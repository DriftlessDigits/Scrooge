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

      // Anchor inside the title bar, left of the X close button. Using
      // RootNode with no positionOverride would size the overlay to the full
      // addon and steal title-bar drag input — positionOverride forces a 1x1
      // base so we only capture clicks where the button actually renders.
      var node = addon->RootNode;
      if (node == null) return;

      var position = AutoPinchOverlay.GetNodePosition(node);
      var scale = AutoPinchOverlay.GetNodeScale(node);
      // Offsets are estimates — tune in-game if the button drifts off the X.
      const float rightOffset = 160f; // button width (~130) + X button (~30) - tuned in-game
      const float topOffset = 9f;
      var topRight = new System.Numerics.Vector2(
        position.X + node->Width * scale.X - rightOffset * scale.X,
        position.Y + topOffset * scale.Y);

      var oldSize = AutoPinchOverlay.ImGuiSetup(node, "###ScroogeDesynthLauncher", topRight);
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
