using MtgoBot.Core.Data;
using MtgoBot.Core.Models;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Core.Trading;

/// <summary>
/// Detects complete sets in a customer's trade window and applies fullset pricing.
/// Rules:
/// - All cards must be from the same set (collector numbers 1 to base_set_size)
/// - All cards must be foil OR all non-foil — no mixing
/// - Only one complete set per trade
/// - Uses fullset buy/sell price from set_price_overrides
/// </summary>
public class FullsetDetector
{
    private readonly CardRepository _cards;
    private readonly ILogger<FullsetDetector> _logger;

    public FullsetDetector(CardRepository cards, ILogger<FullsetDetector> logger)
    {
        _cards  = cards;
        _logger = logger;
    }

    /// <summary>
    /// Check if the customer's offered cards form a complete set.
    /// Returns null if no complete set is detected.
    /// </summary>
    public async Task<FullsetTradeResult?> DetectAsync(
        List<TradeWindowCard> customerCards,
        bool fullsetBuyEnabled)
    {
        if (!fullsetBuyEnabled) return null;
        if (customerCards == null || customerCards.Count == 0) return null;

        // Group by set code
        var bySets = customerCards
            .Where(c => !string.IsNullOrEmpty(c.SetCode))
            .GroupBy(c => c.SetCode)
            .ToList();

        foreach (var setGroup in bySets)
        {
            var setCode = setGroup.Key;
            var cards   = setGroup.ToList();

            // Get set info
            var setInfo = await _cards.GetSetAsync(setCode);
            if (setInfo == null || setInfo.BaseSetSize == null) continue;

            int baseSize = setInfo.BaseSetSize.Value;

            // Check foil consistency
            bool hasNonFoil = cards.Any(c => !c.IsFoil);
            bool hasFoil    = cards.Any(c => c.IsFoil);

            if (hasNonFoil && hasFoil)
            {
                _logger.LogInformation(
                    "⚠️ Set {Set}: mixed foil/non-foil — not a valid complete set.", setCode);
                continue;
            }

            bool isFoil = hasFoil;

            // Check all collector numbers 1-baseSetSize are present
            var collectorNums = cards
                .Select(c => c.CollectorNumber)
                .Where(n => n > 0 && n <= baseSize)
                .ToHashSet();

            if (collectorNums.Count < baseSize)
            {
                _logger.LogInformation(
                    "Set {Set}: {Have}/{Need} cards — incomplete.",
                    setCode, collectorNums.Count, baseSize);
                continue;
            }

            bool complete = Enumerable.Range(1, baseSize).All(n => collectorNums.Contains(n));
            if (!complete) continue;

            // Get fullset pricing
            var pricing = await _cards.GetFullsetPricingAsync(setCode, isFoil);
            if (pricing == null || !pricing.FullsetEnabled)
            {
                _logger.LogInformation(
                    "Set {Set}: complete set detected but fullset pricing not enabled.", setCode);
                continue;
            }

            _logger.LogInformation(
                "✅ Complete {Type} set detected: {Set} ({Size} cards) — buy price: {Price} TIX",
                isFoil ? "foil" : "non-foil", setCode, baseSize, pricing.FullsetBuy);

            return new FullsetTradeResult
            {
                SetCode     = setCode,
                IsFoil      = isFoil,
                CardCount   = baseSize,
                BuyPriceTix = pricing.FullsetBuy ?? 0m,
                SellPriceTix = pricing.FullsetSell ?? 0m,
                Cards       = cards
            };
        }

        return null;
    }
}

public class FullsetTradeResult
{
    public string SetCode      { get; set; } = string.Empty;
    public bool   IsFoil       { get; set; }
    public int    CardCount    { get; set; }
    public decimal BuyPriceTix  { get; set; }
    public decimal SellPriceTix { get; set; }
    public List<TradeWindowCard> Cards { get; set; } = new();
}
