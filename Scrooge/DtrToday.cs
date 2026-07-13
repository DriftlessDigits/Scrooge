using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.DalamudServices;
using System;

namespace Scrooge;

/// <summary>
/// The one glanceable number, in the server info bar: today's total-gil
/// delta ("$ Today: +214k"). One DTR slot, ever - the glanceability rule
/// from the UI pass; goals and ventures stay out of the bar. Click opens
/// the dashboard. Refreshes on a 30s gate off the same rollup the money
/// line uses.
/// </summary>
internal sealed class DtrToday : IDisposable
{
  private readonly IDtrBarEntry _entry;
  private DateTime _lastRefresh = DateTime.MinValue;

  public DtrToday()
  {
    _entry = Svc.DtrBar.Get("Scrooge");
    _entry.OnClick = _ => Plugin.GilDashboard.Toggle();
    _entry.Tooltip = "Today's gil movement - click for the Scrooge dashboard";
    Svc.Framework.Update += OnUpdate;
  }

  public void Dispose()
  {
    Svc.Framework.Update -= OnUpdate;
    _entry.Remove();
  }

  private void OnUpdate(Dalamud.Plugin.Services.IFramework _)
  {
    if (DateTime.UtcNow - _lastRefresh < TimeSpan.FromSeconds(30))
      return;
    _lastRefresh = DateTime.UtcNow;

    if (!Plugin.Configuration.EnableDtrToday)
    {
      _entry.Shown = false;
      return;
    }

    long? delta = null;
    try { delta = Windows.GilWindow.ComputeTodayDelta(); }
    catch { /* storage unavailable - hide rather than lie */ }

    if (delta is not long d)
    {
      _entry.Shown = false;
      return;
    }

    _entry.Shown = true;
    _entry.Text = new SeStringBuilder()
      .AddText($"$ Today: {Windows.Format.SignedGil(d)}")
      .Build();
  }
}
