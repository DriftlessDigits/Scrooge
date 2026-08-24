using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;

namespace Scrooge.Windows;

/// <summary>
/// The one reader of ImGui's sort state, and the one ordering it drives. Every
/// sortable table in the plugin asked the same two questions - which column is
/// the header arrow on, and which way - and each window had answered them in its
/// own copy of the same eight lines.
/// </summary>
internal static class TableSort
{
  /// <summary>
  /// The raw sort spec ImGui is holding for whichever table is open. Deliberately
  /// an INDEX rather than a named column: the tables that read this have their own
  /// columns, and only a caller that HAS board columns can name one (see
  /// <see cref="BoardLayout.ColumnAt"/>). Unsorted tables read as (0, ascending),
  /// which is the default-sort column every setup marks.
  /// </summary>
  internal static (int Column, bool Ascending) Spec()
  {
    var specs = ImGui.TableGetSortSpecs();
    if (specs.SpecsCount > 0)
    {
      var spec = specs.Specs;
      return (spec.ColumnIndex, spec.SortDirection == ImGuiSortDirection.Ascending);
    }
    return (0, true);
  }

  /// <summary>Orders a list by one key in the direction the header asked for.</summary>
  internal static List<T> Order<T, TKey>(List<T> list, Func<T, TKey> keySelector, bool ascending)
  {
    return ascending
        ? list.OrderBy(keySelector).ToList()
        : list.OrderByDescending(keySelector).ToList();
  }
}
