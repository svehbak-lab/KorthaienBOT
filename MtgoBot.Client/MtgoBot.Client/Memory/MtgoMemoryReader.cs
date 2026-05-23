using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Client.Memory;

// ══════════════════════════════════════════════════════════════════
// Win32 P/Invoke declarations
// We use ReadProcessMemory to read MTGO's address space directly.
// MTGOSDK (if available) provides higher-level hooks; this layer
// is the low-level fallback and building block.
// ══════════════════════════════════════════════════════════════════
internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        bool bInheritHandle,
        int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostMessage(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // Access rights
    public const uint PROCESS_VM_READ        = 0x0010;
    public const uint PROCESS_VM_WRITE       = 0x0020;
    public const uint PROCESS_VM_OPERATION   = 0x0008;
    public const uint PROCESS_QUERY_INFO     = 0x0400;

    // Windows messages
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP   = 0x0202;
}

/// <summary>
/// Opens and reads from the MTGO.exe process memory.
/// 
/// ARCHITECTURE NOTE:
/// ------------------
/// Rather than scanning raw memory for every object (fragile),
/// the recommended approach is to hook into the .NET runtime that
/// MTGO itself runs on via MTGOSDK (ClrMD / dnSpy-style reflection).
/// 
/// This class provides:
///   1. Direct ReadProcessMemory helpers (low-level, always available)
///   2. Hooks into MTGOSDK objects via ClrMD (high-level, preferred)
/// 
/// The trade loop uses whichever is available.
/// </summary>
public class MtgoMemoryReader : IDisposable
{
    private readonly ILogger<MtgoMemoryReader> _logger;
    private IntPtr _processHandle = IntPtr.Zero;
    private Process? _mtgoProcess;
    private bool _disposed;

    public bool IsAttached => _processHandle != IntPtr.Zero;

    public MtgoMemoryReader(ILogger<MtgoMemoryReader> logger)
    {
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────
    // Process attachment
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the running MTGO.exe process and opens a handle to it.
    /// Throws if MTGO is not running.
    /// </summary>
    public void Attach()
    {
        var processes = Process.GetProcessesByName("MTGO");
        if (processes.Length == 0)
            throw new InvalidOperationException(
                "MTGO.exe is not running. Start the client first.");

        _mtgoProcess = processes[0];
        _processHandle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_READ    |
            NativeMethods.PROCESS_VM_WRITE   |
            NativeMethods.PROCESS_VM_OPERATION |
            NativeMethods.PROCESS_QUERY_INFO,
            false,
            _mtgoProcess.Id);

        if (_processHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Failed to open MTGO process handle. Win32 error: {err}. " +
                "Try running the bot as Administrator.");
        }

        _logger.LogInformation(
            "✅ Attached to MTGO.exe (PID {Pid})", _mtgoProcess.Id);
    }

    public void Detach()
    {
        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }
        _logger.LogInformation("Detached from MTGO.exe.");
    }

    // ─────────────────────────────────────────────────────────────────
    // Raw memory read helpers
    // ─────────────────────────────────────────────────────────────────

    public byte[] ReadBytes(IntPtr address, int count)
    {
        var buffer = new byte[count];
        NativeMethods.ReadProcessMemory(_processHandle, address, buffer, count, out _);
        return buffer;
    }

    public int ReadInt32(IntPtr address)
        => BitConverter.ToInt32(ReadBytes(address, 4));

    public long ReadInt64(IntPtr address)
        => BitConverter.ToInt64(ReadBytes(address, 8));

    public float ReadFloat(IntPtr address)
        => BitConverter.ToSingle(ReadBytes(address, 4));

    public double ReadDouble(IntPtr address)
        => BitConverter.ToDouble(ReadBytes(address, 8));

    /// <summary>Read a null-terminated UTF-16 string from MTGO memory.</summary>
    public string ReadUnicodeString(IntPtr address, int maxLength = 256)
    {
        var bytes = ReadBytes(address, maxLength * 2);
        int nullIdx = 0;
        while (nullIdx + 1 < bytes.Length &&
               !(bytes[nullIdx] == 0 && bytes[nullIdx + 1] == 0))
            nullIdx += 2;
        return Encoding.Unicode.GetString(bytes, 0, nullIdx);
    }

    // ─────────────────────────────────────────────────────────────────
    // MTGO Trade Window reading
    //
    // NOTE: The exact memory offsets below are PLACEHOLDER values.
    // Real offsets must be determined by:
    //   a) Using MTGOSDK which exposes typed C# objects via ClrMD, OR
    //   b) Reverse-engineering with dnSpy / Cheat Engine
    //      and updating MtgoOffsets.cs with discovered addresses.
    //
    // The interface contract here will not change — only the offsets.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the complete state of the current trade window.
    /// Returns null if no trade window is open.
    /// </summary>
    public TradeWindowSnapshot? ReadTradeWindow()
    {
        if (!IsAttached)
            throw new InvalidOperationException("Not attached to MTGO.");

        // In production: use MTGOSDK to get the TradeWindow object reference
        // and read .PlayerOffers and .BotOffers from the CLR heap.
        //
        // Stub implementation — replace with real MTGOSDK calls:
        return ReadTradeWindowViaSdk();
    }

    /// <summary>
    /// Preferred path: use MTGOSDK's ClrMD-based runtime inspection
    /// to get strongly-typed trade window data from the .NET heap.
    /// 
    /// MTGOSDK exposes objects like:
    ///   TradeWindow.CurrentTrade.PlayerCollection
    ///   TradeWindow.CurrentTrade.BotCollection
    /// 
    /// Replace the stub body with actual MTGOSDK API calls once the
    /// SDK package is added to the project.
    /// </summary>
    private TradeWindowSnapshot? ReadTradeWindowViaSdk()
    {
        // TODO: Replace with real MTGOSDK integration.
        // Example (pseudocode for when SDK is available):
        //
        //   var sdk   = MtgoSdk.Instance;
        //   var trade = sdk.GetCurrentTrade();
        //   if (trade == null) return null;
        //
        //   return new TradeWindowSnapshot
        //   {
        //       IsOpen       = true,
        //       PlayerName   = trade.OpponentName,
        //       PlayerOffers = trade.PlayerSide.Cards
        //           .Select(c => new OfferedCard(c.CatalogId, c.Name, c.Quantity))
        //           .ToList(),
        //       BotOffers    = trade.BotSide.Cards
        //           .Select(c => new OfferedCard(c.CatalogId, c.Name, c.Quantity))
        //           .ToList(),
        //       BothSubmitted = trade.BothPlayersReady,
        //   };

        _logger.LogDebug("ReadTradeWindow: SDK stub — returning null (no trade open).");
        return null;
    }

    /// <summary>
    /// Simulates clicking the Submit button in the MTGO trade window.
    /// In production: use MTGOSDK's UI automation helpers or
    /// PostMessage WM_LBUTTONDOWN/UP to the correct window handle.
    /// </summary>
    public void ClickSubmit()
    {
        _logger.LogInformation("→ [UI] Clicking Submit");
        // TODO: obtain window handle via FindWindow / EnumChildWindows
        // and PostMessage WM_LBUTTONDOWN to the Submit button rect.
    }

    /// <summary>
    /// Simulates clicking the Accept button.
    /// </summary>
    public void ClickAccept()
    {
        _logger.LogInformation("→ [UI] Clicking Accept");
        // TODO: same pattern as ClickSubmit
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
