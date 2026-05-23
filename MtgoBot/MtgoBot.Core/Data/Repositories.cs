using Dapper;
using MtgoBot.Core.Models;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Core.Data;

// ══════════════════════════════════════════════════════════════════
// CardRepository
// Handles card + set lookups. The trade loop calls this heavily,
// so queries are kept lean — no joins unless absolutely necessary.
// ══════════════════════════════════════════════════════════════════
public class CardRepository
{
    private readonly DatabaseConnectionFactory _db;
    private readonly ILogger<CardRepository> _logger;

    public CardRepository(DatabaseConnectionFactory db, ILogger<CardRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Bulk-load cards by their IDs (e.g. all cards the user is offering).
    /// Returns a dictionary keyed by CardId for O(1) lookups in the trade loop.
    /// </summary>
    public async Task<Dictionary<string, Card>> GetCardsByIdsAsync(IEnumerable<string> cardIds)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            SELECT c.card_id, c.card_name, c.set_code, c.rarity,
                   c.market_price_tix, c.custom_buy_price, c.custom_sell_price,
                   c.custom_max_stock, c.redeem_reserved
            FROM   cards c
            WHERE  c.card_id = ANY(@Ids)
            """;
        var rows = await (await conn).QueryAsync<Card>(sql, new { Ids = cardIds.ToArray() });
        return rows.ToDictionary(c => c.CardId);
    }

    public async Task<Card?> GetCardByIdAsync(string cardId)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = "SELECT * FROM cards WHERE card_id = @CardId";
        return await (await conn).QuerySingleOrDefaultAsync<Card>(sql, new { CardId = cardId });
    }

    public async Task<MagicSet?> GetSetAsync(string setCode)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = "SELECT * FROM sets WHERE set_code = @SetCode";
        return await (await conn).QuerySingleOrDefaultAsync<MagicSet>(sql, new { SetCode = setCode });
    }

    public async Task<Dictionary<string, MagicSet>> GetAllSetsAsync()
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = "SELECT * FROM sets";
        var rows = await (await conn).QueryAsync<MagicSet>(sql);
        return rows.ToDictionary(s => s.SetCode);
    }

    /// <summary>Upsert a card's market price (called by price-feed updater).</summary>
    public async Task UpdateMarketPriceAsync(string cardId, decimal newPrice)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            UPDATE cards SET market_price_tix = @Price
            WHERE  card_id = @CardId
            """;
        await (await conn).ExecuteAsync(sql, new { Price = newPrice, CardId = cardId });
    }
}

// ══════════════════════════════════════════════════════════════════
// InventoryRepository
// Reads and writes per-bot stock levels.
// ══════════════════════════════════════════════════════════════════
public class InventoryRepository
{
    private readonly DatabaseConnectionFactory _db;

    public InventoryRepository(DatabaseConnectionFactory db) => _db = db;

    /// <summary>
    /// Returns a map of cardId → quantity for one bot.
    /// Loaded once per session start and updated in-memory during the trade,
    /// then flushed to DB on commit.
    /// </summary>
    public async Task<Dictionary<string, int>> GetBotInventoryAsync(string botId)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            SELECT card_id, quantity FROM bot_inventory
            WHERE  bot_id = @BotId AND quantity > 0
            """;
        var rows = await (await conn).QueryAsync<BotInventoryEntry>(sql, new { BotId = botId });
        return rows.ToDictionary(r => r.CardId, r => r.Quantity);
    }

    /// <summary>
    /// Apply inventory deltas after a completed trade.
    /// Positive delta = bot gained cards; negative = bot gave cards.
    /// Uses a transaction so it's all-or-nothing.
    /// </summary>
    public async Task ApplyInventoryDeltasAsync(string botId, Dictionary<string, int> deltas)
    {
        await using var rawConn = await _db.CreateConnectionAsync();
        // Dapper doesn't expose NpgsqlConnection directly, so we cast:
        var conn = (Npgsql.NpgsqlConnection)rawConn;
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            foreach (var (cardId, delta) in deltas)
            {
                const string sql = """
                    INSERT INTO bot_inventory (bot_id, card_id, quantity)
                    VALUES (@BotId, @CardId, @Delta)
                    ON CONFLICT (bot_id, card_id)
                    DO UPDATE SET quantity = GREATEST(0, bot_inventory.quantity + EXCLUDED.quantity)
                    """;
                await conn.ExecuteAsync(sql, new { BotId = botId, CardId = cardId, Delta = delta }, tx);
            }
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// How many of a card a bot currently holds (excluding redeem_reserved).
    /// </summary>
    public async Task<int> GetAvailableStockAsync(string botId, string cardId, int redeemReserved)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            SELECT COALESCE(quantity, 0) FROM bot_inventory
            WHERE bot_id = @BotId AND card_id = @CardId
            """;
        var total = await (await conn).QuerySingleOrDefaultAsync<int>(sql, new { BotId = botId, CardId = cardId });
        return Math.Max(0, total - redeemReserved);
    }
}

// ══════════════════════════════════════════════════════════════════
// CreditRepository
// The shared credit wallet — must be ACID-safe.
// ══════════════════════════════════════════════════════════════════
public class CreditRepository
{
    private readonly DatabaseConnectionFactory _db;
    private readonly ILogger<CreditRepository> _logger;

    public CreditRepository(DatabaseConnectionFactory db, ILogger<CreditRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<UserCredit> GetOrCreateUserAsync(string playerName)
    {
        var lower = playerName.ToLowerInvariant();
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            INSERT INTO users (player_name, credit_tix, last_trade_at)
            VALUES (@Name, 0, NOW())
            ON CONFLICT (player_name) DO NOTHING;
            SELECT * FROM users WHERE player_name = @Name;
            """;
        return await (await conn).QuerySingleAsync<UserCredit>(sql, new { Name = lower });
    }

    /// <summary>
    /// Atomically apply a credit delta and write the audit log row.
    /// Negative delta = user is spending credit.
    /// Throws if the result would go negative.
    /// </summary>
    public async Task<decimal> ApplyCreditDeltaAsync(
        string playerName,
        string botId,
        decimal delta,
        string reason)
    {
        var lower = playerName.ToLowerInvariant();
        await using var rawConn = await _db.CreateConnectionAsync();
        var conn = (Npgsql.NpgsqlConnection)rawConn;
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            // Lock the user row for this transaction
            const string lockSql = """
                SELECT credit_tix FROM users
                WHERE player_name = @Name
                FOR UPDATE
                """;
            var currentBalance = await conn.QuerySingleOrDefaultAsync<decimal>(
                lockSql, new { Name = lower }, tx);

            var newBalance = currentBalance + delta;
            if (newBalance < 0)
                throw new InvalidOperationException(
                    $"Credit underflow for {lower}: {currentBalance} + {delta} = {newBalance}");

            const string updateSql = """
                UPDATE users
                SET credit_tix = @NewBalance, last_trade_at = NOW()
                WHERE player_name = @Name
                """;
            await conn.ExecuteAsync(updateSql, new { NewBalance = newBalance, Name = lower }, tx);

            const string logSql = """
                INSERT INTO credit_log (player_name, bot_id, delta_amount, new_balance, reason)
                VALUES (@Player, @Bot, @Delta, @NewBal, @Reason)
                """;
            await conn.ExecuteAsync(logSql,
                new { Player = lower, Bot = botId, Delta = delta, NewBal = newBalance, Reason = reason },
                tx);

            await tx.CommitAsync();
            _logger.LogInformation(
                "Credit [{Player}]: {Delta:+0.0000;-0.0000} → {New:0.0000} TIX ({Reason})",
                lower, delta, newBalance, reason);

            return newBalance;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Deletes credits for players inactive for more than <paramref name="days"/> days.
    /// Run nightly via a scheduled task.
    /// Returns the number of players purged.
    /// </summary>
    public async Task<int> PurgeInactiveCreditAsync(int days = 90)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            DELETE FROM users
            WHERE credit_tix > 0
              AND last_trade_at < NOW() - INTERVAL '1 day' * @Days
            RETURNING player_name
            """;
        var purged = await (await conn).QueryAsync<string>(sql, new { Days = days });
        var list = purged.ToList();
        _logger.LogInformation(
            "🧹 Credit purge: removed stale credits for {Count} inactive players.", list.Count);
        return list.Count;
    }
}
