using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Scrooge.Windows
{
  /// <summary>
  /// The action inbox (shake-out findings 4+5, Sam's call 2026-07-11): ONE
  /// table of everything awaiting the player's judgment. This run's skipped
  /// items and persistent held flags (V12) are the same list with different
  /// lifetimes - every row carries its full action set (Vend / Pull /
  /// Reprice where applicable / Dismiss), fresh rows are marked, and the run
  /// summary stays as a header line.
  ///
  /// Flag-backed rows act through the same orchestrator as live rows: slot
  /// targeting is re-resolved by item id at execution time (see
  /// TriageOrchestrator.OpenSellListRow), so stale flag slots can never pull
  /// the wrong item.
  /// </summary>
  internal sealed class TriageWindow : Window
  {
    /// <summary>One inbox row: a live run item, or a held flag wearing a synthetic PricingItem.</summary>
    private sealed record InboxRow(PricingItem Item, TriageFlag? Flag)
    {
      public bool IsFresh => Flag == null;
    }

    private List<PricingItem> _triageItems = [];
    private List<TriageFlag> _heldFlags = [];
    private readonly Dictionary<long, PricingItem> _flagItems = []; // flag id -> synthetic (stable identity for _actions)
    private Dictionary<PricingItem, TriageAction> _actions = [];
    private List<InboxRow>? _sortedRows;

    internal TriageWindow() : base("Scrooge - Triage")
    {
      SizeConstraints = new WindowSizeConstraints
      {
        MinimumSize = new Vector2(600, 200),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
      };
      Size = new Vector2(1100, 400);
      SizeCondition = ImGuiCond.FirstUseEver;
      IsOpen = false;
    }

    public override void OnOpen()
    {
      RefreshHeldFlags();
    }

    public override void Draw()
    {
      // A batch killed by a task timeout leaves IsRunning latched and the
      // gil-tracking block leaked - detect and recover (finding #14).
      Plugin.TriageOrchestrator.RecoverIfStalled();

      var rows = BuildRows();

      if (rows.Count == 0)
      {
        ImGui.TextDisabled("No triage items.");
        return;
      }

      DrawHeader();
      DrawInbox(rows);
    }

    /// <summary>Loads open persistent flags (V12). Called on window open and after flag mutations.</summary>
    private void RefreshHeldFlags()
    {
      try { _heldFlags = GilStorage.GetOpenTriageFlags(); }
      catch { _heldFlags = []; }
      _sortedRows = null;
    }

    /// <summary>Marks any open flags for this item as actioned (it was queued in a triage batch).</summary>
    private void CloseFlagsFor(PricingItem item)
    {
      foreach (var flag in _heldFlags.Where(f => f.ItemId == item.ItemId && f.IsHq == item.IsHq && f.RetainerName == item.RetainerName))
        try { GilStorage.SetTriageFlagStatus(flag.Id, "actioned"); } catch { /* storage unavailable */ }
    }

    /// <summary>
    /// The unified row list: fresh run items first, then held flags not
    /// already represented by a live row (the live row carries the actions).
    /// </summary>
    private List<InboxRow> BuildRows()
    {
      var rows = new List<InboxRow>();
      foreach (var item in _triageItems)
        rows.Add(new InboxRow(item, null));

      foreach (var flag in _heldFlags)
      {
        if (_triageItems.Any(t => t.ItemId == flag.ItemId && t.IsHq == flag.IsHq && t.RetainerName == flag.RetainerName))
          continue;
        rows.Add(new InboxRow(SyntheticItem(flag), flag));
      }
      return rows;
    }

    /// <summary>
    /// A held flag's actionable stand-in. Quantity starts 0 (unknown - shown
    /// as a dash; the orchestrator fills the real stack at pull time). The
    /// Result mapping decides Reprice eligibility: price-hold flags can be
    /// repriced (guards bypassed), evict flags can only move or dismiss.
    /// </summary>
    private PricingItem SyntheticItem(TriageFlag flag)
    {
      if (_flagItems.TryGetValue(flag.Id, out var cached)) return cached;

      var item = new PricingItem
      {
        SlotIndex = flag.SlotIndex, // stale hint only - never used for targeting
        ItemId = flag.ItemId,
        IsHq = flag.IsHq,
        ItemName = GilTracker.GetItemName(flag.ItemId),
        RetainerName = flag.RetainerName,
        Quantity = 0,
        CurrentListingPrice = flag.OldPrice > 0 ? flag.OldPrice : null,
        MbPrice = flag.FlaggedPrice > 0 ? flag.FlaggedPrice : null,
        VendorPrice = VendorPriceOf(flag.ItemId),
        Result = flag.Reason switch
        {
          "upward_held" or "outlier_warn" => PricingResult.UpwardHeld,
          "lane_held" => PricingResult.LaneHeld,
          "cap_blocked" => PricingResult.CapBlocked,
          _ => PricingResult.NoData,
        },
      };
      _flagItems[flag.Id] = item;
      return item;
    }

    private static int VendorPriceOf(uint itemId)
    {
      try
      {
        return (int)ECommons.DalamudServices.Svc.Data
          .GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(itemId).PriceLow;
      }
      catch { return 0; }
    }

    /// <summary>Run summary + batch buttons. The summary line covers this run's fresh rows only.</summary>
    private void DrawHeader()
    {
      var isRunning = Plugin.TriageOrchestrator.IsRunning;

      if (_triageItems.Count > 0)
      {
        var totalVendorGil = _triageItems.Sum(t => t.VendorPrice * t.Quantity);
        ImGui.Text($"{_triageItems.Count} {(_triageItems.Count == 1 ? "item" : "items")} skipped this run — vendoring all would free {_triageItems.Count} {(_triageItems.Count == 1 ? "slot" : "slots")} and net {totalVendorGil:N0} gil");
      }
      var flagCount = _heldFlags.Count(f => !_triageItems.Any(t => t.ItemId == f.ItemId && t.IsHq == f.IsHq && t.RetainerName == f.RetainerName));
      if (flagCount > 0)
        ImGui.TextDisabled($"{flagCount} held {(flagCount == 1 ? "flag" : "flags")} from earlier sessions — open until you act on or dismiss them.");
      ImGui.Spacing();

      if (!isRunning)
      {
        var vendorCount = _actions.Count(a => a.Value == TriageAction.Vendor);
        var pullCount = _actions.Count(a => a.Value == TriageAction.Pull);
        var repriceCount = _actions.Count(a => a.Value == TriageAction.Reprice);
        var totalActions = vendorCount + pullCount + repriceCount;

        if (totalActions > 0)
        {
          var parts = new List<string>();
          if (vendorCount > 0) parts.Add($"{vendorCount} vendor");
          if (pullCount > 0) parts.Add($"{pullCount} pull");
          if (repriceCount > 0) parts.Add($"{repriceCount} reprice");

          if (ImGui.Button($"Go ({string.Join(", ", parts)})"))
            ExecuteBatch();
          ImGui.SameLine();
        }

        if (_triageItems.Count > 0 && ImGui.Button("Dismiss Run Items"))
        {
          _triageItems.Clear();
          _actions = _actions.Where(a => _flagItems.ContainsValue(a.Key))
            .ToDictionary(a => a.Key, a => a.Value);
          _sortedRows = null;
        }
      }
      else
      {
        if (ImGui.Button("Cancel"))
          Plugin.TriageOrchestrator.Abort();
        ImGui.SameLine();
        ImGui.TextDisabled("Triage in progress...");
      }
      ImGui.Spacing();
    }

    private void ExecuteBatch()
    {
      var batch = new Dictionary<PricingItem, TriageAction>(_actions);
      if (!Plugin.TriageOrchestrator.QueueTriageBatch(batch))
        return;

      // Rows and flags are retired per-item ON COMPLETION (the orchestrator
      // calls RemoveItem as each action lands) - never here at queue time.
      // A batch that dies mid-flight leaves the unfinished work visible
      // instead of silently swallowing it (finding #15).
      _actions.Clear();
      _sortedRows = null;
    }

    private void DrawInbox(List<InboxRow> rows)
    {
      var isRunning = Plugin.TriageOrchestrator.IsRunning;
      long? dismissFlagId = null;
      PricingItem? dismissItem = null;

      if (ImGui.BeginTable("TriageInbox", 12,
        ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH
        | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp
        | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate))
      {
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.DefaultSort, 55);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Listed", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("MB/ea", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Vendor/ea", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Gil In", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("vs MB", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Reason", ImGuiTableColumnFlags.WidthStretch, 2.0f);
        ImGui.TableSetupColumn("Details", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 1.2f);
        ImGui.TableHeadersRow();

        // Column header tooltips
        for (int col = 0; col <= 10; col++)
        {
          ImGui.TableSetColumnIndex(col);
          if (ImGui.IsItemHovered())
          {
            var tip = col switch
            {
              0 => "'new' = from this run; otherwise how long the flag has been open",
              3 => "Stack size of the listing (— when unknown until pull time)",
              4 => "Your current listing price per unit",
              5 => "Current market board price per unit",
              6 => "NPC vendor sell price per unit",
              7 => "Total gil received if vendor-sold (Vendor/ea × Qty)",
              8 => "Gil difference if you vendor instead of selling on MB at the listed price",
              9 => "Why this row needs your judgment",
              10 => "Sale history and pricing context",
              _ => ""
            };
            if (tip.Length > 0) ImGui.SetTooltip(tip);
          }
        }

        SortRows(rows);

        for (int i = 0; i < _sortedRows!.Count; i++)
        {
          var row = _sortedRows[i];
          var item = row.Item;
          ImGui.TableNextRow();

          // When
          ImGui.TableNextColumn();
          if (row.IsFresh)
          {
            ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Earned);
            ImGui.Text("new");
            ImGui.PopStyleColor();
          }
          else
          {
            var ageDays = (System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() - row.Flag!.CreatedAt) / 86400;
            ImGui.TextDisabled(ageDays < 1 ? "today" : $"{ageDays}d ago");
          }

          // Item
          ImGui.TableNextColumn();
          var color = row.IsFresh
            ? item.Result switch
            {
              PricingResult.BelowFloor => ScroogeColors.Spent,
              PricingResult.BelowMinimum => ScroogeColors.Amber,
              PricingResult.CapBlocked => ScroogeColors.Warning,
              PricingResult.UndercutTooDeep => ScroogeColors.Warning,
              PricingResult.UpwardHeld => ScroogeColors.Warning,
              PricingResult.LaneHeld => ScroogeColors.Amber,
              _ => ScroogeColors.Muted,
            }
            : ScroogeColors.Warning;
          ImGui.PushStyleColor(ImGuiCol.Text, color);
          ImGui.Text(Format.Hq(item.ItemName, item.IsHq));
          ImGui.PopStyleColor();

          // Retainer, Qty
          ImGui.TableNextColumn(); ImGui.Text(item.RetainerName);
          ImGui.TableNextColumn();
          if (item.Quantity > 0) ImGui.Text($"{item.Quantity}");
          else ImGui.TextDisabled("—");

          // Listed
          ImGui.TableNextColumn();
          ImGui.Text(item.CurrentListingPrice.HasValue ? $"{item.CurrentListingPrice:N0}" : "—");

          // MB/ea, Vendor/ea
          ImGui.TableNextColumn(); ImGui.Text(item.MbPrice.HasValue ? $"{item.MbPrice:N0}" : "—");
          ImGui.TableNextColumn(); ImGui.Text($"{item.VendorPrice:N0}");

          // Gil In
          ImGui.TableNextColumn();
          if (item.Quantity > 0) ImGui.Text($"{item.VendorPrice * item.Quantity:N0}");
          else ImGui.TextDisabled("—");

          // vs MB
          ImGui.TableNextColumn();
          if (item.MbPrice.HasValue && item.Quantity > 0)
          {
            var vsMb = (item.VendorPrice - item.MbPrice.Value) * item.Quantity;
            ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.ForDelta(vsMb));
            ImGui.Text(Format.SignedGil(vsMb));
            ImGui.PopStyleColor();
          }
          else
            ImGui.TextDisabled("—");

          // Reason
          ImGui.TableNextColumn();
          ImGui.TextWrapped(row.IsFresh ? BuildReason(item) : row.Flag!.Detail);

          // Details
          ImGui.TableNextColumn();
          ImGui.Text(row.IsFresh ? BuildDetails(item) : "held flag");

          // Actions - the full set on every row
          ImGui.TableNextColumn();
          if (!isRunning)
          {
            _actions.TryGetValue(item, out var current);
            var canReprice = item.Result is PricingResult.CapBlocked or PricingResult.UndercutTooDeep or PricingResult.UpwardHeld;

            DrawActionToggle(item, i, TriageAction.Vendor, "Vend", current);
            ImGui.SameLine(0, 2);
            DrawActionToggle(item, i, TriageAction.Pull, "Pull", current);
            if (canReprice)
            {
              ImGui.SameLine(0, 2);
              DrawActionToggle(item, i, TriageAction.Reprice, "Reprc", current);
            }
            ImGui.SameLine(0, 2);
            if (ImGui.SmallButton($"Dismiss##row{i}"))
            {
              if (row.IsFresh) dismissItem = item;
              else dismissFlagId = row.Flag!.Id;
            }
          }
        }
        ImGui.EndTable();
      }

      // Mutations after the table draw
      if (dismissItem != null)
      {
        _triageItems.Remove(dismissItem);
        _actions.Remove(dismissItem);
        _sortedRows = null;
      }
      if (dismissFlagId is long id)
      {
        try { GilStorage.SetTriageFlagStatus(id, "dismissed"); } catch { /* storage unavailable */ }
        if (_flagItems.TryGetValue(id, out var stale)) _actions.Remove(stale);
        _flagItems.Remove(id);
        RefreshHeldFlags();
      }
    }

    /// <summary>Sorts into _sortedRows. Default: fresh rows first, then flags newest-first.</summary>
    private void SortRows(List<InboxRow> rows)
    {
      var sortSpecs = ImGui.TableGetSortSpecs();
      if (!sortSpecs.SpecsDirty && _sortedRows != null && _sortedRows.Count == rows.Count)
        return;

      _sortedRows = new List<InboxRow>(rows);

      if (sortSpecs.SpecsCount > 0)
      {
        var spec = sortSpecs.Specs;
        var col = spec.ColumnIndex;
        var asc = spec.SortDirection == ImGuiSortDirection.Ascending;

        _sortedRows.Sort((ra, rb) =>
        {
          var a = ra.Item;
          var b = rb.Item;
          var cmp = col switch
          {
            0 => RowAge(ra).CompareTo(RowAge(rb)),
            1 => string.Compare(a.ItemName, b.ItemName),
            2 => string.Compare(a.RetainerName, b.RetainerName),
            3 => a.Quantity.CompareTo(b.Quantity),
            4 => (a.CurrentListingPrice ?? 0).CompareTo(b.CurrentListingPrice ?? 0),
            5 => (a.MbPrice ?? 0).CompareTo(b.MbPrice ?? 0),
            6 => a.VendorPrice.CompareTo(b.VendorPrice),
            7 => (a.VendorPrice * a.Quantity).CompareTo(b.VendorPrice * b.Quantity),
            8 => VsMb(a).CompareTo(VsMb(b)),
            9 => a.Result.CompareTo(b.Result),
            10 => a.HistorySaleCount.CompareTo(b.HistorySaleCount),
            _ => 0
          };
          return asc ? cmp : -cmp;
        });
      }

      sortSpecs.SpecsDirty = false;
    }

    /// <summary>Sort key for the When column: fresh rows are age 0, flags by age.</summary>
    private static long RowAge(InboxRow row)
      => row.IsFresh ? 0 : System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() - row.Flag!.CreatedAt;

    /// <summary>Draws a single action toggle button. Highlighted when selected, click to toggle.</summary>
    private void DrawActionToggle(PricingItem item, int rowIndex, TriageAction action, string label, TriageAction current)
    {
      var isActive = current == action;
      if (isActive)
      {
        var highlight = action switch
        {
          TriageAction.Vendor => new Vector4(0.8f, 0.2f, 0.2f, 1f),
          TriageAction.Pull => new Vector4(0.2f, 0.5f, 0.8f, 1f),
          TriageAction.Reprice => new Vector4(0.7f, 0.6f, 0.1f, 1f),
          _ => new Vector4(0.4f, 0.4f, 0.4f, 1f),
        };
        ImGui.PushStyleColor(ImGuiCol.Button, highlight);
      }

      if (ImGui.SmallButton($"{label}##{rowIndex}_{action}"))
      {
        if (isActive)
          _actions.Remove(item); // Toggle off
        else
          _actions[item] = action; // Set or change
      }

      if (isActive)
        ImGui.PopStyleColor();
    }

    private static string BuildReason(PricingItem item)
    {
      return item.Result switch
      {
        PricingResult.BelowMinimum =>
          $"Below Minimum (MB/ea at {item.MbPrice:N0} gil < {Plugin.Configuration.MinimumListingPrice:N0} gil min)",
        PricingResult.BelowFloor =>
          $"Below Floor (MB/ea at {item.MbPrice:N0} gil < {item.VendorPrice:N0} gil vendor)",
        PricingResult.CapBlocked =>
          $"Cap ({item.CurrentListingPrice:N0} → {item.MbPrice:N0}, {item.PriceChangePercent:F0}%)",
        PricingResult.UndercutTooDeep =>
          $"Undercut Too Deep ({item.PriceChangePercent:F0}%)",
        PricingResult.UpwardHeld =>
          $"Upward Held (market {item.MbPrice:N0} exceeds {Plugin.Configuration.UpwardRepriceMultiplier:0.#}x own-sales sanity; listed {item.CurrentListingPrice:N0})",
        PricingResult.LaneHeld =>
          item.Lane?.Evidence is { } ev ? $"Held (thin history) — {ev}" : "Held (thin history)",
        PricingResult.NoData => "No Data (no listings)",
        _ => "Unknown"
      };
    }

    private static string BuildDetails(PricingItem item)
    {
      if (item.HistorySaleCount <= 0)
        return "No sales in past 14 days";

      var label = item.HistorySaleCount == 1 ? "sale" : "sales";
      return $"{item.HistorySaleCount} {label} over the past 14 days: median {item.HistoryMedianPrice:N0}, avg {item.HistoryAvgPrice:N0}";
    }

    private static int VsMb(PricingItem item) =>
      item.MbPrice.HasValue ? (item.VendorPrice - item.MbPrice.Value) * item.Quantity : 0;

    /// <summary>Stores triage items from the completed run so they survive after CurrentRun is cleared.</summary>
    internal void SetRun(RunData run)
    {
      _triageItems = run.TriageItems;
      _sortedRows = null; // force re-sort on new data
      _actions.Clear();
    }

    /// <summary>
    /// Retires a row on COMPLETION of its action (vendored, pulled, repriced)
    /// - or when the orchestrator skipped it because it's no longer listed
    /// (sold; the row is moot either way). Closes any matching held flags.
    /// This is the ONLY place actions resolve rows - queue time never does.
    /// </summary>
    internal void RemoveItem(PricingItem item)
    {
      _triageItems.Remove(item);
      _actions.Remove(item);
      CloseFlagsFor(item);
      RefreshHeldFlags();
      _sortedRows = null;
    }
  }
}
