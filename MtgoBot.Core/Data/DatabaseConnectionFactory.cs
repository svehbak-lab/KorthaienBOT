using System.Data;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Core.Data;

/// <summary>
/// Creates and manages PostgreSQL connections.
/// One instance is registered as a singleton in DI; each repository
/// calls CreateConnection() to get a fresh, pooled connection.
/// Npgsql handles the underlying connection pool automatically.
/// </summary>
public class DatabaseConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseConnectionFactory> _logger;

    public DatabaseConnectionFactory(IConfiguration config, ILogger<DatabaseConnectionFactory> logger)
    {
        _connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "Missing connection string 'Postgres' in appsettings.json");
        _logger = logger;
    }

    /// <summary>Returns an open connection from the Npgsql pool.</summary>
    public async Task<IDbConnection> CreateConnectionAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// Verifies connectivity on startup. Throws if the database is unreachable.
    /// Call this once from Program.cs before starting the trade loop.
    /// </summary>
    public async Task VerifyConnectivityAsync()
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            await cmd.ExecuteScalarAsync();
            _logger.LogInformation("✅ PostgreSQL connection verified.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "❌ Cannot connect to PostgreSQL. Check appsettings.json.");
            throw;
        }
    }
}

/// <summary>
/// Runs the DDL to create all tables if they don't already exist.
/// Safe to call on every startup (uses IF NOT EXISTS).
/// </summary>
public class SchemaInitializer
{
    private readonly DatabaseConnectionFactory _factory;
    private readonly ILogger<SchemaInitializer> _logger;

    public SchemaInitializer(DatabaseConnectionFactory factory, ILogger<SchemaInitializer> logger)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing database schema...");
        await using var conn = (NpgsqlConnection)await _factory.CreateConnectionAsync();
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = SchemaSQL;
        await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation("✅ Schema ready.");
    }

    // ─────────────────────────────────────────────────────────────────
    // Full PostgreSQL schema — mirrors the spec exactly.
    // DECIMAL(6,4) gives us values up to 99.9999 TIX with 4dp precision.
    // ─────────────────────────────────────────────────────────────────
    private const string SchemaSQL = """
        -- 1. Magic sets & per-set pricing rules
        CREATE TABLE IF NOT EXISTS sets (
            set_code                VARCHAR(10)     PRIMARY KEY,
            set_name                VARCHAR(100)    NOT NULL,
            default_buy_multiplier  DECIMAL(3,2)    NOT NULL DEFAULT 0.80,
            default_sell_multiplier DECIMAL(3,2)    NOT NULL DEFAULT 1.00,
            default_max_stock       INT             NOT NULL DEFAULT 8,
            updated_at              TIMESTAMPTZ     NOT NULL DEFAULT NOW()
        );

        -- 2. Master card catalogue
        CREATE TABLE IF NOT EXISTS cards (
            card_id             VARCHAR(50)     PRIMARY KEY,
            card_name           VARCHAR(255)    NOT NULL,
            set_code            VARCHAR(10)     REFERENCES sets(set_code),
            rarity              VARCHAR(20),
            market_price_tix    DECIMAL(10,4)   NOT NULL DEFAULT 0.0000,
            custom_buy_price    DECIMAL(10,4),
            custom_sell_price   DECIMAL(10,4),
            custom_max_stock    INT,
            redeem_reserved     INT             NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_cards_set     ON cards(set_code);
        CREATE INDEX IF NOT EXISTS idx_cards_name    ON cards(card_name);

        -- 3. Per-bot real-time inventory
        CREATE TABLE IF NOT EXISTS bot_inventory (
            bot_id      VARCHAR(50)     NOT NULL,
            card_id     VARCHAR(50)     NOT NULL REFERENCES cards(card_id),
            quantity    INT             NOT NULL DEFAULT 0 CHECK (quantity >= 0),
            PRIMARY KEY (bot_id, card_id)
        );
        CREATE INDEX IF NOT EXISTS idx_inv_bot ON bot_inventory(bot_id);

        -- 4. Player credit balances (shared across all bots)
        CREATE TABLE IF NOT EXISTS users (
            player_name     VARCHAR(100)    PRIMARY KEY,   -- always lowercase
            credit_tix      DECIMAL(10,4)   NOT NULL DEFAULT 0.0000,
            last_trade_at   TIMESTAMPTZ     NOT NULL DEFAULT NOW()
        );

        -- 5. Immutable credit audit trail
        CREATE TABLE IF NOT EXISTS credit_log (
            log_id          BIGSERIAL       PRIMARY KEY,
            player_name     VARCHAR(100)    NOT NULL,
            bot_id          VARCHAR(50)     NOT NULL,
            delta_amount    DECIMAL(10,4)   NOT NULL,
            new_balance     DECIMAL(10,4)   NOT NULL,
            reason          VARCHAR(255),
            timestamp       TIMESTAMPTZ     NOT NULL DEFAULT NOW()
        );
        CREATE INDEX IF NOT EXISTS idx_log_player ON credit_log(player_name);
        CREATE INDEX IF NOT EXISTS idx_log_ts     ON credit_log(timestamp DESC);
        """;
}
