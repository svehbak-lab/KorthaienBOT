using Dapper;
using MtgoBot.Core.Models;
using MtgoBot.Core.Services;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Core.Data;

// ══════════════════════════════════════════════════════════════════
// CardRepository
// ══════════════════════════════════════════════════════════════════
public class CardRepository
{
    private readonly DatabaseConnectionFactory _db;
    private readonly ILogger<CardRepository> _logger;

    public CardRepository(DatabaseConnectionFactory db, ILogger<CardRepository> logger)
    { _db = db; _logger = logger; }

    public async Task<Dictionary<string, Card>> GetCardsByIdsAsync(IEnumerable<string> cardIds)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = "SELECT * FROM cards WHERE card_id = ANY(@Ids)";
        var rows = await (await conn).QueryAsync<Card>(sql, new { Ids = cardIds.ToArray() });
        return rows.ToDictionary(c => c.CardId);
    }

    public async Task<Card?> GetCardByIdAsync(string cardId)
    {
        await using var conn = _db.CreateConnectionAsync();
        return await (await conn).QuerySingleOrDefaultAsync<Card>(
            "SELECT * FROM cards WHERE card_id = @Id", new { Id = cardId });
    }

    public async Task<MagicSet?> GetSetAsync(string setCode)
    {
        await using var conn = _db.CreateConnectionAsync();
        return await (await conn).QuerySingleOrDefaultAsync<MagicSet>(
            "SELECT * FROM sets WHERE set_code = @Code", new { Code = setCode });
    }

    public async Task<Dictionary<string, MagicSet>> GetAllSetsAsync()
    {
        await using var conn = _db.CreateConnectionAsync();
        var rows = await (await conn).QueryAsync<MagicSet>("SELECT * FROM sets");
        return rows.ToDictionary(s => s.SetCode);
    }

    public async Task UpdateMarketPriceAsync(string cardId, decimal newPrice)
    {
        await using var conn = _db.CreateConnectionAsync();
        await (await conn).ExecuteAsync(
            "UPDATE cards SET market_price_tix = @Price WHERE card_id = @Id",
            new { Price = newPrice, Id = cardId });
    }

    /// <summary>Set a per-card custom buy/sell override from the dashboard.</summary>
    public async Task SetCardOverridesAsync(
        string cardId,
        decimal? customBuy,
        decimal? customSell,
        int? customMaxStock,
        int redeemReserved)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            UPDATE cards SET
                custom_buy_price  = @Buy,
                custom_sell_price = @Sell,
                custom_max_stock  = @MaxStock,
                redeem_reserved   = @Redeem
            WHERE card_id = @Id
            """;
        await (await conn).ExecuteAsync(sql, new
        {
            Buy = customBuy, Sell = customSell,
            MaxStock = customMaxStock, Redeem = redeemReserved, Id = cardId
        });
    }

    /// <summary>Update a set's global multipliers from the dashboard.</summary>
    public async Task UpdateSetRulesAsync(
        string setCode,
        decimal buyMultiplier,
        decimal sellMultiplier,
        int maxStock)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            UPDATE sets SET
                default_buy_multiplier  = @Buy,
                default_sell_multiplier = @Sell,
                default_max_stock       = @Max,
                updated_at              = NOW()
            WHERE set_code = @Code
            """;
        await (await conn).ExecuteAsync(sql, new
        {
            Buy = buyMultiplier, Sell = sellMultiplier, Max = maxStock, Code = setCode
        });
    }
}

// ══════════════════════════════════════════════════════════════════
// InventoryRepository
// ══════════════════════════════════════════════════════════════════
public class InventoryRepository
{
    private readonly DatabaseConnectionFactory _db;

    public InventoryRepository(DatabaseConnectionFactory db) => _db = db;

    public async Task<Dictionary<string, int>> GetBotInventoryAsync(string botId)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            SELECT card_id, quantity FROM bot_inventory
            WHERE bot_id = @BotId AND quantity > 0
            """;
        var rows = await (await conn).QueryAsync<BotInventoryEntry>(sql, new { BotId = botId });
        return rows.ToDictionary(r => r.CardId, r => r.Quantity);
    }

    /// <summary>Network-wide totals across ALL bots (used by Mule logic).</summary>
    public async Task<Dictionary<string, int>> GetNetworkInventoryAsync()
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            SELECT card_id, SUM(quantity) AS quantity
            FROM bot_inventory
            GROUP BY card_id
            """;
        var rows = await (await conn).QueryAsync<BotInventoryEntry>(sql);
        return rows.ToDictionary(r => r.CardId, r => r.Quantity);
    }

    /// <summary>Total TIX value of inventory per set (for Classifieds ad).</summary>
    public async Task<Dictionary<string, decimal>> GetInventoryValueBySetAsync(string botId)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            SELECT c.set_code, SUM(bi.quantity * c.market_price_tix) AS total_value
            FROM bot_inventory bi
            JOIN cards c ON bi.card_id = c.card_id
            WHERE bi.bot_id = @BotId AND bi.quantity > 0
            GROUP BY c.set_code
            """;
        var rows = await (await conn).QueryAsync(sql, new { BotId = botId });
        return rows.ToDictionary(
            r => (string)r.set_code,
            r => (decimal)r.total_value);
    }

    public async Task ApplyInventoryDeltasAsync(string botId, Dictionary<string, int> deltas)
    {
        await using var rawConn = await _db.CreateConnectionAsync();
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
        catch { await tx.RollbackAsync(); throw; }
    }

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

    /// <summary>Write pending mule transfer orders to the queue table.</summary>
    public async Task QueueMuleTransfersAsync(
        string fromBotId,
        string toBotId,
        IEnumerable<TransferOrder> orders)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            INSERT INTO mule_transfer_queue (from_bot_id, to_bot_id, card_id, quantity, status)
            VALUES (@From, @To, @CardId, @Qty, 'PENDING')
            """;
        foreach (var order in orders)
        {
            await (await conn).ExecuteAsync(sql, new
            {
                From = fromBotId, To = toBotId,
                CardId = order.CardId, Qty = order.Quantity
            });
        }
    }

    /// <summary>Dashboard: full inventory view aggregated across all bots.</summary>
    public async Task<IEnumerable<InventoryDashboardRow>> GetDashboardInventoryAsync(
        string? search = null,
        string? setCode = null)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            SELECT
                c.card_id,
                c.card_name,
                c.set_code,
                c.rarity,
                c.is_foil,
                c.market_price_tix,
                COALESCE(c.custom_buy_price,  c.market_price_tix * s.default_buy_multiplier)  AS effective_buy_price,
                COALESCE(c.custom_sell_price, c.market_price_tix * s.default_sell_multiplier) AS effective_sell_price,
                COALESCE(c.custom_max_stock,  s.default_max_stock) AS max_stock,
                c.redeem_reserved,
                COALESCE(SUM(bi.quantity), 0) AS total_quantity,
                STRING_AGG(bi.bot_id || ':' || bi.quantity, ' ') AS bot_distribution
            FROM cards c
            JOIN sets s ON c.set_code = s.set_code
            LEFT JOIN bot_inventory bi ON c.card_id = bi.card_id AND bi.quantity > 0
            WHERE (@Search IS NULL OR c.card_name ILIKE '%' || @Search || '%')
              AND (@SetCode IS NULL OR c.set_code = @SetCode)
            GROUP BY c.card_id, c.card_name, c.set_code, c.rarity, c.is_foil,
                     c.market_price_tix, c.custom_buy_price, c.custom_sell_price,
                     c.custom_max_stock, c.redeem_reserved,
                     s.default_buy_multiplier, s.default_sell_multiplier, s.default_max_stock
            HAVING COALESCE(SUM(bi.quantity), 0) > 0
            ORDER BY c.market_price_tix DESC
            """;
        return await (await conn).QueryAsync<InventoryDashboardRow>(sql,
            new { Search = search, SetCode = setCode });
    }
}

/// <summary>Flat row returned for the dashboard inventory table.</summary>
public class InventoryDashboardRow
{
    public string CardId { get; set; } = string.Empty;
    public string CardName { get; set; } = string.Empty;
    public string SetCode { get; set; } = string.Empty;
    public string Rarity { get; set; } = string.Empty;
    public bool IsFoil { get; set; }
    public decimal MarketPriceTix { get; set; }
    public decimal EffectiveBuyPrice { get; set; }
    public decimal EffectiveSellPrice { get; set; }
    public int MaxStock { get; set; }
    public int RedeemReserved { get; set; }
    public int TotalQuantity { get; set; }
    public string BotDistribution { get; set; } = string.Empty;

    public string Status => TotalQuantity >= MaxStock
        ? "🛑 Max nådd"
        : TotalQuantity <= RedeemReserved
            ? "🟡 Kun Redeem"
            : "🟢 Kjøper";
}

// ══════════════════════════════════════════════════════════════════
// CreditRepository
// ══════════════════════════════════════════════════════════════════
public class CreditRepository
{
    private readonly DatabaseConnectionFactory _db;
    private readonly ILogger<CreditRepository> _logger;

    public CreditRepository(DatabaseConnectionFactory db, ILogger<CreditRepository> logger)
    { _db = db; _logger = logger; }

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

    public async Task<decimal> ApplyCreditDeltaAsync(
        string playerName, string botId, decimal delta, string reason)
    {
        var lower = playerName.ToLowerInvariant();
        await using var rawConn = await _db.CreateConnectionAsync();
        var conn = (Npgsql.NpgsqlConnection)rawConn;
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var current = await conn.QuerySingleOrDefaultAsync<decimal>(
                "SELECT credit_tix FROM users WHERE player_name = @Name FOR UPDATE",
                new { Name = lower }, tx);

            var newBalance = current + delta;
            if (newBalance < 0)
                throw new InvalidOperationException(
                    $"Credit underflow for {lower}: {current} + {delta} = {newBalance}");

            await conn.ExecuteAsync(
                "UPDATE users SET credit_tix = @Bal, last_trade_at = NOW() WHERE player_name = @Name",
                new { Bal = newBalance, Name = lower }, tx);

            await conn.ExecuteAsync(
                "INSERT INTO credit_log (player_name, bot_id, delta_amount, new_balance, reason) VALUES (@P,@B,@D,@N,@R)",
                new { P = lower, B = botId, D = delta, N = newBalance, R = reason }, tx);

            await tx.CommitAsync();
            _logger.LogInformation("Credit [{Player}]: {Delta:+0.0000;-0.0000} → {New:0.0000}", lower, delta, newBalance);
            return newBalance;
        }
        catch { await tx.RollbackAsync(); throw; }
    }

    public async Task<int> PurgeInactiveCreditAsync(int days = 90)
    {
        await using var conn = _db.CreateConnectionAsync();
        const string sql = """
            DELETE FROM users
            WHERE credit_tix > 0 AND last_trade_at < NOW() - INTERVAL '1 day' * @Days
            RETURNING player_name
            """;
        var purged = await (await conn).QueryAsync<string>(sql, new { Days = days });
        var count  = purged.Count();
        _logger.LogInformation("🧹 Credit purge: {Count} inactive players.", count);
        return count;
    }

    public async Task<IEnumerable<UserCredit>> GetAllCreditsAsync()
    {
        await using var conn = _db.CreateConnectionAsync();
        return await (await conn).QueryAsync<UserCredit>(
            "SELECT * FROM users WHERE credit_tix > 0 ORDER BY credit_tix DESC");
    }
}
