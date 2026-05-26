using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace MtgoBot.Client.Memory;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint PROCESS_VM_READ      = 0x0010;
    public const uint PROCESS_VM_WRITE     = 0x0020;
    public const uint PROCESS_VM_OPERATION = 0x0008;
    public const uint PROCESS_QUERY_INFO   = 0x0400;
}

/// <summary>
/// Reads MTGO trade window state by communicating with MtgoBot.Bridge,
/// a companion net48 process that wraps MTGOSDK.
///
/// The bridge runs as a separate process and exposes a named pipe
/// "KorthaienBotBridge". This class connects to that pipe and sends
/// simple JSON commands to read trade state and control the UI.
/// </summary>
public class MtgoMemoryReader : IDisposable
{
    private readonly ILogger<MtgoMemoryReader> _logger;
    private IntPtr _processHandle = IntPtr.Zero;
    private Process? _mtgoProcess;
    private Process? _bridgeProcess;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _pipeReader;
    private StreamWriter? _pipeWriter;
    private bool _disposed;

    private const string BridgePipeName    = "KorthaienBotBridge";
    private const string BridgeExePath     = @"C:\KorthaienBOT\publish\bridge\MtgoBot.Bridge.exe";
    private const int    PipeConnectTimeout = 10000; // 10 seconds

    public bool IsAttached => _processHandle != IntPtr.Zero;

    public MtgoMemoryReader(ILogger<MtgoMemoryReader> logger)
    {
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────
    // Attach — find MTGO and start/connect to the bridge
    // ─────────────────────────────────────────────────────────────────

    public void Attach()
    {
        var processes = Process.GetProcessesByName("MTGO");
        if (processes.Length == 0)
            throw new InvalidOperationException("MTGO.exe is not running. Start the client first.");

        _mtgoProcess = processes[0];

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

        _logger.LogInformation("✅ Attached to MTGO.exe (PID {Pid})", _mtgoProcess.Id);

        // Start the bridge process and connect to its pipe
        StartBridge();
    }

    private void StartBridge()
    {
        try
        {
            // Check if bridge is already running
            var existing = Process.GetProcessesByName("MtgoBot.Bridge");
            if (existing.Length == 0)
            {
                _logger.LogInformation("Starting MtgoBot.Bridge...");
                _bridgeProcess = Process.Start(new ProcessStartInfo
                {
                    FileName        = BridgeExePath,
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });
                // Give bridge time to start up and connect to MTGO
                System.Threading.Thread.Sleep(3000);
            }
            else
            {
                _logger.LogInformation("MtgoBot.Bridge already running (PID {Pid})", existing[0].Id);
            }

            ConnectToPipe();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start bridge — running in stub mode.");
        }
    }

    private void ConnectToPipe()
    {
        try
        {
            _pipe = new NamedPipeClientStream(".", BridgePipeName, PipeDirection.InOut);
            _pipe.Connect(PipeConnectTimeout);
            _pipeReader = new StreamReader(_pipe, Encoding.UTF8);
            _pipeWriter = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };
            _logger.LogInformation("✅ Connected to MtgoBot.Bridge pipe.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to bridge pipe — running in stub mode.");
            _pipe = null;
        }
    }

    public void Detach()
    {
        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        _pipeReader?.Dispose();
        _pipeWriter?.Dispose();
        _pipe?.Dispose();
        _pipe = null;

        _logger.LogInformation("Detached from MTGO.exe.");
    }

    // ─────────────────────────────────────────────────────────────────
    // Trade window reading
    // ─────────────────────────────────────────────────────────────────

    public TradeWindowSnapshot? ReadTradeWindow()
    {
        if (!IsAttached)
            throw new InvalidOperationException("Not attached to MTGO.");

        if (_pipe == null || !_pipe.IsConnected)
        {
            _logger.LogDebug("Bridge not connected — returning null.");
            TryReconnectPipe();
            return null;
        }

        try
        {
            var response = SendCommand(new { cmd = "READ" });
            if (response == null) return null;

            var result = JsonConvert.DeserializeObject<dynamic>(response);
            if (result?.snapshot == null) return null;

            var snapshot = result.snapshot;
            var playerOffers = new List<OfferedCard>();
            var botOffers    = new List<OfferedCard>();

            foreach (var c in snapshot.playerOffers ?? new Newtonsoft.Json.Linq.JArray())
                playerOffers.Add(new OfferedCard(
                    (string)c.cardId, (string)c.cardName, (int)c.quantity));

            foreach (var c in snapshot.botOffers ?? new Newtonsoft.Json.Linq.JArray())
                botOffers.Add(new OfferedCard(
                    (string)c.cardId, (string)c.cardName, (int)c.quantity));

            return new TradeWindowSnapshot(
                IsOpen:        (bool)snapshot.isOpen,
                PlayerName:    (string)snapshot.playerName,
                PlayerOffers:  playerOffers,
                BotOffers:     botOffers,
                BothSubmitted: (bool)snapshot.bothSubmitted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read trade window from bridge.");
            _pipe = null;
            return null;
        }
    }

    public void SendChatMessage(string message)
    {
        if (_pipe == null || !_pipe.IsConnected) return;
        try
        {
            SendCommand(new { cmd = "CHAT", message });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send chat message.");
        }
    }

    public void ClickSubmit()
    {
        _logger.LogInformation("→ [UI] Clicking Submit");
        if (_pipe == null || !_pipe.IsConnected) return;
        try { SendCommand(new { cmd = "SUBMIT" }); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to click Submit."); }
    }

    public void ClickAccept()
    {
        _logger.LogInformation("→ [UI] Clicking Accept");
        if (_pipe == null || !_pipe.IsConnected) return;
        try { SendCommand(new { cmd = "ACCEPT" }); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to click Accept."); }
    }

    // ─────────────────────────────────────────────────────────────────
    // Pipe communication
    // ─────────────────────────────────────────────────────────────────

    private string? SendCommand(object command)
    {
        if (_pipeWriter == null || _pipeReader == null) return null;
        var json = JsonConvert.SerializeObject(command);
        _pipeWriter.WriteLine(json);
        return _pipeReader.ReadLine();
    }

    private void TryReconnectPipe()
    {
        try
        {
            _pipe?.Dispose();
            _pipe = null;
            ConnectToPipe();
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────
    // Raw memory helpers
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
