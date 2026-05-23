using MtgoBot.Client.Memory;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace MtgoBot.Client.Loop;

public class MtgoWatchdogService : BackgroundService
{
    private static readonly TimeOnly RestartTime  = new(5, 30, 0);
    private static readonly TimeSpan TradeTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan WatchdogTick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UpdateWaitTime    = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaxUpdateWaitTime = TimeSpan.FromMinutes(15);
    private static readonly int MaxReconnectAttempts   = 5;

    private readonly ILogger<MtgoWatchdogService> _logger;
    private readonly string _mtgoPath;
    private readonly MtgoMemoryReader _reader;

    public DateTime? LastTradeActivity { get; set; }
    public bool TradeTimedOut { get; private set; }
    public event EventHandler? OnTradeTimeout;
    public event EventHandler? OnClientRestarted;

    private bool _intentionalRestart = false;
    private DateTime _lastRestart = DateTime.MinValue;
    private int _reconnectAttempts = 0;

    public MtgoWatchdogService(ILogger<MtgoWatchdogService> logger, IConfiguration config, MtgoMemoryReader reader)
    {
        _logger   = logger;
        _mtgoPath = config["Watchdog:MtgoExePath"] ?? @"C:\MTGO\MTGO.exe";
        _reader   = reader;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("🐕 Watchdog started.");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(WatchdogTick, ct);
            CheckTradeTimeout();
            await CheckNightlyRestartAsync(ct);
            await CheckMtgoAliveAsync(ct);
        }
    }

    private void CheckTradeTimeout()
    {
        if (LastTradeActivity == null) return;
        var idle = DateTime.UtcNow - LastTradeActivity.Value;
        if (idle >= TradeTimeout && !TradeTimedOut)
        {
            TradeTimedOut = true;
            _logger.LogWarning("⏱️ Trade timeout after {Sec}s.", (int)idle.TotalSeconds);
            OnTradeTimeout?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ResetTradeTimeout() { LastTradeActivity = DateTime.UtcNow; TradeTimedOut = false; }
    public void ClearTradeTimeout() { LastTradeActivity = null; TradeTimedOut = false; }

    private async Task CheckMtgoAliveAsync(CancellationToken ct)
    {
        if (_intentionalRestart) return;
        var processes = Process.GetProcessesByName("MTGO");

        if (processes.Length == 0 && _reader.IsAttached)
        {
            _logger.LogWarning("⚠️ MTGO.exe disappeared — likely updating or crashed.");
            _reader.Detach();
            if (_reconnectAttempts >= MaxReconnectAttempts)
            {
                _logger.LogError("❌ Max reconnect attempts reached. Manual intervention required.");
                return;
            }
            await ReconnectAfterUpdateAsync(ct);
        }
        else if (processes.Length > 0 && !_reader.IsAttached)
        {
            _logger.LogInformation("🔄 MTGO detected but not attached — reattaching...");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            TryReattach();
        }
    }

    private async Task ReconnectAfterUpdateAsync(CancellationToken ct)
    {
        _reconnectAttempts++;
        _logger.LogInformation("🔄 Reconnect attempt {N}/{Max} — waiting {Min} min...",
            _reconnectAttempts, MaxReconnectAttempts, UpdateWaitTime.TotalMinutes);

        var deadline = DateTime.UtcNow + MaxUpdateWaitTime;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(UpdateWaitTime, ct);
            var procs = Process.GetProcessesByName("MTGO");
            if (procs.Length > 0)
            {
                _logger.LogInformation("✅ MTGO relaunched after update — waiting for load...");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                TryReattach();
                return;
            }
        }
        _logger.LogInformation("🚀 MTGO didn't relaunch — starting manually...");
        await LaunchMtgoAsync(ct);
    }

    private void TryReattach()
    {
        try
        {
            _reader.Attach();
            _reconnectAttempts = 0;
            _logger.LogInformation("✅ Reattached to MTGO successfully.");
            OnClientRestarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to reattach to MTGO."); }
    }

    private async Task CheckNightlyRestartAsync(CancellationToken ct)
    {
        var now    = DateTime.Now;
        var target = now.Date.Add(RestartTime.ToTimeSpan());
        if (now >= target && now < target.AddMinutes(1) && _lastRestart.Date < now.Date)
        {
            _logger.LogInformation("🔄 Nightly restart at 05:30...");
            _lastRestart = now;
            _intentionalRestart = true;
            await KillMtgoAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            await LaunchMtgoAsync(ct);
            _intentionalRestart = false;
        }
    }

    private async Task KillMtgoAsync(CancellationToken ct)
    {
        foreach (var proc in Process.GetProcessesByName("MTGO"))
        {
            try { proc.Kill(); await proc.WaitForExitAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not kill MTGO."); }
        }
        _reader.Detach();
    }

    private async Task LaunchMtgoAsync(CancellationToken ct)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _mtgoPath, UseShellExecute = true });
            _logger.LogInformation("✅ MTGO.exe launched — waiting 30s...");
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            TryReattach();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to launch MTGO."); }
    }
}
