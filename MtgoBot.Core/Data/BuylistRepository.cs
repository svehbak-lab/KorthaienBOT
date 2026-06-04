// ════════════════════════════════════════════════════════════════════
//  BuylistRepository.cs
//
//  WHERE THIS FILE GOES:
//      KorthaienBOT/MtgoBot.Core/Data/BuylistRepository.cs
//
//  This is a NEW, self-contained file. You do not edit any existing
//  class to use it. It talks to PostgreSQL through the existing
//  DatabaseConnectionFactory.
//
//  A bot's buylist is DERIVED, not stored: every card in the sets the
//  bot is enabled for (i.e. has a row in bot_set_rules), where the bot
//  currently holds fewer than its target, priced by the effective buy
//  price. The trade loop walks this list and types each CardName into
//  the MTGO trade-window search field, double-clicking matches up to
//  QtyNeeded.
//
//  Requires the bot_set_rules table (see 001_bot_set_rules.sql).
// ════════════════════════════════════════════════════════════════════

using System.Data;
using Dapper;

namespace MtgoBot.Core.Data;

/// <summary>
/// One line of a bot's buylist: a card the bot wants to acquire, how many
/// it still needs, and what it will pay. <see cref="CardName"/> is the
/// string typed into the MTGO trade search field.
/// </summary>
public record BuylistItem(
    string  CardId,
    string  CardName,
    string  SetCode,
    int     QtyNeeded,
    decimal BuyPrice)
{
    public decimal TotalValue => QtyNeeded * BuyPrice;
}

/// <summary>
/// Builds the live buylist for a single bot from its set rules, current
/// inventory, and pricing. Registered as a singleton in DI.
/// </summary>
public class BuylistRepository
{
    private readonly DatabaseConnectionFactory _dbf;

    public BuylistRepository(DatabaseConnectionFactory dbf)
    {
        _dbf = dbf;
    }

    /// <summary>
    /// Build the buylist for <paramref name="botId"/>.
    ///
    /// Scope:  sets the bot is enabled for (rows in bot_set_rules).
    /// Target: custom_max_stock (card) ?? max_local_stock (bot+set) ?? default_max_stock (set).
    /// Need:   target - current stock on THIS bot, only where > 0.
    /// Price:  custom_buy_price (card) ?? market_price_tix * (bot buy_mult ?? set buy_mult).
    ///         Cards whose effective buy price is 0 are skipped.
    ///
    /// Ordered highest-value first, so a trade cut short still grabs the
    /// most important cards. Switch to ORDER BY c.card_name for plain
    /// alphabetical typing into the search box.
    /// </summary>
    public async Task<IReadOnlyList<BuylistItem>> GetBuylistAsync(string botId)
    {
        using IDbConnection conn = await _dbf.CreateConnectionAsync();

        const string sql = """
            SELECT
                c.card_id   AS CardId,
                c.card_name AS CardName,
                c.set_code  AS SetCode,
                (COALESCE(c.custom_max_stock, r.max_local_stock, s.default_max_stock)
                     - COALESCE(bi.quantity, 0))                       AS QtyNeeded,
                COALESCE(
                    c.custom_buy_price,
                    ROUND(c.market_price_tix
                          * COALESCE(r.buy_multiplier, s.default_buy_multiplier), 4)
                )                                                      AS BuyPrice
            FROM bot_set_rules r
            JOIN sets  s ON s.set_code = r.set_code
            JOIN cards c ON c.set_code = r.set_code
            LEFT JOIN bot_inventory bi
                   ON bi.bot_id  = r.bot_id
                  AND bi.card_id = c.card_id
            WHERE r.bot_id = @BotId
              AND COALESCE(c.custom_max_stock, r.max_local_stock, s.default_max_stock)
                  > COALESCE(bi.quantity, 0)
              AND COALESCE(
                    c.custom_buy_price,
                    ROUND(c.market_price_tix
                          * COALESCE(r.buy_multiplier, s.default_buy_multiplier), 4)
                  ) > 0
            ORDER BY BuyPrice DESC, c.card_name
            """;

        var rows = await conn.QueryAsync<BuylistItem>(sql, new { BotId = botId });
        return rows.AsList();
    }
}
