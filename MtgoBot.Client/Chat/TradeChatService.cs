using MtgoBot.Core.Models;
using MtgoBot.Client.Memory;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Client.Chat;

/// <summary>
/// Formats and sends chat messages to the MTGO trade window.
/// Now wired to MtgoMemoryReader.SendChatMessage() which uses
/// UI Automation to type into MTGO's chat input field.
/// </summary>
public class TradeChatService
{
    private readonly MtgoMemoryReader _reader;
    private readonly ILogger<TradeChatService> _logger;

    public TradeChatService(MtgoMemoryReader reader, ILogger<TradeChatService> logger)
    {
        _reader = reader;
        _logger = logger;
    }

    public Task SendWelcomeAndScanningAsync(string playerName)
    {
        var msg = "Hei! 🤖 Jeg skanner kortene dine nå for å se hva jeg har behov for. Vennligst vent et øyeblikk...";
        return SendAsync(playerName, msg);
    }

    public Task SendNoMatchesFoundAsync(string playerName)
    {
        var msg = "Takk for titten! Dessverre fant jeg ingen kort denne gangen som matcher mine gjeldende filtere eller lagerbeholdning. Ha en fin dag videre!";
        return SendAsync(playerName, msg);
    }

    public Task SendWindowLoadedAsync(string playerName)
    {
        var msg = "Jeg har lagt til de første 100 kortene jeg trenger. " +
                  "Dersom du ønsker å beholde noen av disse selv, er det bare å ta dem ut av vinduet, " +
                  "så fyller jeg automatisk på med andre!";
        return SendAsync(playerName, msg);
    }

    public Task SendTradeStatusAsync(
        string playerName,
        TradeBalance balance,
        decimal oldCredit,
        decimal newCredit)
    {
        decimal wholeTix    = Math.Floor(Math.Max(0, balance.NetBalance + oldCredit));
        decimal creditSaved = (balance.NetBalance + oldCredit) - wholeTix;

        var msg = $"--- 📊 BYTTESTATUS --- " +
                  $"Kort du gir meg: {balance.UserCards.Count} stk ({balance.ValueUserGives:0.0000} TIX) | " +
                  $"Kort du tar: {balance.BotCards.Count} stk ({balance.ValueBotGives:0.0000} TIX) | " +
                  $"Netto: {balance.NetBalance:+0.0000;-0.0000} TIX | " +
                  $"TIX i vindu: {wholeTix:0.0000} | " +
                  $"Credit lagret: {creditSaved:0.0000} TIX. Klikk Submit! 👍";

        // Note: MTGO chat has a ~250 char limit per message, so status is kept on one line.
        return SendAsync(playerName, msg);
    }

    public Task SendTradeCompleteAsync(string playerName, decimal finalCredit)
    {
        var msg = $"✅ Handel fullført! Din totale credit-saldo er nå {finalCredit:0.0000} TIX. Velkommen igjen!";
        return SendAsync(playerName, msg);
    }

    public Task SendTradeCancelledAsync(string playerName)
    {
        var msg = "Handelen ble avbrutt. Ingen endringer er gjort. Ta gjerne kontakt igjen!";
        return SendAsync(playerName, msg);
    }

    public Task SendReplenishingAsync(string playerName)
    {
        var msg = "🔄 Oppdaterer vinduet med nye kort...";
        return SendAsync(playerName, msg);
    }

    // ─────────────────────────────────────────────────────────────────
    // Internal send — uses UI Automation via MtgoMemoryReader
    // ─────────────────────────────────────────────────────────────────

    private Task SendAsync(string playerName, string message)
    {
        _logger.LogInformation("[CHAT → {Player}] {Msg}", playerName, message);
        _reader.SendChatMessage(message);
        return Task.CompletedTask;
    }
}
