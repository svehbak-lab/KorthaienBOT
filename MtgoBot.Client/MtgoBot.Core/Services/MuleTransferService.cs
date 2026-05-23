using MtgoBot.Core.Data;
using MtgoBot.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace MtgoBot.Core.Services;

/// <summary>
/// Runs every 10 minutes and checks if any Trade Bot has accumulated
/// enough cards to trigger a transfer to the Mule (vault) bot.
///
/// Gemini logic:
///   - Trade Bot accumulates cards via normal trading
///   - When Trade Bot holds >= RedeemReserved units of a card across
///     the whole network, those units get "flagged" for Mule transfer
///   - Both bots are controlled by the same C# code, so the transfer
///     is programmatic — no human needed
///
/// In MTGO: the actual item transfer requires opening a trade between
/// the two bot accounts. This service creates the transfer ORDER in the
/// DB; the bot loop executes it when the Mule bot is available.
/// </summary>
public class MuleTransferService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    private readonly InventoryRepository _inventory;
    private readonly CardRepository _cards;
    private readonly ILogger<MuleTransferService> _logger;
    private readonly string _tradeBotId;
    private readonly string _muleBotId;

    public MuleTransferService(
        InventoryRepository inventory,
        CardRepository cards,
        ILogger<MuleTransferService> logger,
        IConfiguration config)
    {
        _inventory  = inventory;
        _cards      = cards;
        _logger     = logger;
        _tradeBotId = config["BotSettings:BotId"]     ?? "Bot_1";
        _muleBotId  = config["BotSettings:MuleBotId"] ?? "Mule_1";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("🏦 MuleTransferService started (checking every 10 min).");

        while (!ct.IsCancellationRequested)
        {
            try { await CheckAndQueueTransfersAsync(ct); }
            catch (Exception ex)
            { _logger.LogError(ex, "MuleTransferService error."); }

            await Task.Delay(CheckInterval, ct);
        }
    }

    private async Task CheckAndQueueTransfersAsync(CancellationToken ct)
    {
        // Get current Trade Bot inventory
        var botStock = await _inventory.GetBotInventoryAsync(_tradeBotId);
        if (botStock.Count == 0) return;

        // Get network-wide totals (Trade + Mule combined)
        var networkStock = await _inventory.GetNetworkInventoryAsync();

        var transfersNeeded = new List<TransferOrder>();

        foreach (var (cardId, quantity) in botStock)
        {
            if (quantity <= 0) continue;

            var card = await _cards.GetCardByIdAsync(cardId);
            if (card == null || card.RedeemReserved <= 0) continue;

            int totalInNetwork = networkStock.GetValueOrDefault(cardId, 0);

            // Only transfer if we haven't yet filled the redeem reserve in the Mule
            var muleStock    = await _inventory.GetBotInventoryAsync(_muleBotId);
            int muleHas      = muleStock.GetValueOrDefault(cardId, 0);
            int muleStillNeeds = Math.Max(0, card.RedeemReserved - muleHas);

            if (muleStillNeeds <= 0) continue;

            // Transfer as many as the Trade Bot has, up to what Mule still needs
            int toTransfer = Math.Min(quantity, muleStillNeeds);

            transfersNeeded.Add(new TransferOrder(cardId, card.CardName, toTransfer));
            _logger.LogInformation(
                "🔄 Mule transfer queued: {Card} x{Qty} → {Mule}",
                card.CardName, toTransfer, _muleBotId);
        }

        if (transfersNeeded.Count > 0)
        {
            await _inventory.QueueMuleTransfersAsync(_tradeBotId, _muleBotId, transfersNeeded);
            _logger.LogInformation(
                "📦 {Count} transfer orders queued for Mule bot.", transfersNeeded.Count);
        }
    }
}

public record TransferOrder(string CardId, string CardName, int Quantity);
