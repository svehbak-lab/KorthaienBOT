using MtgoBot.Core.Data;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Core.Trading;

/// <summary>
/// Handles TIX rounding and credit storage.
///
/// MTGO trades in whole TIX only. Fractional TIX go to customer credit.
///
/// BUY (bot buys from customer):
///   - Customer offers cards worth 5.6 TIX
///   - Bot pays 5 TIX cash + 0.6 TIX stored as credit
///   - Customer can use credit in future trades
///
/// SELL (bot sells to customer):
///   - Customer buys cards worth 5.6 TIX
///   - Customer pays 6 TIX cash
///   - 0.4 TIX stored as credit for customer
///   - OR customer uses existing credit to cover fraction
/// </summary>
public class CreditRoundingService
{
    private readonly CardRepository _credits;
    private readonly TradeLogRepository _tradeLog;
    private readonly ILogger<CreditRoundingService> _logger;

    public CreditRoundingService(
        CardRepository credits,
        TradeLogRepository tradeLog,
        ILogger<CreditRoundingService> logger)
    {
        _credits  = credits;
        _tradeLog = tradeLog;
        _logger   = logger;
    }

    /// <summary>
    /// Calculate how much TIX to pay/charge and how much goes to credit.
    ///
    /// Returns (wholeTix, creditDelta) where:
    /// - wholeTix = actual TIX changing hands in the trade window
    /// - creditDelta = change to player's credit balance (positive = credit added)
    /// </summary>
    public async Task<(int WholeTix, decimal CreditDelta)> CalculateAsync(
        string playerName,
        decimal exactTix,
        TradeDirection direction)
    {
        decimal existingCredit = await _credits.GetPlayerCreditAsync(playerName);
        decimal fraction       = exactTix % 1.0m;
        int     whole          = (int)Math.Floor(exactTix);

        if (direction == TradeDirection.BotBuys)
        {
            // Bot buys from customer: pay floor, store remainder as credit
            // 5.6 TIX → pay 5, store 0.6 credit
            decimal newCredit = fraction;
            _logger.LogInformation(
                "BUY: {Player} gets {Whole} TIX + {Credit:F4} TIX credit (exact: {Exact:F4})",
                playerName, whole, newCredit, exactTix);
            return (whole, newCredit);
        }
        else
        {
            // Bot sells to customer: charge ceiling, store overpay as credit
            // 5.6 TIX → charge 6, store 0.4 credit back to customer
            // BUT first use any existing credit to cover the fraction
            decimal needed = fraction == 0 ? 0 : (1.0m - fraction);

            if (existingCredit >= needed && needed > 0)
            {
                // Customer has enough credit to cover — charge whole, deduct credit
                _logger.LogInformation(
                    "SELL: {Player} pays {Whole} TIX, uses {Credit:F4} credit (exact: {Exact:F4})",
                    playerName, whole, needed, exactTix);
                return (whole, -needed); // credit decreases
            }
            else
            {
                // Charge ceiling, store overpay as credit
                int charged    = whole + (fraction > 0 ? 1 : 0);
                decimal stored = fraction > 0 ? (1.0m - fraction) : 0;
                _logger.LogInformation(
                    "SELL: {Player} pays {Charged} TIX, gets {Stored:F4} credit back (exact: {Exact:F4})",
                    playerName, charged, stored, exactTix);
                return (charged, stored);
            }
        }
    }

    /// <summary>
    /// Apply credit change and log the trade.
    /// Call this after the trade is confirmed in MTGO.
    /// </summary>
    public async Task CommitAsync(
        string botId,
        string playerName,
        decimal exactTix,
        decimal creditDelta,
        TradeDirection direction,
        string? cardId = null,
        string? cardName = null,
        string? setCode = null,
        int quantity = 1)
    {
        decimal creditBefore = await _credits.GetPlayerCreditAsync(playerName);
        decimal creditAfter  = creditBefore + creditDelta;

        // Update credit
        if (creditDelta != 0)
            await _credits.SetCreditAsync(playerName, creditAfter);

        // Log trade
        await _tradeLog.LogTradeAsync(new TradeLogEntry
        {
            BotId        = botId,
            PlayerName   = playerName,
            TradeType    = direction == TradeDirection.BotBuys ? "BUY" : "SELL",
            SetCode      = setCode,
            CardId       = cardId,
            CardName     = cardName,
            Quantity     = quantity,
            PriceTix     = exactTix / quantity,
            TotalTix     = exactTix,
            CreditBefore = creditBefore,
            CreditAfter  = creditAfter,
            CreditChange = creditDelta
        });

        _logger.LogInformation(
            "✅ Trade committed: {Bot} {Dir} {Card} x{Qty} @ {Price:F4} TIX | Credit: {Before:F4} → {After:F4}",
            botId, direction, cardName ?? cardId, quantity, exactTix / quantity,
            creditBefore, creditAfter);
    }
}

public enum TradeDirection
{
    BotBuys,  // Customer sells to bot
    BotSells  // Bot sells to customer
}
