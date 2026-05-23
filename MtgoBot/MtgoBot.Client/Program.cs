using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using MtgoBot.Core.Data;
using MtgoBot.Core.Trading;
using MtgoBot.Client.Memory;
using MtgoBot.Client.Chat;
using MtgoBot.Client.Loop;

// ══════════════════════════════════════════════════════════════════
// Bot entry point
//
// Usage: MtgoBot.Client.exe --bot-id Bot_1
//
// Each physical VPS/MTGO instance runs ONE copy of this executable.
// All bots share the same PostgreSQL database.
// ══════════════════════════════════════════════════════════════════

// Read bot identity from args (default to Bot_1 for dev)
string botId = args.Length > 0 && args[0].StartsWith("--bot-id=")
    ? args[0]["--bot-id=".Length..]
    : "Bot_1";

// ── Serilog: structured logs to console + rolling file ──────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        path:               $"logs/{botId}-.log",
        rollingInterval:    RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate:     "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureServices((ctx, services) =>
    {
        var config = ctx.Configuration;

        // ── Core infrastructure ──────────────────────────────────────
        services.AddSingleton<DatabaseConnectionFactory>();
        services.AddSingleton<SchemaInitializer>();

        // ── Repositories ─────────────────────────────────────────────
        services.AddSingleton<CardRepository>();
        services.AddSingleton<InventoryRepository>();
        services.AddSingleton<CreditRepository>();

        // ── Trade engine & client services ───────────────────────────
        services.AddSingleton<TradeEngine>();
        services.AddSingleton<MtgoMemoryReader>();
        services.AddSingleton<TradeChatService>();

        // ── Bot loop (registered as BackgroundService) ────────────────
        services.AddSingleton<TradeBotLoop>(sp => new TradeBotLoop(
            botId:   botId,
            memory:  sp.GetRequiredService<MtgoMemoryReader>(),
            engine:  sp.GetRequiredService<TradeEngine>(),
            chat:    sp.GetRequiredService<TradeChatService>(),
            cards:   sp.GetRequiredService<CardRepository>(),
            credits: sp.GetRequiredService<CreditRepository>(),
            logger:  sp.GetRequiredService<ILogger<TradeBotLoop>>()));
        services.AddHostedService(sp => sp.GetRequiredService<TradeBotLoop>());
    })
    .Build();

// ── Startup sequence ─────────────────────────────────────────────────
var logger   = host.Services.GetRequiredService<ILogger<Program>>();
var dbVerify = host.Services.GetRequiredService<DatabaseConnectionFactory>();
var schema   = host.Services.GetRequiredService<SchemaInitializer>();
var reader   = host.Services.GetRequiredService<MtgoMemoryReader>();

logger.LogInformation("═══════════════════════════════");
logger.LogInformation("  MTGO Bot [{BotId}] starting  ", botId);
logger.LogInformation("═══════════════════════════════");

// 1. Verify DB connectivity
await dbVerify.VerifyConnectivityAsync();

// 2. Ensure schema is up to date
await schema.InitializeAsync();

// 3. Attach to MTGO.exe
reader.Attach();

// 4. Run until Ctrl+C
try
{
    await host.RunAsync();
}
finally
{
    reader.Detach();
    Log.CloseAndFlush();
}
