using MtgoBot.Core.Models;
using MtgoBot.Core.Data;
using MtgoBot.Core.Trading;
using MtgoBot.Client.Memory;
using MtgoBot.Client.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace MtgoBot.Client.Loop;

/// <summary>
/// Main trade loop. Trade direction is decided by a chat command:
///   - Customer types "sell"  → bot BUYS (imports buylist .dek, MTGO auto-adds matches)
///   - Customer picks cards from bot binder → bot SELLS (adds TIX change)
///
/// The bot adds TIX to ITS OWN side in both directions:
///   - BUY  : customer pays, bot adds floor(value) TIX? No — bot adds CARDS, customer adds TIX.
///   - SELL : customer takes cards, bot adds ceil(value) TIX as change... see rules below.
///
/// Pricing rules (both sides end equal in TIX value):
///   - BUY  (bot receives cards worth V): customer must add ceil/floor? 
///           cards worth 6.5 → customer adds 6 TIX, 0.5 saved as credit. floor(V) TIX, credit = V - floor(V).
///   - SELL (customer receives cards worth V): bot adds TIX to its side.
///           cards worth 5.5 → bot adds 6 TIX, customer pays 6, 0.5 saved as credit. ceil(V) TIX, credit = ceil(V) - V.
/// </summary>
public class TradeBotLoop : BackgroundService
{
    private const int PollIntervalMs  = 800;
    private const int ScanningDelayMs = 1000;
    private const string DekPath      = @"C:\KorthaienBOT\buylist.dek";

    private readonly string _botId;
    private readonly MtgoMemoryReader _memory;
    private readonly TradeEngine _engine;
    private readonly TradeChatService _chat;
    private readonly CardRepository _cards;
    private readonly CreditRepository _credits;
    private readonly ILogger<TradeBotLoop> _logger;

    private ActiveSession? _session;

    public TradeBotLoop(
        string botId, MtgoMemoryReader memory, TradeEngine engine,
        TradeChatService chat, CardRepository cards, CreditRepository credits,
        ILogger<TradeBotLoop> logger)
    {
        _botId = botId; _memory = memory; _engine = engine;
        _chat = chat; _cards = cards; _credits = credits; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("🤖 Bot [{BotId}] trade loop started.", _botId);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Unhandled error in trade loop tick."); }
            await Task.Delay(PollIntervalMs, ct);
        }
        _logger.LogInformation("Bot [{BotId}] loop stopped.", _botId);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var snapshot = _memory.ReadTradeWindow();

        // ── No trade window visible ──────────────────────────────────
        if (snapshot == null || !snapshot.IsOpen)
        {
            if (_session != null)
            {
                _logger.LogInformation("Trade window closed for [{Player}]. Clearing session.", _session.PlayerName);
                _session = null;
            }
            _memory.AcceptTradeRequest();
            return;
        }

        var playerName = snapshot.PlayerName.ToLowerInvariant();

        // ── New trade opened ─────────────────────────────────────────
        if (_session == null)
        {
            _session = new ActiveSession(playerName)
            {
                OldCredit = (await _credits.GetOrCreateUserAsync(playerName)).CreditTix
            };
            _logger.LogInformation("📬 Trade opened by [{Player}] (credit {Cr:0.0000})", playerName, _session.OldCredit);
            _memory.SendChatMessage(
                "Hi! I'm a buy bot — I'll scan your collection for cards I want and " +
                "add them to my side, then tell you the total in TIX. One moment...");
            return;
        }

        // ── Both submitted → Accept & commit ─────────────────────────
        if (snapshot.BothSubmitted)
        {
            await HandleBothSubmittedAsync(snapshot);
            return;
        }

        // ── Buy-only: automatically scan & buy once per trade ────────
        if (!_session.BuyStarted)
        {
            await HandleBuyFlowAsync(snapshot, ct);
            return;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Detect "sell" command in trade chat
    // ─────────────────────────────────────────────────────────────────
    private bool CustomerWantsToSell(string playerName)
    {
        var messages = _memory.ReadChatMessages();
        // Look for a line where the customer says "sell"
        foreach (var line in messages)
        {
            // chat lines look like "6:11 PM jallamikkel: Sell"
            if (line.Contains(playerName, StringComparison.OrdinalIgnoreCase)
                && line.Contains("sell", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    // BUY FLOW — bot imports buylist .dek, MTGO auto-adds matches
    // ─────────────────────────────────────────────────────────────────
    private async Task HandleBuyFlowAsync(TradeWindowSnapshot snapshot, CancellationToken ct)
    {
        if (_session == null) return;
        _session.BuyStarted = true;

        _memory.SendChatMessage("Scanning your collection for cards I want...");
        _logger.LogInformation("🔍 BUY flow: generating buylist .dek for [{Bot}]", _botId);

        var buylist = await _cards.GetBuylistAsync(_botId);
        if (buylist.Count == 0)
        {
            _memory.SendChatMessage("I'm not buying any cards right now. You can still buy from me.");
            _logger.LogInformation("Buylist empty — nothing to buy.");
            _session.BuyStarted = false; // allow sell flow instead
            return;
        }

        DekFileGenerator.WriteBuylistDek(buylist, DekPath);
        _logger.LogInformation("Wrote .dek with {Count} entries to {Path}", buylist.Count, DekPath);

        await Task.Delay(ScanningDelayMs, ct);
        _memory.ImportDeck(DekPath);

        // Give MTGO time to add the matching cards to the bot side.
        await Task.Delay(2500, ct);

        // Read ALL card names on the bot's side (scrolling through the panel).
        var offeredNames = _memory.ReadAllBotOfferNames();
        if (offeredNames.Count == 0)
        {
            _memory.SendChatMessage("I didn't find any cards I need in your collection. Thanks for the look!");
            _logger.LogInformation("No cards on bot side after import.");
            return;
        }

        // Price each card. Bot is one-set + one-foil, so each name maps to exactly
        // one buylist entry — match by exact (case-insensitive) name.
        decimal totalValue = 0m;
        var priced = new List<TradeWindowCard>();
        var buyByName = new Dictionary<string, BuylistEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in buylist)
            buyByName[b.CardName.Trim()] = b; // last wins; names unique per set+foil

        int matched = 0, unmatched = 0;
        foreach (var name in offeredNames)
        {
            if (buyByName.TryGetValue(name.Trim(), out var match))
            {
                totalValue += match.BuyPrice; // one copy per card
                priced.Add(new TradeWindowCard
                {
                    CardId = match.CardId, CardName = match.CardName,
                    Quantity = 1, Side = TradeSide.BotSide, PriceTix = match.BuyPrice
                });
                matched++;
            }
            else
            {
                unmatched++;
                _logger.LogDebug("Unmatched offered card: '{Name}'", name);
            }
        }
        _logger.LogInformation("Priced {Matched} cards ({Unmatched} unmatched), value={Val:0.00}",
            matched, unmatched, totalValue);

        if (priced.Count == 0)
        {
            _memory.SendChatMessage("I didn't find any cards I need in your collection. Thanks for the look!");
            _logger.LogInformation("No buylist matches among offered cards.");
            return;
        }

        // Apply existing credit, then round: customer pays floor(value), remainder saved as credit.
        decimal net      = totalValue + _session.OldCredit;
        int tixToPay      = (int)Math.Floor(net);
        decimal newCredit = net - tixToPay;

        _session.BotSideCards = priced;
        _session.BuyValue     = totalValue;
        _session.TixExpected  = tixToPay;
        _session.NewCredit    = newCredit;

        _memory.SendChatMessage(
            $"I'll buy these {priced.Count} cards for {totalValue:0.00} TIX. " +
            $"Please add {tixToPay} TIX to your side. " +
            $"({newCredit:0.00} TIX will be saved as your credit.) " +
            $"Note: I take 1 copy of each card per trade — open another trade to sell more copies.");

        _logger.LogInformation("BUY: value={Val:0.00} payTix={Pay} newCredit={Cr:0.00}",
            totalValue, tixToPay, newCredit);

        // Bot does NOT submit yet — it waits for the customer to add the correct TIX.
        _session.AwaitingCustomerTix = true;
    }

    // ─────────────────────────────────────────────────────────────────
    // SELL FLOW — customer picks bot cards, bot adds TIX change
    // ─────────────────────────────────────────────────────────────────
    private async Task HandleSellFlowAsync(TradeWindowSnapshot snapshot, CancellationToken ct)
    {
        if (_session == null) return;
        _session.SellStarted = true;

        // Cards the customer has put on THEIR side = cards they want to buy from the bot.
        // Price them via DB lookup by name (best-effort) and sum the sell value.
        decimal totalValue = 0m;
        foreach (var pick in snapshot.PlayerOffers)
        {
            if (pick.CardId == TradeEngine.TixCardId) continue;
            // Look up sell price by name+set if possible
            var parts = pick.CardId.Split('|');
            string name = parts[0];
            string set  = parts.Length > 1 ? parts[1] : "";
            var card = await _cards.GetCardByNameAndSetAsync(name, set);
            if (card != null)
            {
                var sets = await _cards.GetAllSetsAsync();
                decimal sell = card.EffectiveSellPrice(
                    sets.TryGetValue(card.SetCode, out var s) ? s.DefaultSellMultiplier : 1.0m);
                totalValue += sell * pick.Quantity;
            }
        }

        if (totalValue <= 0)
        {
            // Nothing priced yet (customer still picking) — wait.
            return;
        }

        // Bot adds ceil(value) TIX to its own side; remainder saved as credit.
        decimal net      = totalValue - _session.OldCredit; // credit reduces what customer owes
        int tixToAdd      = (int)Math.Ceiling(Math.Max(0, net));
        decimal newCredit = tixToAdd - net;

        if (tixToAdd != _session.TixAdded)
        {
            // (Re)add TIX to match. Simple approach: clear & re-add not supported;
            // for now only add when nothing added yet.
            if (_session.TixAdded == 0 && tixToAdd > 0)
            {
                _memory.AddTixToBotSide(tixToAdd);
                _session.TixAdded = tixToAdd;
                _memory.SendChatMessage(
                    $"Total {totalValue:0.00} TIX. I've added {tixToAdd} TIX. " +
                    $"Your credit will be {newCredit:0.00} TIX. Click Submit when ready.");
                _logger.LogInformation("SELL: value={Val:0.00} tixAdded={Tix} newCredit={Cr:0.00}",
                    totalValue, tixToAdd, newCredit);
            }
        }

        _session.SellValue   = totalValue;
        _session.NewCredit   = newCredit;
        await Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────
    // Both submitted — commit and accept
    // ─────────────────────────────────────────────────────────────────
    private async Task HandleBothSubmittedAsync(TradeWindowSnapshot snapshot)
    {
        if (_session == null) return;

        _logger.LogInformation("✅ Both submitted for [{Player}]. Completing.", _session.PlayerName);

        await Task.Delay(500);
        _memory.ClickAccept();
        _logger.LogInformation("✅ Clicked Accept.");

        // Persist credit
        decimal finalCredit = _session.NewCredit;
        if (finalCredit != _session.OldCredit)
        {
            await _cards.SetCreditAsync(_session.PlayerName, finalCredit);
            _logger.LogInformation("Credit for [{Player}] set to {Cr:0.0000}",
                _session.PlayerName, finalCredit);
        }

        _memory.SendChatMessage($"Trade complete! Your credit is now {finalCredit:0.00} TIX. Thanks!");
        _session = null;
    }
}

internal class ActiveSession
{
    public string PlayerName { get; }
    public decimal OldCredit { get; set; }

    public bool BuyStarted { get; set; }
    public bool SellStarted { get; set; }
    public bool AwaitingCustomerTix { get; set; }
    public bool BotHasSubmitted { get; set; }

    public List<TradeWindowCard> BotSideCards { get; set; } = [];
    public decimal BuyValue { get; set; }
    public decimal SellValue { get; set; }
    public int TixExpected { get; set; }
    public int TixAdded { get; set; }
    public decimal NewCredit { get; set; }

    public ActiveSession(string playerName)
    {
        PlayerName = playerName;
    }
}
