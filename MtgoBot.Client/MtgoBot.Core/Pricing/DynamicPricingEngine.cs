using MtgoBot.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Core.Pricing;

/// <summary>
/// Calculates the effective buy/sell price for any card, combining:
///   1. Tiered multipliers  (expensive / medium / bulk — from Gemini chat)
///   2. Set-level overrides (per MagicSet row)
///   3. Card-level overrides (CustomBuyPrice / CustomSellPrice)
///   4. Stock-based dynamic adjustment (0 stock → raise buy, full → stop buying)
///   5. Foil premium (foils get a separate multiplier, default 1.0x — adjust in config)
///
/// Priority (highest wins): card override > set override > tier > default
/// </summary>
public class DynamicPricingEngine
{
    private readonly PricingConfig _config;
    private readonly ILogger<DynamicPricingEngine> _logger;

    public DynamicPricingEngine(IConfiguration config, ILogger<DynamicPricingEngine> logger)
    {
        _config = config.GetSection("Pricing").Get<PricingConfig>() ?? new PricingConfig();
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────
    // Core method: what will the bot PAY for this card?
    // ─────────────────────────────────────────────────────────────────

    public decimal CalculateBuyPrice(
        Card card,
        MagicSet set,
        int currentStock,
        int maxStock)
    {
        // 1. Card-level custom override wins immediately
        if (card.CustomBuyPrice.HasValue)
            return card.CustomBuyPrice.Value;

        // 2. Market price is our base
        decimal market = card.MarketPriceTix;
        if (market <= 0) return 0m;

        // 3. Pick multiplier from tier (expensive / medium / bulk)
        var tier = GetTier(market);
        decimal multiplier;

        if (tier.FixedBuyPrice.HasValue)
        {
            // Bulk: fixed price, no multiplier — skip stock adjustment
            return ApplyFoilPremium(tier.FixedBuyPrice.Value, card.IsFoil);
        }

        // 4. Use set-level multiplier if set overrides the tier
        multiplier = set.DefaultBuyMultiplier != _config.DefaultBuyMultiplier
            ? set.DefaultBuyMultiplier
            : tier.BuyMultiplier;

        decimal basePrice = Math.Round(market * multiplier, 4);

        // 5. Dynamic stock adjustment (Gemini: 0 stock = +10%, full = 0)
        decimal adjusted = StockPriceAdjustment.Calculate(currentStock, maxStock, basePrice);

        // 6. Foil premium
        return ApplyFoilPremium(adjusted, card.IsFoil);
    }

    // ─────────────────────────────────────────────────────────────────
    // Core method: what does the bot CHARGE for this card?
    // ─────────────────────────────────────────────────────────────────

    public decimal CalculateSellPrice(Card card, MagicSet set)
    {
        if (card.CustomSellPrice.HasValue)
            return card.CustomSellPrice.Value;

        decimal market = card.MarketPriceTix;
        if (market <= 0) return 0m;

        var tier = GetTier(market);

        if (tier.FixedSellPrice.HasValue)
            return ApplyFoilPremium(tier.FixedSellPrice.Value, card.IsFoil);

        decimal multiplier = set.DefaultSellMultiplier != _config.DefaultSellMultiplier
            ? set.DefaultSellMultiplier
            : tier.SellMultiplier;

        return ApplyFoilPremium(Math.Round(market * multiplier, 4), card.IsFoil);
    }

    // ─────────────────────────────────────────────────────────────────
    // Tier lookup — from Gemini chat:
    //   >10 TIX  → 90% buy, 100% sell  (expensive, handle carefully)
    //   1-10 TIX → 80% buy, 100% sell  (medium, standard spread)
    //   <1 TIX   → fixed 0.005 buy, 0.01 sell  (bulk)
    // ─────────────────────────────────────────────────────────────────

    private PricingTier GetTier(decimal marketPrice)
    {
        // Check custom tiers from config first
        foreach (var tier in _config.Tiers)
        {
            if (marketPrice >= tier.MinPriceTix && marketPrice < tier.MaxPriceTix)
                return tier;
        }

        // Built-in fallback tiers matching Gemini spec exactly
        return marketPrice switch
        {
            > 10m  => new PricingTier { TierName = "Expensive", BuyMultiplier = 0.90m, SellMultiplier = 1.00m },
            >= 1m  => new PricingTier { TierName = "Medium",    BuyMultiplier = 0.80m, SellMultiplier = 1.00m },
            _      => new PricingTier { TierName = "Bulk",      FixedBuyPrice = 0.005m, FixedSellPrice = 0.010m }
        };
    }

    private decimal ApplyFoilPremium(decimal price, bool isFoil)
        => isFoil ? Math.Round(price * _config.FoilPriceMultiplier, 4) : price;
}

// ─────────────────────────────────────────────────────────────────
// Configuration model (maps to appsettings.json "Pricing" section)
// ─────────────────────────────────────────────────────────────────

public class PricingConfig
{
    /// <summary>Global default — used when set has no override.</summary>
    public decimal DefaultBuyMultiplier  { get; set; } = 0.80m;
    public decimal DefaultSellMultiplier { get; set; } = 1.00m;

    /// <summary>Foil cards get this multiplier on top of normal price.</summary>
    public decimal FoilPriceMultiplier   { get; set; } = 1.00m; // adjust per market

    /// <summary>Optional custom tiers loaded from config (overrides built-in tiers).</summary>
    public List<PricingTier> Tiers { get; set; } = [];
}
