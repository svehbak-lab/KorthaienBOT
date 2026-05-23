using System.Data;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Core.Data;

public class DatabaseConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseConnectionFactory> _logger;

    public DatabaseConnectionFactory(IConfiguration config, ILogger<DatabaseConnectionFactory> logger)
    {
        _connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("Missing connection string 'Postgres'.");
        _logger = logger;
    }

    public async Task<IDbConnection> CreateConnectionAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

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
            _logger.LogCritical(ex, "❌ Cannot connect to PostgreSQL.");
            throw;
        }
    }
}

public class SchemaInitializer
{
    private readonly DatabaseConnectionFactory _factory;
    private readonly ILogger<SchemaInitializer> _logger;

    public SchemaInitializer(DatabaseConnectionFactory factory, ILogger<SchemaInitializer> logger)
    {
        _factory = factory;
        _logger  = logger;
    }

    public async Task InitializeAsync()
    {
        _logger.LogInformation("Initializing database schema...");
        await using var conn = (NpgsqlConnection)await _factory.CreateConnectionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = SchemaSQL;
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("✅ Schema ready.");
    }

    private const string SchemaSQL = """
        -- 1. Magic sets & per-set pricing rules
        CREATE TABLE IF NOT EXISTS sets (
            set_code                VARCHAR(10)  PRIMARY KEY,
            set_name                VARCHAR(100) NOT NULL,
            default_buy_multiplier  DECIMAL(4,3) NOT NULL DEFAULT 0.800,
            default_sell_multiplier DECIMAL(4,3) NOT NULL DEFAULT 1.000,
            default_max_stock       INT          NOT NULL DEFAULT 8,
            updated_at              TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );

        -- 2. Master card catalogue (includes foil flag)
        CREATE TABLE IF NOT EXISTS cards (
            card_id             VARCHAR(50)  PRIMARY KEY,
            card_name           VARCHAR(255) NOT NULL,
            set_code            VARCHAR(10)  REFERENCES sets(set_code),
            rarity              VARCHAR(20),
            is_foil             BOOLEAN      NOT NULL DEFAULT FALSE,
            market_price_tix    DECIMAL(10,4) NOT NULL DEFAULT 0.0000,
            custom_buy_price    DECIMAL(10,4),
            custom_sell_price   DECIMAL(10,4),
            custom_max_stock    INT,
            redeem_reserved     INT          NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_cards_set    ON cards(set_code);
        CREATE INDEX IF NOT EXISTS idx_cards_name   ON cards(card_name);
        CREATE INDEX IF NOT EXISTS idx_cards_foil   ON cards(is_foil);

        -- 3. Bot registry (TRADE vs MULE)
        CREATE TABLE IF NOT EXISTS bots (
            bot_id       VARCHAR(50)  PRIMARY KEY,
            account_name VARCHAR(100) NOT NULL,
            bot_type     VARCHAR(10)  NOT NULL DEFAULT 'TRADE', -- TRADE | MULE
            is_online    BOOLEAN      NOT NULL DEFAULT FALSE,
            last_seen    TIMESTAMPTZ  NOT NULL DEFAULT NOW()
        );

        -- 4. Per-bot real-time inventory
        CREATE TABLE IF NOT EXISTS bot_inventory (
            bot_id      VARCHAR(50)  NOT NULL REFERENCES bots(bot_id),
            card_id     VARCHAR(50)  NOT NULL REFERENCES cards(card_id),
            quantity    INT          NOT NULL DEFAULT 0 CHECK (quantity >= 0),
            PRIMARY KEY (bot_id, card_id)
        );
        CREATE INDEX IF NOT EXISTS idx_inv_bot  ON bot_inventory(bot_id);
        CREATE INDEX IF NOT EXISTS idx_inv_card ON bot_inventory(card_id);

        -- 5. Mule transfer queue (orders to move redeem-reserved cards)
        CREATE TABLE IF NOT EXISTS mule_transfer_queue (
            transfer_id  BIGSERIAL    PRIMARY KEY,
            from_bot_id  VARCHAR(50)  NOT NULL,
            to_bot_id    VARCHAR(50)  NOT NULL,
            card_id      VARCHAR(50)  NOT NULL REFERENCES cards(card_id),
            quantity     INT          NOT NULL,
            status       VARCHAR(20)  NOT NULL DEFAULT 'PENDING', -- PENDING | IN_PROGRESS | DONE
            created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
            completed_at TIMESTAMPTZ
        );
        CREATE INDEX IF NOT EXISTS idx_transfer_status ON mule_transfer_queue(status);

        -- 6. Player credit balances (shared across all bots)
        CREATE TABLE IF NOT EXISTS users (
            player_name   VARCHAR(100) PRIMARY KEY,
            credit_tix    DECIMAL(10,4) NOT NULL DEFAULT 0.0000,
            last_trade_at TIMESTAMPTZ   NOT NULL DEFAULT NOW()
        );

        -- 7. Credit audit log
        CREATE TABLE IF NOT EXISTS credit_log (
            log_id       BIGSERIAL     PRIMARY KEY,
            player_name  VARCHAR(100)  NOT NULL,
            bot_id       VARCHAR(50)   NOT NULL,
            delta_amount DECIMAL(10,4) NOT NULL,
            new_balance  DECIMAL(10,4) NOT NULL,
            reason       VARCHAR(255),
            timestamp    TIMESTAMPTZ   NOT NULL DEFAULT NOW()
        );
        CREATE INDEX IF NOT EXISTS idx_log_player ON credit_log(player_name);
        CREATE INDEX IF NOT EXISTS idx_log_ts     ON credit_log(timestamp DESC);

        -- 8. Completed trade log (full audit trail)
        CREATE TABLE IF NOT EXISTS trade_log (
            trade_id        BIGSERIAL     PRIMARY KEY,
            bot_id          VARCHAR(50)   NOT NULL,
            player_name     VARCHAR(100)  NOT NULL,
            value_user_gave DECIMAL(10,4) NOT NULL,
            value_bot_gave  DECIMAL(10,4) NOT NULL,
            tix_in_window   DECIMAL(10,4) NOT NULL DEFAULT 0,
            credit_applied  DECIMAL(10,4) NOT NULL DEFAULT 0,
            credit_saved    DECIMAL(10,4) NOT NULL DEFAULT 0,
            completed_at    TIMESTAMPTZ   NOT NULL DEFAULT NOW()
        );
        CREATE INDEX IF NOT EXISTS idx_trade_player ON trade_log(player_name);
        CREATE INDEX IF NOT EXISTS idx_trade_bot    ON trade_log(bot_id);
        """;
}
