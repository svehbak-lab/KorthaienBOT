using MtgoBot.Core.Models;
using MtgoBot.Core.Data;
using MtgoBot.Core.Trading;
using MtgoBot.Client.Memory;
using MtgoBot.Client.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

namespace MtgoBot.Client.Loop;

/// <summary>
/// The main bot loop. Runs as a .NET BackgroundService (IHostedService).
/// 
/// State machine:
///   Idle → TradeOpened → Scanning → WindowLoaded → LiveUpdating → Committed/Cancelled
/// 
/// The loop polls MTGO memory every <see cref="PollIntervalMs"/> milliseconds.
/// On each tick it compares the window state to the last known state and
/// reacts only when something changes — keeping CPU usage minimal.
/// </summary>
public class TradeBotLoop : BackgroundService
{
    private const int PollIntervalMs       = 500;   // How often we check MTGO memory
    private const int ScanningDelayMs      = 1500;  // Fake "thinking" delay after hello
    private const int ReplenishDelayMs     = 300;   // Brief pause before replenish msg

    private readonly string _botId;
    private readonly MtgoMemoryReader _memory;
    private readonly TradeEngine _engine;
    private readonly TradeChatService _chat;
    private readonly CardRepository _cards;
    private readonly CreditRepository _credits;
    private readonly ILogger<TradeBotLoop> _logger;

    // ── Active session state (null = no trade open) ──
    private ActiveSession? _session;

    public TradeBotLoop(
        string botId,
        MtgoMemoryReader memory,
        TradeEngine engine,
        TradeChatService chat,
        CardRepository cards,
        CreditRepository credits,
        ILogger<TradeBotLoop> logger)
    {
        _botId   = botId;
        _memory  = memory;
        _engine  = engine;
        _chat    = chat;
        _cards   = cards;
        _credits = credits;
        _logger  = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("🤖 Bot [{BotId}] trade loop started.", _botId);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in trade loop tick.");
            }

            await Task.Delay(PollIntervalMs, ct);
        }

        _logger.LogInformation("Bot [{BotId}] loop stopped.", _botId);
    }

    // ─────────────────────────────────────────────────────────────────
    // Main tick — called every PollIntervalMs
    // ─────────────────────────────────────────────────────────────────

    private async Task TickAsync(CancellationToken ct)
    {
        var snapshot = _memory.ReadTradeWindow();

        // ── No trade window visible ──────────────────────────────────
        if (snapshot == null || !snapshot.IsOpen)
        {
            if (_session != null)
            {
                _logger.LogInformation(
                    "Trade window closed unexpectedly for [{Player}]. Cancelling session.",
                    _session.PlayerName);
                await _chat.SendTradeCancelledAsync(_session.PlayerName);
                _session = null;
            }
            return;
        }

        // ── New trade opened ─────────────────────────────────────────
        if (_session == null)
        {
            await HandleTradeOpenedAsync(snapshot, ct);
            return;
        }

        // ── Ongoing trade — check for window changes ─────────────────
        if (snapshot.BothSubmitted)
        {
            await HandleBothSubmittedAsync(snapshot);
            return;
        }

        await HandleWindowChangeAsync(snapshot);
    }

    // ─────────────────────────────────────────────────────────────────
    // Phase: Trade opened
    // ─────────────────────────────────────────────────────────────────

    private async Task HandleTradeOpenedAsync(TradeWindowSnapshot snapshot, CancellationToken ct)
    {
        var playerName = snapshot.PlayerName.ToLowerInvariant();
        _logger.LogInformation("📬 Trade opened by [{Player}]", playerName);

        // 1. Greet and scan
        await _chat.SendWelcomeAndScanningAsync(playerName);
        await Task.Delay(ScanningDelayMs, ct);  // Simulate scanning delay

        // 2. Load user's credit and all set metadata
        var userCredit = await _credits.GetOrCreateUserAsync(playerName);
        var sets       = await _cards.GetAllSetsAsync();

        // 3. Build the full filtered list from everything the user is offering
        var userOffers = snapshot.PlayerOffers
            .Select(o => (o.CardId, o.Quantity))
            .ToList();

        var filtered = await _engine.FilterUserOffersAsync(_botId, userOffers, sets);

        if (filtered.Count == 0)
        {
            await _chat.SendNoMatchesFoundAsync(playerName);
            // Don't clear session yet — user might add more cards
            _session = new ActiveSession(playerName, userCredit.CreditTix, sets, [], new ReplenishQueue());
            return;
        }

        // 4. Take the first ≤100 into the window; queue the rest
        var rawCards = (await _cards.GetCardsByIdsAsync(filtered.Select(f => f.CardId))).Values.ToList();
        var (windowBatch, queue) = TradeEngine.BuildWindowAndQueue(filtered, rawCards);

        // 5. Populate MTGO window (via SDK)
        // TODO: call MTGOSDK to add cards to the bot's trade window side
        _logger.LogInformation(
            "Adding {Count} cards to window ({Queued} queued for replenish).",
            windowBatch.Count, queue.Count);

        await _chat.SendWindowLoadedAsync(playerName);

        // 6. Initial balance calculation
        var botSideCards = BuildBotSideFromBatch(windowBatch);
        var balance = TradeEngine.RecalculateBalance(
            playerName, _botId, [], botSideCards, userCredit.CreditTix);

        await _chat.SendTradeStatusAsync(playerName, balance, userCredit.CreditTix, userCredit.CreditTix);

        // 7. Create session
        _session = new ActiveSession(playerName, userCredit.CreditTix, sets, botSideCards, queue)
        {
            LastBalance = balance,
        };
    }

    // ─────────────────────────────────────────────────────────────────
    // Phase: Window changed (card added or removed by user)
    // ─────────────────────────────────────────────────────────────────

    private async Task HandleWindowChangeAsync(TradeWindowSnapshot snapshot)
    {
        if (_session == null) return;

        // Detect cards removed from bot's side by user (they pulled them back)
        var currentBotCardIds = snapshot.BotOffers.Select(o => o.CardId).ToHashSet();
        var removedCards = _session.BotSideCards
            .Where(c => !currentBotCardIds.Contains(c.CardId))
            .ToList();

        if (removedCards.Count > 0)
        {
            foreach (var removed in removedCards)
            {
                _logger.LogInformation(
                    "[{Player}] removed {Card} from window — blacklisting for this session.",
                    _session.PlayerName, removed.CardName);
                _session.ReplenishQueue.Blacklist(removed.CardId);
            }

            // Replenish empty slots from the queue
            await ReplenishWindowAsync();
        }

        // Update balance with current window state
        var userCards = snapshot.PlayerOffers.Select(o => new TradeWindowCard
        {
            CardId   = o.CardId,
            CardName = o.CardName,
            Quantity = o.Quantity,
            Side     = TradeSide.UserSide,
            PriceTix = 0 // TODO: lookup price from card meta
        }).ToList();

        var balance = TradeEngine.RecalculateBalance(
            _session.PlayerName, _botId,
            userCards, _session.BotSideCards,
            _session.OldCredit);

        if (balance.NetBalance != _session.LastBalance?.NetBalance)
        {
            await _chat.SendTradeStatusAsync(
                _session.PlayerName, balance,
                _session.OldCredit, _session.OldCredit);
            _session.LastBalance = balance;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Phase: Both sides clicked Submit → commit
    // ─────────────────────────────────────────────────────────────────

    private async Task HandleBothSubmittedAsync(TradeWindowSnapshot snapshot)
    {
        if (_session?.LastBalance == null) return;

        var balance    = _session.LastBalance;
        var creditLeft = TradeEngine.CalculateCreditRemainder(balance, _session.OldCredit);

        _logger.LogInformation(
            "✅ Both submitted. Committing trade for [{Player}]. Credit remainder: {Cr:0.0000}",
            _session.PlayerName, creditLeft);

        await _engine.CommitTradeAsync(balance, creditLeft);

        // Click Accept in MTGO to finalise
        _memory.ClickAccept();

        await _chat.SendTradeCompleteAsync(_session.PlayerName, _session.OldCredit + creditLeft);

        _session = null;  // Ready for next trade
    }

    // ─────────────────────────────────────────────────────────────────
    // Replenish: pull next card from queue to fill an empty slot
    // ─────────────────────────────────────────────────────────────────

    private async Task ReplenishWindowAsync()
    {
        if (_session == null) return;

        await Task.Delay(ReplenishDelayMs);
        await _chat.SendReplenishingAsync(_session.PlayerName);

        var next = _session.ReplenishQueue.DequeueNext();
        while (next != null && _session.BotSideCards.Count < TradeEngine.MaxWindowSlots)
        {
            // TODO: add card to MTGO window via MTGOSDK
            _logger.LogInformation(
                "Replenish: adding [{Card}] to window.", next.CardName);
            next = _session.ReplenishQueue.DequeueNext();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static List<TradeWindowCard> BuildBotSideFromBatch(List<FilteredCard> batch)
        => batch.Select(f => new TradeWindowCard
        {
            CardId   = f.CardId,
            CardName = f.CardName,
            Quantity = f.Quantity,
            Side     = TradeSide.BotSide,
            PriceTix = f.BuyPrice,
        }).ToList();
}

// ─────────────────────────────────────────────────────────────────
// Session state — lives only for the duration of one trade
// ─────────────────────────────────────────────────────────────────

internal class ActiveSession
{
    public string PlayerName { get; }
    public decimal OldCredit { get; }
    public Dictionary<string, MagicSet> Sets { get; }
    public List<TradeWindowCard> BotSideCards { get; }
    public ReplenishQueue ReplenishQueue { get; }
    public TradeBalance? LastBalance { get; set; }

    public ActiveSession(
        string playerName,
        decimal oldCredit,
        Dictionary<string, MagicSet> sets,
        List<TradeWindowCard> botSideCards,
        ReplenishQueue replenishQueue)
    {
        PlayerName    = playerName;
        OldCredit     = oldCredit;
        Sets          = sets;
        BotSideCards  = botSideCards;
        ReplenishQueue = replenishQueue;
    }
}
