using Scrooge;
using Scrooge.Windows;
using Xunit;

namespace Scrooge.Tests;

/// <summary>
/// PINS ON THE PRICING PASS'S VOICE (review pricing split, 2026-08-16).
///
/// <para>These five composers moved out of <c>ItemPricingPipeline</c>, where they sat
/// as private statics between unsafe addon reads and could not be reached from a test.
/// The hold sentences are the rows a player comes back to days later, so what they SAY
/// - and, more importantly, which of the four holds they pick - is pinned here.</para>
/// </summary>
public class PricingVoiceTests
{
  private static LaneDecision Decision(LaneOutcome outcome, int sellers = 0, long? anchor = null)
    => new()
    {
      Outcome = outcome,
      Anchor = anchor,
      Evidence = "3 sales on record",
      Census = new LaneCensus(sellers, 0, 0, 0, null, null, 0, null, null, 0, null),
    };

  // --- VoiceName: the row's own name column ---

  [Fact]
  public void VoiceName_flags_quality()
  {
    Assert.Equal("Kudzu Thread HQ",
      PricingVoice.VoiceName(new PricingItem { IsHq = true }, "Kudzu Thread"));
    Assert.Equal("Kudzu Thread",
      PricingVoice.VoiceName(new PricingItem { IsHq = false }, "Kudzu Thread"));
  }

  [Fact]
  public void VoiceName_without_an_item_reads_NQ()
    => Assert.Equal("Kudzu Thread", PricingVoice.VoiceName(null, "Kudzu Thread"));

  // --- HeldReason: the short form, on the line ---

  [Fact]
  public void HeldReason_names_the_silent_board_over_thin_history()
  {
    Assert.Equal(RunLogVoice.Reasons.BoardSilent,
      PricingVoice.HeldReason(new PricingItem { MbTimedOut = true }));
    Assert.Equal(RunLogVoice.Reasons.TooFewSales,
      PricingVoice.HeldReason(new PricingItem { MbTimedOut = false }));
  }

  // --- HeldEvidence: the long form, quoted by four surfaces ---

  [Fact]
  public void HeldEvidence_falls_back_when_no_lane_ran()
    => Assert.Equal("history too thin to build a lane", PricingVoice.HeldEvidence(null));

  [Fact]
  public void HeldEvidence_quotes_the_lane_when_one_ran()
    => Assert.Equal("3 sales on record",
      PricingVoice.HeldEvidence(new PricingItem { Lane = Decision(LaneOutcome.HeldThinHistory) }));

  [Fact]
  public void HeldEvidence_says_didnt_respond_with_the_attempt_count()
    => Assert.Equal("market board didn't respond (4 attempts) - held; will retry next pinch.",
      PricingVoice.HeldEvidence(new PricingItem { MbTimedOut = true, MbAttempts = 4 }));

  [Fact]
  public void HeldEvidence_a_silent_board_outranks_a_queued_community_fetch()
  {
    // Both flags set: the board's silence is the fact the player needs, and it is
    // the one that decides whether the item is even askable next pinch.
    var item = new PricingItem { MbTimedOut = true, MbAttempts = 2, CommunityQueued = true };
    Assert.Equal("market board didn't respond (2 attempts) - held; will retry next pinch.",
      PricingVoice.HeldEvidence(item));
  }

  [Fact]
  public void HeldEvidence_appends_the_community_retry_when_one_is_queued()
    => Assert.Equal("3 sales on record Checking community sales history — will retry next pinch.",
      PricingVoice.HeldEvidence(new PricingItem
      {
        CommunityQueued = true,
        Lane = Decision(LaneOutcome.HeldThinHistory),
      }));

  // --- ReconHoldReason: which of four silences ---

  [Fact]
  public void ReconHoldReason_names_the_guard_that_refused_before_any_evidence_story()
  {
    // The floor outranks a timed-out board: the guard refused a real number, and
    // "the board never answered" would send the player looking at the wrong thing.
    var below = new PricingItem
    {
      Result = PricingResult.BelowFloor, RejectedPrice = 44, MbTimedOut = true,
    };
    var floor = PriceFloor.Effective(PriceFloorMode.None, 0, 75);
    Assert.Equal(RunLogVoice.Reasons.BelowFloor(floor, 44),
      PricingVoice.ReconHoldReason(below, Decision(LaneOutcome.HeldThinHistory), floor));
  }

  [Fact]
  public void ReconHoldReason_below_floor_names_the_floor_that_refused_it()
  {
    // ONE verdict species, three possible binding floors (the one floor law,
    // 2026-08-21). The sentence names whichever one actually bound.
    var item = new PricingItem { Result = PricingResult.BelowFloor, RejectedPrice = 900 };
    var vendor = PriceFloor.Effective(PriceFloorMode.Vendor, 1_159, 0);
    var enclave = PriceFloor.Effective(PriceFloorMode.DomanEnclave, 1_159, 0);
    var minimum = PriceFloor.Effective(PriceFloorMode.None, 1_159, 5_000);

    Assert.Contains("the vendor's 1,159",
      PricingVoice.ReconHoldReason(item, Decision(LaneOutcome.HeldThinHistory), vendor));
    Assert.Contains("the Enclave's 2,318",
      PricingVoice.ReconHoldReason(item, Decision(LaneOutcome.HeldThinHistory), enclave));
    Assert.Contains("your 5,000 minimum",
      PricingVoice.ReconHoldReason(item, Decision(LaneOutcome.HeldThinHistory), minimum));

    // The operand is the honest ask that lost, never the floor and never the board.
    Assert.Contains("900/ea",
      PricingVoice.ReconHoldReason(item, Decision(LaneOutcome.HeldThinHistory), vendor));
  }

  [Fact]
  public void ReconHoldReason_a_board_with_sellers_is_a_different_silence_from_an_empty_one()
  {
    var item = new PricingItem { Result = PricingResult.LaneHeld };
    Assert.Equal(RunLogVoice.Reasons.NothingWorthStandingBehind,
      PricingVoice.ReconHoldReason(item, Decision(LaneOutcome.HeldThinHistory, sellers: 7), NoFloor));
    Assert.Equal(RunLogVoice.Reasons.TooFewSales,
      PricingVoice.ReconHoldReason(item, Decision(LaneOutcome.HeldThinHistory, sellers: 0), NoFloor));
  }

  [Fact]
  public void ReconHoldReason_a_timed_out_board_outranks_the_census_story()
  {
    var item = new PricingItem { Result = PricingResult.LaneHeld, MbTimedOut = true };
    Assert.Equal(RunLogVoice.Reasons.BoardSilent,
      PricingVoice.ReconHoldReason(item, Decision(LaneOutcome.HeldThinHistory, sellers: 7), NoFloor));
  }

  // --- LaneOutcomeEntry: the run-log row, or silence ---

  [Fact]
  public void LaneOutcomeEntry_is_silent_without_a_lane()
    => Assert.Null(PricingVoice.LaneOutcomeEntry(new PricingItem(), "Kudzu Thread"));

  [Fact]
  public void LaneOutcomeEntry_is_silent_on_a_plain_undercut()
  {
    var item = new PricingItem { Lane = Decision(LaneOutcome.Undercut, anchor: 400), FinalPrice = 399 };
    Assert.Null(PricingVoice.LaneOutcomeEntry(item, "Kudzu Thread"));
  }

  [Fact]
  public void LaneOutcomeEntry_names_a_stepped_over_crazy()
    => AssertRow(LaneOutcome.CrazySkipped, ItemOutcome.CrazySkipped);

  [Fact]
  public void LaneOutcomeEntry_names_an_empty_board()
    => AssertRow(LaneOutcome.EmptyBoard, ItemOutcome.EmptyBoard);

  [Fact]
  public void LaneOutcomeEntry_names_an_HQ_premium_taken_off_NQ()
    => AssertRow(LaneOutcome.PremiumFromNq, ItemOutcome.PremiumFromNq);

  // LaneOutcome is internal, so the cases ride a helper rather than [InlineData].
  private static void AssertRow(LaneOutcome lane, ItemOutcome expected)
  {
    var item = new PricingItem
    {
      IsHq = true,
      Lane = Decision(lane, anchor: 400),
      FinalPrice = 399,
      CurrentListingPrice = 500,
    };

    var row = PricingVoice.LaneOutcomeEntry(item, "Kudzu Thread");

    Assert.NotNull(row);
    Assert.Equal(expected, row!.Value.Outcome);
    Assert.Equal("Kudzu Thread HQ", row.Value.Name);
    Assert.NotEmpty(row.Value.Voice.Line);
  }

  /// <summary>No floor configured at all - the shape every non-floor hold is asked in.</summary>
  private static EffectiveFloor NoFloor
    => PriceFloor.Effective(PriceFloorMode.None, vendorPrice: 0, minimumListingPrice: 0);
}
