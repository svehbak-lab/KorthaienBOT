using Npgsql;
using Dapper;
using MtgoBot.Core.Models;

namespace MtgoBot.Core.Data;

public class TradeLogEntry
{
    public string  BotId        { get; set; } = string.Empty;
    public string  PlayerName   { get; set; } = string.Empty;
    public string  TradeType    { get; set; } = string.Empty;
    public string? SetCode      { get; set; }
    public string? CardId       { get; set; }
    public string? CardName     { get; set; }
    public int     Quantity     { get; set; } = 1;
    public decimal PriceTix     { get; set; }
    public decimal TotalTix     { get; set; }
    public decimal CreditBefore { get; set; }
    public decimal CreditAfter  { get; set; }
    public decimal CreditChange { get; set; }
}

public class TradeLogRepository
{
    private readonly DatabaseConnectionFactory _db;

    public TradeLogRepository(DatabaseConnectionFactory db) => _db = db;

    public async Task LogTradeAsync(TradeLogEntry entry)
    {
        using var conn = (Npgsql.NpgsqlConnection)_db.CreateConnectionAsync().Result;
        await conn.ExecuteAsync("""
            INSERT INTO trade_log (
                bot_id, player_name, trade_type, set_code, card_id, card_name,
                quantity, price_tix, total_tix, credit_before, credit_after, credit_change
            ) VALUES (
                @BotId, @PlayerName, @TradeType, @SetCode, @CardId, @CardName,
                @Quantity, @PriceTix, @TotalTix, @CreditBefore, @CreditAfter, @CreditChange
            )
            """, entry);
    }

    public async Task<List<TradeLogEntry>> GetRecentTradesAsync(string botId, int limit = 100)
    {
        using var conn = (Npgsql.NpgsqlConnection)_db.CreateConnectionAsync().Result;
        var rows = await conn.QueryAsync<TradeLogEntry>("""
            SELECT * FROM trade_log
            WHERE bot_id = @BotId
            ORDER BY traded_at DESC
            LIMIT @Limit
            """, new { BotId = botId, Limit = limit });
        return rows.ToList();
    }

    public async Task<List<TradeLogEntry>> GetPlayerHistoryAsync(string playerName, int limit = 50)
    {
        using var conn = (Npgsql.NpgsqlConnection)_db.CreateConnectionAsync().Result;
        var rows = await conn.QueryAsync<TradeLogEntry>("""
            SELECT * FROM trade_log
            WHERE player_name = @Player
            ORDER BY traded_at DESC
            LIMIT @Limit
            """, new { Player = playerName, Limit = limit });
        return rows.ToList();
    }

    public async Task<(decimal TotalBought, decimal TotalSold, decimal NetProfit)> GetSummaryAsync(
        string botId, DateTime from, DateTime to)
    {
        using var conn = (Npgsql.NpgsqlConnection)_db.CreateConnectionAsync().Result;
        var result = await conn.QuerySingleOrDefaultAsync("""
            SELECT
                COALESCE(SUM(CASE WHEN trade_type = 'BUY' THEN total_tix ELSE 0 END), 0) AS total_bought,
                COALESCE(SUM(CASE WHEN trade_type = 'SELL' THEN total_tix ELSE 0 END), 0) AS total_sold
            FROM trade_log
            WHERE bot_id = @BotId AND traded_at BETWEEN @From AND @To
            """, new { BotId = botId, From = from, To = to });

        decimal bought = result?.total_bought ?? 0m;
        decimal sold   = result?.total_sold ?? 0m;
        return (bought, sold, sold - bought);
    }
}
