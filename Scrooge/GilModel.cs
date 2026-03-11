using System;
using System.Collections.Generic;
using System.Text;

namespace Scrooge
{
  /// <summary>
  /// A retainer sale captured from the RetainerHistory hook.
  /// Contains the actual sale data, and the time it was captured.
  /// </summary>
  public record SaleRecord
  {
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;     // ItemUICategory name
    public int UnitPrice { get; init; }                       // Price per unit, in gil.
    public int Quantity { get; init; }
    public long TotalGil => (long)UnitPrice * Quantity;
    public bool IsHQ { get; init; }
    public bool IsMannequin { get; init; }
    public string RetainerName { get; init; } = string.Empty;
    public string BuyerName { get; init; } = string.Empty;
    public long SaleTimest { get; init; }                     // Unix seconds from game server
    public DateTime SaleTime => DateTimeOffset.FromUnixTimeSeconds(SaleTimest).LocalDateTime;

  }

  /// <summary>
  /// A listing snapshot captured during a pinch run.
  /// Tracks time-on-market via FirstSeenTimestamp.
  /// </summary>
}
