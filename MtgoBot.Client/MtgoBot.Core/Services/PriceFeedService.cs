using System.Text.Json;
using Npgsql;
using MtgoBot.Core.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace MtgoBot.Core.Services;

/// <summary>
/// Downloads GoatBots' free daily JSON files and bulk-upserts them into the database.
///
/// GoatBots publishes two files at ~05:30 each morning:
///   definitions.json — { "74201": ["Lightning Bolt", "MM2", "R", "N"], ... }
///   prices.json      — { "74201": 0.35, ... }
///
/// "N" = Normal, "F" = Foil — critical because MTGO uses separate IDs per version.
///
/// GoatBots ask that you credit their site if using the data publicly.
/// Per their terms: https://www.goatbots.com/download-prices
/// </summary>
public class PriceFeedService : BackgroundService
{
    private const string DefinitionsUrl = "https://www.goatbots.com/download/card-definitions.json";
    private const string PricesUrl      = "https://www.goatbots.com/download/card-prices.json";

    // Run once at startup, then daily at 05:35 (5 mins after GoatBots publishes)
    private static readonly TimeOnly DailyRunTime = new(5, 35, 0);

    private readonly DatabaseConnectionFactory _db;
    private readonly ILogger<PriceFeedService> _logger;
    private readonly HttpClient _http;
    private readonly bool _runOnStartup;

    public PriceFeedService(
        DatabaseConnectionFactory db,
        ILogger<PriceFeedService> logger,
        IHttpClientFactory httpFactory,
        IConfiguration config)
    {
        _db           = db;
        _logger       = logger;
        _http         = httpFactory.CreateClient("GoatBots");
        _runOnStartup = config.GetValue("PriceFeed:RunOnStartup", true);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("📈 PriceFeedService started.");

        // Run immediately on first boot so the DB isn't empty
        if (_runOnStartup)
        {
            await RunFeedCycleAsync(ct);
        }

        while (!ct.IsCancellationRequested)
        {
            var delay = TimeUntilNextRun();
            _logger.LogInformation(
                "⏰ Next price update in {Hours}h {Min}m",
                (int)delay.TotalHours, delay.Minutes);

            await Task.Delay(delay, ct);

            if (!ct.IsCancellationRequested)
                await RunFeedCycleAsync(ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Main cycle: download both files, then upsert
    // ─────────────────────────────────────────────────────────────────

    public async Task RunFeedCycleAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("🔄 Starting GoatBots price feed download...");
        try
        {
            var definitionsJson = await DownloadJsonAsync(DefinitionsUrl, ct);
            var pricesJson      = await DownloadJsonAsync(PricesUrl, ct);

            var definitions = ParseDefinitions(definitionsJson);
            var prices      = ParsePrices(pricesJson);

            await BulkUpsertCardsAsync(definitions, prices, ct);
            _logger.LogInformation("✅ Price feed complete. {Count} cards processed.", definitions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Price feed failed. Will retry at next scheduled run.");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Download
    // ─────────────────────────────────────────────────────────────────

    private async Task<string> DownloadJsonAsync(string url, CancellationToken ct)
    {
        _logger.LogDebug("Downloading: {Url}", url);
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────
    // Parse definitions.json
    // Format: { "74201": ["Lightning Bolt", "MM2", "R", "N"], ... }
    // Index:       0=name   1=setCode  2=rarity  3=foilFlag
    // ─────────────────────────────────────────────────────────────────

    private static Dictionary<string, CardDefinition> ParseDefinitions(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
            ?? throw new InvalidDataException("definitions.json was empty or malformed.");

        var result = new Dictionary<string, CardDefinition>(raw.Count);
        foreach (var (id, arr) in raw)
        {
            try
            {
                var parts = arr.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                if (parts.Length < 4) continue;

                result[id] = new CardDefinition(
                    CardId:   id,
                    CardName: parts[0],
                    SetCode:  parts[1],
                    Rarity:   parts[2],
                    IsFoil:   parts[3].Equals("F", StringComparison.OrdinalIgnoreCase));
            }
            catch { /* skip malformed entries */ }
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────
    // Parse prices.json
    // Format: { "74201": 0.3500, ... }
    // ─────────────────────────────────────────────────────────────────

    private static Dictionary<string, decimal> ParsePrices(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
            ?? throw new InvalidDataException("prices.json was empty or malformed.");

        var result = new Dictionary<string, decimal>(raw.Count);
        foreach (var (id, priceEl) in raw)
        {
            try { result[id] = priceEl.GetDecimal(); }
            catch { /* skip */ }
        }
        return result;
    }

    // ─────────────────────────────────────────────────────────────────
    // Bulk upsert — the fast path
    //
    // Uses a single transaction with parameterised INSERT ... ON CONFLICT
    // to upsert ~40,000 rows in one shot. Completes in < 5 seconds on VPS.
    // ─────────────────────────────────────────────────────────────────

    private async Task BulkUpsertCardsAsync(
        Dictionary<string, CardDefinition> definitions,
        Dictionary<string, decimal> prices,
        CancellationToken ct)
    {
        await using var conn = (NpgsqlConnection)await _db.CreateConnectionAsync();
        await using var tx   = await conn.BeginTransactionAsync(ct);

        try
        {
            // Ensure the set exists (insert unknown sets as placeholders)
            var setsSeen = definitions.Values.Select(d => d.SetCode).Distinct().ToList();
            foreach (var setCode in setsSeen)
            {
                await using var setCmd = conn.CreateCommand();
                setCmd.Transaction = tx;
                setCmd.CommandText = """
                    INSERT INTO sets (set_code, set_name)
                    VALUES (@Code, @Name)
                    ON CONFLICT (set_code) DO NOTHING
                    """;
                setCmd.Parameters.AddWithValue("Code", setCode);
                setCmd.Parameters.AddWithValue("Name", setCode); // name = code until manually updated
                await setCmd.ExecuteNonQueryAsync(ct);
            }

            // Upsert cards + prices together
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO cards (card_id, card_name, set_code, rarity, is_foil, market_price_tix)
                VALUES (@Id, @Name, @Set, @Rarity, @Foil, @Price)
                ON CONFLICT (card_id) DO UPDATE SET
                    card_name        = EXCLUDED.card_name,
                    set_code         = EXCLUDED.set_code,
                    rarity           = EXCLUDED.rarity,
                    is_foil          = EXCLUDED.is_foil,
                    market_price_tix = EXCLUDED.market_price_tix
                """;

            var pId     = cmd.Parameters.Add("Id",     NpgsqlTypes.NpgsqlDbType.Varchar);
            var pName   = cmd.Parameters.Add("Name",   NpgsqlTypes.NpgsqlDbType.Varchar);
            var pSet    = cmd.Parameters.Add("Set",    NpgsqlTypes.NpgsqlDbType.Varchar);
            var pRarity = cmd.Parameters.Add("Rarity", NpgsqlTypes.NpgsqlDbType.Varchar);
            var pFoil   = cmd.Parameters.Add("Foil",   NpgsqlTypes.NpgsqlDbType.Boolean);
            var pPrice  = cmd.Parameters.Add("Price",  NpgsqlTypes.NpgsqlDbType.Numeric);

            await cmd.PrepareAsync(ct); // prepare once, execute 40k times

            int processed = 0;
            foreach (var (id, def) in definitions)
            {
                pId.Value     = id;
                pName.Value   = def.CardName;
                pSet.Value    = def.SetCode;
                pRarity.Value = def.Rarity;
                pFoil.Value   = def.IsFoil;
                pPrice.Value  = prices.TryGetValue(id, out var p) ? p : 0m;

                await cmd.ExecuteNonQueryAsync(ct);
                processed++;

                if (processed % 5000 == 0)
                    _logger.LogDebug("Upserted {N} / {Total} cards...", processed, definitions.Count);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Scheduling helper
    // ─────────────────────────────────────────────────────────────────

    private static TimeSpan TimeUntilNextRun()
    {
        var now  = DateTime.Now;
        var next = now.Date.Add(DailyRunTime.ToTimeSpan());
        if (next <= now) next = next.AddDays(1);
        return next - now;
    }
}

internal record CardDefinition(
    string CardId,
    string CardName,
    string SetCode,
    string Rarity,
    bool IsFoil);
