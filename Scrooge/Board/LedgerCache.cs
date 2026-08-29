using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Scrooge.Board;

/// <summary>One routed inventory item (bag gear); Pile tracks player moves.</summary>
internal sealed class RoutedItem
{
  public uint ItemId { get; init; }
  public string Name { get; init; } = "";
  public bool IsHq { get; init; }
  public int Ilvl { get; init; }
  public int Quantity { get; init; }
  public InventoryType Container { get; init; }
  public int SlotIndex { get; init; }
  public int LastSalePrice { get; init; }
  /// <summary>DC-scope community median (Universalis history), for the Contradicted-row objection. 0 = none.</summary>
  public long CommunityMedian { get; init; }
  /// <summary>DC settled sales backing that median.</summary>
  public int CommunitySampleCount { get; init; }
  /// <summary>Home-world velocity (units/day), for the Contradicted-row objection. Null = no trusted data.</summary>
  public double? MarketVelocity { get; init; }
  /// <summary>The verdict leaned on community fallback (no local sale, community median in play) - tag it while world data warms.</summary>
  public bool CommunityFallback { get; init; }
  /// <summary>The router's original call - immutable; overrides move Pile, not this.</summary>
  public RoutingVerdict Verdict { get; init; }
  /// <summary>Current routing exit. Starts at Verdict.Exit; the player may move it.</summary>
  public RoutingExit Pile { get; set; }
  /// <summary>Still in the Review pile (ambiguous, unresolved).</summary>
  public bool InReview { get; set; }
  public bool OverrideRecorded { get; set; }
  /// <summary>Evidence-refined confidence for this verdict (design Section 4). Bulk gates on Unanimous.</summary>
  public ConfidenceTier Confidence { get; set; } = ConfidenceTier.Mixed;

  /// <summary>
  /// THE EVIDENCE THE TIER WAS SCORED FROM, banked with the row (triage walk, Task 3).
  /// The tier is a three-word summary of it; a case has to say the sentence behind the
  /// summary - which axis disagreed, which way the verdict leaned, how many settled
  /// sales were standing there - and re-deriving that at draw time would be a second
  /// opinion about a number the board already published (the <see cref="CaseEvidence"/>
  /// doctrine). The routing inputs it came from do not survive the refresh; this does.
  /// </summary>
  public BoardConfidence.Evidence Evidence { get; init; }
  /// <summary>
  /// The player ruled on this row - a move click now, or a persisted ruling
  /// re-applied from routing_overrides on refresh. Resolves Review (even a
  /// Contradicted demotion) and makes the row bulk-confirmable. A ruling holds
  /// while the router's verdict is unchanged; a new verdict re-asks.
  /// </summary>
  public bool PlayerResolved { get; set; }

  /// <summary>
  /// The skillup color the melt scorer priced into this row's Melt score
  /// (null = not skillup-eligible). Carried so the Melt header can split the
  /// currencies: knob worth is not gil and must not be summed as gil - and, since
  /// V44, so the routing receipt banks which of the two skillup knobs the row was
  /// standing under when it was ruled on.
  /// </summary>
  public DesynthSkillupColor? SkillupColor { get; init; }

  /// <summary>Which exits exist for this item (sheet facts, from the routing inputs). Cells respect these.</summary>
  public ExitDoors Doors { get; init; } = ExitDoors.AllOpen;

  /// <summary>
  /// Expert Delivery seals for one of these, null when the GC won't take it.
  /// Carried from the routing inputs rather than looked up at draw time: the
  /// detail pane shows the GC score's multiplication (seals x rate), and a
  /// per-frame sheet read for a number the batch already answered is how two
  /// surfaces come to quote different seal counts for one item.
  /// </summary>
  public int? SealValue { get; init; }

  /// <summary>
  /// WHERE THIS ROW EXECUTES (confidence-demoted Contradicted -&gt; Review).
  /// Every work set reads this - the bell's list and vendor sets, the melt
  /// count, the turn-in - because the question they are asking is "what will
  /// the round do with it", and Defer deliberately does not change that
  /// answer for a single row.
  /// </summary>
  public BoardPile ActivePile
    => BoardPiles.Effective(BoardPiles.ForRoutingExit(Pile, InReview), Confidence, PlayerResolved);

  /// <summary>
  /// Which named doubt this row acted through. Bag gear has no board walk
  /// behind it - its four exits are scored off sheet facts and sale history,
  /// never a lane queue - so the lane's own branches (dead heat, no tape,
  /// unconvictable HQ) cannot fire here and the honest answer is the tier's:
  /// Mixed evidence means the exits disagree about the item's fate.
  /// </summary>
  public DoubtBranch Doubt => DeferPlan.Classify(DoubtBranch.None, Confidence);

  /// <summary>
  /// Does this row DEFER - act on the system's own call, flagged, blocking
  /// nothing? A Melt row never does: the melt run takes its pile entire, so
  /// a Mixed melt row was never waiting on anybody and has no doubt anyone
  /// could act on differently.
  /// </summary>
  public bool Deferred
    => DeferPlan.IsDeferred(Doubt,
      inReview: ActivePile == BoardPile.Review,
      hasWinner: ActivePile is BoardPile.List or BoardPile.PullAndVendor
        or BoardPile.Melt or BoardPile.Churn,
      playerResolved: PlayerResolved,
      ridesWholePile: ActivePile == BoardPile.Melt);

  /// <summary>
  /// WHERE THIS ROW IS DRAWN. One home (ruled 08-06): a deferring row lives
  /// in the Defer group and nowhere else, so its exit pile's header counts
  /// fewer rows than the round will spend there. The strip still presses the
  /// planned verb, which is what keeps that honest rather than hidden.
  /// </summary>
  public BoardPile DrawnPile => Deferred ? BoardPile.Defer : ActivePile;
}

/// <summary>One inbox row: a live run item, or a held flag wearing a synthetic PricingItem.</summary>
internal sealed record InboxRow(PricingItem Item, StandingFlag? Flag)
{
  public bool IsFresh => Flag == null;
}

/// <summary>
/// A listed variant's SCORING OPERANDS - the facts behind its four numbers,
/// not the numbers themselves. A bag row carries these on its RoutedItem; a
/// listed row has no RoutedItem, so the same routing inputs that produced its
/// scores drop their operands here on the way past. Without them the detail
/// pane could show a listed row's GC score but not the seal count and rate it
/// is made of - and a pane that explains the bag rows and shrugs at the
/// listed ones is two panes wearing one name.
/// </summary>
internal readonly record struct ListedFacts(
  int Ilvl, long OwnSale, long CommunityMedian, int CommunitySamples, bool CommunityFallback,
  int? SealValue, DesynthSkillupColor? SkillupColor);

/// <summary>
/// THE BOARD'S ONE READ (Rounds code-shine, batch 3-3a). Intake and scoring: the bag
/// scan, the routing batch, the listed-lane scores, and every cached storage read the
/// surfaces draw from.
///
/// <para>Nothing here paints. The surfaces redraw sixty times a second and every fact
/// they quote has to be answered once and remembered, so this class owns the answering
/// and the remembering, and the windows own only the quoting. That is also what makes
/// the storage mutations on this pass - the melt contest raised, the settled flag
/// closed - honest: they are the scoring pass writing what the scoring pass concluded,
/// not a draw call reaching for the database.</para>
/// </summary>
internal sealed class LedgerCache
{
  private readonly Action<RoutedItem, RoutingExit> _recordRoutedSignal;

  /// <summary>
  /// A fresh bag scan landed and every per-row derivation over it is void. Raised at the
  /// top of <see cref="Refresh"/>, before a single row is built, so a listener's caches
  /// can never be repopulated off the scan this one is replacing.
  /// </summary>
  internal event System.Action? Rescanned;

  /// <summary>
  /// The listings snapshot has been re-read. Everything hung off this rides the ledger's
  /// refresh clock rather than a clock of its own - which is the whole contract for a
  /// number a surface draws every frame.
  /// </summary>
  internal event System.Action? ListingsRefreshed;

  /// <summary>
  /// The open held flags have been re-read. Raised so the surfaces that key
  /// session state by FLAG ID can drop the entries this read just closed - a flag
  /// dismissed, actioned, or settled by the melt contest is a row nobody will ask
  /// about again, and a per-session dictionary that never hears about it grows for
  /// as long as the session lasts.
  /// </summary>
  internal event System.Action? HeldFlagsRefreshed;

  internal LedgerCache(Action<RoutedItem, RoutingExit> recordRoutedSignal)
    => _recordRoutedSignal = recordRoutedSignal;

  // --- Bag-routing state ---
  private List<RoutedItem> _items = [];
  private int? _ventureStock;

  /// <summary>
  /// THE MEASURED WEEKLY TOKEN BURN this batch ran under, cached with the rest of
  /// the scan. Read once per Refresh rather than once per receipt for the same
  /// reason <see cref="_ventureStock"/> and <see cref="_sealRate"/> are: the burn
  /// costs two gil_snapshots queries (see GilStorage.MeasureWeeklyVentureBurn) and
  /// there is one bag scan's worth of receipts written under one answer. Null is
  /// "not measured" - fewer than a full week of token reads, or a storage failure -
  /// and it is banked as null, never as a zero that would read as "burned nothing".
  /// </summary>
  private int? _weeklyBurn;
  /// <summary>
  /// The seal rate this ledger was scored at, runway discount and all. Default is
  /// an undiscounted zero-rate, which renders as "no runway to report" until the
  /// first Refresh - never as "the discount is off", which would be a claim.
  /// </summary>
  private SealRate _sealRate;
  private int _uniVersion;
  private int _uniHistVersion;

  // --- Listed pile (session 3): cached own-board snapshot, refreshed with the
  // rest of the ledger so the section never queries the DB per frame. ---
  private List<ListedLine> _listed = [];

  // --- On Market tab (stage 3): the standing asks, derived from banked receipts.
  // Rebuilt with the rest of the ledger; the relative-time labels are recomputed
  // there too, so "2h ago" ages with the refresh, never per frame. ---
  private List<OnMarketRow> _onMarket = [];

  // --- Ripeness sensors: cached with the listings refresh, never per frame ---
  private long _lastFullScanAt;

  // --- Recon's clock, folded on the refresh clock for the same reason the scan age
  // is: the header draws it sixty times a second, and a Max() over the whole banked
  // table per frame is the per-frame query this cache exists to prevent. ---
  private long _reconLastBankedAt;
  private int _reconBankedCount;

  // --- The book-kept listing count: the last scan plus our own placings/pullings
  // since, minus what sold - the roster a pinch would actually walk (ruled 08-16
  // round walk: the stale scan count priced 93 items over a live roster of 145).
  // Cached with the listings refresh like everything else on this surface. ---
  private int _listedNow;

  // --- The one-door bell (WALK unit 4): the Hawk gate's answer over ALL bags,
  // cached with the rest of the ledger. The Ledger BORROWS this row source (it
  // does not fork the gate); the routing brain below still owns every gear
  // variant it evaluated. Fresh melt yields and coffer dyes arrive through here
  // now - the old "check them into the Hawk run" side door is gone. ---
  private List<ListableItem> _bellGateRows = [];

  /// <summary>
  /// When each cached decision was banked, keyed by variant - the recon stage's one
  /// operand, cached alongside the listable scan it is asked against. See
  /// <see cref="RefreshListings"/> for why it is read there and not per frame.
  /// </summary>
  private Dictionary<(uint ItemId, bool IsHq), long> _reconBankTimes = [];

  /// <summary>
  /// YOUR LAST SETTLED SALE per variant - price, when, and how long it sat. Read on the
  /// refresh clock like every other cached read on this surface, because the case
  /// tribunal quotes all three in one sentence and a per-case round trip for a fact the
  /// book already holds in one table is the per-frame query the memoir cache exists to
  /// prevent. Storage failure reads as no sales at all, which fires no sentence.
  /// </summary>
  private Dictionary<(uint ItemId, bool IsHq), (int Price, long Timestamp, int? SoldAfterDays)>
    _lastSales = [];

  /// <summary>
  /// Every bag variant the routing brain evaluated this refresh - staged, held in
  /// Review, or excluded as zero-exit. The bell's precedence rule reads this: if
  /// the router has an opinion, the gate does not get a vote (see BellPlan).
  /// </summary>
  private HashSet<BellVariant> _routerJurisdiction = [];

  /// <summary>Venture Coffers sitting in the bags - they ARM the bell stage.</summary>
  private int _cofferCount;

  /// <summary>The open persistent flags (V12) - the absorbed triage inbox's other half.</summary>
  private List<StandingFlag> _heldFlags = [];

  /// <summary>
  /// What a skillup is priced at this batch - the Melt header's shared sentence.
  /// Snapshotted from the batch config alongside the seal rate, so the header can
  /// never quote a number scoring did not use.
  /// </summary>
  private int _skillupYellow;
  private int _skillupRed;

  /// <summary>
  /// The ilvl-banded melt prior this batch scored with, snapshotted for the case
  /// tribunal's "you have never melted one" line. The batch builds it once
  /// (<see cref="RoutingInputService.BuildMeltPriors"/>) and the case reads THAT table,
  /// never a fresh one - a band average quoted from a second build is a number the
  /// decision never saw.
  /// </summary>
  private MeltPriorTable? _meltPriors;

  /// <summary>
  /// Whether this batch's seal rate came from your own turn-ins or from the config
  /// knob. The GC case's one argument line says which
  /// (<see cref="CaseVoice.SealRate"/>), because a placeholder rate that reads like a
  /// measured one is precisely the display lie that line exists to stop.
  /// </summary>
  private bool _sealRateMeasured;

  /// <summary>
  /// EVIDENCE'S VERSION NUMBER (triage walk, Task 3). Bumped by every refresh path -
  /// the hinge's Refresh button, the Universalis landing, a flag mutation - and read by
  /// the walk, which recomposes exactly when it changes.
  ///
  /// <para>A COUNTER RATHER THAN A CALLBACK, deliberately: the walk is composed inside a
  /// draw and the refreshes happen outside one, so the only honest question the pane can
  /// ask is "has the board moved since I last looked", and a number answers it without
  /// anything having to remember to tell anybody.</para>
  /// </summary>
  private int _evidenceStamp;

  // --- Confidence refinement (design Section 4) ---
  private Dictionary<string, int> _overrideCounts = new(StringComparer.Ordinal);
  private Dictionary<(uint ItemId, bool IsHq, string RouterVerdict), string> _persistedRulings = new();

  // --- The listed rows' scores (walk unit 3): melt/GC/vend from the routing
  // scorers, List from the relist preview - both computed at Refresh, in
  // last-pinch tense like everything else on the ledger. ---
  private Dictionary<(uint ItemId, bool IsHq), RoutingScores?> _listedExitScores = [];
  private Dictionary<(uint ItemId, bool IsHq, string Retainer), long?> _listedHonest = [];
  // A listed row's List door is open by definition (it IS listed); the other
  // three come from the same sheet facts the bag rows read.
  private Dictionary<(uint ItemId, bool IsHq), ExitDoors> _listedDoors = [];

  /// <summary>
  /// THE DOUBT BRANCH THE SPINE FIRED for each standing lane, banked at
  /// Refresh beside the relist number it was computed with. The lane walk is
  /// the ONE place that knows about dead heats, silent tapes and unconvictable
  /// HQ rows; that knowledge used to end up as prose in the evidence string and
  /// die there. Keyed per lane (item, quality, retainer) because the walk runs
  /// per lane - the current ask is one of its inputs.
  /// </summary>
  private Dictionary<(uint ItemId, bool IsHq, string Retainer), DoubtBranch> _listedDoubt = [];

  private Dictionary<(uint ItemId, bool IsHq), ListedFacts> _listedFacts = [];

  // ==========================================================================
  // What the surfaces read
  // ==========================================================================

  internal IReadOnlyList<RoutedItem> Items => _items;
  internal IReadOnlyList<ListedLine> Listed => _listed;
  internal List<OnMarketRow> OnMarketRows => _onMarket;
  internal IReadOnlyList<ListableItem> BellGateRows => _bellGateRows;
  internal IReadOnlyList<StandingFlag> HeldFlags => _heldFlags;
  internal IReadOnlySet<BellVariant> RouterJurisdiction => _routerJurisdiction;
  internal IReadOnlyDictionary<(uint ItemId, bool IsHq), long> ReconBankTimes => _reconBankTimes;
  internal int CofferCount => _cofferCount;
  internal int? VentureStock => _ventureStock;
  internal SealRate SealRate => _sealRate;
  internal bool SealRateMeasured => _sealRateMeasured;
  internal int SkillupYellow => _skillupYellow;
  internal int SkillupRed => _skillupRed;
  internal MeltPriorTable? MeltPriors => _meltPriors;
  internal long LastFullScanAt => _lastFullScanAt;

  /// <summary>When recon last banked a decision (unix seconds), or 0 when it never has.</summary>
  internal long ReconLastBankedAt => _reconLastBankedAt;

  /// <summary>How many variants recon is holding a banked decision for.</summary>
  internal int ReconBankedCount => _reconBankedCount;
  internal int ListedNow => _listedNow;
  internal int EvidenceStamp => _evidenceStamp;

  /// <summary>
  /// The player's standing rulings, as the last scan loaded them - keyed by variant and
  /// the router verdict each one answered. The row memoir quotes them; the scan applies
  /// them. One table, one load, two readers.
  /// </summary>
  internal IReadOnlyDictionary<(uint ItemId, bool IsHq, string RouterVerdict), string> PersistedRulings
    => _persistedRulings;

  /// <summary>
  /// New Universalis answers have landed since the last bag scan. Asked at draw time so
  /// the board can settle its "no evidence" verdicts when the data arrives - the two
  /// version counters are the batch's own, so nothing else has to remember to say so.
  /// </summary>
  internal bool UniversalisLanded()
    => UniversalisStats.Version != _uniVersion || UniversalisHistory.Version != _uniHistVersion;

  /// <summary>The last settled sale for one variant, or null when the book has none.</summary>
  internal (int Price, long Timestamp, int? SoldAfterDays)? LastSaleFor(uint itemId, bool isHq)
    => _lastSales.TryGetValue((itemId, isHq), out var sale) ? sale : null;

  /// <summary>A listed variant's scoring operands, or null when this refresh never scored it.</summary>
  internal ListedFacts? ListedFactsFor(uint itemId, bool isHq)
    => _listedFacts.TryGetValue((itemId, isHq), out var facts) ? facts : null;

  /// <summary>The doubt branch a standing lane's price was decided through, or null when no walk banked one.</summary>
  internal DoubtBranch? ListedDoubtFor(uint itemId, bool isHq, string retainer)
    => _listedDoubt.TryGetValue((itemId, isHq, retainer), out var doubt) ? doubt : null;

  /// <summary>
  /// The relist preview for one standing lane, straight out of the Refresh-time
  /// cache the On Market tab's List cell renders. Null when the ledger has never
  /// scored this lane (no refresh yet, or the item is not standing) or when the
  /// spine refused to price it - callers show NOTHING for a null, never a
  /// placeholder and never a number nobody computed.
  /// </summary>
  internal long? RelistPreview(uint itemId, bool isHq, string retainer)
    => _listedHonest.TryGetValue((itemId, isHq, retainer), out var honest) ? honest : null;

  /// <summary>The doors for a listed variant - AllOpen when the scoring pass had no inputs (unknowns keep today's behavior).</summary>
  internal ExitDoors ListedDoors(uint itemId, bool isHq)
    => _listedDoors.TryGetValue((itemId, isHq), out var doors) ? doors : ExitDoors.AllOpen;

  /// <summary>What a listed variant's Melt score IS - the same grade the bag rows carry.</summary>
  internal MeltGrade ListedMeltGrade(uint itemId, bool isHq)
    => _listedExitScores.TryGetValue((itemId, isHq), out var exits) && exits is { } s
      ? s.MeltGrade : MeltGrade.None;

  /// <summary>
  /// The four-score strip for a listed row, from the Refresh-time caches: List
  /// is the relist preview (dash when the floors refuse or the spine held),
  /// melt/GC/vend are the routing scorers' answers for the variant. All in
  /// strip order, all null-safe - an unscored lane draws four dashes, which is
  /// exactly what it earned.
  /// </summary>
  internal long?[] ListedRowScores(uint itemId, bool isHq, string retainer)
  {
    var scores = new long?[BoardCalls.Exits.Length];
    _listedHonest.TryGetValue((itemId, isHq, retainer), out scores[0]);
    if (_listedExitScores.TryGetValue((itemId, isHq), out var exits) && exits is { } s)
    {
      // THE LIST SLOT'S SECOND WITNESS (F7, ruled 08-22 - the Silvergrace
      // all-null panel). The honest relist (the live would-write) leads, but a
      // lane the spine held used to null the slot while the router's own List
      // score - the own sale / settled tape / DC witness that routed the item -
      // sat unread in the same record. Discarded-by-hold and nonexistent are
      // different facts; the witnessed worth stands when the preview cannot.
      scores[0] ??= s.List;
      scores[1] = s.Melt;
      scores[2] = s.Gc;
      scores[3] = s.Vendor;
    }
    return scores;
  }

  // ==========================================================================
  // Intake
  // ==========================================================================

  /// <summary>
  /// THE WHOLE RE-READ, in the one order it may run in. The bag scan first, because
  /// it is what builds the rows every later read decorates; the held flags next, so a
  /// flag raised against a row that just left the bags is reconciled against the scan
  /// that saw it go; the listings snapshot last, because it derives the On Market rows
  /// off the same refreshed evidence stamp. Spelled once so no call site can rehearse
  /// two-thirds of it and leave the third surface quoting yesterday.
  /// </summary>
  internal void RefreshAll()
  {
    Refresh();
    RefreshHeldFlags();
    RefreshListings();
  }

  /// <summary>
  /// Loads the own-board snapshot for the Listed pile from captured data (the
  /// listings table). Read-only, cached - called on open and on refresh, never
  /// per frame. Storage failure degrades to an empty section.
  /// </summary>
  internal void RefreshListings()
  {
    _evidenceStamp++;
    try
    {
      _listed = GilStorage.GetAllCurrentListings()
        .Select(l => new ListedLine(
          l.RetainerName, l.ItemId, l.ItemName, l.IsHQ, l.UnitPrice, l.Quantity, l.FirstSeenTimestamp))
        .ToList();
      _lastFullScanAt = GilStorage.GetLastFullScanTime();
      _listedNow = StandingBookFeed.ListedCountNow(_listed.Count, _lastFullScanAt);
    }
    catch { _listed = []; _lastFullScanAt = 0; _listedNow = 0; }

    // The On Market tab's rows, from the same refresh. Its own try: a receipt read
    // that fails must not empty the Listed pile above it, and vice versa.
    try { _onMarket = BuildOnMarketRows(); }
    catch { _onMarket = []; }

    // The bell's non-gear half. Kept outside the try above so a storage failure
    // in the listings snapshot cannot silently empty the bell (a 0-count bell
    // over a full bag is exactly the lie unit 4 exists to kill).
    try { _bellGateRows = ListableInventoryScanner.Scan(); }
    catch { _bellGateRows = []; }
    try { _cofferCount = CofferOrchestrator.CoffersInBags(); }
    catch { _cofferCount = 0; }
    // Recon's operand: one banked_at per cached decision. Read HERE, on the same
    // schedule as every other cached read on this surface, because the recon count
    // is recomputed every frame the deck draws - the arithmetic is in memory over
    // this dictionary and the already-cached listable scan, and the SQLite hit
    // stays on the refresh clock. A storage failure reads as an EMPTY cache, which
    // makes everything stale: recon walks more than it needs to, which is the
    // direction that costs minutes rather than trust.
    try { _reconBankTimes = GilStorage.GetDecisionCacheBankTimes(); }
    catch { _reconBankTimes = []; }
    // The header's second clock, folded here off the same read: when recon last
    // banked anything, and how many variants it is holding. An empty table reads as
    // 0/0, which the header says plainly ("no recon read yet") rather than dating
    // itself to the epoch.
    _reconLastBankedAt = _reconBankTimes.Count > 0 ? _reconBankTimes.Values.Max() : 0;
    _reconBankedCount = _reconBankTimes.Count;
    // The case tribunal's strongest receipt, on the same clock and its own try: an
    // unreadable sale book must not empty the recon cache above it, or vice versa.
    try { _lastSales = GilStorage.GetLastSalePrices(); }
    catch { _lastSales = []; }
    // The resume line's two numbers, on the same clock and for the same reason: the
    // line is drawn every frame a held round is on screen.
    ListingsRefreshed?.Invoke();
  }

  /// <summary>
  /// Scans the player's bags (gear only), gathers evidence once, runs every item
  /// through the rules engine into routing exits, and scores each verdict's
  /// confidence. No-op on the bag scan when the routing brain is off - the Ledger
  /// still shows the absorbed triage rows.
  /// </summary>
  internal void Refresh()
  {
    _evidenceStamp++;
    _items = [];
    _routerJurisdiction = [];
    _overrideCounts = LoadOverrideCounts();
    _persistedRulings = LoadPersistedRulings();
    Rescanned?.Invoke();

    _uniVersion = UniversalisStats.Version;
    _uniHistVersion = UniversalisHistory.Version;
    var batch = RoutingInputService.BeginBatch();
    _ventureStock = batch.VentureStock;
    // The receipt's other venture-context number, measured once for the batch. Its
    // own try: an unreadable token history costs the receipts one context column,
    // never the scan.
    try { _weeklyBurn = GilStorage.MeasureWeeklyVentureBurn(); }
    catch { _weeklyBurn = null; }
    // The batch's seal rate, derived ONCE by the rules engine's own method - the
    // header's runway line, the gate's advisory and every receipt written below
    // all read this, so no surface can quote a rate scoring didn't use.
    _sealRate = RoutingRules.SealRateFor(batch);
    // The Melt header's shared sentence reads these - the same snapshot the
    // skillup pricing below scored with, never a second read of the config.
    _skillupYellow = batch.Rules.SkillupWorthYellow;
    _skillupRed = batch.Rules.SkillupWorthRed;
    // The band table the melt scores were weighed on - the case's "you have never
    // melted one" line reads THIS one, on the same terms as the two knobs above.
    _meltPriors = batch.MeltPriors;
    _sealRateMeasured = batch.SealRateEmpirical;
    var gearsetIds = DesynthInventoryScanner.SnapshotGearsetItemIds();
    var itemSheet = Svc.Data.GetExcelSheet<Item>();

    // Pull intents (V31, walk ruling 2): rulings staged as pull-for-melt/GC,
    // waiting for their item to land in the bags. Loaded once per scan; a
    // matching variant below is stamped with the ruling and the intent is
    // consumed - the router never re-asks an answered question.
    Dictionary<(uint ItemId, bool IsHq), string> pullIntents;
    try { pullIntents = GilStorage.GetOpenPullIntents(); }
    catch { pullIntents = []; }

    unsafe
    {
      var im = InventoryManager.Instance();
      if (im == null) return;

      var containers = new[]
      {
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
      };

      foreach (var containerType in containers)
      {
        var container = im->GetInventoryContainer(containerType);
        if (container == null) continue;

        for (int i = 0; i < container->Size; i++)
        {
          var slot = container->GetInventorySlot(i);
          if (slot == null || slot->ItemId == 0) continue;

          var itemId = slot->ItemId;
          if (!itemSheet.TryGetRow(itemId, out var row)) continue;
          // Gear and desynthables. Fish entered the contest 2026-08-28 (the
          // main way to train CUL desynth) — a non-equipment item with
          // Desynth > 0 routes like gear; plain mats still sit out.
          if (row.EquipSlotCategory.RowId == 0 && row.Desynth == 0) continue;

          var isHq = (slot->Flags & InventoryItem.ItemFlags.HighQuality) != 0;

          var gearsetKey = isHq ? itemId + 1_000_000u : itemId;
          var protections = new ItemProtections(
            InGearset: gearsetIds.Contains(gearsetKey),
            Spiritbond100: slot->SpiritbondOrCollectability >= 10000,
            HasMateria: HasAnyMateria(slot));

          if (RoutingInputService.Collect(batch, itemId, isHq, protections) is not { } inputs)
            continue;

          var verdict = RoutingRules.Evaluate(inputs, batch);

          // The router has now looked at this variant, whatever it concluded -
          // claim it BEFORE the exclusion filter below. Jurisdiction is about who
          // decides, not about who decided yes: zero-exit gear must not reappear
          // on the market through the bell's gate half just because the gate has
          // no opinion on it (see BellPlan).
          _routerJurisdiction.Add(new BellVariant(itemId, isHq));

          // Zero-exit gear is not a decision (Drift, 07-23): no pile, no Review,
          // no bulk/round count, no receipt. THE single exclusion filter — the
          // determination lives in the pure core (RoutingRules.HasNoViableExit
          // via IsExcluded); this is the one place that acts on it. Skipping
          // before the receipt/add/persisted-ruling steps means a stale ruling
          // on the item (the Vana'dielian Melt squatter) is loaded but never
          // looked up — inert, not a phantom. The check is live every scan, so
          // the item re-enters automatically the moment SE grants it a sale
          // value; nothing is persisted here to unset.
          if (verdict.IsExcluded) continue;

          // Scored ONCE, banked with the row: the tier and the case both read it, and
          // the case's Alexander sentence must be about the axis this verdict actually
          // failed rather than one re-derived from facts that have moved since.
          var evidence = RoutedEvidence(inputs, verdict, batch.Rules, batch.BookIsNew);
          var item = new RoutedItem
          {
            ItemId = itemId,
            Name = inputs.Name,
            IsHq = isHq,
            Ilvl = inputs.Ilvl,
            Quantity = (int)slot->Quantity,
            Container = containerType,
            SlotIndex = i,
            LastSalePrice = inputs.LastSale?.Price ?? 0,
            CommunityMedian = inputs.CommunityMedian ?? 0,
            CommunitySampleCount = inputs.CommunitySampleCount,
            MarketVelocity = inputs.MarketVelocity,
            // The flag means what its doc says - the verdict LEANED on the DC -
            // and that takes every local witness silent, not just your own sales
            // (Drift, 08-23: "I'm not a fan of DC stats getting prominence over
            // local history when we have it" - a 4-sale local tape scored the
            // sword's List at 16,000 while this row led with the DC's one sale).
            // Local-outranks-DC is F4's law; the display flag follows the same
            // ladder the router walks.
            CommunityFallback = inputs.LastSale is null
              && inputs.LocalTapeMedian is null
              && inputs.LookAsk is null
              && inputs.CommunityMedian is not null,
            Verdict = verdict,
            Pile = verdict.Exit,
            InReview = verdict.IsReview,
            // A Desynth verdict led by the skill-up peg grades on the PEG's
            // certainty (F3) - the melt score IS the knob when the grade says
            // Skillup, and Review verdicts never take this door.
            Confidence = BoardConfidence.PegOrGraded(
              verdict.Exit == RoutingExit.Desynth && !verdict.IsReview
                && (verdict.Scores?.MeltGrade ?? MeltGrade.None) == MeltGrade.Skillup,
              evidence,
              _overrideCounts.GetValueOrDefault(verdict.Exit.ToString())),
            Evidence = evidence,
            SkillupColor = inputs.DesynthSkillupEligible ? inputs.DesynthColor : null,
            SealValue = inputs.SealValue,
            Doors = new ExitDoors(inputs.IsMarketable, inputs.IsDesynthable,
              (inputs.SealValue ?? 0) > 0, inputs.VendorPrice > 0),
          };
          ApplyPersistedRuling(item);

          // The retainer->bag crossing completes here: a pulled-for-X item has
          // landed, the banked ruling stamps it, and the intent is spent. The
          // stamp wins over a persisted ruling above - it is the newer answer.
          if (pullIntents.TryGetValue((itemId, isHq), out var destination)
              && destination is nameof(StandingAction.Melt) or nameof(StandingAction.Gc))
          {
            item.Pile = destination == nameof(StandingAction.Melt) ? RoutingExit.Desynth : RoutingExit.Gc;
            item.InReview = false;
            item.PlayerResolved = true;
            // The stamp is a RULING, and rulings persist (the Hose regression,
            // 08-02 night): RoutedItems are rebuilt every Refresh and the intent
            // is one-shot, so a stamp that only mutated the in-memory row lived
            // exactly one refresh - then the router re-asked the question the
            // player already answered on the Board tab. Same write a cell click
            // makes; ApplyPersistedRuling re-applies it from here on.
            _recordRoutedSignal(item, item.Pile);
            // The stamp above stands whether or not the intent could be spent -
            // RecordRoutedSignal already persisted the ruling, so the cost of an
            // unspent intent is a re-stamp on the next Refresh, not a lost answer.
            // Dropping the stamp instead would throw away what the player decided.
            try { GilStorage.ConsumePullIntent(itemId, isHq); }
            catch (Exception ex)
            {
              Svc.Log.Warning($"[Board] Pull intent not consumed for {itemId}: {ex.Message}");
            }
            pullIntents.Remove((itemId, isHq));
          }

          _items.Add(item);

          // V20 routing receipt: the decision + its alternatives, deduped in
          // storage (same item/exit within 24h = same standing decision).
          // Telemetry only - never blocks routing.
          try
          {
            GilStorage.InsertRoutingReceipt(itemId, isHq, inputs.Ilvl,
              verdict.Exit.ToString(), verdict.Reason, verdict.IsReview,
              item.Confidence.ToString(), verdict.Scores,
              batch.SealToGilRate, batch.SealRateEmpirical,
              _sealRate.EffectiveRate, _sealRate.Discounted,
              // THE VENTURE CONTEXT, both halves (the V20 header's words for these
              // two columns). Stock is an OPERAND - the S-curve reads it and
              // nothing else (2026-08-05) - and the burn is CONTEXT: the world the
              // tilt ran under, not an input any score consulted. The column rode
              // null for exactly that reason, and the reason does not hold: a
              // context column is not a claim about what was scored, the schema
              // named it context the day it was cut, and the 4.0 scoreboard cannot
              // ask whether the stock reading meant scarcity or a full stockpile
              // draining fast without knowing which. The distinction stays honest
              // where it matters - in the doc, and in the null when nothing measured.
              batch.VentureStock, _weeklyBurn,
              item.CommunityFallback ? "community" : "world",
              // V44: the skillup standing the melt score was composed from, so a
              // knob-priced melt reads back as one.
              item.SkillupColor);
          }
          catch (Exception ex)
          {
            Svc.Log.Warning($"[Board] Routing receipt not banked for {itemId}: {ex.Message}");
          }
        }
      }
    }

    _items = _items.OrderByDescending(i => i.Ilvl).ThenBy(i => i.Name).ToList();

    // The listed rows join the four-score contract (walk unit 3, 08-02): the
    // same batch that scored the bags scores the standing book. Its own guard:
    // a receipts read that fails costs the listed rows their scores, never the
    // bag rows theirs.
    try { ScoreListedLanes(batch); }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Board] Listed-lane scoring failed: {ex.Message}");
      _listedExitScores = [];
      _listedHonest = [];
      _listedDoors = [];
      _listedFacts = [];
      _listedDoubt = [];
    }
  }

  /// <summary>
  /// Scores every standing lane: the exit scores once per (item, quality) via
  /// the SAME scorers the bag rows ride, and the relist number once per
  /// lane (item, quality, retainer) via the extracted pricing spine over BANKED
  /// evidence - the ring tape and the last-look board snapshot. A lane the
  /// spine holds, or whose number the floors refuse, reads null: the List cell
  /// draws a DASH, because refusing to price crasher-chasing is A10's
  /// personality (walk ruling 3).
  /// </summary>
  private void ScoreListedLanes(RoutingBatch batch)
  {
    _listedExitScores = [];
    _listedHonest = [];
    _listedDoors = [];
    _listedFacts = [];
    _listedDoubt = [];

    var lanes = OnMarket.Standing(GilStorage.GetOpenReceipts());
    var itemSheet = Svc.Data.GetExcelSheet<Item>();

    // EVERY STANDING LANE SCORES, not just the receipted ones (F7, ruled 08-22).
    // The receipt join below is the melt-contest's spine, but a lane whose
    // receipt closed (graded, evicted) still stands on the board - and its rows
    // drew four dashes while the router could have scored them from the same
    // banked evidence (the Silvergrace: a fresh own sale in last_sale_prices,
    // "no evidence either way" on the surface). Sweep the current listings for
    // variants the receipt loop will not reach; the loop's ContainsKey guard
    // makes the union cheap and double-score-proof.
    var swept = new List<(uint ItemId, bool IsHq, string Retainer, long Ask)>();
    try
    {
      foreach (var l in GilStorage.GetAllCurrentListings())
        swept.Add((l.ItemId, l.IsHQ, l.RetainerName, l.UnitPrice));
    }
    catch { /* listings unreadable - the receipted lanes still score */ }

    // The melt contest's live operands, per lane - collected on the way past so
    // the close pass below can ask its question of every flag ever raised, not
    // just the lanes that happen to still be standing.
    var meltLanes = new Dictionary<(uint, bool, string),
      BoardListings.MeltContestLane>();

    foreach (var lane in lanes)
    {
      var variant = (lane.ItemId, lane.IsHq);
      if (!_listedExitScores.ContainsKey(variant))
      {
        var inputs = RoutingInputService.Collect(batch, lane.ItemId, lane.IsHq);
        _listedExitScores[variant] = inputs is { } inp
          ? RoutingRules.Evaluate(inp, batch).Scores
          : null;
        _listedDoors[variant] = inputs is { } di
          ? new ExitDoors(true, di.IsDesynthable, (di.SealValue ?? 0) > 0, di.VendorPrice > 0)
          : ExitDoors.AllOpen;
        // The operands behind those scores, banked for the detail pane. Same
        // read, same instant - never a second Collect at draw time.
        if (inputs is { } fi)
          _listedFacts[variant] = new ListedFacts(
            fi.Ilvl,
            fi.LastSale?.Price ?? 0,
            fi.CommunityMedian ?? 0,
            fi.CommunitySampleCount,
            fi.LastSale is null && fi.CommunityMedian is not null,
            fi.SealValue,
            fi.DesynthSkillupEligible ? fi.DesynthColor : null);
      }

      var laneKey = (lane.ItemId, lane.IsHq, lane.Retainer);
      if (!_listedHonest.ContainsKey(laneKey))
      {
        // One walk, both answers. The doubt branch is a property OF the walk
        // that produced the number - computing it in a second pass would be
        // the two-compositions drift the LaneEvaluation extraction exists to
        // prevent, wearing a different hat.
        var (honest, doubt) = ComputeHonestRelist(lane.ItemId, lane.IsHq, lane.DecidedPrice, itemSheet);
        _listedHonest[laneKey] = honest;
        _listedDoubt[laneKey] = doubt;
      }

      if (lane.DecidedPrice is long ask && ask > 0
          && _listedExitScores.TryGetValue(variant, out var scored) && scored is { } sc)
        meltLanes[laneKey] = new BoardListings.MeltContestLane(ask, sc.Melt, sc.MeltGrade);

      RaiseMeltContest(lane);
    }

    // The sweep's turn: same scorers, same caches, only for what the receipt
    // loop left unscored. No melt contest here - that spine is the receipts'.
    foreach (var (sItemId, sIsHq, sRetainer, sAsk) in swept)
    {
      var variant = (sItemId, sIsHq);
      if (!_listedExitScores.ContainsKey(variant))
      {
        var inputs = RoutingInputService.Collect(batch, sItemId, sIsHq);
        _listedExitScores[variant] = inputs is { } inp
          ? RoutingRules.Evaluate(inp, batch).Scores
          : null;
        _listedDoors[variant] = inputs is { } di
          ? new ExitDoors(true, di.IsDesynthable, (di.SealValue ?? 0) > 0, di.VendorPrice > 0)
          : ExitDoors.AllOpen;
        if (inputs is { } fi)
          _listedFacts[variant] = new ListedFacts(
            fi.Ilvl,
            fi.LastSale?.Price ?? 0,
            fi.CommunityMedian ?? 0,
            fi.CommunitySampleCount,
            fi.LastSale is null && fi.CommunityMedian is not null,
            fi.SealValue,
            fi.DesynthSkillupEligible ? fi.DesynthColor : null);
      }

      var laneKey = (sItemId, sIsHq, sRetainer);
      if (!_listedHonest.ContainsKey(laneKey))
      {
        var (honest, doubt) = ComputeHonestRelist(sItemId, sIsHq, sAsk, itemSheet);
        _listedHonest[laneKey] = honest;
        _listedDoubt[laneKey] = doubt;
      }
    }

    SettleMeltContests(meltLanes);
  }

  /// <summary>
  /// THE RAISER CLOSING ITS OWN. Self-heal is exempted from this flag class on
  /// purpose (a pricing pass never asks its question), which means nothing else
  /// in the plugin can ever close one - so an open flag would outlive the
  /// numbers that justified it: a repriced ask, or a listing that sold weeks
  /// ago. That is the V36 immortal tenant of the old Watch pile, reborn. The pick is pure and
  /// tested; this only reads the rows and stamps the closes, 'resolved' - the
  /// same word self-heal uses for a condition that went away on its own.
  /// </summary>
  private static void SettleMeltContests(
    IReadOnlyDictionary<(uint ItemId, bool IsHq, string Retainer), BoardListings.MeltContestLane> standing)
  {
    try
    {
      var open = GilStorage.GetOpenFlagLanes(BoardListings.MeltBeatsAskReason);
      if (open.Count == 0) return;
      foreach (var id in BoardListings.MeltContestsToClose(open, standing))
        GilStorage.SetStandingFlagStatus(id, "resolved");
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Board] Melt-beats-ask flags not settled: {ex.Message}");
    }
  }

  /// <summary>
  /// THE MELT-BEATS-ASK CONTEST (Drift's ruling, 2026-08-06; the Bread Rack that
  /// provoked it was seen standing under its own melt on 08-03). The one place
  /// both numbers are in hand at once - the standing ask off the receipt, the
  /// melt score off the same scorer that fills the row's Melt cell - so this is
  /// where the question gets asked. Measured melt only: "a prior is a rumor,
  /// not evidence", and inviting a pull on a band average would be inviting an
  /// action against a measurement nobody made.
  ///
  /// <para>It RAISES A FLAG and nothing else. The flag lands in the same triage
  /// inbox every router flag lands in, drawn in the Melt group proposing Melt;
  /// the player's click is what stages a verb and the round is what spends it.
  /// Dismissing it books a 'dismissed' contest receipt like any other flag, and
  /// the dismissal is remembered against the ask it answered - a repriced ask
  /// is a new question, an unchanged one is asked and answered.</para>
  /// </summary>
  private void RaiseMeltContest(ReceiptLine lane)
  {
    if (lane.DecidedPrice is not long ask || ask <= 0) return;
    if (!_listedExitScores.TryGetValue((lane.ItemId, lane.IsHq), out var scores)
        || scores is not { } s || s.Melt is not long melt)
      return;

    try
    {
      var dismissedAt = GilStorage.LastDismissedFlagPrice(
        lane.ItemId, lane.IsHq, lane.Retainer, BoardListings.MeltBeatsAskReason);
      if (!BoardListings.ShouldRaiseMeltContest(ask, melt, s.MeltGrade, dismissedAt))
        return;

      GilStorage.UpsertStandingFlag(lane.ItemId, lane.IsHq, lane.Retainer, -1,
        BoardListings.MeltBeatsAskReason,
        BoardListings.MeltBeatsAskDetail(ask, melt),
        ask <= int.MaxValue ? (int)ask : int.MaxValue,
        melt <= int.MaxValue ? (int)melt : int.MaxValue);
    }
    catch (Exception ex)
    {
      Svc.Log.Warning($"[Board] Melt-beats-ask flag not raised for {lane.ItemId}: {ex.Message}");
    }
  }

  /// <summary>
  /// The relist preview for one lane: the extracted pricing spine over the
  /// banked ring + the banked board snapshot, with the pinch's own config. A
  /// missing snapshot previews off the tape alone (an empty board is a verdict
  /// the spine already knows how to price); any failure previews as a dash
  /// rather than a guess.
  ///
  /// <para>Returns the walk's DOUBT BRANCH beside the number (08-06). A dash in
  /// the List cell and a price the spine wrote on thin ice are two different
  /// facts, and only one of them was ever visible.</para>
  /// </summary>
  /// <summary>
  /// THE BANKED-EVALUATION SEAM: the extracted pricing spine over the banked ring
  /// + the banked board snapshot, with the pinch's own config - the shared half of
  /// <see cref="ComputeHonestRelist"/>, split out (08-22) so the board mirror can
  /// run the SAME composition. One composition, three callers now: the live pinch,
  /// the relist preview, and the mirror. Returns the raw banked inputs beside the
  /// answer because the mirror displays exactly what was judged - a mirror fed a
  /// second read would be the drift this seam exists to prevent. Null = storage
  /// failed; the caller says so rather than guessing.
  /// </summary>
  internal static (LaneAnswer Answer, List<MarketEvents.BoardListing> Snapshot, long ScanAt,
    List<SaleHistorySchema.BankedSale> SalesRaw)? EvaluateBanked(uint itemId, bool isHq, long? currentAsk)
  {
    try
    {
      var cfg = new LaneConfig
      {
        CeilingMult = Plugin.Configuration.UpwardRepriceMultiplier,
        MinHistorySamples = Plugin.Configuration.LaneMinHistorySamples,
        HalfLifeDays = LaneHalfLife.Resolve(itemId),
        HqPremiumPct = Math.Clamp(Plugin.Configuration.HqPremiumPercent, 0, 100) / 100.0,
        SeatBudget = Plugin.Configuration.SeatBudget,
      };

      var salesRaw = GilStorage.ReadSaleHistory(itemId);
      var sales = salesRaw.Select(r => new LaneSale(r.UnitPrice, r.SaleTime, r.IsHq)).ToList();
      var (snapshot, scanAt) = GilStorage.GetBoardSnapshot(itemId);
      // The same queue the live pinch walks (A12): COMBINED for an NQ item -
      // one physical line, quality flagged per row - and HQ-only for an HQ
      // item. A preview filtered differently from the pinch was the exact
      // two-compositions drift the LaneEvaluation extraction exists to prevent.
      var board = snapshot.Where(l => isHq ? l.IsHq : true)
        .Select(l => new LaneListing(l.UnitPrice, l.IsOwn, l.IsHq)).ToList();

      var answer = LaneEvaluation.Evaluate(
        sales, board, isHq, isHq, cfg, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        communityProvider: () => UniversalisHistory.TryGet(itemId),
        fallbackVelocityProvider: () => UniversalisStats.TryGet(itemId, isHq)?.Velocity,
        currentAsk);

      return (answer, snapshot, scanAt, salesRaw);
    }
    catch
    {
      return null;
    }
  }

  private static (long? Honest, DoubtBranch Doubt) ComputeHonestRelist(
    uint itemId, bool isHq, long? currentAsk, Lumina.Excel.ExcelSheet<Item> itemSheet)
  {
    try
    {
      if (!itemSheet.TryGetRow(itemId, out var row)) return (null, DoubtBranch.None);

      if (EvaluateBanked(itemId, isHq, currentAsk) is not { } banked)
        return (null, DoubtBranch.None);
      var answer = banked.Answer;

      // ONE definition of the floor, shared with the pinch's lane guard (see
      // PriceFloor). A preview that computed its own would eventually show the player
      // a relist target the pinch then refuses - the two-compositions drift again.
      var floor = PriceFloor.Effective(
        Plugin.Configuration.PriceFloorMode, (long)row.PriceLow,
        Plugin.Configuration.MinimumListingPrice);
      return (LaneEvaluation.HonestRelist(answer.Decision, floor), answer.Decision.Doubt);
    }
    catch
    {
      // A failed walk knows nothing, including whether it was doubtful. None
      // here is "no branch on record", never "we were sure".
      return (null, DoubtBranch.None);
    }
  }

  /// <summary>
  /// Re-applies the player's most recent persisted ruling to a freshly routed row.
  /// A ruling holds only while it answers the SAME question - keyed on the router's
  /// current verdict, so a changed verdict (new evidence) misses and re-asks. This
  /// is what stops the Ledger asking the same question every reload.
  /// </summary>
  private void ApplyPersistedRuling(RoutedItem item)
  {
    var routerVerdict = item.Verdict.IsReview ? "Review" : item.Verdict.Exit.ToString();
    if (!_persistedRulings.TryGetValue((item.ItemId, item.IsHq, routerVerdict), out var ruled))
      return;
    if (!Enum.TryParse<RoutingExit>(ruled, out var exit))
      return;
    item.Pile = exit;
    item.InReview = false;
    item.PlayerResolved = true;
  }

  private static Dictionary<(uint ItemId, bool IsHq, string RouterVerdict), string> LoadPersistedRulings()
  {
    try { return GilStorage.GetLatestRoutingRulings(); }
    catch { return []; }
  }

  private static Dictionary<string, int> LoadOverrideCounts()
  {
    try { return GilStorage.GetRoutingOverrideCounts(); }
    catch { return new Dictionary<string, int>(StringComparer.Ordinal); }
  }

  /// <summary>
  /// Confidence for a bag-gear verdict. The evidence axes come from the same inputs
  /// the rules engine judged; the override history for this verdict class refines the
  /// tier. NOTE (Fable QA): the bag path has no full lane model, so lane n is
  /// approximated from the community sample count - the signal is strongest on the
  /// triage (listed) rows that carry real sale history.
  ///
  /// <para>LANE SPREAD IS 0.0 HERE BECAUSE THE BOARD HAS NOT BEEN READ YET (ruled
  /// 08-21). The ORDER is the reason, not the data shape: recon runs BEFORE triage
  /// decides, so at the moment a bag row is scored no board spread exists to hand
  /// it - the quantiles a lane walk would produce are downstream of the very
  /// decision this evidence feeds. 0.0 is the absence answer
  /// (<see cref="LanePricing.BandSpread"/>): the tier's tight gate stays open rather
  /// than closing on a number this method would have had to invent. Confirmed
  /// correct 08-20; the behaviour does not change.</para>
  /// </summary>
  private static BoardConfidence.Evidence RoutedEvidence(
    RoutingItemInputs inputs, RoutingVerdict verdict, RoutingConfig cfg, bool bookIsNew = false)
  {
    var lean = verdict.IsReview ? VerdictLean.Neutral : verdict.Exit switch
    {
      RoutingExit.List => VerdictLean.OnMarket,
      RoutingExit.Vendor or RoutingExit.Gc or RoutingExit.Desynth => VerdictLean.OffMarket,
      _ => VerdictLean.Neutral,
    };
    var recentSales = inputs.CommunitySampleCount > 0 ? inputs.CommunitySampleCount
      : inputs.LastSale is not null ? 1 : 0;
    // The market-must-outbid axis (Drift's 07-22 ruling): the verdict defends the
    // worth the router actually scored it at; the market's bid is the List value
    // as weighed (own sale or community median). Null worth (pre-value exits,
    // Review) falls back to the existence rule.
    var verdictWorth = verdict.Scores is { } sc
      ? verdict.Exit switch
      {
        RoutingExit.Gc => sc.Gc,
        RoutingExit.Desynth => sc.Melt,
        RoutingExit.Vendor => sc.Vendor,
        RoutingExit.List => sc.List,
        _ => null,
      }
      : null;
    var marketBid = verdict.Scores?.List ?? inputs.CommunityMedian;
    return new BoardConfidence.Evidence(
      Lean: lean,
      LaneSampleCount: Math.Max(inputs.CommunitySampleCount, inputs.LastSale is not null ? 1 : 0),
      LaneSpread: 0.0,
      VelocityPerDay: inputs.MarketVelocity,
      RecentSalesCount: recentSales,
      EvidenceAgeDays: inputs.MarketLastSaleDays ?? 0,
      LocalCommunityAccord: Accord.Unknown,
      MinSamples: cfg.CommunityMinSamples,
      StaleDays: BoardConfidence.EvidenceStaleDays,
      VerdictWorth: verdictWorth,
      MarketBid: marketBid,
      BookIsNew: bookIsNew);
  }

  /// <summary>Confidence for an absorbed triage (listed) row - the Alexander Miniature path.</summary>
  internal ConfidenceTier ScoreStanding(PricingItem item, BoardPile natural)
    => BoardConfidence.Tier(StandingEvidence(item, natural),
      _overrideCounts.GetValueOrDefault(natural.ToString()));

  /// <summary>
  /// The evidence behind a standing row's tier - split out for the case tribunal, which
  /// needs the AXES and not just the three-word summary they fold into (Task 3). Same
  /// shape as <see cref="RoutedEvidence"/> and for the same reason: two derivations of
  /// one row's evidence is how a case comes to argue against a verdict the board never
  /// made.
  ///
  /// <para>THE SPREAD IS THE LANE'S OWN (3-3b; Drift: <i>"I 100% want confidence in the
  /// router decision to play a part"</i>). The walk that priced this lane banked its
  /// band on the decision's census, so the tier's tight gate reads the same
  /// disagreement the run log's "they agree / they disagree" clause reports - one
  /// measure (<see cref="LanePricing.BandSpread"/>), one boundary
  /// (<see cref="BoardConfidence.TapeNoiseCeiling"/> - the TAPE's own, un-welded from
  /// the board-now "scattered" line in 3b-4). A lane with no walk behind it - a
  /// synthetic flag row, a player's own contest - keeps 0.0, because absence of
  /// evidence is not disagreement.</para>
  /// </summary>
  internal static BoardConfidence.Evidence StandingEvidence(PricingItem item, BoardPile natural)
  {
    var lean = natural switch
    {
      BoardPile.Reprice or BoardPile.List => VerdictLean.OnMarket,
      BoardPile.PullAndVendor or BoardPile.Melt or BoardPile.Churn => VerdictLean.OffMarket,
      _ => VerdictLean.Neutral,
    };
    var census = item.Lane?.Census;
    return new BoardConfidence.Evidence(
      Lean: lean,
      LaneSampleCount: item.HistorySaleCount,
      LaneSpread: LanePricing.BandSpread(census?.BandLow, census?.BandHigh, census?.Median),
      VelocityPerDay: null,
      RecentSalesCount: item.HistorySaleCount, // sales in the past EvidenceStaleDays
      EvidenceAgeDays: 0,                       // the current listing is live evidence
      LocalCommunityAccord: Accord.Unknown,
      MinSamples: Plugin.Configuration.LaneMinHistorySamples,
      StaleDays: BoardConfidence.EvidenceStaleDays);
  }

  private static unsafe bool HasAnyMateria(InventoryItem* slot)
  {
    for (int m = 0; m < 5; m++)
      if (slot->Materia[m] != 0) return true;
    return false;
  }

  /// <summary>Loads open persistent flags (V12). Called on window open and after flag mutations.</summary>
  internal void RefreshHeldFlags()
  {
    _evidenceStamp++;
    try { _heldFlags = GilStorage.GetOpenStandingFlags(); }
    catch { _heldFlags = []; }
    HeldFlagsRefreshed?.Invoke();
  }

  internal static int VendorPriceOf(uint itemId)
  {
    try
    {
      return (int)Svc.Data.GetExcelSheet<Item>().GetRow(itemId).PriceLow;
    }
    catch { return 0; }
  }

  // ---- On Market (stage 3: the standing asks, from banked receipts) ----

  /// <summary>
  /// Builds the On Market rows from the decision receipts and whatever fresher
  /// facts are already lying around. Read-only, cached with the rest of the ledger,
  /// never per frame.
  ///
  /// <para><b>The freshness source, and why it is this one.</b> The board cache
  /// that MarketBoardHandler accumulates lives inside the pricing pipeline and
  /// holds exactly ONE item - whatever was last priced - with no timestamp of its
  /// own. Every batch it takes is banked, the instant it lands, into
  /// market_board_snapshot with a real seen_at. So the banked snapshot IS that
  /// cache, dated, and for every item instead of one. Reading it here costs one
  /// small query per standing row on refresh and causes no fetch of any kind: a
  /// pinch or a board open put those rows there, and this tab only reads what they
  /// already paid for. Nothing here polls, and there is no refresh button by
  /// ruling - the tab refreshes when the ledger does.</para>
  /// </summary>
  private List<OnMarketRow> BuildOnMarketRows()
  {
    var receipts = GilStorage.GetOpenReceipts()
      .Select(r => r with { ItemName = GilTracker.GetItemName(r.ItemId) })
      .ToList();

    // The four-score strip (walk unit 3): the Refresh-time caches, projected
    // per lane. Missing entries stay null - the row draws dashes, honestly.
    var laneScores = new Dictionary<(uint, bool, string), long?[]>();
    var meltGrades = new Dictionary<(uint, bool), MeltGrade>();
    foreach (var r in OnMarket.Standing(receipts))
    {
      var key = (r.ItemId, r.IsHq, r.Retainer);
      if (!laneScores.ContainsKey(key))
        laneScores[key] = ListedRowScores(r.ItemId, r.IsHq, r.Retainer);
      meltGrades[(r.ItemId, r.IsHq)] = ListedMeltGrade(r.ItemId, r.IsHq);
    }

    return OnMarket.Build(receipts, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), laneScores, meltGrades);
  }
}
