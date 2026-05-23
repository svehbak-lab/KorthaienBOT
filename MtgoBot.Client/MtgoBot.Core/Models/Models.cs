namespace MtgoBot.Core.Models;

/// <summary>
/// Represents a Magic set with its global buy/sell multipliers and stock limits.
/// </summary>
public class MagicSet
{
    public string SetCode { get; set; } = string.Empty;
    public string SetName { get; set; } = string.Empty;
    public decimal DefaultBuyMultiplier { get; set; } = 0.80m;
    public decimal DefaultSellMultiplier { get; set; } = 1.00m;
    public int DefaultMaxStock { get; set; } = 8;
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A single card from the master card list.
/// IsFoil is critical — MTGO uses completely separate IDs for foil versions.
/// </summary>
public class Card
{
    public string CardId { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string SetCode { get; set; } = string.Empty;
    public string Rarity { get; set; } = string.Empty;
    public bool IsFoil { get; set; }

    public decimal MarketPriceTix { get; set; }
    public decimal? CustomBuyPrice { get; set; }
    public decimal? CustomSellPrice { get; set; }
    public int? CustomMaxStock { get; set; }
    public int RedeemReserved { get; set; }

    public decimal EffectiveBuyPrice(decimal setMultiplier)
        => CustomBuyPrice ?? Math.Round(MarketPriceTix * setMultiplier, 4);

    public decimal EffectiveSellPrice(decimal setMultiplier)
        => CustomSellPrice ?? Math.Round(MarketPriceTix * setMultiplier, 4);

    public int EffectiveMaxStock(int setDefault)
        => CustomMaxStock ?? setDefault;
}

public class BotInventoryEntry
{
    public string BotId { get; set; } = string.Empty;
    public string CardId { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

/// <summary>Bot types: TRADE bots face the public; MULE bots are private vaults.</summary>
public enum BotType { Trade, Mule }

public class BotInfo
{
    public string BotId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public BotType BotType { get; set; } = BotType.Trade;
    public bool IsOnline { get; set; }
    public DateTime LastSeen { get; set; }
}

public class UserCredit
{
    public string PlayerName { get; set; } = string.Empty;
    public decimal CreditTix { get; set; }
    public DateTime LastTradeAt { get; set; }
}

public class CreditLogEntry
{
    public int LogId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string BotId { get; set; } = string.Empty;
    public decimal DeltaAmount { get; set; }
    public decimal NewBalance { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

// ══════════════════════════════════════════════════════════════════
// Trading session models
// ══════════════════════════════════════════════════════════════════

public class TradeWindowCard
{
    public string CardId { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public TradeSide Side { get; set; }
    public decimal PriceTix { get; set; }
    public decimal TotalValue => Quantity * PriceTix;
}

public enum TradeSide { UserSide, BotSide }

public class TradeBalance
{
    public string PlayerName { get; set; } = string.Empty;
    public string BotId { get; set; } = string.Empty;
    public List<TradeWindowCard> UserCards { get; set; } = [];
    public List<TradeWindowCard> BotCards { get; set; } = [];
    public decimal TixInWindow { get; set; }

    public decimal ValueUserGives => UserCards.Sum(c => c.TotalValue);
    public decimal ValueBotGives  => BotCards.Sum(c => c.TotalValue) + TixInWindow;
    public decimal NetBalance     => ValueUserGives - ValueBotGives;
    public decimal WholeTixToPlace => Math.Floor(NetBalance);
    public decimal CreditRemainder => NetBalance - WholeTixToPlace;
}

public class ReplenishQueue
{
    private readonly Queue<Card> _queue = new();
    private readonly HashSet<string> _blacklist = new();

    public int Count => _queue.Count;
    public void Enqueue(Card card) => _queue.Enqueue(card);
    public void Blacklist(string cardId) => _blacklist.Add(cardId);

    public Card? DequeueNext()
    {
        while (_queue.Count > 0)
        {
            var card = _queue.Dequeue();
            if (!_blacklist.Contains(card.CardId))
                return card;
        }
        return null;
    }
}

// ══════════════════════════════════════════════════════════════════
// Pricing tier models
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Price tier as discussed with Gemini:
/// Expensive (>10 TIX): 90% buy
/// Medium (1-10 TIX):   80% buy
/// Bulk (<1 TIX):       fixed 0.005 buy / 0.01 sell
/// </summary>
public class PricingTier
{
    public string TierName { get; set; } = string.Empty;
    public decimal MinPriceTix { get; set; }
    public decimal MaxPriceTix { get; set; }
    public decimal BuyMultiplier { get; set; }
    public decimal SellMultiplier { get; set; }
    public decimal? FixedBuyPrice { get; set; }   // overrides multiplier for bulk
    public decimal? FixedSellPrice { get; set; }
}

/// <summary>
/// Dynamic pricing adjustment based on current stock level.
/// Gemini: 0 stock = raise buy to attract sellers; full = drop buy to 0.
/// </summary>
public class StockPriceAdjustment
{
    public int StockLevel { get; set; }
    public int MaxStock { get; set; }
    public decimal BuyPriceMultiplierOverride { get; set; }

    public static decimal Calculate(int currentStock, int maxStock, decimal baseBuyPrice)
    {
        if (maxStock <= 0) return 0;
        double fillRatio = (double)currentStock / maxStock;

        return fillRatio switch
        {
            0              => Math.Round(baseBuyPrice * 1.10m, 4),  // 0 stock: +10% to attract sellers
            < 0.25         => Math.Round(baseBuyPrice * 1.05m, 4),  // <25% full: slight premium
            < 0.75         => baseBuyPrice,                          // normal range: standard price
            < 1.0          => Math.Round(baseBuyPrice * 0.90m, 4),  // >75% full: slight discount
            _              => 0m                                     // full: stop buying
        };
    }
}
