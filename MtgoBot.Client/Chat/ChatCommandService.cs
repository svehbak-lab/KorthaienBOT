using MtgoBot.Core.Data;
using MtgoBot.Core.Models;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Client.Chat;

/// <summary>
/// Handles in-game chat commands from customers.
/// Commands are triggered when a customer types in the trade chat window.
/// </summary>
public class ChatCommandService
{
    private readonly CardRepository _cards;
    private readonly ILogger<ChatCommandService> _logger;
    private readonly string _botId;

    private static readonly string HelpMessage =
        "🤖 KorthaienBOT commands: " +
        "HELP = this message | " +
        "PRICE [cardname] = get buy/sell price | " +
        "CREDIT = check your credit balance | " +
        "SETS = list sets we trade";

    public ChatCommandService(
        CardRepository cards,
        ILogger<ChatCommandService> logger,
        string botId)
    {
        _cards  = cards;
        _logger = logger;
        _botId  = botId;
    }

    /// <summary>
    /// Process a chat message from a customer.
    /// Returns a response string if the message is a command, null otherwise.
    /// </summary>
    public async Task<string?> ProcessAsync(string playerName, string message)
    {
        var msg = message.Trim().ToUpperInvariant();

        if (msg == "HELP")
            return HelpMessage;

        if (msg == "CREDIT")
            return await HandleCreditAsync(playerName);

        if (msg == "SETS")
            return await HandleSetsAsync();

        if (msg.StartsWith("PRICE "))
        {
            var cardName = message.Substring(6).Trim();
            return await HandlePriceAsync(cardName);
        }

        return null; // Not a command
    }

    private async Task<string> HandleCreditAsync(string playerName)
    {
        var credit = await _cards.GetPlayerCreditAsync(playerName);
        if (credit <= 0)
            return $"💰 {playerName}: No credit on file.";
        return $"💰 {playerName}: You have {credit:F4} TIX credit. It will be applied to your next trade.";
    }

    private async Task<string> HandleSetsAsync()
    {
        var sets = await _cards.GetActiveSetsAsync();
        var setList = string.Join(", ", sets.Take(10).Select(s => s.SetCode));
        return $"📦 Trading: {setList} and more. Ask for PRICE [cardname].";
    }

    private async Task<string> HandlePriceAsync(string cardName)
    {
        var cards = await _cards.SearchCardsByNameAsync(cardName);
        if (!cards.Any())
            return $"❓ Card not found: {cardName}. Check spelling.";

        var card = cards.First();
        var set  = await _cards.GetSetAsync(card.SetCode ?? "");

        decimal buyMult  = set?.DefaultBuyMultiplier  ?? 0.80m;
        decimal sellMult = set?.DefaultSellMultiplier ?? 1.00m;
        decimal buy      = card.CustomBuyPrice  ?? Math.Round(card.MarketPriceTix * buyMult, 4);
        decimal sell     = card.CustomSellPrice ?? Math.Round(card.MarketPriceTix * sellMult, 4);

        return $"💳 {card.CardName} ({card.SetCode}): " +
               $"Buy {buy:F4} TIX | Sell {sell:F4} TIX | Market {card.MarketPriceTix:F4} TIX";
    }
}
