using MtgoBot.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace MtgoBot.Client.Chat;

/// <summary>
/// Generates and refreshes the bot's Classifieds ad in MTGO.
///
/// Gemini: "You must write a parser that auto-generates ad text based on
/// what you actually have in stock. The client has strict character limits."
///
/// Example output (under 200 chars):
///   "BUYING/SELLING [OTJ:42t] [MH3:31t] [DMU:12t] COMPLETE SETS
///    Credit system. Foils OK. 24/7 auto. goatbots.com prices."
///
/// The ad is refreshed every 30 minutes, or immediately after a trade
/// changes your set totals significantly.
/// </summary>
public class ClassifiedsAdService : BackgroundService
{
    private const int MaxAdLength     = 200;    // MTGO classifieds hard limit
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly CardRepository _cards;
    private readonly InventoryRepository _inventory;
    private readonly ILogger<ClassifiedsAdService> _logger;
    private readonly string _botId;
    private readonly bool _isRedeemBot;

    public ClassifiedsAdService(
        CardRepository cards,
        InventoryRepository inventory,
        ILogger<ClassifiedsAdService> logger,
        IConfiguration config)
    {
        _cards       = cards;
        _inventory   = inventory;
        _logger      = logger;
        _botId       = config["BotSettings:BotId"] ?? "Bot_1";
        _isRedeemBot = config.GetValue("BotSettings:IsRedeemBot", false);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("📢 ClassifiedsAdService started.");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var ad = await BuildAdTextAsync();
                await PostAdAsync(ad);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update Classifieds ad.");
            }

            await Task.Delay(RefreshInterval, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Build the ad text from current inventory
    // ─────────────────────────────────────────────────────────────────

    public async Task<string> BuildAdTextAsync()
    {
        // Get inventory grouped by set
        var setTotals = await _inventory.GetInventoryValueBySetAsync(_botId);
        var sets      = await _cards.GetAllSetsAsync();

        // Sort sets by total TIX value descending — show most valuable first
        var sortedSets = setTotals
            .OrderByDescending(s => s.Value)
            .Take(8)  // Don't overflow the char limit
            .ToList();

        // Build the set listing part: "[OTJ:42t] [MH3:31t]"
        var setChunks = sortedSets
            .Select(s =>
            {
                string code  = s.Key;
                string value = $"{s.Value:0}t";
                return $"[{code}:{value}]";
            })
            .ToList();

        var prefix = _isRedeemBot
            ? "BUYING COMPLETE SETS FOR REDEMPTION "
            : "BUYING+SELLING 24/7 ";

        var suffix = " | Credit system | Foils OK | Prices: GoatBots";

        // Build progressively shorter until it fits
        string ad = prefix + string.Join(" ", setChunks) + suffix;

        while (ad.Length > MaxAdLength && setChunks.Count > 1)
        {
            setChunks.RemoveAt(setChunks.Count - 1);
            ad = prefix + string.Join(" ", setChunks) + suffix;
        }

        // Hard truncate as last resort (shouldn't be needed)
        if (ad.Length > MaxAdLength)
            ad = ad[..MaxAdLength];

        _logger.LogInformation("📣 Classifieds ad ({Len} chars): {Ad}", ad.Length, ad);
        return ad;
    }

    // ─────────────────────────────────────────────────────────────────
    // Post the ad to MTGO Classifieds
    // TODO: implement via MTGOSDK classifieds posting API
    // ─────────────────────────────────────────────────────────────────

    private Task PostAdAsync(string adText)
    {
        // MTGOSDK: MtgoSdk.Instance.Classifieds.PostAd(adText);
        _logger.LogDebug("[CLASSIFIEDS] Would post: {Ad}", adText);
        return Task.CompletedTask;
    }
}
