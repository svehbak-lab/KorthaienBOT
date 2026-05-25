using MtgoBot.Core.Models;
using MtgoBot.Core.Data;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Core.Trading;

/// <summary>
/// The heart of the bot: calculates what to buy/sell, at what price,
/// and what the net balance is at any point in the trade window.
/// 
/// Design principle: pure calculation methods are static and testable
/// in isolation. Methods that touch the DB are async instance methods.
/// </summary>
public class TradeEngine
{
    // TIX always trade at exactly 1.0000 — no multipliers apply.
    public const string TixCardId   = "EVENT_TICKET";
    public const decimal TixPrice   = 1.0000m;
    public const int MaxWindowSlots = 100;  // MTGO trade window hard limit

    private readonly CardRepository _cards;
    private readonly InventoryRepository _inventory;
    private readonly CreditRepository _credits;
    private readonly ILogger<TradeEngine> _logger;

    public TradeEngine(
        CardRepository cards,
        InventoryRepository inventory,
        CreditRepository credits,
        ILogger<TradeEngine> logger)
    {
        _cards     = cards;
        _inventory = inventory;
        _credits   = credits;
        _logger    = logger;
    }

    // ─────────────────────────────────────────────────────────────────
    // STEP 1: Filter what the bot wants from a user's full offer list
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Given the full list of cards a user is offering (read from memory),
    /// return the ordered list of cards the bot actually wants to buy.
    /// Respects stock caps and filters out reserved cards.
    /// </summary>
    public async Task<List<FilteredCard>> FilterUserOffersAsync(
        string botId,
        IReadOnlyList<(string CardId, int Quantity)> userOffers,
        Dictionary<string, MagicSet> sets)
    {
        var cardIds  = userOffers.Select(o => o.CardId).Distinct();
        var cardMeta = await _cards.GetCardsByIdsAsync(cardIds);
        var botStock = await _inventory.GetBotInventoryAsync(botId);

        var wanted = new List<FilteredCard>();

        foreach (var (cardId, offeredQty) in userOffers)
        {
            // TIX are always welcome
            if (cardId == TixCardId)
            {
                wanted.Add(new FilteredCard(cardId, "Event Ticket", offeredQty, TixPrice));
                continue;
            }

            if (!cardMeta.TryGetValue(cardId, out var card)) continue;
            if (!sets.TryGetValue(card.SetCode, out var set)) continue;

            int currentStock   = botStock.GetValueOrDefault(card.CardId, 0);
            int maxStock       = card.EffectiveMaxStock(set.DefaultMaxStock);
            int redeemReserved = card.RedeemReserved;

            // FIX: original code subtracted currentStock twice.
            // maxStock  = units needed for normal trading
            // redeemReserved = extra units needed for set redemption
            // canBuy = how many more we need total
            int canBuy = Math.Max(0, (maxStock + redeemReserved) - currentStock);
            if (canBuy <= 0) continue;

            int toBuy        = Math.Min(offeredQty, canBuy);
            decimal buyPrice = card.EffectiveBuyPrice(set.DefaultBuyMultiplier);

            if (buyPrice <= 0) continue;

            wanted.Add(new FilteredCard(card.CardId, card.CardName, toBuy, buyPrice));
        }

        return [.. wanted.OrderByDescending(c => c.TotalValue)];
    }

    // ─────────────────────────────────────────────────────────────────
    // STEP 2: Build the initial window (max 100 slots)
    // ─────────────────────────────────────────────────────────────────

    public static (List<FilteredCard> WindowBatch, ReplenishQueue Queue)
        BuildWindowAndQueue(List<FilteredCard> filtered, List<Card> rawCards)
    {
        var window     = new List<FilteredCard>();
        var queue      = new ReplenishQueue();
        int slots      = 0;
        var cardLookup = rawCards.ToDictionary(c => c.CardId);

        foreach (var item in filtered)
        {
            if (slots < MaxWindowSlots)
            {
                window.Add(item);
                slots++;
            }
            else
            {
                if (cardLookup.TryGetValue(item.CardId, out var card))
                    queue.Enqueue(card);
            }
        }

        return (window, queue);
    }

    // ─────────────────────────────────────────────────────────────────
    // STEP 3: Recalculate net balance
    // ─────────────────────────────────────────────────────────────────

    public static TradeBalance RecalculateBalance(
        string playerName,
        string botId,
        List<TradeWindowCard> userSideCards,
        List<TradeWindowCard> botSideCards,
        decimal existingCredit,
        IntradayVelocityTracker? velocity = null)
    {
        if (velocity != null)
            foreach (var card in botSideCards)
                card.PriceTix = velocity.ApplyUplift(card.CardId, card.PriceTix);

        var balance = new TradeBalance
        {
            PlayerName = playerName,
            BotId      = botId,
            UserCards  = userSideCards,
            BotCards   = botSideCards,
        };

        decimal net = balance.NetBalance + existingCredit;
        balance.TixInWindow = Math.Max(0, Math.Floor(net));

        return balance;
    }

    public static decimal CalculateCreditRemainder(TradeBalance balance, decimal existingCredit)
    {
        decimal net = balance.NetBalance + existingCredit;
        return net - Math.Floor(Math.Max(0, net));
    }

    // ─────────────────────────────────────────────────────────────────
    // STEP 4: Commit trade to DB
    // ─────────────────────────────────────────────────────────────────

    public async Task CommitTradeAsync(TradeBalance balance, decimal creditRemainder)
    {
        _logger.LogInformation(
            "Committing trade for [{Player}]: in={In:0.0000} out={Out:0.0000} credit={C:0.0000}",
            balance.PlayerName, balance.ValueUserGives, balance.ValueBotGives, creditRemainder);

        var deltas = new Dictionary<string, int>();

        foreach (var card in balance.UserCards)
            deltas[card.CardId] = deltas.GetValueOrDefault(card.CardId) + card.Quantity;

        foreach (var card in balance.BotCards)
            deltas[card.CardId] = deltas.GetValueOrDefault(card.CardId) - card.Quantity;

        await _inventory.ApplyInventoryDeltasAsync(balance.BotId, deltas);

        if (creditRemainder != 0)
        {
            await _credits.ApplyCreditDeltaAsync(
                balance.PlayerName,
                balance.BotId,
                creditRemainder,
                $"Trade remainder from {balance.BotId}");
        }
    }
}

/// <summary>A card that passed the buy filter, ready to enter the window.</summary>
public record FilteredCard(
    string CardId,
    string CardName,
    int Quantity,
    decimal BuyPrice)
{
    public decimal TotalValue => Quantity * BuyPrice;
}
