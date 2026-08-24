using System;

namespace Scrooge;

/// <summary>
/// The seal rate the GC exit actually scored at, plus everything the reason
/// string needs to say so out loud. A discounted score that narrates like a
/// full-rate score is a display lie, and this codebase treats those as
/// first-order bugs — so the effective rate and its justification travel
/// together, never as two operands a caller can drift apart.
/// </summary>
/// <param name="BaseRate">The undiscounted gil-per-seal the batch carried (empirical or config placeholder).</param>
/// <param name="EffectiveRate">What GC scoring must actually multiply seals by.</param>
/// <param name="Discounted">True when the curve took anything off the rate.</param>
/// <param name="Stock">The venture token stock the curve read, or null when the read failed.</param>
/// <param name="Factor">The curve's multiplier at that stock (1 = full value, 0 = worthless).</param>
internal readonly record struct SealRate(
  int BaseRate,
  double EffectiveRate,
  bool Discounted,
  int? Stock,
  double Factor)
{
  /// <summary>
  /// The honesty clause appended to the turn-in reason (and lifted onto the GC
  /// group header). Empty when nothing was discounted — an untouched verdict
  /// reads exactly as it always has. Player language (strings pass, 08-02):
  /// the fact and the consequence, stated in the dial Drift actually thinks in —
  /// TOKENS, never weeks.
  /// </summary>
  internal string Narration => Discounted && Stock is int stock
    ? Factor <= 0.0
      ? $" Seals scored at nothing this round - {stock:N0} ventures stocked, past the point where more seals buy anything you'd use."
      : $" Seal value reduced this round - {stock:N0} ventures stocked, so seals count at ~{Factor * 100:0}% of face."
    : "";
}

/// <summary>
/// THE SEAL S-CURVE (Drift's ruling, 2026-08-05, replacing the 07-25 runway
/// step). Pure — no game, no storage, no config statics; linked into
/// Scrooge.Tests.
///
/// <para>Drift, verbatim: "value of turning in gear for GC is a generic S-type
/// curve, centered on 2k ventures. If I am less that 1k ventures, turn in
/// everything, only melt skill ups. if I am above 3k ventures, melt everything,
/// don't turn in anything." And the goal, same session: "the end result, I
/// hope, is that we naturally stay around 2k ventures."</para>
///
/// <para><b>Why stock, not runway-weeks.</b> The 07-25 design converted Drift's
/// "2k tokens = melt mode" into weeks so a changing burn would move the line
/// with it. His dial never moved — it was always tokens — so the self-adjusting
/// conversion adjusted itself away from the instinct it encoded. (The burn
/// measurement itself was honest — audited 08-05, spend ~1,036 tokens/wk with
/// purchases running alongside — the anchor was simply wrong.) Stock is a
/// direct read: no measurement between the dial and the hand that set it.</para>
///
/// <para><b>The homeostat.</b> The curve is a thermostat around its center:
/// above it seals cheapen, turn-in loses fights, seal inflow slows, and the
/// stockpile drains at the real spend rate; below it the loop reverses. Nothing
/// procedural fires — the centering force is the scoring itself. Smoothstep
/// between the two anchors: exactly 1 at/below the full line, exactly 0
/// at/above the melt line, 0.5 at the midpoint, S-shaped between, no constants
/// that are not Drift's own numbers.</para>
///
/// <para>First law: this changes SCORING only. Nothing here fires anything.
/// The ends need no hard rules — a factor of 1 or 0 wins those arguments
/// arithmetically, and the sub-500 venture panic still owns the desperate
/// end. A9 override-grading stays the retune signal: overriding a curve-scored
/// verdict, in either direction, is the evidence the anchors are wrong.</para>
/// </summary>
internal static class SealRunway
{
  /// <summary>
  /// The curve's value multiplier at a given stock: 1.0 at/below
  /// <paramref name="fullBelow"/>, 0.0 at/above <paramref name="zeroAbove"/>,
  /// smoothstep (3t²-2t³) falling between. Degenerate anchors (crossed or
  /// equal) collapse to a hard step at the melt line rather than dividing by
  /// zero — a broken config degrades to a cliff, never to a premium.
  /// </summary>
  internal static double CurveFactor(int stock, int fullBelow, int zeroAbove)
  {
    // Melt line first, so degenerate anchors (equal or crossed) resolve as the
    // cliff: at the shared line you are stocked, not short.
    if (stock >= zeroAbove) return 0.0;
    if (stock <= fullBelow) return 1.0;
    if (zeroAbove <= fullBelow) return 0.0;

    var t = (stock - fullBelow) / (double)(zeroAbove - fullBelow);
    var s = t * t * (3.0 - 2.0 * t); // smoothstep: 0 at the full line, 1 at the melt line
    return 1.0 - s;
  }

  /// <summary>
  /// The effective gil-per-seal for one batch.
  ///
  /// Fails toward CURRENT BEHAVIOR in every uncertain case — a failed stock
  /// read means the curve is unknowable, and the router never discounts blind.
  /// </summary>
  /// <param name="baseRate">Batch gil-per-seal (empirical or config placeholder).</param>
  /// <param name="ventureStock">Current venture token stock, or null when the read failed.</param>
  /// <param name="fullBelow">At or below this stock, seals score at full value.</param>
  /// <param name="zeroAbove">At or above this stock, seals score at nothing.</param>
  internal static SealRate Effective(int baseRate, int? ventureStock, int fullBelow, int zeroAbove)
  {
    if (ventureStock is not int stock || stock < 0)
      return new SealRate(baseRate, baseRate, false, null, 1.0);

    var factor = CurveFactor(stock, fullBelow, zeroAbove);
    return factor >= 1.0
      ? new SealRate(baseRate, baseRate, false, stock, 1.0)
      : new SealRate(baseRate, baseRate * factor, true, stock, factor);
  }
}
