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
        req.RedeemReserved);
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
        req.MaxStock);
    return Results.Ok(new { message = $"Rules updated for set {setCode}" });
})
.WithSummary("Update set-level buy/sell multipliers and max stock");

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
    int RedeemReserved);

record SetRulesRequest(
    decimal BuyMultiplier,
    decimal SellMultiplier,
    int MaxStock);

record CreditAdjustRequest(decimal Delta, string? Reason);
