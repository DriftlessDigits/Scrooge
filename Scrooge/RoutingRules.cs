using System;

namespace Scrooge;

/// <summary>
/// One of the five exits an item can route to, plus Hold for protected items.
/// Review is not an exit — it's a verdict state (see RoutingVerdict.IsReview).
/// </summary>
internal enum RoutingExit
{
  /// <summary>Earns real gil on the market board — Hawk run lists it.</summary>
  List,
  /// <summary>Worthless everywhere else — NPC vendor.</summary>
  Vendor,
  /// <summary>Player-marked never-touch. Observed, untouched.</summary>
  Ban,
  /// <summary>Skillup value or yields beat the alternatives — melt it.</summary>
  Desynth,
  /// <summary>GC Expert Delivery — seals beat gil, or venture stock demands it.</summary>
  Gc,
  /// <summary>Protected (gearset / spiritbond / materia) — never auto-routed.</summary>
  Hold,
}

/// <summary>
/// WHERE A MELT NUMBER CAME FROM. The full reason string has always said it
/// ("yields ~1,064 gil/attempt from your ledger" vs "ilvl 500-549 band average
/// over 204 melts of 169 items - no history for this one"), but the reason only
/// rides the verdict, and the verdict only carries the melt clause when melt
/// WON. Everywhere else - the score cells, their tooltips, the pile headers -
/// a band average and a measurement rendered as the same digits, in exactly the
/// place Drift compares them (08-03: two different items priced 1,474 apiece,
/// because they share an ilvl band). The grade travels with the number now.
/// </summary>
internal enum MeltGrade
{
  /// <summary>No melt number at all.</summary>
  None,
  /// <summary>Own-ledger desynths of THIS item. Evidence.</summary>
  Measured,
  /// <summary>The ilvl band's average. A reasonable expectation, not a measurement of this item.</summary>
  Prior,
  /// <summary>The player's own skill-up knob. Not gil from mats at all.</summary>
  Skillup,
}

/// <summary>
/// The four gil-equivalent scores on the table at decision time - the
/// ALTERNATIVES, not just the winner. The routing receipt (V20) persists these
/// so the 4.0 scoreboard can ask the counterfactual ("what did churning this
/// leave on the table?"). Null = that exit had no evidence when the call was
/// made. List carries the value the router actually weighed (own sale, or the
/// community median when that was the witness).
///
/// <para><see cref="MeltGrade"/> is not a fifth score - it is what the fourth
/// number IS, carried so a cell can tell a measurement from an estimate without
/// re-reading the reason it does not have. Since V44 the receipt banks it too:
/// a melt score read back a year later has the same problem the cell had, and
/// the scoreboard's skillup crossover cannot even be asked without it.</para>
/// </summary>
internal readonly record struct RoutingScores(long? List, long? Gc, long? Melt, long? Vendor,
  MeltGrade MeltGrade = MeltGrade.None);

/// <summary>
/// The rules engine's answer for one item. The reason string is the product —
/// it's what makes the pile trustworthy enough to one-confirm. When two exits
/// score too close to call, IsReview is set and both reasons are carried:
/// the router prefers a small honest Review pile over a confident wrong routing.
/// Scores rides along for the receipt; null on the pre-value early exits
/// (ban / protected / always-vendor), where no comparison ran.
///
/// IsExcluded is the "no exits" outcome (Drift, 07-23): gear with zero viable
/// exits is not a decision, so the Ledger drops it entirely rather than routing
/// it. The Exit field is inert on an excluded verdict (nothing reads it — the
/// materializer filters on the flag first); it is set to Hold so a leak past the
/// filter fails safe into the observed-not-routed SILENT board, never a phantom
/// action.
/// </summary>
internal readonly record struct RoutingVerdict(
  RoutingExit Exit,
  string Reason,
  bool IsReview = false,
  RoutingExit? RunnerUp = null,
  string RunnerUpReason = "",
  RoutingScores? Scores = null,
  bool IsExcluded = false,
  // TRUE only at the review band's own door (ruled 08-23, the Nightsteel batch):
  // two scores genuinely too close to call. The case page's "a dead heat" sentence
  // keys off THIS, never off IsReview - the other review doors (thin melt lead,
  // Look-priced List, no-evidence shrugs) are referrals, not ties, and a 20,454
  // List reviewed against a 2,154 GC narrated as "a dead heat" would be the
  // Silvergrace lie with fresh paint.
  bool BandTie = false);

/// <summary>
/// The routing brain's decision core. Deterministic rules over pre-gathered
/// evidence (RoutingItemInputs) and a batch-snapshotted config — same inputs,
/// same pile, every time. Pure functions: no game reads, no storage, no
/// statics — unit-testable without FFXIV (see Scrooge.Tests).
///
/// Rule order (first match wins): ban, protection, always-vendor flags;
/// then the value rules — list evidence,
/// desynth skillup, GC seals, melt-for-gil, vendor fallback. Items whose
/// evidence is genuinely ambiguous land in Review instead of a guess.
/// </summary>
internal static class RoutingRules
{
  /// <summary>
  /// HOW LONG YOUR OWN LAST SALE STAYS THE SENIOR WITNESS. Past this, the local tape
  /// counts as silent and the DC-wide community read is allowed to speak (SF-B2, ruled
  /// 2026-08-13: "let the DC speak if the local sale is stale"). This is WITNESS
  /// SENIORITY - how long one receipt of yours outranks everybody else's - and it is the
  /// only thing it measures.
  ///
  /// <para><b>NOT the same 14 as <see cref="BoardConfidence.EvidenceStaleDays"/></b>
  /// (3b-8, RULED B1.5b/c). The two agreed at 14 by coincidence and the old doc claimed
  /// one "mirrored" the other, which welded two measurements that answer different
  /// questions: this one asks WHOSE evidence wins, that one asks whether ANY evidence is
  /// still fresh enough to act on. They are kept apart deliberately so tuning either
  /// cannot move the other by accident - the same lesson the spread un-weld taught one
  /// row over.</para>
  ///
  /// <para><b>A constant, not a knob, moved only by evidence</b> (the B1.5a frame). It
  /// is a model internal, and it is NOT the player's "does this move / is it worth
  /// listing" gauge - that was <c>ListingVelocityDays</c>, which died in Phase 1, and
  /// the liquidity want it served is answered by a "last sold X days ago" display. No
  /// player-facing sentence quotes this number.</para>
  ///
  /// <para>It used to run by accident: a <see cref="RoutingConfig"/> init property that
  /// <c>RoutingInputService.BeginBatch</c> never copied, so the record default always
  /// won. Same behaviour, honest mechanism.</para>
  /// </summary>
  internal const int LocalSaleSeniorityDays = 14;

  /// <summary>
  /// Rule 7's "meaningfully": melt must beat vendor by this factor before
  /// desynth-for-gil wins. A melt value barely above vendor isn't worth the
  /// attempt time; between 1x and this factor the call goes to Review.
  /// </summary>
  private const double MeltOverVendorFactor = 1.5;

  /// <summary>
  /// The velocity window, in days, the "never sold one" narration reads: a market
  /// at or above 1/this many days is described as moving. It was the
  /// ListingVelocityDays knob until the door gates retired; it survives as a
  /// constant because it no longer gates anything - both arms of the branch that
  /// consults it return the same List verdict, and only the sentence differs.
  /// </summary>
  private const int MarketVelocityWindowDays = 10;

  /// <summary>
  /// Universalis's home-world velocity (units/day) at or above this rate means a
  /// unit moves inside <see cref="MarketVelocityWindowDays"/>.
  /// </summary>
  private const double MarketVelocityFloor = 1.0 / MarketVelocityWindowDays;

  /// <summary>
  /// The zero-exit determination: an item every router exit is closed for. All
  /// four exits, expressed as the existing evidence signals — never new
  /// eligibility logic:
  ///   List   — <see cref="RoutingItemInputs.IsMarketable"/> (has a market category)
  ///   Melt   — <see cref="RoutingItemInputs.IsDesynthable"/> (sheet Desynth != 0),
  ///            or any melt evidence / skillup eligibility (which only exist for a
  ///            desynthable item, OR'd in so a melt-carrying item is never a dead end)
  ///   Gc     — <see cref="RoutingItemInputs.SealValue"/> (GcSeals.For, which itself
  ///            returns null when PriceLow == 0 — the "too new" gate)
  ///   Vendor — <see cref="RoutingItemInputs.VendorPrice"/> &gt; 0 (PriceLow)
  /// When every one is closed there is no gil/seal/list destination the item can
  /// reach, so it is no decision at all. Pure — same inputs, same answer; it is
  /// recomputed from the sheet on every scan, so re-inclusion is automatic once
  /// SE flips any signal.
  /// </summary>
  internal static bool HasNoViableExit(RoutingItemInputs item)
  {
    var meltExit = item.IsDesynthable
      || item.MeltValuePerAttempt is not null
      || item.DesynthSkillupEligible;
    return !item.IsMarketable
      && !meltExit
      && item.SealValue is null
      && item.VendorPrice <= 0;
  }

  /// <summary>
  /// THE batch's seal rate - one derivation, so scoring and the receipts can never
  /// disagree about what a turn-in was actually worth.
  ///
  /// <para>It exists as its own method because the receipt writer needs the SAME
  /// answer <see cref="Evaluate"/> scored with, including the stock normalization
  /// (a non-positive token read is "unknown", not "zero"). Re-deriving it at the
  /// insert site would be two numbers that agree until the day one of them
  /// changes - and the whole point of <see cref="SealRunway"/> is that a
  /// discounted score must never narrate, or persist, as a full-rate one.</para>
  /// </summary>
  internal static SealRate SealRateFor(RoutingBatch batch)
    => SealRunway.Effective(
      batch.SealToGilRate,
      batch.VentureStock is int s && s > 0 ? s : null,
      batch.Rules.SealCurveFullBelow,
      batch.Rules.SealCurveZeroAbove);

  internal static RoutingVerdict Evaluate(RoutingItemInputs item, RoutingBatch batch)
  {
    // --- Flag rules: the player already decided these ---

    if (item.IsBanned)
      return new(RoutingExit.Ban, "On your ban list.");

    if (item.IsProtected)
      return new(RoutingExit.Hold, item.ProtectionReason.Length > 0
        ? $"Protected: {item.ProtectionReason}."
        : "Protected.");

    if (item.IsAlwaysVendor)
      return new(RoutingExit.Vendor, "Always-vendor flag.");

    // --- No viable exit: not a decision, so not in the Ledger at all ---
    // Drift's 07-23 ruling: gear that can't be listed, melted, turned in, OR
    // vendored has no exit to weigh — the router must not manufacture a verdict
    // or a Review row for it. The materializer filters on IsExcluded (one place)
    // so it never joins a pile, a bulk/round count, or a receipt. Runs after the
    // player-flag rules (an explicit ban / protection / always-vendor is a state
    // the player chose to see) but before every inferred rule below. The
    // determination is LIVE — recomputed from the sheet each scan — so when SE
    // later grants the item a sale value (or lifts the desynth/trade lock) the
    // next scan simply stops excluding it. Nothing is persisted to unset.
    if (HasNoViableExit(item))
      return new(RoutingExit.Hold,
        "No viable exit (untradable, not desynthable, no seals, no vendor value) — nothing to decide. Ignored until the game gives it a sale value.",
        IsExcluded: true);

    // Tilt only acts on a POSITIVE token read; null (read failed) and 0
    // (ambiguous until the token id is verified in-game) mean no tilt —
    // the router never tilts on missing evidence.
    //
    // VENTURE PANIC RETIRED (cleanup pass): a sub-500 hard override on top of the
    // seal S-curve was two mechanisms answering one question. The curve owns the
    // whole stock range - at panic stock it is already scoring seals at full value,
    // so the turn-in wins the ordinary comparison on its merits or it should not
    // win at all. See SealRunway.
    var cfg = batch.Rules;
    var stock = batch.VentureStock is int s && s > 0 ? s : (int?)null;

    // --- Value rules: gil-equivalent scores from local evidence ---

    // The seal S-curve (Drift, 2026-08-05): seal value slides with venture token
    // stock - full below the low anchor, nothing above the melt line, centered
    // on the 2k pivot - so the stockpile self-centers. The decision is pure and
    // lives in SealRunway; the rate and its narration travel together so a
    // curved score can never render as a full-rate one.
    var seal = SealRateFor(batch);
    var sealRate = seal.EffectiveRate;
    // The placeholder rate is a guess wearing numbers — the reason says so
    // until venture returns measure the real rate.
    var rateTag = batch.SealRateEmpirical ? "" : ", rough";
    var gcScore = item.SealValue is int seals ? (long)Math.Round(seals * sealRate) : (long?)null;
    var gcReason = item.SealValue is int gs
      ? $"Turn in: {gs:N0} seals (~{gcScore:N0} gil at {sealRate:0.##} gil/seal{rateTag}).{seal.Narration}"
      : "";

    // A non-equipment desynthable (fish, furnishings) melts ONLY for a skill
    // up (ruled 2026-08-28: "fish melt for shit"; "if a housing item gives me
    // a skill up, we should include it as an option just like anything else").
    // Without a color on offer the melt door is closed - measured yield
    // included - and the skillup block below is the one way back in.
    var skillupIsTheOnlyMeltCase = !item.IsEquipment && !item.DesynthSkillupEligible;

    var meltScore = skillupIsTheOnlyMeltCase ? null : item.MeltValuePerAttempt;
    var meltGrade = meltScore is null ? MeltGrade.None : MeltGrade.Measured;
    var meltReason = meltScore is long mv ? $"Desynth: yields ~{mv:N0} gil/attempt from your ledger." : "";

    // THE MELT PRIOR (Drift 07-25). Gear the ledger has never seen melted scored
    // melt as NULL, and a null loses every comparison it enters - so the turn-in
    // was winning by forfeit, not on the merits. The band average is what we
    // actually know about gear of this weight, and it is enough to make the
    // comparison real. Item history SUPERSEDES it (seed, then measured): the
    // prior only ever fills a null, and only for gear the game will actually
    // desynth. Non-desynthable gear keeps its null and keeps losing to the
    // turn-in, which is the correct answer there.
    //
    // The narration is load-bearing. A band estimate that renders like item
    // history invites Drift to act on a measurement nobody made, so it names the
    // band, the sample, and the absence out loud.
    // Equipment only: the band average is GEAR knowledge - a fish reading it
    // would borrow yields it doesn't have (same ruling as above).
    if (meltScore is null && item.IsEquipment && item.IsDesynthable && item.MeltBandPrior is { } prior)
    {
      meltScore = prior.ValuePerAttempt;
      meltGrade = MeltGrade.Prior;
      var reach = prior.Widened ? " widened" : "";
      meltReason = $"Desynth: ~{prior.ValuePerAttempt:N0} gil expected ({prior.BandLabel}{reach} band average over {prior.Attempts:N0} melts of {prior.SourceItems:N0} items — no history for this one).";
    }

    // Skillup pricing (Drift 07-18: price the skillup, don't gate it). A rare
    // red/yellow skillup makes the desynth candidate worth AT LEAST the
    // configured gil value; it then competes in the ordinary comparison, so
    // "very-very-high gil beats a skillup" is emergent (a 200k sale outbids a
    // 100k red; a 20k sale loses to it; near-worth lands in Review honestly).
    if (item.DesynthSkillupEligible)
    {
      long worth = item.DesynthColor == DesynthSkillupColor.Red
        ? cfg.SkillupWorthRed : cfg.SkillupWorthYellow;
      if (worth > (meltScore ?? 0))
      {
        meltScore = worth;
        // The knob outbid the yields, so the number on the cell is no longer a
        // yield at all - measured or estimated. Say what it actually is.
        meltGrade = MeltGrade.Skillup;
        meltReason = $"Skillup: {item.DesynthColor?.ToString().ToLowerInvariant()} desynth at ilvl {item.Ilvl} — worth {worth:N0} gil to you (skillups are scarce).";
        // The outbid clause is appended AFTER the List ladder seats its witness
        // (below) - it must quote the operand List actually carried, which is
        // not known yet here.
      }
    }

    var vendorScore = item.VendorPrice > 0 ? (long)item.VendorPrice : (long?)null;
    var vendorReason = vendorScore is long vv ? $"Vendor: {vv:N0} gil." : "";

    // THE ONE FLOOR LAW (ruled 2026-08-21), asked here with the same arithmetic the
    // pinch asks it with. An ask that cannot clear the effective floor has no legal
    // listing behind it, so List forfeits and every other exit competes normally -
    // the router never clamps a price up to a floor to manufacture one.
    var floor = PriceFloor.Effective(cfg.FloorMode, item.VendorPrice, cfg.MinimumListingPrice);

    // THE OWN SALE'S SILENCE (SF-B2, ruled 2026-08-13): no sale ever, or none
    // inside the staleness window. Every witness-precedence gate below reads this
    // one predicate - it is a fact about the OWN-sale witness only; the settled
    // tape and the DC have their own voices below it.
    var ownSaleSilent = item.LastSale is null
      || (item.LastSaleAgeDays is int staleSaleAge && staleSaleAge > LocalSaleSeniorityDays);

    // THE SETTLED TAPE SPEAKS (F6, ruled 08-22) when this world's banked ring
    // carries enough sales to trust - the same min-samples bar the DC witness
    // answers to. Age never silences it outright (the ring is recency-WEIGHTED,
    // ruled: "I don't want to vendor a 100k item just because it hasn't sold in
    // 14 days"); it only decays the median toward what still clears.
    var tapeSpeaks = item.LocalTapeMedian is long && item.LocalTapeSampleCount >= cfg.CommunityMinSamples;

    // Rule 4 — list evidence, in F4's ruled precedence: your own fresh sale, then
    // THIS WORLD's settled tape, then (in the veto branch below) the DC. A sale
    // is evidence of what the market pays, and the exits compete on score. A sale
    // too small to be worth listing simply loses to melt, seals or the vendor.
    long? listScore = null;
    var listReason = "";
    var tapeScoredList = false;
    var lookScoredList = false;
    if (!ownSaleSilent && item.LastSale is { } sale)
    {
      listScore = sale.Price;
      listReason = sale.SoldAfterDays is int d
        ? $"List: sold at {sale.Price:N0} gil after {d}d listed."
        : $"List: sold at {sale.Price:N0} gil.";
    }
    else if (tapeSpeaks && item.LocalTapeMedian is long tapeMedian)
    {
      // The router's List witness is everyone's settled sales on OUR board -
      // the accuser finally seated as a witness. The reason names the scope
      // (this world) and the weighting, per the instrument-honesty law.
      listScore = tapeMedian;
      tapeScoredList = true;
      var age = item.LocalTapeAgeDays is double a && a >= 1 ? $", ~{a:0}d weighted age" : "";
      listReason = $"List: this world's settled sales clear ~{tapeMedian:N0} gil ({item.LocalTapeSampleCount} banked{age}).";
    }
    else if (item.LookAsk is long look)
    {
      // THE LOOK'S RUNG (ruled 08-23, the Nightsteel Sword: a 5-seller queue at
      // 20,456+ lost to 1,450 seals with List never scored). Recon ran the whole
      // pricing spine on this item's real board and banked its answer; that IS
      // the router's best available evidence when the settled rungs are silent.
      // Reaching this rung at all means the sale history is thin - so a List
      // that WINS on the Look's number goes to Bag decisions rather than riding
      // unattended (Drift: "if the sale history is thin or we aren't confident
      // in the price calc, then send it to Decisions"); a Look that loses fairly
      // lets the better exit ride - the lane was consulted, which is the whole fix.
      listScore = look;
      lookScoredList = true;
      var age = item.LookAgeHours is double h
        ? h < 1 ? ", read under an hour ago" : $", read {h:0}h ago"
        : "";
      listReason = $"List: your Look priced it at {look:N0} gil off the live queue{age}.";
    }

    // Every verdict from here down carries the four scores as weighed - the
    // receipt's alternatives. listOverride: branches where the List witness is
    // the community median (not an own sale) put the value they actually used
    // in the List slot.
    RoutingVerdict Scored(RoutingVerdict v, long? listOverride = null)
      => v with { Scores = new RoutingScores(listOverride ?? listScore, gcScore, meltScore, vendorScore, meltGrade) };

    // THE HONEST ASK the router has a witness for, in the same precedence the List
    // scoring above uses: our own sale while it is live, this world's settled tape,
    // the DC's read last - resolved ONCE so the forfeit is one fact about the item
    // rather than a different answer per branch. No witness means no ask, and a
    // floor cannot refuse a price nobody named: the item routes exactly as it did
    // before the law existed.
    long? honestAsk = !ownSaleSilent && item.LastSale is { } liveSale
      ? (long?)liveSale.Price
      : tapeSpeaks && item.LocalTapeMedian is long tapeAsk
        ? tapeAsk
        // The Look before the DC (F4's local-outranks-DC law, 08-23): recon read
        // OUR board; the DC is another market's story.
        : item.LookAsk is long lookAsk2
          ? lookAsk2
          : item.CommunityMedian is long dcMedian && item.CommunitySampleCount >= cfg.CommunityMinSamples
            ? dcMedian
            : null;
    var floorForfeitsList = honestAsk is long ask && floor.Refuses(ask);

    if (floorForfeitsList)
    {
      // The List exit simply produces no score; the others compete normally.
      listScore = null;
      listReason = "";

      // THE DOMAN DESTINY (ruled 2026-08-21). Under the Enclave floor a forfeited item
      // is worth twice vendor AT THE ENCLAVE, so sending it to the counter realizes
      // half of what holding it is worth. The vendor exit closes for this item; melt
      // and the turn-in compete on their merits, and if nothing scores the item is
      // HELD in the bags. No enclave exit, no automation, no budget tracking - the
      // hold pile is advice.
      if (cfg.FloorMode == PriceFloorMode.DomanEnclave)
      {
        vendorScore = null;
        vendorReason = "";
      }
    }

    // THE OUTBID CLAUSE names the SEATED witness (ruled 2026-08-29, the
    // Ceremonial Earring receipt: the tape seated List at ~70,000 while this
    // sentence quoted the unseated Look at 99,499 - one row, two "what List is
    // worth" numbers). The contest operand is the List score the cell shows;
    // when the seat is not the Look, the ask still rides as a labeled aside in
    // recon's own grammar - "what it settles for" and "what you'd ask" answer
    // different questions. Only spoken when the outbid is numerically true;
    // a floor-forfeited List had no witness to outbid and stays unquoted.
    if (meltGrade == MeltGrade.Skillup && meltScore is long meltWorth
        && listScore is long seated && meltWorth > seated)
    {
      meltReason += lookScoredList
        ? $" Outbids your Look at {seated:N0}."
        : tapeScoredList
          ? $" Outbids what it settles for, ~{seated:N0} ({item.LocalTapeSampleCount} sales)."
          : $" Outbids your own sale at {seated:N0}.";
      if (!lookScoredList && item.LookAsk is long aside && aside != seated)
        meltReason += $" Listed, you'd ask {aside:N0}.";
    }

    // Rule 4 winner check: List wins outright only when a LIVE sale beats every
    // scored exit - the exits compete on score, and a sale is not a trump card.
    // A stale sale falls through instead: the community veto (rule 6) and the
    // evidence-only branch below are the seats built for a silent local tape,
    // and a sale outside the staleness window must not shortcut past them.
    if (listScore is long list && (!ownSaleSilent || tapeScoredList || lookScoredList)
        && list >= (meltScore ?? 0) && list >= (gcScore ?? 0) && list >= (vendorScore ?? 0))
    {
      // A win that rests on the Look alone goes to Bag decisions, always (ruled
      // 08-23): the rung only seats when the settled rungs are silent, so the
      // history is thin by construction and the human rules the exit. The Look's
      // number and the runner-up both ride so the case argues with real operands.
      if (lookScoredList)
      {
        var best = BestOf((RoutingExit.Desynth, meltScore, meltReason),
                          (RoutingExit.Gc, gcScore, gcReason),
                          (RoutingExit.Vendor, vendorScore, vendorReason));
        return Scored(new(RoutingExit.List,
          $"{listReason} Too few settled sales to trust it alone - your call.",
          IsReview: true, RunnerUp: best.Exit, RunnerUpReason: best.Reason));
      }
      return Scored(Resolve(RoutingExit.List, list, listReason,
        BestOf((RoutingExit.Desynth, meltScore, meltReason),
               (RoutingExit.Gc, gcScore, gcReason),
               (RoutingExit.Vendor, vendorScore, vendorReason)),
        stock, cfg));
    }

    // Rule 6 — GC turn-in: seals beat every gil exit, or low stock tilts it.
    if (gcScore is long gc)
    {
      var bestGil = Math.Max(meltScore ?? 0, vendorScore ?? 0);
      // THE LOW-STOCK DECREE IS GONE (ruled 08-22): sub-750 stock used to force GC
      // over any gil exit under 45k, here and on the list branch - the last seat
      // where an exit won by decree instead of by score. Exits compete on score,
      // period; restocking is the player's call, and the dashboard already says
      // "600 tokens" in warning color where he decides such things. The S-curve
      // keeps seals at full value when stock is low, which is the honest version
      // of the same instinct: it raises the SCORE, it does not overrule the fight.
      // Community veto (almanac cross-check): gear whose LOCAL tape is silent -
      // no sale ever, or none inside the staleness window (SF-B2, ruled
      // 2026-08-13: "let the DC speak if the local sale is stale") - can win
      // seals by forfeit, because a silent tape produces no list score and the
      // market was never consulted. DC-scope settled sales are the missing
      // witness: when the community pays more than the seals are worth, the
      // item routes to the Hawk run instead (which prices it off the live board
      // through the same community lane). The 08-13 receipt: an April sale at
      // 13,338 silenced 9 fresh DC sales at ~7,780 and a Labrys sat unruled in
      // Review over a question the DC had already answered. A FRESH local sale
      // still outranks the DC - it is our own tape, on our own board. Resolve
      // applies the normal review band and venture tilt, so a near-tie still
      // degrades honestly. Runs AFTER the low-stock guard on purpose: thin
      // venture stock still churns.
      // ... and a fresh Look closes the veto's door (final pass, 08-23): the
      // veto's whole premise is "the market was never consulted", and a Look IS
      // the market consulted - recon read our own live board. A Look that lost
      // rule 4 lost to a better exit fairly; letting the DC re-open List here
      // would seat another market's story above our own board's (F4) and ride
      // it confident where the Look rung's own win is review by construction.
      if (item.IsMarketable && ownSaleSilent && !tapeSpeaks && !lookScoredList
          && !floorForfeitsList
          && item.CommunityMedian is long cm
          && item.CommunitySampleCount >= cfg.CommunityMinSamples
          && cm > gc && cm > (meltScore ?? 0))
      {
        // The reason names the overruled sale when there is one - a verdict that
        // quietly ignored the player's own tape would read as the router
        // forgetting it, not outranking it.
        var staleClause = item.LastSale is { } overruled
          ? $" Your own sale ({overruled.Price:N0}) is {item.LastSaleAgeDays}d old - the DC's read is fresher."
          : "";
        return Scored(Resolve(RoutingExit.List, cm,
          $"List: the DC pays ~{cm:N0} gil ({item.CommunitySampleCount} sales, Universalis community) — beats {gcReason.TrimEnd('.')}.{staleClause} The Hawk run prices it off the live MB.",
          (RoutingExit.Gc, gcScore, gcReason), stock, cfg), listOverride: cm);
      }

      if (gc > bestGil)
        return Scored(Resolve(RoutingExit.Gc, gc, gcReason,
          BestOf((RoutingExit.Desynth, meltScore, meltReason),
                 (RoutingExit.Vendor, vendorScore, vendorReason)),
          stock, cfg));
    }

    // Rule 7 — melt for gil: yields must beat vendor meaningfully. No vendor
    // price is read as NO vendor floor (0), not an unknown one — deliberate:
    // for unvendorable gear any known-positive melt is the only gil exit,
    // and "unknown floor" would strand the item in the shrug below.
    if (meltScore is long melt && melt > 0)
    {
      var vendorFloor = vendorScore ?? 0;
      if (melt > vendorFloor * MeltOverVendorFactor)
        // List rides as a runner-up candidate: a sale just under a priced
        // skillup's worth reaches here, and the review band should see it.
        return Scored(Resolve(RoutingExit.Desynth, melt, meltReason,
          BestOf((RoutingExit.Vendor, vendorScore, vendorReason),
                 (RoutingExit.List, listScore, listReason)),
          stock, cfg));
      if (melt > vendorFloor)
        return Scored(new(RoutingExit.Desynth, meltReason, IsReview: true,
          RunnerUp: RoutingExit.Vendor,
          RunnerUpReason: $"{vendorReason} Desynth lead is thin — attempt time may not pay."));
    }

    // THE DOMAN DESTINY's verdict (ruled 2026-08-21). Reaching here with the floor
    // holding List shut under the Enclave mode means melt and the turn-in have both
    // had their comparison and neither won, and the vendor exit was closed on purpose.
    // The honest answer is that the item is worth more sitting in the bags.
    if (floorForfeitsList && cfg.FloorMode == PriceFloorMode.DomanEnclave)
    {
      // The Enclave pays twice vendor - ALWAYS that, never floor.Floor, which is
      // a max() that reads as the player's minimum whenever the minimum is the
      // binding rule (final pass, 08-23: "worth 5,000 there" over an Enclave that
      // pays 20). When the minimum bound, the sentence names it - the verdict
      // must send the player to the right knob.
      var enclavePays = PriceFloor.For(PriceFloorMode.DomanEnclave, item.VendorPrice);
      var boundBy = floor.Binding == FloorBinding.PlayerMinimum
        ? $" Your {floor.Floor:N0} minimum kept it off the board."
        : "";
      return Scored(new(RoutingExit.Hold,
        $"Kept in bags for the Enclave - worth {enclavePays:N0} there vs {item.VendorPrice:N0} at a vendor.{boundBy}"));
    }

    // Evidence-only: gear the router knows nothing about is YOUR call,
    // not a vendor trip. The Universalis almanac upgrades this from a guess
    // when it can — a market that moves keeps the List call (the Hawk run
    // prices off the live MB at list time); a dead market means a listing
    // never sells, so vendor is the honest exit. No almanac data = today's
    // behavior: leans List but lands in Review — the router won't guess.
    // Silent tape rather than strictly no-sale (SF-B2): a stale local sale is
    // the same evidential silence, and this branch's community/velocity
    // witnesses deserve the same chance to speak here as at the veto above.
    // A forfeited item never reaches the lean-List shrugs below: this block's whole
    // job is to hand an unpriced item to the List exit for the Hawk run to price, and
    // the law has just said no legal listing exists for it. It falls to the vendor
    // rule instead (the Enclave's own answer was given above).
    // A Look bars this door too (final pass, 08-23): "the router knows nothing
    // about" is false once recon has priced the item off our live board. A Look
    // that scored and lost falls through to rule 8 with its loss on the record,
    // instead of this branch narrating "no price on record" over a banked one.
    if ((item.IsEquipment || item.IsDesynthable) && ownSaleSilent && !tapeScoredList
        && !lookScoredList && !floorForfeitsList
        && meltScore is null && gcScore is null)
    {
      // Untradable gear can't be listed at all — with no melt or seal
      // evidence either, vendor is the only executable exit. Confident
      // verdict, never Review: this is the dungeon-clear vendor-trash tail,
      // and Universalis can never settle it (no market to ask about), so a
      // Review here would re-ask the same dead question every single run.
      if (!item.IsMarketable)
      {
        if (vendorScore is long uv)
          return Scored(new(RoutingExit.Vendor,
            $"Untradable, no desynth or seal evidence — vendor is the only exit: {uv:N0} gil."));
        // True zero-exit gear was already excluded up top (HasNoViableExit).
        // Reaching here means the item IS desynthable — that is the surviving
        // exit — we just have no yield history for it yet.
        return Scored(new(RoutingExit.Desynth,
          "Untradable, no seal or vendor value — desynth is the only exit, but you've no yield history yet. Melt one to learn what it gives, or hold it.",
          IsReview: true));
      }

      // DC sale history is stronger evidence than home-world velocity when it
      // exists: velocity ~0 on one world hides gear that sells DC-wide
      // (buyers travel for gear). Checked first; velocity remains the
      // fallback witness.
      if (item.CommunityMedian is long cMed
          && item.CommunitySampleCount >= cfg.CommunityMinSamples)
      {
        // The DC's read IS the price witness - it no longer has a worth floor to
        // clear (cleanup pass). A community median too small to be worth the trip
        // loses to the other exits on score, in the ordinary comparison, instead of
        // being talked out of the market by a threshold.
        return Scored(new(RoutingExit.List,
          $"Never sold one locally, but the DC buys it: ~{cMed:N0} gil ({item.CommunitySampleCount} sales, Universalis community) — the Hawk run prices it off the live MB."),
          listOverride: cMed);
      }

      if (item.MarketVelocity is double marketV)
      {
        // Finding 9 — price x velocity are ONE witness. Reaching here means the
        // PRICE witness is silent: no local sale, and no community median that
        // cleared the sample bar above. Velocity measures MOVEMENT, not WORTH,
        // so on its own it can no longer confidently route a marketable item in
        // EITHER direction — both halves of the Cashmere Hood / Green Beret
        // defect. A live velocity at an unknown price is not a confident List
        // (an item can "move" at 1 gil forever); a dead world velocity does not
        // prove a DC-tradable item is worthless (dead-world listings still sell
        // to world-hoppers). Lean List — marketable, the Hawk run prices it off
        // the live MB — but land in Review so the player supplies the price read.
        var moves = marketV >= MarketVelocityFloor;
        return Scored(new(RoutingExit.List,
          moves
            ? $"Moves here (~{marketV:0.##}/day on your world, Universalis) but you've never sold one — no price on record. List and let the Hawk run price it, or vendor if it's junk."
            : $"Doesn't move on your world (~{marketV:0.##}/day, Universalis), but it's DC-tradable — dead-world listings still sell to world-hoppers. List-and-forget, or vendor if you know it's junk.",
          IsReview: true,
          RunnerUp: RoutingExit.Vendor,
          RunnerUpReason: vendorReason.Length > 0 ? vendorReason : "Vendor if you know it's junk."));
      }
      return Scored(new(RoutingExit.List,
        "No local evidence for this gear — never sold or desynthed one. Check the MB if it looks valuable.",
        IsReview: true, RunnerUp: RoutingExit.Vendor, RunnerUpReason: vendorReason));
    }

    // Rule 8 — nothing beat the vendor. A sale on record is the "why not list"
    // context (reaching here with LastSale set means the sale lost the value
    // comparison) — say so instead of making the human wonder.
    if (vendorScore is long vendor)
    {
      // Two ways a sale on record loses (final pass, 08-23): it competed and
      // lost, or the floor nulled its score before the comparison ever ran -
      // "no exit scored better" over a sale that never competed sends the
      // player auditing a fight that didn't happen.
      var soldNote = item.LastSale is { } ls
        ? floorForfeitsList
          ? $" Sold at {ls.Price:N0} once — under today's floor, so no legal listing exists."
          : $" Sold at {ls.Price:N0} once — no exit scored better than the counter."
        : "";
      return Scored(new(RoutingExit.Vendor, $"Vendor: {vendor:N0} gil — no better exit in evidence.{soldNote}"));
    }

    // Unvendorable, no evidence for anything else — honest shrug.
    return Scored(new(RoutingExit.Vendor, "No viable exit known (can't even vendor it).",
      IsReview: true));
  }

  /// <summary>
  /// Finalizes a value-rule winner against its best-scoring alternative.
  /// Within the review band the verdict degrades to Review — unless thin
  /// venture stock tilts the borderline call TO churn (BP4 Q5: escalating
  /// bands).
  ///
  /// <para>The SATURATION tilt (07-25's burn-projection tie-break away from
  /// churn) retired with the seal S-curve (Drift, 2026-08-05): everywhere the
  /// projection used to act, the curve has already cut the seal score smoothly
  /// with stock, so the tie the tilt existed to break no longer forms — one
  /// continuous mechanism instead of a discrete hack layered on a flat rate.
  /// The burn measurement leaves routing with it.</para>
  /// </summary>
  private static RoutingVerdict Resolve(
    RoutingExit winner, long winnerScore, string winnerReason,
    (RoutingExit Exit, long? Score, string Reason) runnerUp,
    int? ventureStock, RoutingConfig cfg)
  {
    if (runnerUp.Score is not long rScore || rScore <= 0)
      return new(winner, winnerReason);

    var withinBand = winnerScore > 0
      && Math.Abs(winnerScore - rScore) <= winnerScore * cfg.RoutingReviewBandPct / 100.0;

    if (!withinBand)
      return new(winner, winnerReason);

    // Borderline + thin venture stock: the tie-break goes to churn.
    var gcContender = winner == RoutingExit.Gc ? winner
      : runnerUp.Exit == RoutingExit.Gc ? runnerUp.Exit
      : (RoutingExit?)null;
    if (gcContender is RoutingExit gcExit
        && ventureStock is int stock && stock < cfg.VentureBandFull)
    {
      var (reason, other, otherReason) = gcExit == winner
        ? (winnerReason, runnerUp.Exit, runnerUp.Reason)
        : (runnerUp.Reason, winner, winnerReason);
      return new(RoutingExit.Gc,
        $"{reason} Borderline vs {other} — tilted to turn-in ({stock:N0} tokens < {cfg.VentureBandFull:N0}).",
        RunnerUp: other, RunnerUpReason: otherReason);
    }

    // The band's own door - the ONE review that is genuinely a tie, and the only
    // verdict allowed to carry BandTie (the case page's "a dead heat" keys off it).
    return new(winner, winnerReason, IsReview: true,
      RunnerUp: runnerUp.Exit, RunnerUpReason: runnerUp.Reason, BandTie: true);
  }

  /// <summary>Highest-scoring alternative among candidates that have evidence.</summary>
  private static (RoutingExit Exit, long? Score, string Reason) BestOf(
    params (RoutingExit Exit, long? Score, string Reason)[] candidates)
  {
    (RoutingExit, long?, string) best = (RoutingExit.Vendor, null, "");
    foreach (var c in candidates)
      if (c.Score is long score && score > (best.Item2 ?? long.MinValue))
        best = c;
    return best;
  }
}
