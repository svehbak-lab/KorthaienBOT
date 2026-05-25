using Dapper;
using MtgoBot.Core.Data;
using Serilog;

MtgoBot.Core.Data.DapperConfig.Initialize();
var builder = WebApplication.CreateBuilder(args);

Log.Logger = new Serilog.LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/dashboard-.log", rollingInterval: Serilog.RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();
builder.Services.AddSingleton<DatabaseConnectionFactory>();
builder.Services.AddSingleton<CardRepository>();
builder.Services.AddSingleton<InventoryRepository>();
builder.Services.AddSingleton<CreditRepository>();
builder.Services.AddHttpClient("GoatBots");
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ══════════════════════════════════════════════════════════════════
// INVENTORY ROUTES
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/inventory", async (
    InventoryRepository inv,
    string? search = null,
    string? set    = null) =>
{
    var rows = await inv.GetDashboardInventoryAsync(search, set);
    return Results.Ok(rows);
})
.WithName("GetInventory")
.WithSummary("Aggregated inventory across all bots");

app.MapGet("/api/inventory/{botId}", async (string botId, InventoryRepository inv) =>
{
    var stock = await inv.GetBotInventoryAsync(botId);
    return Results.Ok(stock);
});

// ══════════════════════════════════════════════════════════════════
// CARD / SET FILTER ROUTES  (global overrides — unchanged)
// ══════════════════════════════════════════════════════════════════

app.MapPut("/api/cards/{cardId}/overrides", async (
    string cardId,
    CardOverrideRequest req,
    CardRepository cards) =>
{
    await cards.SetCardOverridesAsync(
        cardId,
        req.CustomBuyPrice,
        req.CustomSellPrice,
        req.CustomMaxStock,
        req.RedeemReserved,
        req.MaxPerTrade);
    return Results.Ok(new { message = $"Overrides updated for {cardId}" });
})
.WithSummary("Set per-card buy/sell/redeem overrides (global)");

app.MapPut("/api/sets/{setCode}/rules", async (
    string setCode,
    SetRulesRequest req,
    CardRepository cards) =>
{
    await cards.UpdateSetRulesAsync(
        setCode,
        req.BuyMultiplier,
        req.SellMultiplier,
        req.MaxStock,
        req.BaseSetSize);
    return Results.Ok(new { message = $"Rules updated for set {setCode}" });
})
.WithSummary("Update set-level buy/sell multipliers and max stock");

app.MapPost("/api/sets/{setCode}/apply-keep", async (string setCode, ApplyKeepRequest req, CardRepository cards) =>
{
    await cards.ApplyKeepToSetAsync(setCode, req.KeepValue);
    return Results.Ok(new { message = $"Keep={req.KeepValue} applied to all cards in {setCode}" });
})
.WithSummary("Set redeem_reserved for all cards in a set");

app.MapGet("/api/sets", async (CardRepository cards) =>
    Results.Ok(await cards.GetAllSetsAsync()))
.WithSummary("Get all set rules");

// ══════════════════════════════════════════════════════════════════
// BOT SET RULES — which sets each bot trades + per-set overrides
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Get all set rules for a bot, joined with set info so the UI has
/// names and current enabled state for every set in one call.
/// </summary>
app.MapGet("/api/bots/{botId}/set-rules", async (string botId, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    var rules = await conn.QueryAsync("""
        SELECT
            s.set_code,
            s.set_name,
            s.default_buy_multiplier,
            s.default_sell_multiplier,
            s.default_max_stock,
            COALESCE(bsr.enabled,          false) AS enabled,
            bsr.max_local_stock,
            bsr.keep_local,
            bsr.max_per_trade,
            bsr.buy_multiplier,
            bsr.sell_multiplier,
            bsr.updated_at
        FROM sets s
        LEFT JOIN bot_set_rules bsr ON bsr.set_code = s.set_code AND bsr.bot_id = @BotId
        ORDER BY s.set_name
        """, new { BotId = botId });
    return Results.Ok(rules);
});

/// <summary>
/// Save a set rule for a bot (upsert).
/// Pass enabled=true to activate, enabled=false to deactivate.
/// </summary>
app.MapPut("/api/bots/{botId}/set-rules/{setCode}", async (
    string botId, string setCode,
    BotSetRuleRequest req,
    DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO bot_set_rules
            (bot_id, set_code, enabled, max_local_stock, keep_local, max_per_trade,
             buy_multiplier, sell_multiplier, updated_at)
        VALUES
            (@BotId, @SetCode, @Enabled, @MaxLocal, @Keep, @Mpt, @Buy, @Sell, NOW())
        ON CONFLICT (bot_id, set_code) DO UPDATE SET
            enabled         = @Enabled,
            max_local_stock = @MaxLocal,
            keep_local      = @Keep,
            max_per_trade   = @Mpt,
            buy_multiplier  = @Buy,
            sell_multiplier = @Sell,
            updated_at      = NOW()
        """, new {
            BotId    = botId,
            SetCode  = setCode,
            Enabled  = req.Enabled ?? true,
            MaxLocal = req.MaxLocalStock,
            Keep     = req.KeepLocal,
            Mpt      = req.MaxPerTrade,
            Buy      = req.BuyMultiplier,
            Sell     = req.SellMultiplier
        });
    return Results.Ok(new { message = $"Set rule saved for {botId}/{setCode}" });
});

/// <summary>
/// Bulk-save which sets a bot has enabled (the checkbox list).
/// Body: { "enabledSets": ["MH3","LTR","DSK"] }
/// All sets not in the list are set to enabled=false.
/// </summary>
app.MapPut("/api/bots/{botId}/enabled-sets", async (
    string botId,
    EnabledSetsRequest req,
    DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();

    // Get all sets
    var allSets = (await conn.QueryAsync<string>("SELECT set_code FROM sets")).ToList();

    foreach (var setCode in allSets)
    {
        bool enabled = req.EnabledSets.Contains(setCode);
        await conn.ExecuteAsync("""
            INSERT INTO bot_set_rules (bot_id, set_code, enabled, updated_at)
            VALUES (@BotId, @SetCode, @Enabled, NOW())
            ON CONFLICT (bot_id, set_code) DO UPDATE SET
                enabled    = @Enabled,
                updated_at = NOW()
            """, new { BotId = botId, SetCode = setCode, Enabled = enabled });
    }

    return Results.Ok(new { message = $"Enabled sets updated for {botId}", count = req.EnabledSets.Count });
});

app.MapDelete("/api/bots/{botId}/set-rules/{setCode}", async (string botId, string setCode, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync(
        "DELETE FROM bot_set_rules WHERE bot_id=@BotId AND set_code=@SetCode",
        new { BotId = botId, SetCode = setCode });
    return Results.Ok(new { message = "Deleted" });
});

// ══════════════════════════════════════════════════════════════════
// BOT CARD RULES — per-bot, per-card overrides
// These are the most specific level and override everything else.
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Get all card-level overrides for a specific bot + set combination.
/// Used to populate the card table in the Filter page.
/// </summary>
app.MapGet("/api/bots/{botId}/card-rules", async (
    string botId,
    string? setCode,
    DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    var rows = await conn.QueryAsync("""
        SELECT
            c.card_id,
            c.card_name,
            c.set_code,
            c.rarity,
            c.is_foil,
            c.collector_number,
            c.market_price_tix,
            -- Priority: bot_card_rules > global cards overrides > set multiplier
            COALESCE(bcr.custom_buy_price,
                     c.custom_buy_price,
                     c.market_price_tix * s.default_buy_multiplier)  AS effective_buy_price,
            COALESCE(bcr.custom_sell_price,
                     c.custom_sell_price,
                     c.market_price_tix * s.default_sell_multiplier) AS effective_sell_price,
            COALESCE(bcr.custom_max_stock,  c.custom_max_stock,  s.default_max_stock)  AS max_stock,
            COALESCE(bcr.redeem_reserved,   c.redeem_reserved)                         AS redeem_reserved,
            COALESCE(bcr.max_per_trade,     c.max_per_trade, 4)                        AS max_per_trade,
            -- Flag: does a bot-specific override exist?
            (bcr.bot_id IS NOT NULL) AS has_bot_override
        FROM cards c
        JOIN sets s ON c.set_code = s.set_code
        LEFT JOIN bot_card_rules bcr ON bcr.card_id = c.card_id AND bcr.bot_id = @BotId
        WHERE (@SetCode IS NULL OR c.set_code = @SetCode)
        ORDER BY c.market_price_tix DESC
        """, new { BotId = botId, SetCode = setCode });
    return Results.Ok(rows);
});

/// <summary>
/// Upsert a bot-specific card override.
/// This is what gets called when you click 💾 on a card row in the Filter page.
/// </summary>
app.MapPut("/api/bots/{botId}/card-rules/{cardId}", async (
    string botId, string cardId,
    BotCardRuleRequest req,
    DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO bot_card_rules
            (bot_id, card_id, custom_buy_price, custom_sell_price,
             custom_max_stock, redeem_reserved, max_per_trade, updated_at)
        VALUES
            (@BotId, @CardId, @Buy, @Sell, @Max, @Keep, @Mpt, NOW())
        ON CONFLICT (bot_id, card_id) DO UPDATE SET
            custom_buy_price  = @Buy,
            custom_sell_price = @Sell,
            custom_max_stock  = @Max,
            redeem_reserved   = @Keep,
            max_per_trade     = @Mpt,
            updated_at        = NOW()
        """, new {
            BotId  = botId,
            CardId = cardId,
            Buy    = req.CustomBuyPrice,
            Sell   = req.CustomSellPrice,
            Max    = req.CustomMaxStock,
            Keep   = req.RedeemReserved,
            Mpt    = req.MaxPerTrade
        });
    return Results.Ok(new { message = $"Bot card rule saved for {botId}/{cardId}" });
});

/// <summary>Delete a bot card override (falls back to global rules).</summary>
app.MapDelete("/api/bots/{botId}/card-rules/{cardId}", async (
    string botId, string cardId, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync(
        "DELETE FROM bot_card_rules WHERE bot_id=@BotId AND card_id=@CardId",
        new { BotId = botId, CardId = cardId });
    return Results.Ok(new { message = "Bot card rule deleted" });
});

// ══════════════════════════════════════════════════════════════════
// CREDIT ROUTES
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/credits", async (CreditRepository credits) =>
    Results.Ok(await credits.GetAllCreditsAsync()))
.WithSummary("Get all player credit balances");

app.MapPost("/api/credits/{playerName}/adjust", async (
    string playerName,
    CreditAdjustRequest req,
    CreditRepository credits) =>
{
    var newBalance = await credits.ApplyCreditDeltaAsync(
        playerName, "DASHBOARD", req.Delta, req.Reason ?? "Manual admin adjustment");
    return Results.Ok(new { playerName, newBalance });
})
.WithSummary("Manually adjust a player's credit balance");

app.MapPost("/api/credits/purge-inactive", async (CreditRepository credits, int days = 90) =>
{
    var count = await credits.PurgeInactiveCreditAsync(days);
    return Results.Ok(new { purged = count, days });
})
.WithSummary("Purge credits for players inactive longer than N days");

// ══════════════════════════════════════════════════════════════════
// BOTS ROUTES
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/bots", async (DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    var bots = await conn.QueryAsync("""
        SELECT b.bot_id, b.account_name, b.bot_type, b.description,
               b.transfer_to, b.tix_reserve, b.max_local_stock,
               b.card_transfer_to, b.fullset_transfer_to, b.trade_message,
               b.fullset_buy_enabled, b.fullset_sell_enabled, b.max_sets_per_trade,
               b.is_online, b.last_seen,
               COALESCE(SUM(bi.quantity), 0) AS total_cards
        FROM bots b
        LEFT JOIN bot_inventory bi ON b.bot_id = bi.bot_id
        GROUP BY b.bot_id, b.account_name, b.bot_type, b.description,
                 b.transfer_to, b.is_online, b.last_seen
        ORDER BY b.bot_type, b.bot_id
        """);
    return Results.Ok(bots);
});

app.MapPut("/api/bots/{botId}", async (string botId, BotUpdateRequest req, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO bots (bot_id, account_name, bot_type, description, transfer_to,
            tix_reserve, max_local_stock, card_transfer_to, fullset_transfer_to,
            trade_message, fullset_buy_enabled, fullset_sell_enabled, max_sets_per_trade, is_online)
        VALUES (@BotId, @Account, @Type, @Desc, @TransferTo, @TixReserve, @MaxLocal,
            @CardXfer, @FullsetXfer, @TradeMsg, @FsBuy, @FsSell, @MaxSets, false)
        ON CONFLICT (bot_id) DO UPDATE SET
            account_name         = @Account,
            bot_type             = @Type,
            description          = @Desc,
            transfer_to          = @TransferTo,
            tix_reserve          = @TixReserve,
            max_local_stock      = @MaxLocal,
            card_transfer_to     = @CardXfer,
            fullset_transfer_to  = @FullsetXfer,
            trade_message        = @TradeMsg,
            fullset_buy_enabled  = @FsBuy,
            fullset_sell_enabled = @FsSell,
            max_sets_per_trade   = @MaxSets
        """, new {
            BotId = botId, Account = req.AccountName, Type = req.BotType,
            Desc = req.Description, TransferTo = req.TransferTo,
            TixReserve = req.TixReserve, MaxLocal = req.MaxLocalStock,
            CardXfer = req.CardTransferTo, FullsetXfer = req.FullsetTransferTo,
            TradeMsg = req.TradeMessage, FsBuy = req.FullsetBuyEnabled,
            FsSell = req.FullsetSellEnabled, MaxSets = req.MaxSetsPerTrade
        });
    return Results.Ok(new { message = $"Bot {botId} updated" });
});

app.MapDelete("/api/bots/{botId}", async (string botId, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("DELETE FROM bots WHERE bot_id = @BotId", new { BotId = botId });
    return Results.Ok(new { message = $"Bot {botId} deleted" });
});

// ══════════════════════════════════════════════════════════════════
// COMPLETE SET PRICING ROUTES
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/sets/{setCode}/fullset-pricing", async (string setCode, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    var existing = await conn.QuerySingleOrDefaultAsync(
        "SELECT fullset_buy, fullset_sell, fullset_enabled FROM set_price_overrides WHERE set_code = @Code",
        new { Code = setCode });
    var autoCalc = await conn.QuerySingleOrDefaultAsync("""
        SELECT
            SUM(COALESCE(c.custom_buy_price,  c.market_price_tix * s.default_buy_multiplier))  AS auto_buy,
            SUM(COALESCE(c.custom_sell_price, c.market_price_tix * s.default_sell_multiplier)) AS auto_sell,
            COUNT(*) AS card_count
        FROM cards c
        JOIN sets s ON c.set_code = s.set_code
        WHERE c.set_code = @Code AND c.is_foil = false
          AND c.collector_number IS NOT NULL
          AND (s.base_set_size IS NULL
               OR CAST(SPLIT_PART(c.collector_number, '/', 1) AS INTEGER) <= s.base_set_size)
        """, new { Code = setCode });
    return Results.Ok(new {
        setCode,
        fullsetBuy     = (decimal?)(existing?.fullset_buy),
        fullsetSell    = (decimal?)(existing?.fullset_sell),
        fullsetEnabled = (bool?)(existing?.fullset_enabled) ?? false,
        autoBuySum     = (decimal?)(autoCalc?.auto_buy)    ?? 0m,
        autoSellSum    = (decimal?)(autoCalc?.auto_sell)   ?? 0m,
        cardCount      = (int?)(autoCalc?.card_count)      ?? 0
    });
});

app.MapPut("/api/sets/{setCode}/fullset-pricing", async (string setCode, FullsetPricingRequest req, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO set_price_overrides (set_code, fullset_buy, fullset_sell, fullset_enabled, updated_at)
        VALUES (@Code, @Buy, @Sell, @Enabled, NOW())
        ON CONFLICT (set_code) DO UPDATE SET
            fullset_buy     = @Buy,
            fullset_sell    = @Sell,
            fullset_enabled = @Enabled,
            updated_at      = NOW()
        """, new { Code = setCode, Buy = req.FullsetBuy, Sell = req.FullsetSell, Enabled = req.FullsetEnabled });
    return Results.Ok(new { message = $"Fullset pricing updated for {setCode}" });
});

app.MapGet("/api/sets/{setCode}/fullset-foil-pricing", async (string setCode, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    var existing = await conn.QuerySingleOrDefaultAsync(
        "SELECT fullset_foil_buy, fullset_foil_sell, fullset_foil_enabled FROM set_price_overrides WHERE set_code = @Code",
        new { Code = setCode });
    var autoCalc = await conn.QuerySingleOrDefaultAsync("""
        SELECT
            SUM(COALESCE(c.custom_buy_price,  c.market_price_tix * s.default_buy_multiplier))  AS auto_buy,
            SUM(COALESCE(c.custom_sell_price, c.market_price_tix * s.default_sell_multiplier)) AS auto_sell,
            COUNT(*) AS card_count
        FROM cards c
        JOIN sets s ON c.set_code = s.set_code
        WHERE c.set_code = @Code AND c.is_foil = true
          AND c.collector_number IS NOT NULL
          AND (s.base_set_size IS NULL
               OR CAST(SPLIT_PART(c.collector_number, '/', 1) AS INTEGER) <= s.base_set_size)
        """, new { Code = setCode });
    return Results.Ok(new {
        setCode,
        fullsetBuy     = (decimal?)(existing?.fullset_foil_buy),
        fullsetSell    = (decimal?)(existing?.fullset_foil_sell),
        fullsetEnabled = (bool?)(existing?.fullset_foil_enabled) ?? false,
        autoBuySum     = (decimal?)(autoCalc?.auto_buy)    ?? 0m,
        autoSellSum    = (decimal?)(autoCalc?.auto_sell)   ?? 0m,
        cardCount      = (int?)(autoCalc?.card_count)      ?? 0
    });
});

app.MapPut("/api/sets/{setCode}/fullset-foil-pricing", async (string setCode, FullsetPricingRequest req, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO set_price_overrides (set_code, fullset_foil_buy, fullset_foil_sell, fullset_foil_enabled, updated_at)
        VALUES (@Code, @Buy, @Sell, @Enabled, NOW())
        ON CONFLICT (set_code) DO UPDATE SET
            fullset_foil_buy     = @Buy,
            fullset_foil_sell    = @Sell,
            fullset_foil_enabled = @Enabled,
            updated_at           = NOW()
        """, new { Code = setCode, Buy = req.FullsetBuy, Sell = req.FullsetSell, Enabled = req.FullsetEnabled });
    return Results.Ok(new { message = $"Foil fullset pricing updated for {setCode}" });
});

// ══════════════════════════════════════════════════════════════════
// SYSTEM / STATUS ROUTES
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/settings/foil-multiplier", async (DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    var val = await conn.QuerySingleOrDefaultAsync<decimal?>(
        "SELECT setting_value::decimal FROM bot_settings WHERE setting_key = 'foil_multiplier'");
    return Results.Ok(new { value = val ?? 1.0m });
});

app.MapPut("/api/settings/foil-multiplier", async (FoilMultiplierRequest req, DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO bot_settings (setting_key, setting_value)
        VALUES ('foil_multiplier', @Val::text)
        ON CONFLICT (setting_key) DO UPDATE SET setting_value = @Val::text
        """, new { Val = req.Value });
    return Results.Ok(new { message = "Foil multiplier saved" });
});

app.MapGet("/api/status", () => Results.Ok(new
{
    service   = "KorthaienBOT Dashboard",
    timestamp = DateTime.UtcNow,
    version   = "1.0.0"
}));

app.Run();

// ─────────────────────────────────────────────────────────────────
// Request/response models
// ─────────────────────────────────────────────────────────────────

record CardOverrideRequest(
    decimal? CustomBuyPrice,
    decimal? CustomSellPrice,
    int? CustomMaxStock,
    int RedeemReserved,
    int MaxPerTrade = 4);

record SetRulesRequest(
    decimal BuyMultiplier,
    decimal SellMultiplier,
    int MaxStock,
    int? BaseSetSize = null);

record CreditAdjustRequest(decimal Delta, string? Reason);
record ApplyKeepRequest(int KeepValue);
record FullsetPricingRequest(decimal? FullsetBuy, decimal? FullsetSell, bool FullsetEnabled);
record FoilMultiplierRequest(decimal Value);

record BotUpdateRequest(
    string AccountName, string BotType, string? Description, string? TransferTo,
    int TixReserve = 500, int MaxLocalStock = 4,
    string? CardTransferTo = null, string? FullsetTransferTo = null,
    string? TradeMessage = null,
    bool FullsetBuyEnabled = false, bool FullsetSellEnabled = true,
    int MaxSetsPerTrade = 1);

// enabled=null means "don't change enabled state, just update the rule values"
record BotSetRuleRequest(
    bool? Enabled,
    int? MaxLocalStock,
    int? KeepLocal,
    int? MaxPerTrade,
    decimal? BuyMultiplier = null,
    decimal? SellMultiplier = null);

record BotCardRuleRequest(
    decimal? CustomBuyPrice,
    decimal? CustomSellPrice,
    int? CustomMaxStock,
    int RedeemReserved = 0,
    int MaxPerTrade = 4);

record EnabledSetsRequest(List<string> EnabledSets);
