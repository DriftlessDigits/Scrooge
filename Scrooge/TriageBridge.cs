namespace Scrooge;

/// <summary>
/// THE SEAM BETWEEN THE BOARD AND THE WALK (Task 3, 08-15). The walk and the case
/// tribunal speak <see cref="BoardPile"/>; the board's cells, doors and staging
/// dictionary speak <see cref="RoutingExit"/>. Every translation between the two
/// lives HERE rather than in the window, for the reason the whole plan layer exists:
/// a routing or evidence decision written in an ImGui method is a decision nobody can
/// pin a test to.
///
/// <para>Pure and Dalamud-free (the BoardPiles / TriageWalk mold, linked into
/// Scrooge.Tests).</para>
/// </summary>
internal static class TriageBridge
{
  /// <summary>
  /// The exit a case verdict presses. It is the inverse of
  /// <see cref="BoardPiles.ForRoutingExit"/> over the four doors a case can be ruled
  /// to, and nothing else - Review, Defer and Silent are not verdicts, they are the
  /// eyes axis, and a case ruled "Review" would be the human answering the question
  /// with the question.
  /// </summary>
  internal static RoutingExit? ExitFor(BoardPile pile) => pile switch
  {
    BoardPile.List => RoutingExit.List,
    BoardPile.Melt => RoutingExit.Desynth,
    BoardPile.Churn => RoutingExit.Gc,
    BoardPile.PullAndVendor => RoutingExit.Vendor,
    _ => null,
  };

  /// <summary>
  /// WHAT A MELT NUMBER IS, in the case's own vocabulary. The grade already answers
  /// this for every other surface (<see cref="MeltGrade"/>, 08-03) - this only says
  /// it in the word the case parks beside the gil, so a band average can never sit in
  /// a tribunal looking exactly like twelve of your own desynths.
  ///
  /// <para>The skill-up knob reads as a PLACEHOLDER, deliberately: it is the player's
  /// own dial and not a measurement of anything, which is precisely what
  /// <see cref="WorthProvenance.Placeholder"/> says.</para>
  /// </summary>
  internal static WorthProvenance MeltProvenance(MeltGrade grade) => grade switch
  {
    MeltGrade.Measured => WorthProvenance.Measured,
    MeltGrade.Prior => WorthProvenance.BandPrior,
    // The Archeo Kingdom Scepter (F3, ruled 08-22): "(placeholder, never
    // measured)" beside the player's own 100k ruling apologized for a constant.
    // A skill-up worth is a peg - certain by definition, and the parenthetical
    // says whose it is instead of doubting it.
    MeltGrade.Skillup => WorthProvenance.Peg,
    _ => WorthProvenance.Placeholder,
  };

  /// <summary>
  /// WHAT A LIST NUMBER IS. Your own settled sale is the only measurement in the
  /// building; a community median is an ask somebody posted, which is exactly
  /// <see cref="WorthProvenance.Asked"/>; nothing behind it at all is a placeholder
  /// and says so rather than borrowing the credit of the two above it.
  /// </summary>
  internal static WorthProvenance ListProvenance(long ownSalePrice, long communityMedian)
    => ownSalePrice > 0 ? WorthProvenance.Measured
      : communityMedian > 0 ? WorthProvenance.Asked
      : WorthProvenance.Placeholder;

  /// <summary>The GC number is only as measured as the seal rate behind it (the
  /// display lie <see cref="CaseVoice.SealRate"/> exists to stop).</summary>
  internal static WorthProvenance SealProvenance(bool rateMeasured)
    => rateMeasured ? WorthProvenance.Measured : WorthProvenance.Placeholder;

  /// <summary>
  /// WHY THIS ROW IS A CASE - the three referral reasons, read off the two facts the
  /// board already published about it.
  ///
  /// <list type="number">
  /// <item>A CONTRADICTED tier is the Alexander rule having fired. It wins outright:
  /// it is the only one of the three that names a specific piece of evidence standing
  /// against a specific verdict, and burying that under "too close" would answer a
  /// question nobody asked.</item>
  /// <item>No scored exit at all is <see cref="ReferralReason.NoData"/> - nothing was
  /// ever measured, and the case says so instead of dressing a default as a finding.</item>
  /// <item>Anything else got here because the router declined to pick between numbers
  /// it had: too close to call.</item>
  /// </list>
  /// </summary>
  internal static ReferralReason ReasonFor(ConfidenceTier tier, bool anyScore)
    => tier == ConfidenceTier.Contradicted ? ReferralReason.Contradicted
      : anyScore ? ReferralReason.TooClose
      : ReferralReason.NoData;
}
