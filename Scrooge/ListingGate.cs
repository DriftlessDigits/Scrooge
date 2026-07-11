using System.Collections.Generic;

namespace Scrooge;

/// <summary>
/// Routing brain Increment 0: the listing gate. Advises AGAINST listing
/// equipment whose better exit is desynth or GC turn-in, judged on local
/// evidence only (own sales, own yields, seal sheet). Items without evidence
/// are marked Unknown, never gated — the gate only acts on what it knows.
/// Verdicts advise; execution of the better exit stays manual until the
/// era builds the executors.
/// </summary>
internal static class ListingGate
{
  internal enum Verdict
  {
    /// <summary>Not routable (non-equipment) or gate disabled — no opinion.</summary>
    None,
    /// <summary>Evidence says it clears the price x velocity floor — list it.</summary>
    Pass,
    /// <summary>No local sale history — listing is a coin flip, not gated.</summary>
    Unknown,
    /// <summary>Fails the floor with no better exit known — informational only.</summary>
    BelowFloor,
    /// <summary>Better exit: desynth (yield value beats the sale price).</summary>
    GateDesynth,
    /// <summary>Better exit: GC Expert Delivery (low/slow on the MB, has seal value).</summary>
    GateGc,
  }

  internal readonly record struct Result(Verdict Verdict, string Reason)
  {
    /// <summary>Gated = advised off the market. Excluded from Select All, defaults unchecked.</summary>
    public bool IsGated => Verdict is Verdict.GateDesynth or Verdict.GateGc;
  }

  /// <summary>
  /// Per-quality melt values from the desynth ledger, keyed by source item.
  /// Build once per evaluation batch via BuildMeltValues.
  /// </summary>
  internal static Dictionary<(uint ItemId, bool IsHq), long> BuildMeltValues(List<DesynthSourceSummary> summaries)
  {
    var melts = new Dictionary<(uint, bool), long>();
    foreach (var s in summaries)
      if (s.Attempts > 0)
        melts[(s.SourceItemId, s.SourceIsHq)] = s.YieldValue / s.Attempts;
    return melts;
  }

  /// <summary>
  /// The equipment listing floor: price x velocity (BP4 Q2). Shared with the
  /// full rules engine so the gate and the pile verdicts can never disagree
  /// about what "worth listing" means. Unknown sit time gets the benefit of
  /// the doubt — evidence only.
  /// </summary>
  internal static bool ClearsEquipmentFloor(int salePrice, int? soldAfterDays)
    => salePrice >= Plugin.Configuration.ListingFloorGil
       && (soldAfterDays is not int days || days <= Plugin.Configuration.ListingVelocityDays);

  /// <summary>
  /// Evaluates one item's aggregated inputs (see RoutingInputService).
  /// Rules only — every piece of evidence arrives pre-gathered.
  /// </summary>
  internal static Result Evaluate(RoutingItemInputs item)
  {
    // Equipment only — the desynth/GC exits are gear exits. Non-gear
    // (mats, consumables) flows through ungated as today.
    if (!item.IsEquipment)
      return new Result(Verdict.None, "");

    if (item.LastSale is null)
      return new Result(Verdict.Unknown, "No sale history for this variant — no evidence to gate on.");

    var (salePrice, _, soldAfterDays) = item.LastSale.Value;

    var floor = Plugin.Configuration.ListingFloorGil;
    var velocityDays = Plugin.Configuration.ListingVelocityDays;

    var saleText = soldAfterDays is int d
      ? $"sold at {salePrice:N0} after {d}d listed"
      : $"sold at {salePrice:N0}";

    if (ClearsEquipmentFloor(salePrice, soldAfterDays))
      return new Result(Verdict.Pass, $"Lists: {saleText}.");

    // Fails price or velocity — is there a better exit with evidence?
    var failText = salePrice < floor
      ? $"{saleText} — below the {floor:N0} floor"
      : $"{saleText} — slower than {velocityDays}d";

    if (item.MeltValuePerAttempt is long melt && melt > salePrice)
      return new Result(Verdict.GateDesynth,
        $"Melt: yields ~{melt:N0}/attempt vs sells ~{salePrice:N0}. ({failText}.)");

    if (item.SealValue is int seals)
      return new Result(Verdict.GateGc,
        $"Churn: {seals:N0} seals. {failText}.");

    return new Result(Verdict.BelowFloor, $"{failText}; no better exit known.");
  }
}
