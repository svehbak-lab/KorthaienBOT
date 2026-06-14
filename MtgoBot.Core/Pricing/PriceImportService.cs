using System.IO.Compression;
using System.Text.Json;
using Npgsql;
using Dapper;
using Microsoft.Extensions.Logging;
using MtgoBot.Core.Data;

namespace MtgoBot.Core.Pricing;

/// <summary>
/// Downloads the GoatBots price-history ZIP, parses the flat { "CatID": price } JSON,
/// and bulk-updates cards.market_price_tix. Our card_id IS the MTGO CatID, so the
/// match is direct.
/// </summary>
public class PriceImportService
{
    private const string PriceZipUrl = "https://www.goatbots.com/download/prices/price-history.zip";

    private readonly IHttpClientFactory _httpFactory;
    private readonly DatabaseConnectionFactory _db;
    private readonly ILogger<PriceImportService> _logger;

    public PriceImportService(
        IHttpClientFactory httpFactory,
        DatabaseConnectionFactory db,
        ILogger<PriceImportService> logger)
    {
        _httpFactory = httpFactory;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Runs a full price refresh. Returns the number of card prices updated.
    /// </summary>
    public async Task<int> RefreshPricesAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("💲 Price import: downloading {Url}", PriceZipUrl);

        // 1. Download the ZIP into memory.
        var client = _httpFactory.CreateClient("GoatBots");
        client.Timeout = TimeSpan.FromMinutes(2);
        // Some sites reject requests without a User-Agent.
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
            client.DefaultRequestHeaders.Add("User-Agent", "KorthaienBOT/1.0");

        byte[] zipBytes;
        try
        {
            zipBytes = await client.GetByteArrayAsync(PriceZipUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Price import: download failed.");
            throw;
        }

        _logger.LogInformation("Price import: downloaded {Bytes:N0} bytes.", zipBytes.Length);

        // 2. Extract the single price .txt file (dated name, so take the first entry).
        string json;
        using (var ms = new MemoryStream(zipBytes))
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Length > 0);
            if (entry == null)
            {
                _logger.LogError("Price import: ZIP contained no files.");
                return 0;
            }
            _logger.LogInformation("Price import: reading {Name}", entry.FullName);
            using var reader = new StreamReader(entry.Open());
            json = await reader.ReadToEndAsync(ct);
        }

        // 3. Parse the flat { "catid": price } JSON.
        Dictionary<string, decimal>? prices;
        try
        {
            prices = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Price import: JSON parse failed.");
            throw;
        }

        if (prices == null || prices.Count == 0)
        {
            _logger.LogWarning("Price import: no prices parsed.");
            return 0;
        }

        _logger.LogInformation("Price import: parsed {Count:N0} prices. Updating DB...", prices.Count);

        // 4. Bulk update. Use a temp table + UPDATE join for speed over 100k rows.
        using var conn = (NpgsqlConnection)(await _db.CreateConnectionAsync());

        using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.ExecuteAsync(
                "CREATE TEMP TABLE _price_import (card_id VARCHAR(50) PRIMARY KEY, price NUMERIC(10,4)) ON COMMIT DROP",
                transaction: tx);

            // Bulk insert via COPY for speed.
            using (var writer = await conn.BeginBinaryImportAsync(
                "COPY _price_import (card_id, price) FROM STDIN (FORMAT BINARY)", ct))
            {
                foreach (var kvp in prices)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(kvp.Key, NpgsqlTypes.NpgsqlDbType.Varchar, ct);
                    await writer.WriteAsync(kvp.Value, NpgsqlTypes.NpgsqlDbType.Numeric, ct);
                }
                await writer.CompleteAsync(ct);
            }

            int updated = await conn.ExecuteAsync("""
                UPDATE cards c
                SET market_price_tix = p.price
                FROM _price_import p
                WHERE c.card_id = p.card_id
                  AND c.market_price_tix IS DISTINCT FROM p.price
                """, transaction: tx);

            await tx.CommitAsync(ct);
            _logger.LogInformation("✅ Price import: updated {Updated:N0} card prices.", updated);
            return updated;
        }
    }
}
