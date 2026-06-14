using Npgsql;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MtgoBot.Core.Data;
using Dapper;

namespace MtgoBot.Client.Loop;

/// <summary>
/// Pings the DB every 30 seconds to mark this bot as online (is_online=true, last_seen=NOW()).
/// Dashboard treats a bot as offline if last_seen is older than ~90 seconds, so a crash
/// (no clean shutdown) still flips the indicator to red automatically.
/// </summary>
public class HeartbeatService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly DatabaseConnectionFactory _db;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly string _botId;

    public HeartbeatService(string botId, DatabaseConnectionFactory db, ILogger<HeartbeatService> logger)
    {
        _botId  = botId;
        _db     = db;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("💓 Heartbeat started for {BotId}", _botId);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var conn = (NpgsqlConnection)(await _db.CreateConnectionAsync());
                await conn.ExecuteAsync("""
                    UPDATE bots SET is_online = true, last_seen = NOW()
                    WHERE bot_id = @BotId
                    """, new { BotId = _botId });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Heartbeat failed.");
            }

            try { await Task.Delay(Interval, ct); }
            catch (TaskCanceledException) { break; }
        }

        // Mark offline on clean shutdown
        try
        {
            using var conn = (NpgsqlConnection)(await _db.CreateConnectionAsync());
            await conn.ExecuteAsync(
                "UPDATE bots SET is_online = false WHERE bot_id = @BotId",
                new { BotId = _botId });
            _logger.LogInformation("Heartbeat: marked {BotId} offline on shutdown.", _botId);
        }
        catch { }
    }
}
