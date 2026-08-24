namespace Scrooge;

/// <summary>
/// The Hawk window's Route column, as a pure mapping over RoutingRules.Evaluate.
/// It gates NOTHING - the door gates retired in the cleanup pass, so Select All
/// no longer skips rows and no verdict keeps an item off the market. What
/// survives is the tag: the router's verdict for an item, in one word, beside
/// the checkbox. The map guarantees the tag and the router's pile are the SAME
/// answer and can never disagree about an item.
/// </summary>
internal static class RouteTagMap
{
  internal enum Verdict
  {
    /// <summary>Not routable (non-equipment, banned, always-vendor) — no opinion.</summary>
    None,
    /// <summary>The router would list it.</summary>
    Pass,
    /// <summary>No confident call (never sold, or verdicts too close).</summary>
    Unknown,
    /// <summary>Better exit: vendor - the router would sell it at the counter.
    /// (Renamed from BelowFloor 08-22: it was never about floors, and the one floor
    /// law owns that word - PricingResult.BelowFloor is the real species.)</summary>
    GateVendor,
    /// <summary>Better exit: desynth (yield value beats the sale price).</summary>
    GateDesynth,
    /// <summary>Better exit: GC Expert Delivery (low/slow on the MB, has seal value).</summary>
    GateGc,
  }

  /// <summary>
  /// One row's tag, its reason, and THE TWO NUMBERS THE DECISION TURNED ON (V24,
  /// ruled 08-22): what the winning exit was worth and what the best loser was worth.
  /// The Route column keeps its one word; the hover leads with these before the
  /// router's prose, because "why turn-in and not list" is answered by the pair and
  /// only illustrated by the sentence. Both null on the pre-value early exits (ban,
  /// protection, always-vendor) where no comparison ever ran, and the runner-up alone
  /// is null when only one exit had evidence - an uncontested win, which is a
  /// different fact from a close one.
  /// </summary>
  internal readonly record struct Result(Verdict Verdict, string Reason,
    long? WinnerScore = null, long? RunnerUpScore = null);

  /// <summary>
  /// Evaluates one item by asking the rules engine and mapping its exit to a
  /// tag. Two deliberate carve-outs: banned/always-vendor items get no tag (the
  /// Hawk window has its own handling for both), and a never-sold item whose
  /// verdict is List maps to Unknown, not Pass — the locked Universalis design
  /// says a healthy market never auto-Passes gear the player has no price
  /// evidence for.
  /// </summary>
  internal static Result Evaluate(RoutingItemInputs item, RoutingBatch batch)
  {
    // Equipment only — the desynth/GC exits are gear exits. Non-gear
    // (mats, consumables) carries no tag.
    if (!item.IsEquipment || item.IsBanned || item.IsAlwaysVendor)
      return new Result(Verdict.None, "");

    var verdict = RoutingRules.Evaluate(item, batch);
    var (winner, runnerUp) = Numbers(verdict);

    if (verdict.IsReview)
      return new Result(Verdict.Unknown, verdict.Reason, winner, runnerUp);

    return verdict.Exit switch
    {
      RoutingExit.List => item.LastSale is null
        ? new Result(Verdict.Unknown, verdict.Reason, winner, runnerUp)
        : new Result(Verdict.Pass, verdict.Reason, winner, runnerUp),
      RoutingExit.Desynth => new Result(Verdict.GateDesynth, verdict.Reason, winner, runnerUp),
      RoutingExit.Gc => new Result(Verdict.GateGc, verdict.Reason, winner, runnerUp),
      RoutingExit.Vendor => new Result(Verdict.GateVendor, verdict.Reason, winner, runnerUp),
      // Ban/Hold are unreachable here (guarded above; this context passes no
      // protections) — defensively: no opinion.
      _ => new Result(Verdict.None, ""),
    };
  }

  /// <summary>
  /// The winning exit's score and the best score any other exit reached, off the
  /// four the verdict already carries. Reads <see cref="RoutingVerdict.Scores"/> and
  /// nothing else - so the pair the hover shows is the pair the router compared,
  /// never a second scoring pass that could disagree with the first.
  /// </summary>
  private static (long? Winner, long? RunnerUp) Numbers(RoutingVerdict verdict)
  {
    if (verdict.Scores is not { } scores) return (null, null);

    long? winner = null, runnerUp = null;
    foreach (var exit in BoardCalls.Exits)
    {
      if (BoardCalls.ScoreOf(scores, exit) is not long score) continue;
      if (exit == verdict.Exit) winner = score;
      else if (runnerUp is not long best || score > best) runnerUp = score;
    }
    return (winner, runnerUp);
  }
}
