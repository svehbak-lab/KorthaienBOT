using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace MtgoBot.Client.Loop;

/// <summary>
/// Watchdog service that handles two critical stability concerns from the Gemini chat:
///
/// 1. NIGHTLY RESTART (05:30 AM)
///    MTGO has severe memory leaks. Without a daily restart, the client will
///    eventually crash mid-trade, potentially locking items in limbo.
///    We kill and restart MTGO.exe at the same time as the price feed runs,
///    so the bot comes back with fresh prices AND a fresh client.
///
/// 2. TRADE TIMEOUT (90 seconds)
///    If a user opens a trade and goes AFK, the bot's trade window stays
///    locked forever. The watchdog fires a cancellation event after 90s
///    of no window changes, allowing the trade loop to clean up gracefully.
/// </summary>
public class MtgoWatchdogService : BackgroundService
{
    private static readonly TimeOnly RestartTime    = new(5, 30, 0);
    private static readonly TimeSpan TradeTimeout   = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan WatchdogTick   = TimeSpan.FromSeconds(5);

    private readonly ILogger<MtgoWatchdogService> _logger;
    private readonly string _mtgoPath;
    private readonly string _mtgoUsername;
    private readonly string _mtgoPassword;

    // Shared state with the trade loop
    public DateTime? LastTradeActivity { get; set; }
    public bool TradeTimedOut { get; private set; }

    public event EventHandler? OnTradeTimeout;
    public event EventHandler? OnClientRestarted;

    public MtgoWatchdogService(ILogger<MtgoWatchdogService> logger, IConfiguration config)
    {
        _logger       = logger;
        _mtgoPath     = config["Watchdog:MtgoExePath"]  ?? @"C:\MTGO\MTGO.exe";
        _mtgoUsername = config["Watchdog:MtgoUsername"] ?? "";
        _mtgoPassword = config["Watchdog:MtgoPassword"] ?? "";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("🐕 Watchdog started.");

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(WatchdogTick, ct);

            CheckTradeTimeout();
            await CheckNightlyRestartAsync(ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Trade timeout: fires if no window change for 90 seconds
    // ─────────────────────────────────────────────────────────────────

    private void CheckTradeTimeout()
    {
        if (LastTradeActivity == null) return;

        var idle = DateTime.UtcNow - LastTradeActivity.Value;
        if (idle >= TradeTimeout && !TradeTimedOut)
        {
            TradeTimedOut = true;
            _logger.LogWarning(
                "⏱️ Trade timeout! No activity for {Sec}s. Firing cancellation.",
                (int)idle.TotalSeconds);
            OnTradeTimeout?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ResetTradeTimeout()
    {
        LastTradeActivity = DateTime.UtcNow;
        TradeTimedOut     = false;
    }

    public void ClearTradeTimeout()
    {
        LastTradeActivity = null;
        TradeTimedOut     = false;
    }

    // ─────────────────────────────────────────────────────────────────
    // Nightly restart at 05:30
    // ─────────────────────────────────────────────────────────────────

    private DateTime _lastRestart = DateTime.MinValue;

    private async Task CheckNightlyRestartAsync(CancellationToken ct)
    {
        var now  = DateTime.Now;
        var target = now.Date.Add(RestartTime.ToTimeSpan());

        // Only fire once per day, within a 1-minute window
        if (now >= target && now < target.AddMinutes(1) && _lastRestart.Date < now.Date)
        {
            _logger.LogInformation("🔄 Nightly MTGO restart initiated...");
            _lastRestart = now;
            await RestartMtgoClientAsync(ct);
        }
    }

    private async Task RestartMtgoClientAsync(CancellationToken ct)
    {
        // 1. Kill existing MTGO processes
        foreach (var proc in Process.GetProcessesByName("MTGO"))
        {
            try
            {
                proc.Kill();
                await proc.WaitForExitAsync(ct);
                _logger.LogInformation("Killed MTGO.exe (PID {Pid})", proc.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not kill MTGO process.");
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(10), ct); // let OS clean up

        // 2. Restart MTGO
        try
        {
            var start = new ProcessStartInfo
            {
                FileName        = _mtgoPath,
                UseShellExecute = true,
                WindowStyle     = ProcessWindowStyle.Normal
            };
            Process.Start(start);
            _logger.LogInformation("✅ MTGO.exe restarted.");

            // 3. Wait for MTGO to fully load before re-attaching
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            OnClientRestarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart MTGO. Manual intervention required.");
        }
    }
}
