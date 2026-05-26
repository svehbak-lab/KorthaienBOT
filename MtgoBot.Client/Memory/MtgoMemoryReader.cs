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

public class MtgoMemoryReader : IDisposable
{
    private readonly ILogger<MtgoMemoryReader> _logger;
    private IntPtr _processHandle = IntPtr.Zero;
    private Process? _mtgoProcess;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _pipeReader;
    private StreamWriter? _pipeWriter;
    private bool _disposed;

    private const string BridgePipeName     = "KorthaienBotBridge";
    private const string BridgeExePath      = @"C:\KorthaienBOT\publish\bridge\MtgoBot.Bridge.exe";
    private const int    PipeConnectTimeout = 10000;

    public bool IsAttached => _processHandle != IntPtr.Zero;

    public MtgoMemoryReader(ILogger<MtgoMemoryReader> logger) => _logger = logger;

    public void Attach()
    {
        var processes = Process.GetProcessesByName("MTGO");
        if (processes.Length == 0)
            throw new InvalidOperationException("MTGO.exe is not running.");

        _mtgoProcess   = processes[0];
        _processHandle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE |
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_QUERY_INFO,
            false, _mtgoProcess.Id);

        if (_processHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to open MTGO handle. Win32: {Marshal.GetLastWin32Error()}");

        _logger.LogInformation("✅ Attached to MTGO.exe (PID {Pid})", _mtgoProcess.Id);
        StartBridge();
    }

    private void StartBridge()
    {
        try
        {
            var existing = Process.GetProcessesByName("MtgoBot.Bridge");
            if (existing.Length == 0)
            {
                _logger.LogInformation("Starting MtgoBot.Bridge...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = BridgeExePath, UseShellExecute = false, CreateNoWindow = true
                });
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
            _logger.LogWarning(ex, "Failed to start bridge.");
        }
    }

    private void ConnectToPipe()
    {
        try
        {
            _pipe?.Dispose();
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
            _pipeReader = null;
            _pipeWriter = null;
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

    public TradeWindowSnapshot? ReadTradeWindow()
    {
        if (!IsAttached)
            throw new InvalidOperationException("Not attached to MTGO.");

        var response = SendCommand(new { cmd = "READ" });
        if (response == null) return null;

        try
        {
            var result = JsonConvert.DeserializeObject<dynamic>(response);
            if (result?.snapshot == null) return null;

            var snapshot     = result.snapshot;
            var playerOffers = new List<OfferedCard>();
            var botOffers    = new List<OfferedCard>();

            foreach (var c in snapshot.playerOffers ?? new Newtonsoft.Json.Linq.JArray())
                playerOffers.Add(new OfferedCard((string)c.cardId, (string)c.cardName, (int)c.quantity));

            foreach (var c in snapshot.botOffers ?? new Newtonsoft.Json.Linq.JArray())
                botOffers.Add(new OfferedCard((string)c.cardId, (string)c.cardName, (int)c.quantity));

            return new TradeWindowSnapshot(
                IsOpen:        (bool)snapshot.isOpen,
                PlayerName:    (string)snapshot.playerName,
                PlayerOffers:  playerOffers,
                BotOffers:     botOffers,
                BothSubmitted: (bool)snapshot.bothSubmitted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse trade window snapshot.");
            return null;
        }
    }

    public void AcceptTradeRequest()
    {
        var response = SendCommand(new { cmd = "ACCEPT_REQUEST" });
        if (response != null)
        {
            try
            {
                var result = JsonConvert.DeserializeObject<dynamic>(response);
                if (result?.ok == true)
                    _logger.LogInformation("✅ Trade request accepted.");
            }
            catch { }
        }
    }

    public void SendChatMessage(string message) =>
        SendCommand(new { cmd = "CHAT", message });

    public void ClickSubmit() =>
        SendCommand(new { cmd = "SUBMIT" });

    public void ClickAccept() =>
        SendCommand(new { cmd = "ACCEPT" });

    /// <summary>
    /// Sends a command to the bridge and returns the response.
    /// Automatically reconnects if the pipe is broken.
    /// </summary>
    private string? SendCommand(object command)
    {
        // Try to send; if it fails, reconnect and try once more
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (_pipeWriter == null || _pipeReader == null)
                {
                    ConnectToPipe();
                    if (_pipeWriter == null) return null;
                }

                var json = JsonConvert.SerializeObject(command);
                _pipeWriter.WriteLine(json);
                return _pipeReader.ReadLine();
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Pipe send failed (attempt {A}): {E}", attempt + 1, ex.Message);
                // Reset pipe and retry
                _pipeReader = null;
                _pipeWriter = null;
                _pipe?.Dispose();
                _pipe = null;
            }
        }
        return null;
    }

    public byte[] ReadBytes(IntPtr address, int count)
    {
        var buffer = new byte[count];
        NativeMethods.ReadProcessMemory(_processHandle, address, buffer, count, out _);
        return buffer;
    }

    public void Dispose()
    {
        if (!_disposed) { Detach(); _mtgoProcess?.Dispose(); _disposed = true; }
        GC.SuppressFinalize(this);
    }
}

public record TradeWindowSnapshot(
    bool IsOpen, string PlayerName,
    List<OfferedCard> PlayerOffers, List<OfferedCard> BotOffers,
    bool BothSubmitted);

public record OfferedCard(string CardId, string CardName, int Quantity);
