namespace MtgoBot.Core.Models;

/// <summary>
/// Represents a Magic set with its global buy/sell multipliers and stock limits.
/// Maps directly to the [sets] table.
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
/// A single card from the master card list. Prices here are the source of truth.
/// Custom overrides (custom_buy_price etc.) take precedence over set-level defaults.
/// </summary>
public class Card
{
    public string CardId { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string SetCode { get; set; } = string.Empty;
    public string Rarity { get; set; } = string.Empty;

    /// <summary>Raw market price pulled from price feed (4 decimal places).</summary>
    public decimal MarketPriceTix { get; set; }

    /// <summary>Manual price override. Null = use set multiplier on MarketPriceTix.</summary>
    public decimal? CustomBuyPrice { get; set; }
    public decimal? CustomSellPrice { get; set; }
    public int? CustomMaxStock { get; set; }

    /// <summary>
    /// Units reserved for owner redemption. These NEVER enter the trade pool.
    /// </summary>
    public int RedeemReserved { get; set; }

    // ──────────────────────────────────────────────
    // Computed helpers (not persisted)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Effective buy price the bot will pay for this card.
    /// Custom override wins; otherwise market * set multiplier.
    /// </summary>
    public decimal EffectiveBuyPrice(decimal setMultiplier)
        => CustomBuyPrice ?? Math.Round(MarketPriceTix * setMultiplier, 4);

    /// <summary>
    /// Effective sell price the bot asks when giving this card to a user.
    /// </summary>
    public decimal EffectiveSellPrice(decimal setMultiplier)
        => CustomSellPrice ?? Math.Round(MarketPriceTix * setMultiplier, 4);

    /// <summary>Effective max units the bot wants to hold.</summary>
    public int EffectiveMaxStock(int setDefault)
        => CustomMaxStock ?? setDefault;
}

/// <summary>
/// How many of a given card a specific bot currently holds.
/// </summary>
public class BotInventoryEntry
{
    public string BotId { get; set; } = string.Empty;
    public string CardId { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

/// <summary>
/// A player's credit balance — the "change" that accumulates when trades
/// don't divide evenly into whole TIX.
/// </summary>
public class UserCredit
{
    public string PlayerName { get; set; } = string.Empty;   // always lowercase
    public decimal CreditTix { get; set; }
    public DateTime LastTradeAt { get; set; }
}

/// <summary>
/// One entry in the immutable credit audit log.
/// </summary>
public class CreditLogEntry
{
    public int LogId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string BotId { get; set; } = string.Empty;
    public decimal DeltaAmount { get; set; }   // + credit given, - credit spent
    public decimal NewBalance { get; set; }
    public DateTime Timestamp { get; set; }
}

// ══════════════════════════════════════════════════════════════════
// Trade session models (in-memory only, not persisted directly)
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// A card currently visible inside the MTGO trade window on either side.
/// </summary>
public class TradeWindowCard
{
    public string CardId { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public TradeSide Side { get; set; }

    /// <summary>Price agreed for this card in this session.</summary>
    public decimal PriceTix { get; set; }

    /// <summary>Total value = Quantity * PriceTix</summary>
    public decimal TotalValue => Quantity * PriceTix;
}

public enum TradeSide
{
    /// <summary>Card the user is offering to the bot (bot buys).</summary>
    UserSide,
    /// <summary>Card the bot is offering to the user (bot sells).</summary>
    BotSide
}

/// <summary>
/// The running balance of an active trade session.
/// Recalculated every time the window changes.
/// </summary>
public class TradeBalance
{
    public string PlayerName { get; set; } = string.Empty;
    public string BotId { get; set; } = string.Empty;

    public List<TradeWindowCard> UserCards { get; set; } = [];
    public List<TradeWindowCard> BotCards { get; set; } = [];

    /// <summary>TIX placed in window on bot's side to cover excess.</summary>
    public decimal TixInWindow { get; set; }

    public decimal ValueUserGives => UserCards.Sum(c => c.TotalValue);
    public decimal ValueBotGives  => BotCards.Sum(c => c.TotalValue) + TixInWindow;

    /// <summary>
    /// Netto Balanse = (Kort brukeren gir) - (Kort boten gir).
    /// Positive = bot owes user; negative = user owes bot.
    /// </summary>
    public decimal NetBalance => ValueUserGives - ValueBotGives;

    /// <summary>Whole TIX to place in window (floor).</summary>
    public decimal WholeTixToPlace => Math.Floor(NetBalance);

    /// <summary>Fractional remainder → stored as credit.</summary>
    public decimal CreditRemainder => NetBalance - WholeTixToPlace;
}

/// <summary>
/// Cards the bot wants from a user's full collection but couldn't fit in the window.
/// These will be pulled in as slots open (replenish queue).
/// </summary>
public class ReplenishQueue
{
    private readonly Queue<Card> _queue = new();
    private readonly HashSet<string> _blacklist = new();

    public int Count => _queue.Count;

    public void Enqueue(Card card) => _queue.Enqueue(card);

    /// <summary>
    /// Blacklist a card for this session (user pulled it from window).
    /// </summary>
    public void Blacklist(string cardId) => _blacklist.Add(cardId);

    /// <summary>
    /// Dequeue the next card that hasn't been blacklisted.
    /// Returns null when queue is empty.
    /// </summary>
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
