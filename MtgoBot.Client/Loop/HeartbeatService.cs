using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using MtgoBot.Core.Data;
using Dapper;

namespace MtgoBot.Client.Loop;

/// <summary>
/// Pings the DB every 60 seconds to mark this bot as online.
/// Dashboard reads last_seen to determine online/offline status.
/// Bot is considered offline if last_seen > 2 minutes ago.
/// </summary>
public class HeartbeatService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(2);

    private readonly DatabaseConnectionFactory _db;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly string _botId;

    public HeartbeatService(
        DatabaseConnectionFactory db,
        ILogger<HeartbeatService> logger,
        IConfiguration config)
    {
        _db    = db;
        _logger = logger;
        _botId  = config["BotSettings:BotId"] ?? "Bot_1";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("💓 Heartbeat started for {BotId}", _botId);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var conn = (Npgsql.NpgsqlConnection)_db.CreateConnectionAsync().Result;
                await conn.ExecuteAsync("""
                    UPDATE bots SET is_online = true, last_seen = NOW()
                    WHERE bot_id = @BotId
                    """, new { BotId = _botId });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed.");
            }
            await Task.Delay(Interval, ct);
        }

        // Mark offline on shutdown
        try
        {
            using var conn = (Npgsql.NpgsqlConnection)_db.CreateConnectionAsync().Result;
            await conn.ExecuteAsync(
                "UPDATE bots SET is_online = false WHERE bot_id = @BotId",
                new { BotId = _botId });
        }
        catch { }
    }
}
