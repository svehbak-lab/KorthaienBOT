using MtgoBot.Core.Models;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Client.Chat;

/// <summary>
/// Formats and sends chat messages to the MTGO trade window.
/// All messages from the spec are implemented here as typed methods
/// so the trade loop stays clean and readable.
/// 
/// Actual message delivery is via MTGOSDK's Chat API or
/// PostMessage to the chat input field — inject that dependency here.
/// </summary>
public class TradeChatService
{
    private readonly ILogger<TradeChatService> _logger;
    // TODO: inject MTGOSDK chat sender or low-level SendKeys wrapper
    // private readonly IMtgoChatSender _sender;

    public TradeChatService(ILogger<TradeChatService> logger)
    {
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────
    // Message templates — exactly as specified
    // ─────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// The live status block, updated every time the window changes.
    /// </summary>
    public Task SendTradeStatusAsync(
        string playerName,
        TradeBalance balance,
        decimal oldCredit,
        decimal newCredit)
    {
        // Whole TIX to put in window
        decimal wholeTix = Math.Floor(Math.Max(0, balance.NetBalance + oldCredit));
        // Remainder saved as credit
        decimal creditSaved = (balance.NetBalance + oldCredit) - wholeTix;

        var msg = $"""
            --- 📊 BYTTESTATUS ---
            Kort du gir meg: {balance.UserCards.Count} stk (Verdi: {balance.ValueUserGives:0.0000} TIX)
            Kort du tar fra meg: {balance.BotCards.Count} stk (Verdi: {balance.ValueBotGives:0.0000} TIX)
            --------------------------------------
            Netto verdi i din favør: {(balance.NetBalance >= 0 ? "+" : "")}{balance.NetBalance:0.0000} TIX

            💵 TIX lagt til i vinduet: {wholeTix:0.0000} TIX
            💾 Ny credit lagret på din bruker: {creditSaved:0.0000} TIX (Du hadde {oldCredit:0.0000} fra før)

            Klikk 'Submit' hvis du er fornøyd! 👍
            """;

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
    // Internal send — replace body with real MTGO chat injection
    // ─────────────────────────────────────────────────────────────────

    private Task SendAsync(string playerName, string message)
    {
        // In production: use MTGOSDK chat API or keyboard automation
        // _sender.SendTradeMessage(message);
        _logger.LogInformation("[CHAT → {Player}] {Msg}", playerName, message);
        return Task.CompletedTask;
    }
}
