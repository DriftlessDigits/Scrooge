using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace Scrooge.Windows;

/// <summary>
/// THE BOARD MIRROR (Drift, 08-22): the last-seen board and the banked sale tape
/// for one item, side by side - the game's item-detail view rebuilt from what the
/// plugin already banked, with the walk's own call badged on every listing row.
/// Opened from a triage case; built for the shakeout, where "do I believe this
/// verdict" is answered by looking at the same rows the engine looked at.
///
/// <para>Paint only (the Windows law): composition lives in
/// <see cref="BoardMirror"/>, the evaluation in the LedgerCache banked seam - one
/// composition shared with the relist preview, so this window can never tell a
/// different story than the pinch would. Data is composed ONCE per Open/Refresh,
/// never per frame.</para>
/// </summary>
internal sealed class BoardMirrorWindow : Window
{
  private uint _itemId;
  private bool _isHq;
  private string _title = "";
  // The row's own standing ask, banked with the identity: it is load-bearing in
  // the walk (AskVouchesFor immunizes a cheap row the player's own ask vouches
  // for), so Refresh must re-walk with the SAME operand or the mirror's verdict
  // changes because the reader pressed a button.
  private long? _currentAsk;
  private BoardMirror.MirrorModel? _model;
  private string _evidence = "";

  public BoardMirrorWindow()
    : base("Board Mirror###BoardMirror", ImGuiWindowFlags.None)
  {
    SizeConstraints = new WindowSizeConstraints
    {
      MinimumSize = new Vector2(560, 380),
      MaximumSize = new Vector2(1000, 1000),
    };
  }

  /// <summary>
  /// Composes the mirror for one item and opens the window. Idempotent - calling
  /// while open retargets it (one mirror, the case in front of you).
  /// </summary>
  public void Open(uint itemId, bool isHq, string itemName, long? currentAsk = null)
  {
    _itemId = itemId;
    _isHq = isHq;
    _title = itemName + (isHq ? " (HQ)" : "");
    _currentAsk = currentAsk;
    Compose();
    IsOpen = true;
  }

  private void Compose()
  {
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (Scrooge.Board.LedgerCache.EvaluateBanked(_itemId, _isHq, _currentAsk) is { } banked)
    {
      _model = BoardMirror.Compose(
        banked.Snapshot, banked.ScanAt, banked.SalesRaw, _isHq, banked.Answer.Decision, now);
      _evidence = banked.Answer.Decision.Evidence;
    }
    else
    {
      // Storage down or the walk failed: the mirror shows nothing rather than a
      // guess, and says which silence this is.
      _model = null;
      _evidence = "";
    }
  }

  public override void Draw()
  {
    ImGui.TextColored(ScroogeColors.Header, _title);
    ImGui.SameLine();
    if (ImGui.SmallButton("Refresh##mirror")) Compose();

    if (_model is not { } m)
    {
      ImGui.TextColored(ScroogeColors.Warning,
        "Storage is unavailable - the mirror has nothing honest to show.");
      return;
    }

    ImGui.Spacing();
    ImGui.TextColored(ScroogeColors.Info, m.BoardHeader);
    if (m.Rows.Count > 0 &&
        ImGui.BeginTable("MirrorBoard", 5,
          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
    {
      ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthStretch, 80);
      ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 40);
      ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthStretch, 30);
      ImGui.TableSetupColumn("Seller", ImGuiTableColumnFlags.WidthStretch, 120);
      ImGui.TableSetupColumn("Call", ImGuiTableColumnFlags.WidthStretch, 90);
      ImGui.TableHeadersRow();
      foreach (var row in m.Rows)
      {
        ImGui.TableNextColumn(); ImGui.Text(Format.Gil(row.UnitPrice));
        ImGui.TableNextColumn(); ImGui.Text($"{row.Quantity}");
        ImGui.TableNextColumn();
        if (row.IsHq) ImGui.TextColored(ScroogeColors.HqGold, "HQ");
        else ImGui.TextDisabled("-");
        ImGui.TableNextColumn(); ImGui.Text(row.Retainer);
        ImGui.TableNextColumn();
        ImGui.TextColored(CallColor(row.Call), BoardMirror.CallWord(row.Call));
      }
      ImGui.EndTable();
    }

    ImGui.Spacing();
    ImGui.TextColored(ScroogeColors.Info, m.TapeHeader);
    if (m.Sales.Count > 0 &&
        ImGui.BeginTable("MirrorTape", 5,
          ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
    {
      ImGui.TableSetupColumn("Price", ImGuiTableColumnFlags.WidthStretch, 80);
      ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthStretch, 40);
      ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthStretch, 30);
      ImGui.TableSetupColumn("Buyer", ImGuiTableColumnFlags.WidthStretch, 120);
      ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch, 70);
      ImGui.TableHeadersRow();
      foreach (var sale in m.Sales)
      {
        ImGui.TableNextColumn(); ImGui.Text(Format.Gil(sale.UnitPrice));
        ImGui.TableNextColumn(); ImGui.Text($"{sale.Quantity}");
        ImGui.TableNextColumn();
        if (sale.IsHq) ImGui.TextColored(ScroogeColors.HqGold, "HQ");
        else ImGui.TextDisabled("-");
        ImGui.TableNextColumn(); ImGui.Text(sale.Buyer);
        ImGui.TableNextColumn(); ImGui.TextDisabled(sale.WhenText);
      }
      ImGui.EndTable();
    }

    // The walk's own sentence over this exact board - the prose the badges above
    // are the arithmetic of. One source (LaneDecision.Evidence), zero rewording.
    if (_evidence.Length > 0)
    {
      ImGui.Spacing();
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Muted);
      ImGui.TextWrapped(_evidence);
      ImGui.PopStyleColor();
    }
  }

  private static Vector4 CallColor(BoardMirror.MirrorCall call) => call switch
  {
    BoardMirror.MirrorCall.Own => ScroogeColors.Earned,
    BoardMirror.MirrorCall.Crasher => ScroogeColors.TagRed,
    BoardMirror.MirrorCall.Dreamer => ScroogeColors.Muted,
    BoardMirror.MirrorCall.OffLane => ScroogeColors.Stale,
    BoardMirror.MirrorCall.Unjudged => ScroogeColors.Stale,
    _ => ScroogeColors.Info,
  };
}
