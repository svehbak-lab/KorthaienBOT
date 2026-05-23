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
        WITH base_cards AS (
            SELECT DISTINCT ON (c.card_name)
                c.card_name, c.market_price_tix, c.custom_buy_price, c.custom_sell_price,
                c.collector_number
            FROM cards c
            JOIN sets s ON c.set_code = s.set_code
            WHERE c.set_code = @Code AND c.is_foil = false
              AND (
                s.base_set_size IS NULL
                OR (
                  c.collector_number IS NOT NULL
                  AND CAST(SPLIT_PART(c.collector_number, '/', 1) AS INTEGER)
                      <= s.base_set_size
                )
              )
            ORDER BY c.card_name, c.market_price_tix ASC
        )
        SELECT
            SUM(COALESCE(bc.custom_buy_price,  bc.market_price_tix * s.default_buy_multiplier))  AS auto_buy,
            SUM(COALESCE(bc.custom_sell_price, bc.market_price_tix * s.default_sell_multiplier)) AS auto_sell,
            COUNT(*) AS card_count
        FROM base_cards bc
        CROSS JOIN sets s
        WHERE s.set_code = @Code
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
