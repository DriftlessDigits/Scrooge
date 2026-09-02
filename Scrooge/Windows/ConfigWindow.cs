using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using static ECommons.GenericHelpers;

namespace Scrooge.Windows;

public sealed class ConfigWindow : Window
{
  private string _currentQuote = string.Empty;

  private string _currentQuoteAuthor = string.Empty;

  public override void OnOpen()
  {
    var quote = GilStorage.GetRandomQuote();
    if (quote != null)
    {
      _currentQuote = quote.Text;
      _currentQuoteAuthor = quote.Author;
    }
  }

  public ConfigWindow()
    : base("Scrooge Configuration")
  { }

  /// <summary>House section header — parchment-gold title over a separator.</summary>
  private static void SectionHeader(string title)
  {
    ImGui.Spacing();
    ImGui.TextColored(ScroogeColors.Header, title);
    ImGui.Separator();
  }

  public override void Draw()
  {

    // Quote header
    if (!string.IsNullOrEmpty(_currentQuote))
    {
      ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Header);
      ImGui.TextWrapped($"\"{_currentQuote}\"");
      ImGui.Text($"    — {_currentQuoteAuthor}");
      ImGui.PopStyleColor();
      ImGui.Spacing();
    }

    // Tab bar
    if (ImGui.BeginTabBar("##ConfigTabs"))
    {
      if (ImGui.BeginTabItem("Pricing"))
      {
        DrawPricingTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Timing"))
      {
        DrawTimingTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Output"))
      {
        DrawOutputTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Gil Tracking"))
      {
        DrawGilTrackingTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Retainers"))
      {
        DrawRetainersTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Hotkeys"))
      {
        DrawHotkeysTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Item Rules"))
      {
        DrawHawkSettingsTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Desynth"))
      {
        DrawDesynthTab();
        ImGui.EndTabItem();
      }

      // "Rounds" since the reconcile (ruled 2026-08-23): the old name "Ledger"
      // collided with the run-transcript window (whose knob lives on Output),
      // while these knobs configure the window titled "Scrooge - Rounds".
      if (ImGui.BeginTabItem("Rounds"))
      {
        DrawRoundsTab();
        ImGui.EndTabItem();
      }

      if (ImGui.BeginTabItem("Routing"))
      {
        DrawRoutingTab();
        ImGui.EndTabItem();
      }

      ImGui.EndTabBar();
    }

    // Players care about the version (installer shows it); the exact-commit fingerprint only matters for dev testing.
    if (Plugin.PluginInterface.IsDev)
    {
      ImGui.Spacing();
      ImGui.TextDisabled($"build {BuildStamp.Line}");
    }
  }

  /// <summary>
  /// Persist a PRICING knob. Saving one and forgetting to drop the round's cached
  /// prices leaves the run quoting numbers the settings no longer describe - and
  /// which knobs needed the drop used to be nine hand-written call sites and a hope.
  /// Every setter on the Pricing tab goes through here, so the question stops being
  /// asked per-knob: if it is on this tab, it re-prices.
  ///
  /// <para>It lives in the window rather than in Configuration because the cache is a
  /// live-run concern (Plugin.PinchHost), and Configuration has to stay reachable
  /// from a test build with no Dalamud in it.</para>
  /// </summary>
  private static void SavePricing()
  {
    Plugin.Configuration.Save();
    Plugin.PinchHost.ClearCachedPrices();
  }

  private void DrawPricingTab()
  {
    // --- Undercut Settings ---
    SectionHeader("Undercut Settings");
    ImGui.BeginGroup();
    ImGui.Text("Mode: ");
    ImGui.SameLine();
    var undercutMode = Plugin.Configuration.UndercutMode;
    ImGui.SetNextItemWidth(150);
    if (ConfigWidgets.EnumCombo("##undercutModeCombo", ref undercutMode))
    {
      Plugin.Configuration.UndercutMode = undercutMode;
      SavePricing();
    }
    ImGui.EndGroup();

    ConfigWidgets.Hint("How the hand writes the price once the lane has picked the seat.\n" +
                       "Every mode sits in the same place in the queue — they differ in the last few gil.\n\n" +
                       "Fixed Amount: Subtract a flat gil amount from the lowest listing.\n" +
                       "Gentlemans Match: Match the lowest listing exactly — no undercut.\n" +
                       "Clean Numbers: Rounds down to a clean number. Interval scales with price.\n" +
                       "Humanized: Randomly picks between Random Pinch, Gentleman's Match, or Clean Numbers per item.\n" +
                       "Simulates natural pricing — as if you checked the price and typed it from memory.\n\n" +
                       "TWO PLACES NO MODE APPLIES:\n" +
                       "Your own listing. If the price being written against is your own retainer's row,\n" +
                       "it is copied exactly and no mode arithmetic runs at all.\n" +
                       "A better-quality row. When the anchor is an HQ listing and the item being priced\n" +
                       "is NQ, the write is forced strictly under it however the mode landed — so\n" +
                       "Gentleman's Match does NOT match across qualities. Matching a strictly better\n" +
                       "item at the same money is a listing no buyer ever reaches.");

    // Fixed Amount is the only surviving mode that takes a number from the player;
    // the other three derive their own step from the price they write against.
    if (Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount)
    {
      int amount = Plugin.Configuration.UndercutAmount;
      if (ConfigWidgets.LabeledInt("Amount:", "##undercutAmountFixed", ref amount, 100, suffix: "Gil"))
      {
        Plugin.Configuration.UndercutAmount = Math.Clamp(amount, 1, int.MaxValue);
        SavePricing();
      }
      ConfigWidgets.Hint("How far under the row in front of us the hand writes, in gil.\n\n" +
                         "The seat is already chosen — this is only the size of the pinch.");
    }

    // --- Max Price Increase Cap ---
    var enableMaxIncrease = Plugin.Configuration.EnableMaxPriceIncreaseCap;
    if (ImGui.Checkbox("Max Price Increase Cap", ref enableMaxIncrease))
    {
      Plugin.Configuration.EnableMaxPriceIncreaseCap = enableMaxIncrease;
      SavePricing();
    }
    ConfigWidgets.Hint("Limit how far a price may climb in one pinch.\n\n" +
                       "Pricing up is a move backward in the queue — you are letting others go first — so one\n" +
                       "board read only buys a bounded step. The price steps up to the cap and keeps climbing on\n" +
                       "later pinches as the reads keep agreeing. It never freezes and it never skips the item.\n\n" +
                       "PINCH RUNS ONLY. A hawk run writes the full climb in one go — it is you standing at\n" +
                       "the board with the row in front of you, and the brake exists for the unattended pass.");

    if (Plugin.Configuration.EnableMaxPriceIncreaseCap)
    {
      float maxIncrease = Plugin.Configuration.MaxPriceIncreasePercentage;
      if (ConfigWidgets.LabeledFloat("Max Price Increase:", "##maxPriceIncreasePercentage",
                                     ref maxIncrease, 10f, 200f, 150, "%.0f", "%"))
      {
        Plugin.Configuration.MaxPriceIncreasePercentage = MathF.Round(maxIncrease);
        SavePricing();
      }
      ConfigWidgets.Hint("How far back in the queue one board read may move you: the maximum upward step, as a\n" +
                         "percentage of your current listing price.\n\n" +
                         "Example at 50%: if your item is listed at 10,000 gil, this pinch writes at most 15,000.\n" +
                         "A bigger jump is clamped to the cap, not skipped — the next pinch climbs from there,\n" +
                         "so the price still gets all the way to the seat over a few passes.\n\n" +
                         "Higher = more permissive. Lower = stricter.");
    }

    if (Plugin.Configuration.UndercutMode == UndercutMode.Humanized)
    {
      int maxPinch = Plugin.Configuration.HumanizedMaxPinch;
      if (ConfigWidgets.LabeledSlider("Max Random Pinch:", "##humanizedMaxPinch", ref maxPinch, 1, 10, 150, suffix: "Gil"))
      {
        Plugin.Configuration.HumanizedMaxPinch = maxPinch;
        SavePricing();
      }
      ConfigWidgets.Hint("The widest Random Pinch. When Humanized rolls Random Pinch, the write lands a\n" +
                         "random 1 to this many gil under the row in front.\n\n" +
                         "It only ever changes the last few gil — the seat in the queue is already chosen\n" +
                         "by the time this runs.");
    }

    var undercutSelf = Plugin.Configuration.UndercutSelf;
    if (ImGui.Checkbox("Undercut Self", ref undercutSelf))
    {
      Plugin.Configuration.UndercutSelf = undercutSelf;
      SavePricing();
    }
    // THE REAL SCOPE, SAID (B8). The lane deliberately never classifies your own rows
    // as competition - a stale own lowball must be free to walk UP - so this knob has
    // no effect on the lane path at all. It governs the first-pass read and nothing
    // else, and a label that implied otherwise was promising a behaviour on a path
    // where it is inert by design.
    ConfigWidgets.Hint("Whether your own retainer's listing counts as competition on the FIRST-PASS\n" +
                       "board read.\n\n" +
                       "On: your own row is treated like any other seller's and gets undercut.\n" +
                       "Off: if your retainer already holds the lowest price, the listing is left\n" +
                       "unchanged.\n\n" +
                       "The lane never reads this. It excludes your own rows from the queue on purpose —\n" +
                       "they are the thing being repriced, not the market — so a stale own lowball can\n" +
                       "still walk up whichever way this sits.");

    ImGui.SameLine(0, 40);
    var hq = Plugin.Configuration.HQ;
    if (ImGui.Checkbox("Use HQ price", ref hq))
    {
      Plugin.Configuration.HQ = hq;
      SavePricing();
    }
    // THE QUALITY LINE THROUGH ALL EVIDENCE (registry reconcile, 2026-08-23). The old
    // text claimed "no HQ listings -> skipped", which is true only on the quick
    // single-item read - the lane prices on regardless. It also owned the listings
    // half alone, while the same flag picks which SALES the lane trusts.
    ConfigWidgets.Hint("For an HQ item, price off HQ evidence only - HQ listings on the board and HQ sales\n" +
                       "in your sale history.\n\n" +
                       "With no HQ listings, the lane still prices: off HQ sales, or off NQ sales plus your\n" +
                       "HQ premium. Only the quick single-item read skips outright.\n\n" +
                       "Disable to compare against the cheapest listing regardless of quality.");

    ImGui.Separator();
    // --- Price Floor Mode dropdown ---
    SectionHeader("Price Floors");
    ImGui.BeginGroup();
    ImGui.Text("Price Floor Mode:");
    ImGui.SameLine();
    var priceFloorMode = Plugin.Configuration.PriceFloorMode;
    ImGui.SetNextItemWidth(150);
    if (ConfigWidgets.EnumCombo("##priceFloorModeCombo", ref priceFloorMode))
    {
      Plugin.Configuration.PriceFloorMode = priceFloorMode;
      // When switching to DomanEnclave, disable auto vendor sell
      if (Plugin.Configuration.PriceFloorMode == PriceFloorMode.DomanEnclave)
      {
        Plugin.Configuration.AutoVendorSellOnPriceCheckFail = false;
      }
      SavePricing();
    }
    ImGui.EndGroup();
    ConfigWidgets.Hint("An honest price under the selected floor forfeits the listing — the other exits\n" +
                       "compete for the item instead.\n\n" +
                       "None: No price floor. Items are listed at any price.\n" +
                       "Vendor: no listing under what a vendor would pay.\n" +
                       "Doman Enclave: no listing under 2x vendor price (assumes the max donation rate).\n\n" +
                       "Under Doman Enclave, a forfeited item is never auto-vendored: it is worth twice as\n" +
                       "much at the Enclave as at the counter, so it is kept in your bags for you.");

    int minPrice = Plugin.Configuration.MinimumListingPrice;
    if (ConfigWidgets.LabeledInt("Minimum Listing Price:", "##minimumListingPrice", ref minPrice, 100, suffix: "Gil"))
    {
      Plugin.Configuration.MinimumListingPrice = Math.Max(minPrice, 0);
      SavePricing();
    }
    ConfigWidgets.Hint("Your own floor, in gil. Scrooge holds ONE floor: the higher of this and the Price\n" +
                       "Floor Mode above.\n\n" +
                       "An honest price that lands under it forfeits the listing — the item is never priced\n" +
                       "up to reach the floor, it simply stops competing for the List exit, and the vendor,\n" +
                       "melt and turn-in exits compete for it instead.\n\n" +
                       "Set to 0 to disable.");

    // --- Auto Vendor Sell toggle ---
    var isDomanEnclave = Plugin.Configuration.PriceFloorMode == PriceFloorMode.DomanEnclave;
    if (isDomanEnclave) ImGui.BeginDisabled();

    var autoVendor = Plugin.Configuration.AutoVendorSellOnPriceCheckFail;
    if (ImGui.Checkbox("Auto vendor-sell items that fail price checks", ref autoVendor))
    {
      Plugin.Configuration.AutoVendorSellOnPriceCheckFail = autoVendor;
      SavePricing();
    }

    if (isDomanEnclave) ImGui.EndDisabled();

    ConfigWidgets.Hint(
      "During hawk runs, items that fail price floor or minimum listing price checks\n" +
      "are vendor-sold via the retainer instead of skipped.\n\n" +
      "Not available with Doman Enclave price floor mode — those items are\n" +
      "saved for manual Enclave donation.\n\n" +
      "Note: Items on the Always Vendor list are always vendor-sold regardless\n" +
      "of this toggle or price floor mode.");

    // MOVED HOME FROM GIL TRACKING (ruled 2026-08-23). It dims stale last-sales in
    // Hawk, but it also gates whether your own last sale may price a market-silent
    // item - a knob that writes real asks. On the wrong tab it saved with a bare
    // Save(), dodging the cache-drop doctrine above: change it, and the next pinch
    // still posted prices computed under the rule you had just revoked.
    int staleDays = Plugin.Configuration.StalePriceDays;
    if (ConfigWidgets.LabeledSlider("Stale Price Threshold:", "##stalePriceDays", ref staleDays, 0, 100, 150, suffix: "days"))
    {
      Plugin.Configuration.StalePriceDays = staleDays;
      SavePricing();
    }
    ConfigWidgets.Hint("How old a sale of yours may be and still count. Two jobs:\n\n" +
                       "Display: last sale prices older than this are dimmed in the Hawk\n" +
                       "Window.\n" +
                       "Pricing: when the board is fully silent, your own last sale prices\n" +
                       "the item - but only if it settled inside this window. Older, and\n" +
                       "the item is left for you.\n\n" +
                       "Set to 0 to turn both off: nothing dims, and the own-sale fallback\n" +
                       "prices at any age.");

    ImGui.Separator();
    // --- Lane Pricing ---
    SectionHeader("Lane Pricing");
    ImGui.TextDisabled("Listings are what people want; sales are what people paid.");
    ImGui.Spacing();

    float laneCeiling = Plugin.Configuration.UpwardRepriceMultiplier;
    if (ConfigWidgets.LabeledFloat("Lane ceiling:", "##laneCeiling", ref laneCeiling, 1.5f, 10f, 150, "%.1fx"))
    {
      Plugin.Configuration.UpwardRepriceMultiplier = MathF.Round(laneCeiling, 1);
      SavePricing();
    }
    ConfigWidgets.Hint("Board listings above (lane median x this) are walls and never\n" +
                       "anchor a price. One idea in every direction: 3x what it actually\n" +
                       "sells for = suspicious.\n\n" +
                       "It also caps what Scrooge writes when the board has nobody real\n" +
                       "on it. No sales history, no ceiling - the rail is built from what\n" +
                       "the item actually sells for.");

    // THE LABEL NAMES ALL FOUR DOORS (B8, RULED: it stays ONE knob; the tape door paid
    // 08-23). This number is read at four call sites and the old label owned one of
    // them, so a player raising his pricing bar silently raised the routing brain's bar
    // and the confidence tier's with it. One knob is the right shape - "enough settled
    // sales to trust" is one question - but a knob with four jobs has to say four jobs.
    int minSamples = Plugin.Configuration.LaneMinHistorySamples;
    if (ConfigWidgets.LabeledSlider("Min sales to trust:", "##laneMinSamples", ref minSamples, 1, 10, 150))
    {
      Plugin.Configuration.LaneMinHistorySamples = minSamples;
      SavePricing();
    }
    ConfigWidgets.Hint("Settled sales needed before evidence counts as enough. One bar, four\n" +
                       "doors - moving it moves all four:\n\n" +
                       "Lane pricing: below this the item is held and flagged instead of\n" +
                       "priced off an unvalidated board.\n" +
                       "Routing's tape witness: settled sales on this world need this many\n" +
                       "before they score the List exit.\n" +
                       "Community routing: the DC-wide sale count the router needs before\n" +
                       "it will weigh community evidence at all.\n" +
                       "Confidence tier: a lane thinner than this cannot seat a verdict\n" +
                       "unasked - the row comes to you at the hinge instead.");

    float halfLife = Plugin.Configuration.LaneHalfLifeDays;
    if (ConfigWidgets.LabeledFloat("Recency half-life:", "##laneHalfLife", ref halfLife, 7f, 60f, 150, "%.0f days"))
    {
      Plugin.Configuration.LaneHalfLifeDays = MathF.Round(halfLife);
      SavePricing();
    }
    ConfigWidgets.Hint("How fast old sales fade from the lane. A sale this many days old\n" +
                       "carries half the weight of one from today. Seed value - receipts\n" +
                       "will derive per-item values over time; erring long fails toward\n" +
                       "holding value.");

    int hqPremium = Plugin.Configuration.HqPremiumPercent;
    if (ConfigWidgets.LabeledSlider("HQ premium:", "##hqPremium", ref hqPremium, 0, 100, 150, "%d%%"))
    {
      Plugin.Configuration.HqPremiumPercent = Math.Clamp(hqPremium, 0, 100);
      SavePricing();
    }
    ConfigWidgets.Hint("How much extra an HQ item asks over its NQ price when there are\n" +
                       "no HQ sales to go on. Used only then - with HQ sales on record,\n" +
                       "or real sellers in the queue, those decide the price instead.");

    int seatBudget = Plugin.Configuration.SeatBudget;
    if (ConfigWidgets.LabeledSlider("Seat budget:", "##seatBudget", ref seatBudget, 0, 10, 150, "%d rows"))
    {
      Plugin.Configuration.SeatBudget = Math.Clamp(seatBudget, 0, 10);
      SavePricing();
    }
    ConfigWidgets.Hint("The judgment budget: the deepest seat a pricing walk may take on\n" +
                       "its own authority, in rows left standing in front of your listing.\n" +
                       "Stepping over crashers is fine while the seat stays this shallow;\n" +
                       "wanting a deeper one means the walk is wrong - it takes the front\n" +
                       "of the line instead. Use good judgement, but don't be wrong.");
  }

  private void DrawRoutingTab()
  {
    // PRICING-BAR DRESS (registry reconcile, 2026-08-23). The two TreeNodes
    // flattened to SectionHeaders - they were the only collapsibles in the
    // window, closed by default, so a fresh install saw an empty tab. "Rules
    // engine" was engine jargon a player never sees elsewhere; the bare
    // "Routing" header repeated the tab strip and led nothing.
    SectionHeader("Where an Item Goes");
    ImGui.TextDisabled("Scrooge weighs selling, melting, and turning in, then picks the best exit.");
    ImGui.Spacing();

    var sealRate = Plugin.Configuration.SealToGilRate;
    if (ConfigWidgets.LabeledInt("A GC seal is worth (gil):", "##sealToGilRate", ref sealRate, 150, 5, 25) && sealRate >= 0)
    {
      Plugin.Configuration.SealToGilRate = sealRate;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("What one Grand Company seal is worth to you in gil, so a turn-in can be\n" +
                       "compared against selling or melting. A rough starting number - once Scrooge\n" +
                       "has watched 10 ventures come back, it measures the real rate from what they\n" +
                       "returned and ignores this setting. The dashboard's Ventures tab says which\n" +
                       "one is in play.");

    // THE SEAL S-CURVE (Drift, 2026-08-05), REPARAMETERIZED (Drift, 2026-08-23:
    // "set the middle and the desired + -"). Storage stays FullBelow/ZeroAbove;
    // the UI speaks center +/- width and writes the endpoints back. The old
    // two-endpoint form let a player set full above zero - SealRunway then
    // scores seals at nothing forever, silently. A width cannot be negative,
    // so the trap is unbuildable now instead of warned about.
    var curveFull = Plugin.Configuration.SealCurveFullBelow;
    var curveZero = Plugin.Configuration.SealCurveZeroAbove;
    var curveCenter = (curveFull + curveZero) / 2;
    var curveWidth = (curveZero - curveFull) / 2;
    ImGui.BeginGroup();
    ImGui.Text("Token stock sweet spot:");
    ImGui.SameLine();
    ImGui.SetNextItemWidth(90);
    var centerChanged = ImGui.InputInt("##sealCurveCenter", ref curveCenter, 0, 0);
    ImGui.SameLine();
    ImGui.TextDisabled("plus or minus");
    ImGui.SameLine();
    ImGui.SetNextItemWidth(90);
    var widthChanged = ImGui.InputInt("##sealCurveWidth", ref curveWidth, 0, 0);
    ImGui.EndGroup();
    if (centerChanged || widthChanged)
    {
      curveCenter = Math.Max(0, curveCenter);
      curveWidth = Math.Clamp(curveWidth, 0, curveCenter);
      Plugin.Configuration.SealCurveFullBelow = curveCenter - curveWidth;
      Plugin.Configuration.SealCurveZeroAbove = curveCenter + curveWidth;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("Where you'd like your venture token stock to settle, give or take. Seals are\n" +
                       "worth full value when stock is at the bottom of this range, nothing at the\n" +
                       "top, sliding smoothly between (half value at the center). The effect is a\n" +
                       "thermostat: stocked up, seals cheapen and melting wins more; running low,\n" +
                       "seals richen and turn-in takes over - so your stockpile hovers near the\n" +
                       "center on its own. Turn-in reasons say when the curve changed a call.\n\n" +
                       "The dashboard's token color follows this too: orange under half the center,\n" +
                       "red under a quarter.");

    var reviewBand = Plugin.Configuration.RoutingReviewBandPct;
    if (ConfigWidgets.LabeledSlider("Send to Review when this close (%):", "##routingReviewBand", ref reviewBand, 0, 50, 150, "%d%%"))
    {
      Plugin.Configuration.RoutingReviewBandPct = reviewBand;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("When the best two exits score within this much of each other, Scrooge stops\n" +
                       "guessing and puts the item in Review with both reasons shown. Larger = more\n" +
                       "items come to you. One exception: if turn-in is one of the two and your\n" +
                       "tokens are below the number beneath, the call goes to turn-in instead.");

      // TWO OF THREE BANDS RIPPED OUT (ruled 2026-08-23). The old hover was a
      // double fossil: it promised a 45,000-gil rule on the "turn in" band that
      // exists nowhere, and called "panic" unread while it colored the
      // dashboard. VentureBandLow/Panic were paint-only - the ramp now derives
      // from the seal curve (orange under half its midpoint, red under a
      // quarter; GilWindow.Ventures) and both fields die in 3.1. Tilt stays:
      // it is the one band that changes a decision.
      ImGui.BeginGroup();
      var bandFull = Plugin.Configuration.VentureBandFull;
      ImGui.SetNextItemWidth(90);
      if (ImGui.InputInt("Prefer turn-in when tokens dip below", ref bandFull, 0, 0) && bandFull >= 0)
      {
        Plugin.Configuration.VentureBandFull = bandFull;
        Plugin.Configuration.Save();
      }
      ImGui.EndGroup();
      ConfigWidgets.Hint("When your venture token stock is below this, a too-close call between\n" +
                         "turn-in and something else goes to turn-in instead of Review - and the\n" +
                         "reason line says so. Above it, turn-in competes on pure value.\n\n" +
                         "The dashboard's token color turns green above this number.");

    SectionHeader("Community Market Data");
    ImGui.TextDisabled("Universalis fills the gaps when your own book has nothing to say.");
    ImGui.Spacing();

    {
      // THE DC PRICES, LAST IN LINE (registry reconcile, 2026-08-23; ruled "code
      // wins, fix words to match"). The old hover swore "advisor data only: it
      // NEVER sets a pinch or listing price" - false since the community lane:
      // when local history is thin, LaneEvaluation builds the pricing lane from
      // DC settled sales (ItemPricingPipeline hands UniversalisHistory in as the
      // communityProvider). The witness ladder and the ingot ruling (DC speaks
      // only when own-sale/tape/Look are ALL silent) settled the doctrine; only
      // this hover and a Configuration.cs comment claimed otherwise. The old
      // label's "(home world)" was also half the truth - sale history is
      // DATA-CENTER scope; only velocity/recency is home-world.
      var uniOn = Plugin.Configuration.EnableUniversalis;
      if (ImGui.Checkbox("Use Universalis market data", ref uniOn))
      {
        Plugin.Configuration.EnableUniversalis = uniOn;
        Plugin.Configuration.Save();
      }
      ConfigWidgets.Hint("Universalis is community-uploaded market data. Scrooge reads two things from\n" +
                         "it and never uploads anything: how fast an item sells on your world, which\n" +
                         "fills in the pace for gear you've never sold - and your data center's settled\n" +
                         "sales, which are consulted only when your own sale history is too thin to\n" +
                         "price from. That second one can set a listing price: your own sales always\n" +
                         "outrank it, and it only speaks when they're silent.\n\n" +
                         "Off, Scrooge uses only what it has watched happen to you.");

      if (uniOn)
      {
        var trustDays = Plugin.Configuration.UniversalisTrustDays;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("Ignore data older than", ref trustDays, 1, 30, "%dd"))
        {
          Plugin.Configuration.UniversalisTrustDays = trustDays;
          Plugin.Configuration.Save();
        }
        ConfigWidgets.Hint("Data nobody has uploaded since this long ago is treated as no data at all,\n" +
                           "rather than as a stale number worth guessing from. Thin-market items stay\n" +
                           "your call.");

        var ttlHours = Plugin.Configuration.UniversalisCacheTtlHours;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("Ask again after", ref ttlHours, 1, 48, "%dh"))
        {
          Plugin.Configuration.UniversalisCacheTtlHours = ttlHours;
          Plugin.Configuration.Save();
        }
        // THE TTL IS ALSO A DELETE CUTOFF (registry reconcile, 2026-08-23):
        // EnsureScope hands it to GetCommunityHistory as the prune line -
        // lowering the slider destroys banked DC rows older than the new
        // setting at the next scope load. Harmless by the schema's own
        // argument (those rows were already invisible), but the player is
        // told now.
        ConfigWidgets.Hint("How long a fetched Universalis answer is reused before Scrooge asks again.\n\n" +
                           "Shorter = fresher numbers and more calls to the service. Longer = fewer\n" +
                           "calls, and a routing verdict can be built on a read this many hours old.\n" +
                           "Nothing is refetched mid-run either way.\n\n" +
                           "Lowering this also clears saved answers older than the new setting.");
      }
    }
  }

  private void DrawTimingTab()
  {
    // PRICING-BAR DRESS (registry reconcile, 2026-08-23): headers only - every
    // tooltip on this tab was rewritten earlier tonight and stands as ruled.
    SectionHeader("Board Timing");
    ImGui.TextDisabled("How long Scrooge gives the market board to answer.");
    ImGui.Spacing();

    float currentMBDelay = Plugin.Configuration.GetMBPricesDelayMS / 1000f;
    if (ConfigWidgets.LabeledFloat("Price Check Delay (s):", "###sliderMBDelay", ref currentMBDelay, 0.1f, 10f, 150, "%.1f"))
    {
      Plugin.Configuration.GetMBPricesDelayMS = (int)(currentMBDelay * 1000);
      Plugin.Configuration.Save();
    }
    // THE TOOLTIP AND THE DEFAULT AGREE (the mechanical pile, 3b). It recommended 3-4s
    // over a knob that ships at 5.0s, so the settings screen advised against its own
    // shipped value and a player following the advice was tightening a margin nobody
    // had measured. The default is what it is; the tooltip says what that buys.
    // "Skipped" was the wrong fate (registry reconcile, 2026-08-23): a board that
    // never answers holds the item with a standing flag - it comes back to the
    // player, it is not passed over. And the delay is only paid on a cache miss.
    ConfigWidgets.Hint("How long to wait before opening the market board price list.\n\n" +
                       "Ships at 5.0s - slow, and reliable on a loaded server. Too low and prices\n" +
                       "fail to load and the item is held for you unpriced; too high and every\n" +
                       "uncached item costs the difference. Lower it only if your runs never miss\n" +
                       "a read.");

    // Keep Open Time DELISTED (3.1 sweep, per its own 08-23 note: "move those gaps
    // onto the ladder, then delist this row"). It was BoardReadLadder window 0 plus
    // side-job beats; the reprice gap folded onto the ladder 08-29 and no flat
    // tuning decision remained. Constant: Configuration.MarketBoardKeepOpenMS.

    SectionHeader("Humanization");
    ImGui.TextDisabled("Randomness in the waits, so the timing doesn't look scripted.");
    ImGui.Spacing();

    var enableJitter = Plugin.Configuration.EnableJitter;
    if (ImGui.Checkbox("Timing Humanization", ref enableJitter))
    {
      Plugin.Configuration.EnableJitter = enableJitter;
      Plugin.Configuration.Save();
    }
    // THE SWITCH COVERS MORE THAN PINCH (registry reconcile, 2026-08-23): it also
    // jitters recon's pacing and the bell reach - and imposes ApplyJitter's 1000ms
    // floor, which makes the 600ms bell reach SLOWER most of the time (the intent,
    // BellReach.cs:82-84). Hawk runs are deliberately unjittered; desynth humanizes
    // itself through Pacing.Jitter and never reads this flag.
    ConfigWidgets.Hint("Randomize the waits during pinch runs, recon runs, and the reach for the bell,\n" +
                       "so the timing doesn't look scripted.\n\n" +
                       "Every randomized wait is at least 1 second.\n" +
                       "Desynth paces itself separately - this switch doesn't touch it.");
    if (Plugin.Configuration.EnableJitter)
    {
      float currentJitter = Plugin.Configuration.JitterMS / 1000f;
      if (ConfigWidgets.LabeledFloat("Timing Jitter (s):", "##timingJitter", ref currentJitter, 0.5f, 3.5f, 150, "%.1f"))
      {
        Plugin.Configuration.JitterMS = (int)(currentJitter * 1000);
        Plugin.Configuration.Save();
      }
      // Scope and the 1s floor live on the Humanization tooltip above; this knob's
      // only job is the width. The old text named two of the five jittered waits.
      ConfigWidgets.Hint("How wide the randomness swings, plus or minus.\n\n" +
                         "Example: 1.0s jitter on a 4.0s wait means anywhere from 3.0s to 5.0s.\n" +
                         "Applies to every wait the Timing Humanization switch covers.");
    }
  }

  private void DrawOutputTab()
  {
    // PRICING-BAR DRESS (registry reconcile, 2026-08-23): headers only - every
    // tooltip on this tab was rewritten earlier tonight and stands as ruled. The
    // Ledger toggle leaves the chat columns for its own section: it is a
    // recording switch, not a chat preference, and it was the odd man in a grid
    // of chat lines.
    SectionHeader("Server Info Bar");
    var dtrOn = Plugin.Configuration.EnableDtrToday;
    if (ImGui.Checkbox("Today's gil in the server info bar", ref dtrOn))
    {
      Plugin.Configuration.EnableDtrToday = dtrOn;
      Plugin.Configuration.Save();
    }
    // THE BAR HAS PRECONDITIONS (registry reconcile, 2026-08-23; tracking-on clause
    // dropped with the master toggle, 3.1): it is a snapshot delta off the daily
    // buckets, and it hides itself without today's snapshot plus one earlier day to
    // compare against - a fresh install shows nothing for a day and the checkbox
    // looks broken.
    ConfigWidgets.Hint("Puts today's gil change in the server info bar. Click it to open the dashboard.\n\n" +
                       "'Today' is your local calendar day. The number only moves when a run records\n" +
                       "your gil, and it stays hidden until it has an earlier day to compare against.");
    ImGui.Spacing();

    SectionHeader("Chat");
    ImGui.TextDisabled("What a run says out loud while it works.");
    ImGui.Spacing();
    ImGui.Columns(2, "##chatOutputColumns", false);

    bool chatErrors = Plugin.Configuration.ShowErrorsInChat;
    if (ImGui.Checkbox("Show errors in chat", ref chatErrors))
    {
      Plugin.Configuration.ShowErrorsInChat = chatErrors;
      Plugin.Configuration.Save();
    }
    // ITEM LINES ONLY (registry reconcile, 2026-08-23). The old text implied it gated
    // errors generally, and cited the max-undercut cap - a reason that can no longer
    // print (the guard is delisted, inert at its 100 default, gone in 3.1). Every
    // orchestrator-level error is deliberately ungated: a run that breaks must say so.
    ConfigWidgets.Hint("Chat lines about individual items the run couldn't price.\n\n" +
                       "Covers: no legal ask above your floor, a board that came back with nothing,\n" +
                       "and pricing errors on a single item.\n\n" +
                       "Run-wide problems - a run that aborted, a view that wasn't open - always\n" +
                       "print, whichever way this sits.");

    ImGui.NextColumn();

    bool adjustmentsMessages = Plugin.Configuration.ShowPriceAdjustmentsMessages;
    if (ImGui.Checkbox("Show Price Adjustments", ref adjustmentsMessages))
    {
      Plugin.Configuration.ShowPriceAdjustmentsMessages = adjustmentsMessages;
      Plugin.Configuration.Save();
    }
    // THREE KINDS OF LINE, ONE GATE (registry reconcile, 2026-08-23). The old text
    // owned the repriced line only, but the same flag silences the Held line and the
    // own-sales-fallback notice - the two lines that explain why NOTHING happened.
    // The Ledger's counters increment from the pipeline, never from these printers,
    // so chat off cannot skew the transcript.
    ConfigWidgets.Hint("Chat lines about what happened to each item's price.\n\n" +
                       "A repriced item shows the old price, the new one, and the percent change,\n" +
                       "with a clickable link. Also covers items the run left alone on purpose and\n" +
                       "items priced off your own past sales.\n\n" +
                       "Vendor sales and the end-of-run summary print either way.");

    ImGui.NextColumn();

    bool retainerNames = Plugin.Configuration.ShowRetainerNames;
    if (ImGui.Checkbox("Show Retainer Names", ref retainerNames))
    {
      Plugin.Configuration.ShowRetainerNames = retainerNames;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("Print the retainer's name in chat before processing their listings.\n\n" +
                       "Helpful for tracking which retainer's items are being adjusted.");

    ImGui.Columns(1);

    SectionHeader("The Ledger");
    ImGui.TextDisabled("The run's written record.");
    ImGui.Spacing();

    // A RECORDING TOGGLE, NOT A WINDOW TOGGLE (registry reconcile, 2026-08-23). The
    // old label said "Show" and the old text promised "errors and warnings are always
    // captured" - but every write path early-returns when this is off (AddEntry, all
    // four counters): nothing is recorded, there is no transcript to go back to. It
    // also transcribes every errand now, and Rounds chapter instead of clearing.
    var enableLedger = Plugin.Configuration.EnableLedger;
    if (ImGui.Checkbox("Keep a run transcript (the Ledger)", ref enableLedger))
    {
      Plugin.Configuration.EnableLedger = enableLedger;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("Opens the Ledger when a run starts and records what happened to every item -\n" +
                       "repriced, held, vendor-sold - plus the run's totals.\n\n" +
                       "Switching this off doesn't just hide the window: nothing is recorded, and\n" +
                       "there's no transcript to go back to afterwards.\n\n" +
                       "A run on its own replaces the last transcript; during a Round each stage adds\n" +
                       "a chapter instead, and the whole Round reads as one book.");

    #if DEBUG
    // Debug-only: reset the learned pace for testing first-run behavior. Clears BOTH
    // banks (ruled 2026-08-23) - it used to zero the overall pace only, so a "reset"
    // Round still quoted its old per-stage ETAs and first-run testing wasn't.
    if (Plugin.Configuration.AvgMsPerItem > 0f || Plugin.Configuration.AvgMsPerItemByStage.Count > 0)
    {
      ImGui.SameLine();
      if (ImGui.SmallButton("Reset pace"))
      {
        Plugin.Configuration.AvgMsPerItem = 0f;
        Plugin.Configuration.AvgMsPerItemByStage.Clear();
        Plugin.Configuration.Save();
      }
      ConfigWidgets.Hint("Forget every learned pace - the overall ms/item and each Round stage's own.\n" +
                         "The next run estimates from scratch, like a first install. Debug builds only.");
      ImGui.SameLine();
      ImGui.TextDisabled($"({Plugin.Configuration.AvgMsPerItem:F0}ms/item)");
    }
    #endif
  }

  private void DrawGilTrackingTab()
  {
    // PRICING-BAR DRESS (registry reconcile, 2026-08-23): header + subheader; the
    // tooltip was rewritten earlier tonight (e8a9bd4) and stands, gaining one
    // line - the Rounds-tab freshness gate depends on this recorder (bffb8ca).
    SectionHeader("Gil Tracking");
    ImGui.TextDisabled("Scrooge's memory of your money.");
    ImGui.Spacing();

    // THE MASTER TOGGLE IS GONE (3.1: "you installed Scrooge, you get Scrooge").
    // The recorder is always-on - the half-gates it created (trued receipts never
    // stamped executed, the freshness gate's stamp starving, the frozen dashboard)
    // died with it. What remains on this tab is reading and resetting the record.
    ImGui.TextWrapped("Scrooge keeps a record of your money: what your retainers sell, what you " +
                      "spend and earn everywhere else, how long your listings sit, and what your " +
                      "retainers are holding. Most of it records quietly in the background; a " +
                      "pinch adds about a second and a half per retainer to read that retainer's " +
                      "sale history.");
    ImGui.Spacing();
    ImGui.TextDisabled("The Gil Dashboard shows it all (button below, or /giltrack).");

    #if DEBUG
    // THE MOST DESTRUCTIVE CONTROL IN THE PLUGIN (guarded 2026-08-23): it drops every
    // table sqlite_master names - the tape, decision receipts, transactions, all of
    // it - then re-runs the legacy migration. A bare SmallButton under the checkbox
    // was live ammunition for a mis-aimed click. Ctrl to arm, like its label says.
    var ctrlHeld = ImGui.GetIO().KeyCtrl;
    if (!ctrlHeld) ImGui.BeginDisabled();
    if (ImGui.SmallButton("Reset DB (drops everything)"))
    {
      GilStorage.ResetDatabase();
    }
    if (!ctrlHeld) ImGui.EndDisabled();
    ConfigWidgets.Hint("Debug: drops every table - sales, receipts, transactions, all of it - and\n" +
                       "re-runs the migration from the legacy backup. For testing migrations.\n" +
                       "Hold Ctrl and click.");
    #endif

    if (ImGui.Button("Open Gil Dashboard"))
      Plugin.GilDashboard.Toggle();
  }

  private void DrawRetainersTab()
  {
    // --- Retainers ---
    // PINCH-ONLY, AND SAY SO (registry reconcile, 2026-08-23). The old header -
    // "included during Auto Pinch", fossil name and all - read as "Scrooge won't
    // touch this retainer", which is false three ways: hawk parks in the first free
    // retainer, recon does the same, and the standing leg navigates wherever the
    // deck's items live. The fleet-capacity walk ignores the filter on purpose.
    // ONE LINE IN THE UI, MECHANICS BEHIND THE (?) (Pricing-bar ruling, same
    // night): skip-scope and the whitelist rule both live in the hover.
    SectionHeader("Pinch Visits");
    ImGui.TextDisabled("Which retainers the pinch run visits when it re-reads your board.");
    ConfigWidgets.Hint("Unchecked retainers are skipped by the pinch only - Scrooge still visits them\n" +
                       "when listing new items, when reading prices, and when repricing or pulling\n" +
                       "their existing listings.\n\n" +
                       "With every box checked, retainers you hire later are included automatically.\n" +
                       "Uncheck even one and the selection becomes a fixed list - new retainers are\n" +
                       "left out until you check them here.");
    ImGui.Spacing();
    // Try to fetch retainer names from the RetainerList addon if available
    unsafe
    {
      string[]? retainerNameArray = null;
      bool namesUpdated = false;

      if (TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && IsAddonReady(addon))
      {
        try
        {
          var retainerList = new AddonMaster.RetainerList(addon);
          retainerNameArray = [.. retainerList.Retainers.Select(r => r.Name)];

          // Update stored retainer names if they changed
          var currentNames = new HashSet<string>(retainerNameArray);
          var storedNames = new HashSet<string>(Plugin.Configuration.LastKnownRetainerNames);

          if (!currentNames.SetEquals(storedNames))
          {
            // Names changed - update the stored list
            Plugin.Configuration.LastKnownRetainerNames = [.. retainerNameArray];

            // NO PRUNE of EnabledRetainerNames here: this bell only shows ONE
            // character's roster, and the set is account-flat - a name missing
            // from THIS roster may be another character's retainer, and pruning
            // it silently reset that character to all-enabled. A stale name
            // (retired/renamed retainer) is inert - it just never matches.
            // Real pruning needs per-character rosters (3.1 income arm).

            Plugin.Configuration.Save();
            namesUpdated = true;
          }
        }
        catch
        {
          // Fallback if we can't read retainer names
        }
      }

      // Use fetched names if available, otherwise use stored names
      var namesToDisplay = retainerNameArray ?? [.. Plugin.Configuration.LastKnownRetainerNames];

      // EDITS ONLY AGAINST A LIVE LIST (ruled 2026-08-23). Two writes below trust
      // namesToDisplay's LENGTH and CONTENTS - the all-checked collapse and the
      // whitelist materialization on the first uncheck. Against a stale cache both
      // write the wrong selection (a cache of 3 vs 10 owned: check all 3 and the
      // collapse enables all 10; uncheck one and a recently hired retainer is
      // dropped from the materialized list). The cached view stays visible, but
      // read-only - editing is a bell errand.
      var editable = retainerNameArray != null;

      // Only display checkboxes if we have retainer names (either fetched or stored)
      if (namesToDisplay.Length > 0)
      {
        if (!editable) ImGui.BeginDisabled();
        // Calculate column offset from longest retainer name + checkbox width + padding
        float maxNameWidth = 0;
        for (int i = 0; i < namesToDisplay.Length; i++)
          maxNameWidth = Math.Max(maxNameWidth, ImGui.CalcTextSize(namesToDisplay[i]).X);
        float columnOffset = maxNameWidth + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + 40;

        for (int i = 0; i < namesToDisplay.Length; i++)
        {
          string retainerName = namesToDisplay[i];

          // Empty set = all enabled, sentinel = all disabled, non-empty = explicit whitelist
          bool allDisabled = Plugin.Configuration.EnabledRetainerNames.Contains(Configuration.ALL_DISABLED_SENTINEL);
          bool enabled = !allDisabled && (Plugin.Configuration.EnabledRetainerNames.Count == 0 || Plugin.Configuration.EnabledRetainerNames.Contains(retainerName));

          string label = $"{retainerName}##retainer{i}";
          if (ImGui.Checkbox(label, ref enabled))
          {
            Plugin.Configuration.EnabledRetainerNames.Remove(Configuration.ALL_DISABLED_SENTINEL);

            if (enabled)
            {
              Plugin.Configuration.EnabledRetainerNames.Add(retainerName);
              // Optimize: if all retainers are enabled, clear set to use default "all enabled" mode
              if (Plugin.Configuration.EnabledRetainerNames.Count == namesToDisplay.Length)
              {
                Plugin.Configuration.EnabledRetainerNames.Clear();
              }
            }
            else
            {
              // Transition from "all enabled" (empty set) to explicit whitelist
              if (Plugin.Configuration.EnabledRetainerNames.Count == 0)
              {
                foreach (string name in namesToDisplay)
                {
                  if (name != retainerName)
                  {
                    Plugin.Configuration.EnabledRetainerNames.Add(name);
                  }
                }
              }
              else
              {
                Plugin.Configuration.EnabledRetainerNames.Remove(retainerName);
                // Use sentinel to mark "all disabled" state (empty set means "all enabled")
                if (Plugin.Configuration.EnabledRetainerNames.Count == 0)
                {
                  Plugin.Configuration.EnabledRetainerNames.Add(Configuration.ALL_DISABLED_SENTINEL);
                }
              }
            }
            Plugin.Configuration.Save();
          }

          // Place next checkbox on same line if it's an even index (0, 2, 4, 6, 8)
          if (i % 2 == 0 && i < namesToDisplay.Length - 1)
            ImGui.SameLine(columnOffset);
        }

        if (!editable) ImGui.EndDisabled();

        if (retainerNameArray == null && !namesUpdated)
        {
          ImGui.TextColored(ScroogeColors.Muted, "(Showing the last list Scrooge saw - open your retainer list to edit)");
        }
      }
      else
      {
        ImGui.TextColored(ScroogeColors.Warning, "Open retainer list in-game to configure retainer selection");
      }
    }
  }

  private void DrawHotkeysTab()
  {
    // --- Hotkeys ---
    bool enablePostPinchKey = Plugin.Configuration.EnablePostPinchkey;
    SectionHeader("Hotkeys");
    ImGui.BeginGroup();
    if (ImGui.Checkbox("Enable Post'n'Pinch:", ref enablePostPinchKey))
    {
      Plugin.Configuration.EnablePostPinchkey = enablePostPinchKey;
      Plugin.Configuration.Save();
    }
    // THE SELL-PRICE PANEL, BOTH DOORS (registry reconcile, 2026-08-23): the panel
    // opens for Put Up for Sale AND Adjust Price, and since 18064c4 the listener is
    // named to it - the old "while posting an item" owned half the trigger.
    ConfigWidgets.Hint("Hold the key when the sell price window opens - a new Put Up for Sale or an\n" +
                       "Adjust Price on an existing listing - and Scrooge fills in the price for you\n" +
                       "instead of you checking the board by hand.");
    ImGui.EndGroup();

    ImGui.BeginGroup();
    if (enablePostPinchKey)
    {
      ImGui.Text("Post'n'Pinch Key:");
      ImGui.SameLine();

      var postPinchKey = Plugin.Configuration.PostPinchKey;
      ImGui.SetNextItemWidth(150);
      if (ConfigWidgets.KeyCombo("##postPinchKeyCombo", ref postPinchKey))
      {
        Plugin.Configuration.PostPinchKey = postPinchKey;
        Plugin.Configuration.Save();
      }

      ConfigWidgets.Hint("The key to hold when posting an item to trigger auto-pricing.\n\n" +
                         "Note: This key still performs its normal game function as well.");
    }
    ImGui.EndGroup();


    bool enablePinchKey = Plugin.Configuration.EnablePinchKey;
    if (ImGui.Checkbox("Enable Pinch Hotkey", ref enablePinchKey))
    {
      Plugin.Configuration.EnablePinchKey = enablePinchKey;
      Plugin.Configuration.Save();
    }
    // THE ROSTER PATH DOES EVERYONE (registry reconcile, 2026-08-23): the old text
    // said "the current retainer" while the retainer-list path pinches every enabled
    // retainer - the two sentences contradicted each other.
    ConfigWidgets.Hint("Press the key to start a pinch without clicking the button.\n\n" +
                       "On the retainer list it pinches every enabled retainer; on one retainer's\n" +
                       "sell list it pinches just that retainer. Holding the key restarts the run\n" +
                       "when the last one finishes.");

    ImGui.BeginGroup();
    if (enablePinchKey)
    {
      ImGui.Text("Pinch Key:");
      ImGui.SameLine();

      var pinchKey = Plugin.Configuration.PinchKey;
      ImGui.SetNextItemWidth(150);
      if (ConfigWidgets.KeyCombo("##pinchKeyCombo", ref pinchKey))
      {
        Plugin.Configuration.PinchKey = pinchKey;
        Plugin.Configuration.Save();
      }

      ConfigWidgets.Hint("The key that starts the pinch.\n\n" +
                         "Note: This key still performs its normal game function as well.");
    }
    ImGui.EndGroup();


    // The Text-To-Speech section is RETIRED with the feature (ruled 2026-08-29)
    // - see the retired-key comment in Configuration.cs for the era semantics.
  }

  /// <summary>
  /// THE ITEM RULES TAB (renamed from "Hawk Settings" 2026-08-23): the Always Vendor
  /// and Ban lists. The old name was hawk-scoped for lists that reach the router's
  /// first two rules, every board pile, recon's work set, and the salvage scan -
  /// these are the player's standing rules per item, not a run's settings.
  /// </summary>
  private void DrawHawkSettingsTab()
  {
    var vendorIds = Plugin.Configuration.AlwaysVendorItemIds;
    var bannedIds = Plugin.Configuration.BannedItemIds;
    var itemSheet = Svc.Data.GetExcelSheet<Item>();

    // INSTRUCTIONS FOR THE READER WHO HAS ITEMS (registry reconcile, 2026-08-23).
    // Each list's explanation only drew on the EMPTY state - shown to the one
    // player who didn't need it - and the vendor copy named the wrong gesture
    // (there is no right-click in the Hawk window; the menu is on the bag while
    // Hawk is open). Headers are unconditional now, lists sort by name, and a
    // vanished item id degrades to its number instead of throwing the tab down.
    // ONE LINE IN THE UI, MECHANICS BEHIND THE (?) (Pricing-bar ruling, same
    // night): each list keeps a plain subheader; the full rule and the add
    // gesture live in the hover.
    // --- Always Vendor list ---
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Amber);
    ImGui.Text("Always Vendor");
    ImGui.PopStyleColor();
    ImGui.Separator();
    ImGui.TextDisabled("Straight to the vendor, never the market board.");
    ConfigWidgets.Hint("Every Hawk run sells these to the vendor, picked or not, and the round skips\n" +
                       "checking prices on them entirely.\n\n" +
                       "To add one, right-click it in your bag and choose Always Vendor.");
    ImGui.Spacing();

    if (vendorIds.Count == 0)
    {
      ImGui.TextDisabled("No items set to always vendor.");
    }
    else
    {
      ImGui.Text($"{vendorIds.Count} item{(vendorIds.Count == 1 ? "" : "s")}");

      uint? vendorToRemove = null;

      foreach (var itemId in SortedByName(vendorIds, itemSheet))
      {
        // Stored in the house convention: HQ is the item id plus one million.
        var v = BellCommit.Decode(itemId);
        ImGui.Text(Format.Hq(SheetName(itemSheet, v.ItemId), v.IsHq));
        ImGui.SameLine();
        if (ImGui.SmallButton($"Remove##{itemId}"))
          vendorToRemove = itemId;
      }

      if (vendorToRemove.HasValue)
      {
        vendorIds.Remove(vendorToRemove.Value);
        Plugin.Configuration.Save();
      }
    }

    ImGui.Spacing();
    ImGui.Spacing();

    // --- Ban list ---
    ImGui.PushStyleColor(ImGuiCol.Text, ScroogeColors.Spent);
    ImGui.Text("Banned");
    ImGui.PopStyleColor();
    ImGui.Separator();

    ImGui.TextDisabled("Scrooge leaves these alone.");
    ConfigWidgets.Hint("They're never listed, melted, or turned in, and a price pass walks past one\n" +
                       "that's already listed without touching it.\n\n" +
                       "To add one, right-click it in your bag or on a retainer's sell list and choose\n" +
                       "Ban from Scrooge.");
    ImGui.Spacing();

    if (bannedIds.Count == 0)
    {
      ImGui.TextDisabled("No items are banned.");
    }
    else
    {
      ImGui.Text($"{bannedIds.Count} item{(bannedIds.Count == 1 ? "" : "s")}");

      uint? banToRemove = null;

      foreach (var itemId in SortedByName(bannedIds, itemSheet))
      {
        // Stored in the house convention: HQ is the item id plus one million.
        var v = BellCommit.Decode(itemId);
        ImGui.Text(Format.Hq(SheetName(itemSheet, v.ItemId), v.IsHq));
        ImGui.SameLine();
        if (ImGui.SmallButton($"Unban##{itemId}"))
          banToRemove = itemId;
      }

      if (banToRemove.HasValue)
      {
        bannedIds.Remove(banToRemove.Value);
        Plugin.Configuration.Save();
      }
    }
  }

  /// <summary>List entries in reading order - the raw HashSet is bucket order,
  /// which reads as random and shuffles across reloads.</summary>
  private static IEnumerable<uint> SortedByName(HashSet<uint> ids, Lumina.Excel.ExcelSheet<Item> sheet)
    => ids.OrderBy(id => SheetName(sheet, BellCommit.Decode(id).ItemId), StringComparer.OrdinalIgnoreCase);

  /// <summary>An id the sheet no longer knows degrades to its number instead of
  /// throwing mid-draw (TryGetRow is the house style - see LedgerCache).</summary>
  private static string SheetName(Lumina.Excel.ExcelSheet<Item> sheet, uint itemId)
    => sheet.TryGetRow(itemId, out var row) ? row.Name.ToString() : $"(unknown item {itemId})";

  /// <summary>
  /// THE ROUNDS TAB (nee Ledger; ruled ledger stage 2a, renamed at the 2026-08-23
  /// reconcile - the old name collided with the run-transcript window). The board
  /// and the round it launches are one surface now - one launch control, four
  /// stage boxes, one completion banner - and the knobs behind that surface had
  /// been scattered into whichever tab they were born next to. This is their home;
  /// new round knobs land here rather than wherever the code that reads them
  /// happens to live.
  /// </summary>
  private void DrawRoundsTab()
  {
    // PRICING-BAR DRESS (registry reconcile, 2026-08-23). The old intro spoke
    // "the ledger board" (a phrase that existed nowhere else in the codebase)
    // and "the one launch control" (the player sees a button called Make the
    // Rounds). Both hovers now obey the 08-02 strings ruling the gate's own
    // in-window message already followed: say what happens, not the
    // mechanism's pet names. "Floored at 1h" is gone from both - the slider
    // minimums are already 1, so the code floor only defends JSON edits.
    SectionHeader("Price Checks");
    ImGui.TextDisabled("How fresh a price has to be before a round trusts it.");
    ImGui.Spacing();

    // "THE BOARD READ" WAS NEVER A BOARD READ (finding folded here by ruling):
    // the clock is the completion stamp of the last all-retainer pinch that
    // FINISHED - single-retainer pinches, recons, hawks, and aborted runs never
    // stamp, and the stamp lands at run end. The hover says so in player words.
    // The stamp's writer (GilTracker.FinalizeRun) is ungated since the master
    // toggle died (3.1) - the gate's clock always ticks now.
    var repinchFloor = Plugin.Configuration.RepinchFloorHours;
    ImGui.SetNextItemWidth(150);
    if (ImGui.SliderInt("Skip the price check if newer than (hours)", ref repinchFloor, 1, 12))
    {
      Plugin.Configuration.RepinchFloorHours = repinchFloor;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("A round opens by re-reading your retainers' prices. If the last full price\n"
      + "check finished less than this many hours ago, the round skips that step and\n"
      + "gets straight to work. Full means it visited every retainer and finished - a\n"
      + "single-retainer check, a Look, or a cancelled run doesn't count.\n\n"
      + "Needs Gil Tracking on - that's what records when a price check finished. With\n"
      + "it off, every round re-checks prices no matter what this says.");

    var reconFresh = Plugin.Configuration.ReconFreshHours;
    ImGui.SetNextItemWidth(150);
    if (ImGui.SliderInt("Looked-up prices stay good for (hours)", ref reconFresh, 1, 72))
    {
      Plugin.Configuration.ReconFreshHours = reconFresh;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("When Scrooge looks up an item's price, it writes the answer down. Inside this\n"
      + "window, that written answer is trusted: the Look stage skips the item, listing\n"
      + "it uses the noted price instead of opening the board again, and the router\n"
      + "hears it when deciding the item's fate. Past this window the answer is old\n"
      + "news - the Look stage re-reads the item, and listing it pays for a fresh board\n"
      + "read. One number, both doors: fresh enough to skip re-reading is fresh enough\n"
      + "to act on.");

    SectionHeader("While Scrooge Is at a Retainer");
    ImGui.TextDisabled("Extra work the price check can finish, instead of a second trip.");
    ImGui.Spacing();

    // THE VENDOR RIDER GETS ITS ROW (B1.1, ruled 08-21). It has defaulted ON since
    // WALK unit 3 and it sells real items, and it had no knob anywhere - a seed that
    // gates behaviour needs a knob a player can see, or the behaviour is a secret the
    // settings screen is keeping. Default stays true; this only makes it visible.
    // "STAGED" WAS FALSE (registry reconcile, 2026-08-23): rows ride two ways -
    // hand-staged Vendor/Pull/Melt/Gc rows AND any untouched row whose natural
    // pile is PullAndVendor at Unanimous confidence. The old hover hid the very
    // thing a player would want to know: Scrooge sells on its own judgment here.
    // The hover now carries all five truths: auto-rides on confidence, retrieves
    // as well as sells (pull-for-melt/GC), never touches unsure rows, a
    // skipped price check skips the rider too, and the Doman Enclave carve-out.
    var vendorRider = Plugin.Configuration.PinchVendorRider;
    if (ImGui.Checkbox("Sell and retrieve items during the price check", ref vendorRider))
    {
      Plugin.Configuration.PinchVendorRider = vendorRider;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("On: while Scrooge is at a retainer checking prices, it also clears that\n"
      + "retainer's Pull & Vendor pile - items it is confident belong at the vendor go\n"
      + "straight there, even ones you never touched, and anything you staged for\n"
      + "melting or the Grand Company is pulled to your bags. Items Scrooge isn't sure\n"
      + "about are never touched; those always wait for you.\n"
      + "Off: all of it waits for the bell run instead. Nothing is lost either way -\n"
      + "only whether it happens now or later.\n\n"
      + "A round that skipped the price check skips this too. With the Doman Enclave\n"
      + "floor, items held for the Enclave are only sold here if you staged them\n"
      + "yourself.");
  }

  private void DrawDesynthTab()
  {
    // PRICING-BAR DRESS (registry reconcile, 2026-08-23): headers + one-line
    // subheaders in the UI, mechanics behind the (?). The old intro paragraph
    // also credited "Mutamix's Desynthesis menu" - Mutamix is the materia
    // melding NPC; the launcher anchors to the game's Desynthesis item list
    // (SalvageItemSelector), so the name is simply gone.
    // THE LAUNCHER TOGGLE IS GONE (same ruling): it only ever hid the preview
    // button on that list - rounds and the wizard showed the preview
    // regardless - and no other overlay button in the plugin offers an
    // opt-out. Its field left in the 3.1 sweep.
    SectionHeader("Desynthesis");
    ImGui.TextDisabled("Scrooge melts the pile for you; the pace is yours to set.");
    ImGui.Spacing();

    var pauses = Plugin.Configuration.DesynthHumanPauses;
    if (ImGui.Checkbox("Pause now and then, like a person would", ref pauses))
    {
      Plugin.Configuration.DesynthHumanPauses = pauses;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("Every 8 to 15 items, Scrooge stops for 3 to 8 seconds before carrying on.\n"
      + "People pause; scripts don't. The pause is added on top of the pace below, so\n"
      + "those items take a little longer.");

    // THE RIDER RIDES THE MELT, NOT THE ROUND FRONT (registry reconcile,
    // 2026-08-23). The old label said "front of a round" while the rider's only
    // call site is FireMeltStage - melt sits immediately before the bell by the
    // 07-25 ruling, so the coffers pop near the round's END and their contents
    // join the pile about to be sorted. The label contradicted its own tooltip.
    var openCoffers = Plugin.Configuration.OpenVentureCoffers;
    if (ImGui.Checkbox("Open Venture Coffers before melting", ref openCoffers))
    {
      Plugin.Configuration.OpenVentureCoffers = openCoffers;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("Just before a round melts, any Venture Coffers in your bags are opened, one at\n"
      + "a time, and what comes out joins the pile Scrooge is about to sort. Each one is\n"
      + "named in the run log. Round-only - the manual desynth button never opens\n"
      + "coffers. Scrooge skips it quietly if a run is already going or you have fewer\n"
      + "than five free bag slots.");

    // NO FALSE FLOOR (registry reconcile, 2026-08-23). The old grey line claimed
    // "Floor: 1500 ms" - the slider min is 800 and the jitter floors at 1 ms
    // (Pacing.Jitter). 1500 is the default, and lower is the player's judgment.
    var baseMs = Plugin.Configuration.DesynthPerActionBaseMs;
    ImGui.SetNextItemWidth(150);
    if (ImGui.SliderInt("Pace between items (ms)", ref baseMs, 800, 4000))
    {
      Plugin.Configuration.DesynthPerActionBaseMs = baseMs;
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint("How long Scrooge waits between one melt and the next, in milliseconds. The\n"
      + "real wait varies up to four-tenths of a second either side of this number, so\n"
      + "it is never the same twice. 1500 is the default and a comfortable human pace -\n"
      + "the lower you go, the faster and less human the run looks. That judgment is\n"
      + "yours. A stack counts one melt per item in it.");

    // The board freshness gate moved to the Ledger tab (stage 2a) - it is a knob
    // about the ROUND's cadence, not about desynthesis, and it was only ever here
    // because the fit check was born next to the melt.

    // THE SKILLUP LECTURE LIVES IN THE HOVERS NOW (same dress pass). The old
    // five-line wrapped paragraph was the ruling comment from Configuration.cs
    // recited at the player; the three-thirds mechanism survives whole in the
    // (?), shared word-for-word by both fields.
    SectionHeader("Skill-up Worth");
    ImGui.TextDisabled("What a yellow or red skill-up is worth to you, in gil.");
    ImGui.Spacing();

    const string skillupHover =
      "These aren't really desynth settings - they are prices you set, and Scrooge\n"
      + "uses them everywhere it decides an item's fate. When an item could give you a\n"
      + "skill-up, melting it is priced at your number, and that price competes against\n"
      + "everything else the item could be worth: what it actually sells for, what a\n"
      + "Grand Company pays in seals. A sale comfortably above your number wins the\n"
      + "market; below it, the melter wins; near it, Scrooge won't guess - the row\n"
      + "lands in Review for you to call. Red skill-ups are rarer than yellow, so most\n"
      + "people set red higher - Scrooge honors whatever you enter.";

    var worthYellow = Plugin.Configuration.SkillupWorthYellow;
    ImGui.SetNextItemWidth(150);
    if (ImGui.InputInt("Yellow skillup worth (gil)", ref worthYellow, 0, 0))
    {
      Plugin.Configuration.SkillupWorthYellow = Math.Max(0, worthYellow);
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint(skillupHover);

    var worthRed = Plugin.Configuration.SkillupWorthRed;
    ImGui.SetNextItemWidth(150);
    if (ImGui.InputInt("Red skillup worth (gil)", ref worthRed, 0, 0))
    {
      Plugin.Configuration.SkillupWorthRed = Math.Max(0, worthRed);
      Plugin.Configuration.Save();
    }
    ConfigWidgets.Hint(skillupHover);
  }
}