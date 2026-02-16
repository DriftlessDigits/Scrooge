using System;

namespace Dagobert
{
  /// <summary>
  /// Carries the calculated price from MarketBoardHandler to AutoPinch.
  /// Value > 0 is a valid price; negative values are sentinel codes (see MarketBoardHandler).
  /// </summary>
  internal sealed class NewPriceEventArgs(int newPrice) : EventArgs
  {
    public int NewPrice { get; } = newPrice;
  }
}