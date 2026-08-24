using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;

namespace Scrooge;

/// <summary>
/// Auto-confirms the "unable to process item buyback requests" dialog that appears
/// when dismissing a retainer after vendor-selling items. Every path that vendors
/// through a retainer arms one of these - the manual triage executor, the pinch's
/// rider, and the Hawk run - and each carried its own byte-identical copy apart
/// from the log tag.
///
/// <para>Ignores unexpected dialogs: a SelectYesno this guard did not recognise
/// belongs to whatever else is on screen, and clicking it would be the automation
/// pressing a button nobody asked for.</para>
///
/// <para><b>NOTE: English-only text match.</b> Non-English clients will not
/// auto-confirm and the dialog will block the run. Acceptable for now - Scrooge
/// targets EN.</para>
/// </summary>
internal sealed class VendorDismissGuard
{
  private const string DialogText = "unable to process item buyback requests";

  private readonly string _logTag;

  /// <param name="logTag">The arming surface's tag, as it reads in /xllog.</param>
  internal VendorDismissGuard(string logTag)
  {
    _logTag = logTag;
  }

  /// <summary>The listener itself - registered and unregistered by the arming orchestrator.</summary>
  internal void Confirm(AddonEvent type, AddonArgs args)
  {
    var addon = new AddonMaster.SelectYesno(args.Addon);
    if (addon.Text.Contains(DialogText))
    {
      Svc.Log.Debug($"[{_logTag}] Auto-confirming vendor dismiss dialog");
      addon.Yes();
    }
  }
}
