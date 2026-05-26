using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using MTGOSDK.API.Trade;
using MTGOSDK.API.Collection;

namespace MtgoBot.Client.Memory;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const uint PROCESS_VM_READ      = 0x0010;
    public const uint PROCESS_VM_WRITE     = 0x0020;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_QUERY_INFO   = 0x0400;
    public const uint WM_LBUTTONDOWN       = 0x0201;
    public const uint WM_LBUTTONUP         = 0x0202;
}

/// <summary>
/// Reads and interacts with the MTGO trade window via MTGOSDK.
///
/// MTGOSDK uses ClrMD to inspect the MTGO .NET runtime directly,
/// giving us typed C# objects for TradeWindow, cards, and chat.
///
/// The SDK must be initialized before MTGO starts a trade.
/// Call Attach() once at startup; it connects to the running MTGO process.
/// </summary>
public class MtgoMemoryReader : IDisposable
{
    private readonly ILogger<MtgoMemoryReader> _logger;
    private IntPtr _processHandle = IntPtr.Zero;
    private Process? _mtgoProcess;
    private bool _disposed;
    private bool _sdkAttached;

    public bool IsAttached => _processHandle != IntPtr.Zero;

    public MtgoMemoryReader(ILogger<MtgoMemoryReader> logger)
    {
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────
    // Process attachment
    // ─────────────────────────────────────────────────────────────────

    public void Attach()
    {
        var processes = Process.GetProcessesByName("MTGO");
        if (processes.Length == 0)
            throw new InvalidOperationException("MTGO.exe is not running. Start the client first.");

        _mtgoProcess = processes[0];

        // Win32 handle — still needed for IsAttached / Watchdog checks
        _processHandle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_READ    |
            NativeMethods.PROCESS_VM_WRITE   |
            NativeMethods.PROCESS_VM_OPERATION |
            NativeMethods.PROCESS_QUERY_INFO,
            false, _mtgoProcess.Id);

        if (_processHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Failed to open MTGO process handle. Win32 error: {err}. Try running as Administrator.");
        }

        // Initialize MTGOSDK — connects to the MTGO .NET runtime via ClrMD
        try
        {
            // MTGOSDK auto-discovers the MTGO process via ClrMD
            // No explicit initialization needed — TradeManager is static
            // and connects lazily on first access.
            _sdkAttached = true;
            _logger.LogInformation("✅ Attached to MTGO.exe (PID {Pid}) — MTGOSDK ready.", _mtgoProcess.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MTGOSDK initialization failed — falling back to stub mode.");
            _sdkAttached = false;
        }
    }

    public void Detach()
    {
        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
        _sdkAttached = false;
        _logger.LogInformation("Detached from MTGO.exe.");
    }

    // ─────────────────────────────────────────────────────────────────
    // Trade window reading via MTGOSDK
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the complete state of the current trade window.
    /// Returns null if no trade window is open.
    /// Uses MTGOSDK.API.Trade.TradeManager.CurrentTrade.
    /// </summary>
    public TradeWindowSnapshot? ReadTradeWindow()
    {
        if (!IsAttached)
            throw new InvalidOperationException("Not attached to MTGO.");

        if (!_sdkAttached)
        {
            _logger.LogDebug("SDK not attached — returning null.");
            return null;
        }

        try
        {
            // Get the current active trade via MTGOSDK
            var trade = TradeManager.CurrentTrade;
            if (trade == null)
                return null;

            // Read opponent name
            string playerName = trade.TradePartner?.Poster?.Name ?? "unknown";

            // Read cards the player is offering (their side of the window)
            var playerOffers = ReadItemCollection(trade.PartnerTradedItems);

            // Read cards the bot is offering (bot's side of the window)
            var botOffers = ReadItemCollection(trade.TradedItems);

            // Check if both sides have submitted
            bool bothSubmitted = trade.State == MTGOSDK.API.Trade.Enums.TradeState.BothConfirmed
                              || trade.IsAccepted;

            _logger.LogDebug(
                "Trade window: [{Player}] offering {PCount} cards, bot offering {BCount} cards, state={State}",
                playerName, playerOffers.Count, botOffers.Count, trade.State);

            return new TradeWindowSnapshot(
                IsOpen:        true,
                PlayerName:    playerName,
                PlayerOffers:  playerOffers,
                BotOffers:     botOffers,
                BothSubmitted: bothSubmitted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MTGOSDK trade read failed — returning null.");
            return null;
        }
    }

    private List<OfferedCard> ReadItemCollection(MTGOSDK.API.Collection.ItemCollection? collection)
    {
        var result = new List<OfferedCard>();
        if (collection == null) return result;

        try
        {
            foreach (var item in collection)
            {
                try
                {
                    // CollectionItem exposes Id, Name, IsCard, IsTicket
                    string cardId   = item.Id.ToString();
                    string cardName = item.Name ?? "Unknown";

                    // For quantity we need CardQuantityPair — check if collection
                    // exposes quantity directly or if we need a different approach
                    int quantity = 1;

                    // TIX (Event Tickets) have a special ID in MTGO
                    if (item.IsTicket)
                        cardId = "EVENT_TICKET";

                    result.Add(new OfferedCard(cardId, cardName, quantity));
                }
                catch { /* Skip malformed items */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read item collection.");
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────
    // Chat message sending via MTGOSDK
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a message in the trade window chat.
    /// Uses MTGOSDK.API.Chat if available.
    /// </summary>
    public void SendChatMessage(string message)
    {
        if (!_sdkAttached) return;

        try
        {
            // MTGOSDK Chat API — channel name for trade chat is typically
            // the opponent's username or a trade-specific channel
            // TODO: verify correct channel name with MTGOSDK Chat API
            _logger.LogInformation("[CHAT] {Msg}", message);

            // Placeholder — wire to MTGOSDK.API.Chat.ChatManager once
            // we confirm the correct channel reference from the trade object
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send chat message via MTGOSDK.");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // UI interactions — Submit and Accept buttons
    // These use PostMessage since MTGOSDK doesn't expose UI actions
    // ─────────────────────────────────────────────────────────────────

    public void ClickSubmit()
    {
        _logger.LogInformation("→ [UI] Clicking Submit");
        // TODO: find Submit button handle via EnumChildWindows and PostMessage
        // This will be calibrated once we have a live trade window to inspect
    }

    public void ClickAccept()
    {
        _logger.LogInformation("→ [UI] Clicking Accept");
        // TODO: same as ClickSubmit
    }

    // ─────────────────────────────────────────────────────────────────
    // Raw memory helpers (kept for diagnostics)
    // ─────────────────────────────────────────────────────────────────

    public byte[] ReadBytes(IntPtr address, int count)
    {
        var buffer = new byte[count];
        NativeMethods.ReadProcessMemory(_processHandle, address, buffer, count, out _);
        return buffer;
    }

    public int    ReadInt32(IntPtr address)  => BitConverter.ToInt32(ReadBytes(address, 4), 0);
    public long   ReadInt64(IntPtr address)  => BitConverter.ToInt64(ReadBytes(address, 8), 0);
    public float  ReadFloat(IntPtr address)  => BitConverter.ToSingle(ReadBytes(address, 4), 0);
    public double ReadDouble(IntPtr address) => BitConverter.ToDouble(ReadBytes(address, 8), 0);

    public string ReadUnicodeString(IntPtr address, int maxLength = 256)
    {
        var bytes = ReadBytes(address, maxLength * 2);
        int nullIdx = 0;
        while (nullIdx + 1 < bytes.Length && !(bytes[nullIdx] == 0 && bytes[nullIdx + 1] == 0))
            nullIdx += 2;
        return Encoding.Unicode.GetString(bytes, 0, nullIdx);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Detach();
            _mtgoProcess?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

// ─────────────────────────────────────────────────────────────────
// Data structures returned by the memory reader
// ─────────────────────────────────────────────────────────────────

public record TradeWindowSnapshot(
    bool IsOpen,
    string PlayerName,
    List<OfferedCard> PlayerOffers,
    List<OfferedCard> BotOffers,
    bool BothSubmitted);

public record OfferedCard(
    string CardId,
    string CardName,
    int Quantity);
