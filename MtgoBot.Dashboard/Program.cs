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

/// <summary>
/// Dashboard main view: paginated inventory across all bots.
/// ?search=Ragavan  &set=MH2
/// </summary>
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

/// <summary>Get raw per-bot stock for a specific bot.</summary>
app.MapGet("/api/inventory/{botId}", async (string botId, InventoryRepository inv) =>
{
    var stock = await inv.GetBotInventoryAsync(botId);
    return Results.Ok(stock);
});

// ══════════════════════════════════════════════════════════════════
// CARD / SET FILTER ROUTES
// ══════════════════════════════════════════════════════════════════

/// <summary>
/// Update per-card overrides (buy price, sell price, max stock, redeem reserved).
/// This is the "single card" filter panel in the dashboard.
/// </summary>
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
.WithSummary("Set per-card buy/sell/redeem overrides");

/// <summary>
/// Update set-level rules (multipliers, max stock).
/// This is the "Set Configurator" tab in the dashboard.
/// </summary>
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

/// <summary>List all sets with their current rules.</summary>
app.MapGet("/api/sets", async (CardRepository cards) =>
    Results.Ok(await cards.GetAllSetsAsync()))
.WithSummary("Get all set rules");

// ══════════════════════════════════════════════════════════════════
// CREDIT ROUTES
// ══════════════════════════════════════════════════════════════════

/// <summary>List all player credits (for the Credits tab).</summary>
app.MapGet("/api/credits", async (CreditRepository credits) =>
    Results.Ok(await credits.GetAllCreditsAsync()))
.WithSummary("Get all player credit balances");

/// <summary>Manually adjust a player's credit (admin override).</summary>
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

/// <summary>Purge inactive credits (normally runs on schedule, but can trigger manually).</summary>
app.MapPost("/api/credits/purge-inactive", async (
    CreditRepository credits,
    int days = 90) =>
{
    var count = await credits.PurgeInactiveCreditAsync(days);
    return Results.Ok(new { purged = count, days });
})
.WithSummary("Purge credits for players inactive longer than N days");





// Bot set-specific rules
app.MapGet("/api/bots/{botId}/set-rules", async (string botId, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    var rules = await conn.QueryAsync(
        "SELECT * FROM bot_set_rules WHERE bot_id = @BotId ORDER BY set_code",
        new { BotId = botId });
    return Results.Ok(rules);
});

app.MapPut("/api/bots/{botId}/set-rules/{setCode}", async (string botId, string setCode, BotSetRuleRequest req, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO bot_set_rules (bot_id, set_code, max_local_stock, keep_local, max_per_trade, buy_multiplier, sell_multiplier, updated_at)
        VALUES (@BotId, @SetCode, @MaxLocal, @Keep, @Mpt, @Buy, @Sell, NOW())
        ON CONFLICT (bot_id, set_code) DO UPDATE SET
            max_local_stock  = @MaxLocal,
            keep_local       = @Keep,
            max_per_trade    = @Mpt,
            buy_multiplier   = @Buy,
            sell_multiplier  = @Sell,
            updated_at       = NOW()
        """, new { BotId = botId, SetCode = setCode, MaxLocal = req.MaxLocalStock, Keep = req.KeepLocal, Mpt = req.MaxPerTrade, Buy = req.BuyMultiplier, Sell = req.SellMultiplier });
    return Results.Ok(new { message = $"Set rule saved for {botId}/{setCode}" });
});

app.MapDelete("/api/bots/{botId}/set-rules/{setCode}", async (string botId, string setCode, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("DELETE FROM bot_set_rules WHERE bot_id=@BotId AND set_code=@SetCode",
        new { BotId = botId, SetCode = setCode });
    return Results.Ok(new { message = "Deleted" });
});

// ══════════════════════════════════════════════════════════════════
// BOTS ROUTES
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/bots", async (MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    var bots = await conn.QueryAsync("""
        SELECT b.bot_id, b.account_name, b.bot_type, b.description,
               b.transfer_to, b.tix_reserve, b.max_local_stock, b.card_transfer_to, b.fullset_transfer_to, b.trade_message, b.fullset_transfer_to,
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

app.MapPut("/api/bots/{botId}", async (string botId, BotUpdateRequest req, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("""
        INSERT INTO bots (bot_id, account_name, bot_type, description, transfer_to, tix_reserve, max_local_stock, card_transfer_to, is_online)
        VALUES (@BotId, @Account, @Type, @Desc, @TransferTo, @TixReserve, @MaxLocal, @CardXfer, false)
        ON CONFLICT (bot_id) DO UPDATE SET
            account_name     = @Account,
            bot_type         = @Type,
            description      = @Desc,
            transfer_to      = @TransferTo,
            tix_reserve      = @TixReserve,
            max_local_stock      = @MaxLocal,
            card_transfer_to     = @CardXfer,
            fullset_transfer_to  = @FullsetXfer,
            trade_message        = @TradeMsg
        """, new { BotId = botId, Account = req.AccountName, Type = req.BotType, Desc = req.Description, TransferTo = req.TransferTo, TixReserve = req.TixReserve, MaxLocal = req.MaxLocalStock, CardXfer = req.CardTransferTo, FullsetXfer = req.FullsetTransferTo, TradeMsg = req.TradeMessage });
    return Results.Ok(new { message = $"Bot {botId} updated" });
});

app.MapDelete("/api/bots/{botId}", async (string botId, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
{
    using var conn = (Npgsql.NpgsqlConnection)await dbf.CreateConnectionAsync();
    await conn.ExecuteAsync("DELETE FROM bots WHERE bot_id = @BotId", new { BotId = botId });
    return Results.Ok(new { message = $"Bot {botId} deleted" });
});

// ══════════════════════════════════════════════════════════════════
// COMPLETE SET PRICING ROUTES
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/sets/{setCode}/fullset-pricing", async (string setCode, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
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
        WHERE c.set_code = @Code
          AND c.is_foil = false
          AND c.collector_number IS NOT NULL
          AND (
            s.base_set_size IS NULL
            OR CAST(SPLIT_PART(c.collector_number, '/', 1) AS INTEGER) <= s.base_set_size
          )
        """, new { Code = setCode });
    return Results.Ok(new {
        setCode,
        fullsetBuy     = (decimal?)(existing?.fullset_buy),
        fullsetSell    = (decimal?)(existing?.fullset_sell),
        fullsetEnabled = (bool?)(existing?.fullset_enabled) ?? false,
        autoBuySum     = (decimal?)(autoCalc?.auto_buy) ?? 0m,
        autoSellSum    = (decimal?)(autoCalc?.auto_sell) ?? 0m,
        cardCount      = (int?)(autoCalc?.card_count) ?? 0
    });
})
.WithSummary("Get complete set pricing for a set");

app.MapPut("/api/sets/{setCode}/fullset-pricing", async (string setCode, FullsetPricingRequest req, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
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
})
.WithSummary("Set complete set buy/sell price");


app.MapGet("/api/sets/{setCode}/fullset-foil-pricing", async (string setCode, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
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
        WHERE c.set_code = @Code
          AND c.is_foil = true
          AND c.collector_number IS NOT NULL
          AND (s.base_set_size IS NULL
            OR CAST(SPLIT_PART(c.collector_number, '/', 1) AS INTEGER) <= s.base_set_size)
        """, new { Code = setCode });
    return Results.Ok(new {
        setCode,
        fullsetBuy     = (decimal?)(existing?.fullset_foil_buy),
        fullsetSell    = (decimal?)(existing?.fullset_foil_sell),
        fullsetEnabled = (bool?)(existing?.fullset_foil_enabled) ?? false,
        autoBuySum     = (decimal?)(autoCalc?.auto_buy) ?? 0m,
        autoSellSum    = (decimal?)(autoCalc?.auto_sell) ?? 0m,
        cardCount      = (int?)(autoCalc?.card_count) ?? 0
    });
})
.WithSummary("Get foil complete set pricing");

app.MapPut("/api/sets/{setCode}/fullset-foil-pricing", async (string setCode, FullsetPricingRequest req, MtgoBot.Core.Data.DatabaseConnectionFactory dbf) =>
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
})
.WithSummary("Set foil complete set pricing");

// ══════════════════════════════════════════════════════════════════
// SYSTEM / STATUS ROUTES
// ══════════════════════════════════════════════════════════════════

app.MapGet("/api/status", () => Results.Ok(new
{
    service    = "KorthaienBOT Dashboard",
    timestamp  = DateTime.UtcNow,
    version    = "1.0.0"
}))
.WithSummary("Health check");

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

record BotUpdateRequest(string AccountName, string BotType, string? Description, string? TransferTo, int TixReserve = 500, int MaxLocalStock = 4, string? CardTransferTo = null, string? FullsetTransferTo = null, string? TradeMessage = null);

record BotSetRuleRequest(int? MaxLocalStock, int? KeepLocal, int? MaxPerTrade, decimal? BuyMultiplier = null, decimal? SellMultiplier = null);
