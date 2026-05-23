# MtgoBot — C# Architecture

## Solution Structure

```
MtgoBot/
├── MtgoBot.sln
│
├── MtgoBot.Core/                  # Shared library (no MTGO deps)
│   ├── Models/Models.cs           # Domain types: Card, TradeBalance, etc.
│   ├── Data/
│   │   ├── DatabaseConnectionFactory.cs   # Npgsql pool + schema DDL
│   │   └── Repositories.cs               # CardRepo, InventoryRepo, CreditRepo
│   └── Trading/
│       └── TradeEngine.cs         # Filter, balance calc, commit logic
│
└── MtgoBot.Client/                # Windows executable (one per VPS)
    ├── Memory/
    │   └── MtgoMemoryReader.cs    # Win32 ReadProcessMemory + MTGOSDK stub
    ├── Chat/
    │   └── TradeChatService.cs    # All in-game chat messages
    ├── Loop/
    │   └── TradeBotLoop.cs        # BackgroundService state machine
    ├── Program.cs                 # DI wiring, startup sequence
    └── appsettings.json           # DB connection string, bot config
```

## Architecture Decisions

| Decision | Choice | Reason |
|---|---|---|
| Database | PostgreSQL | Free, excellent DECIMAL(6,4), handles concurrent bot writers |
| .NET driver | Npgsql + Dapper | Async-first, no ORM overhead in hot trade loop |
| Memory reading | Win32 + MTGOSDK stub | MTGOSDK is the recommended path; raw P/Invoke as fallback |
| Bot process | BackgroundService | Clean lifecycle, Ctrl+C handling, easy to host as Windows Service |
| Logging | Serilog | Structured, per-bot rolling log files |

## Database Connection String

Edit `MtgoBot.Client/appsettings.json`:

```json
"Postgres": "Host=YOUR_VPS_IP;Port=5432;Database=mtgobot;Username=mtgobot;Password=YOUR_PASSWORD"
```

## Running Multiple Bots

Each bot is a separate process on its own VPS, all pointing to the **same** PostgreSQL instance:

```
VPS 1: MtgoBot.Client.exe --bot-id=Bot_1
VPS 2: MtgoBot.Client.exe --bot-id=Bot_2
```

## Next Steps

### 1. MTGOSDK Integration (Priority #1)
Replace the stub in `MtgoMemoryReader.ReadTradeWindowViaSdk()` with real MTGOSDK calls.
MTGOSDK exposes the MTGO .NET runtime via ClrMD — it gives you typed C# objects for:
- `TradeWindow.CurrentTrade`
- `TradeWindow.PlayerCollection`
- `ChatService.SendMessage()`

Add the NuGet package reference once available:
```xml
<PackageReference Include="MTGOSDK" Version="*" />
```

### 2. Price Feed
Implement a background task that:
- Pulls prices from Scryfall / MTGO Traders API on a schedule
- Calls `CardRepository.UpdateMarketPriceAsync()` for each card
- This keeps `market_price_tix` fresh without manual intervention

### 3. Web Dashboard (Step 2)
Build as a separate ASP.NET Core project (`MtgoBot.Dashboard`) referencing `MtgoBot.Core`.
The dashboard connects to the same PostgreSQL and reads inventory/credits in real time.

### 4. Credit Purge Job
Wire `CreditRepository.PurgeInactiveCreditAsync(90)` to a nightly scheduled task
(use `IHostedService` with a timer, or a cron job via Hangfire).

### 5. Windows Service Installation
```powershell
sc create MtgoBotSvc binPath= "C:\Bots\MtgoBot.Client.exe --bot-id=Bot_1"
sc start MtgoBotSvc
```

## Key Business Logic Reference

### Netto Balanse
```
Net = Sum(UserCards × BuyPrice) - Sum(BotCards × SellPrice)
TIX in window = Floor(Net + OldCredit)   [whole numbers only]
Credit saved  = (Net + OldCredit) - TIX in window
```

### Buy Filter
```
effectiveBuyPrice = CustomBuyPrice ?? (MarketPrice × SetBuyMultiplier)
canBuy = Max(0, MaxStock - CurrentStock)
```

### TIX (Event Tickets)
Always valued at exactly 1.0000 TIX — no multipliers applied.
