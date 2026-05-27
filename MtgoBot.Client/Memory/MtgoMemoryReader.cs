using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;

namespace MtgoBot.Client.Memory;

internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);
    public const uint PROCESS_QUERY_INFO = 0x0400;
}

/// <summary>
/// Reads the MTGO trade window using Windows UI Automation.
/// No MTGOSDK, no bridge, no named pipe — direct WPF accessibility tree.
/// </summary>
public class MtgoMemoryReader : IDisposable
{
    private readonly ILogger<MtgoMemoryReader> _logger;
    private Process? _mtgoProcess;
    private AutomationElement? _mtgoRoot;
    private bool _disposed;

    public bool IsAttached => _mtgoProcess != null && !_mtgoProcess.HasExited;

    public MtgoMemoryReader(ILogger<MtgoMemoryReader> logger)
    {
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────
    // Attach
    // ─────────────────────────────────────────────────────────────────

    public void Attach()
    {
        var processes = Process.GetProcessesByName("MTGO");
        if (processes.Length == 0)
            throw new InvalidOperationException("MTGO.exe is not running.");

        _mtgoProcess = processes[0];

        // Get the root automation element for the MTGO window
        _mtgoRoot = AutomationElement.FromHandle(_mtgoProcess.MainWindowHandle);
        if (_mtgoRoot == null)
            throw new InvalidOperationException("Could not get UI Automation root for MTGO.");

        _logger.LogInformation("✅ Attached to MTGO.exe (PID {Pid}) via UI Automation.", _mtgoProcess.Id);
    }

    public void Detach()
    {
        _mtgoRoot = null;
        _mtgoProcess = null;
        _logger.LogInformation("Detached from MTGO.exe.");
    }

    // ─────────────────────────────────────────────────────────────────
    // Trade window reading
    // ─────────────────────────────────────────────────────────────────

    public TradeWindowSnapshot? ReadTradeWindow()
    {
        if (!IsAttached || _mtgoRoot == null) return null;

        try
        {
            // Find the trade window — name starts with "Trade: "
            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null)
                return null;

            // Get player name from window title ("Trade: playername")
            string windowName = tradeWindow.Current.Name ?? "";
            string playerName = windowName.StartsWith("Trade: ", StringComparison.OrdinalIgnoreCase)
                ? windowName.Substring(7).Trim().ToLowerInvariant()
                : windowName.ToLowerInvariant();

            _logger.LogDebug("Trade window found: [{Name}]", windowName);

            // Find all Custom(50025) elements inside the trade window
            var allItems = tradeWindow.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));

            // Split items into player side and bot side
            // MTGO trade window has two panels — we detect which side by position
            var playerOffers = new List<OfferedCard>();
            var botOffers    = new List<OfferedCard>();

            if (allItems != null)
            {
                // Get window bounds to determine left/right split
                var windowRect = tradeWindow.Current.BoundingRectangle;
                double midX = windowRect.Left + windowRect.Width / 2;

                foreach (AutomationElement item in allItems)
                {
                    try
                    {
                        var rect = item.Current.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width < 10 || rect.Height < 10) continue;

                        string name = item.Current.Name ?? "";
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        // Skip if it looks like a container, not a card
                        if (name.Length < 2) continue;

                        bool isTix = name.Contains("Event Ticket") || name.Contains("Ticket");
                        string cardId = isTix ? "EVENT_TICKET" : name;

                        var card = new OfferedCard(cardId, name, 1);

                        // Cards on the left = player's side, right = bot's side
                        if (rect.Left < midX)
                            playerOffers.Add(card);
                        else
                            botOffers.Add(card);
                    }
                    catch { }
                }
            }

            // Check for Submit/Accept buttons to detect trade state
            bool bothSubmitted = IsTradeSubmitted(tradeWindow);

            _logger.LogDebug("Trade: player={Player} offers={POffers} botOffers={BOffers} submitted={Sub}",
                playerName, playerOffers.Count, botOffers.Count, bothSubmitted);

            return new TradeWindowSnapshot(
                IsOpen:        true,
                PlayerName:    playerName,
                PlayerOffers:  playerOffers,
                BotOffers:     botOffers,
                BothSubmitted: bothSubmitted);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadTradeWindow failed.");
            return null;
        }
    }

    private AutomationElement? FindTradeWindow()
    {
        try
        {
            // Search the entire desktop — trade window may be a separate top-level window
            var searchRoot = AutomationElement.RootElement;
            var allElements = searchRoot.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.IsEnabledProperty, true));

            if (allElements != null)
            {
                foreach (AutomationElement el in allElements)
                {
                    try
                    {
                        string name = el.Current.Name ?? "";
                        if (name.StartsWith("Trade: ", StringComparison.OrdinalIgnoreCase))
                            return el;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FindTradeWindow failed.");
        }
        return null;
    }

    private bool IsTradeSubmitted(AutomationElement tradeWindow)
    {
        try
        {
            // If the Accept button is visible and enabled, both sides have submitted
            var accept = tradeWindow.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Accept"),
                    new PropertyCondition(AutomationElement.IsEnabledProperty, true)));
            return accept != null;
        }
        catch { return false; }
    }

    // ─────────────────────────────────────────────────────────────────
    // UI interactions
    // ─────────────────────────────────────────────────────────────────

    public void AcceptTradeRequest()
    {
        try
        {
            // Search the entire desktop — the trade request dialog is a popup window
            var desktop = AutomationElement.RootElement;

            // First look for a window containing "Trade Request" 
            var tradeRequestWindow = desktop.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "Trade Request"));

            var searchRoot = tradeRequestWindow ?? desktop;

            var btn = searchRoot.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Accept"),
                    new PropertyCondition(AutomationElement.IsEnabledProperty, true)));

            if (btn != null)
            {
                var invoke = btn.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                invoke?.Invoke();
                _logger.LogInformation("✅ Clicked Accept (trade request).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AcceptTradeRequest failed.");
        }
    }

    public void ClickSubmit()
    {
        ClickButton("Submit");
    }

    public void ClickAccept()
    {
        ClickButton("Accept");
    }

    private void ClickButton(string name)
    {
        if (_mtgoRoot == null) return;
        try
        {
            var tradeWindow = FindTradeWindow();
            var root = tradeWindow ?? _mtgoRoot;

            var btn = root.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, name),
                    new PropertyCondition(AutomationElement.IsEnabledProperty, true)));

            if (btn != null)
            {
                var invoke = btn.GetCurrentPattern(InvokePattern.Pattern) as InvokePattern;
                invoke?.Invoke();
                _logger.LogInformation("→ Clicked [{Name}]", name);
            }
            else
            {
                _logger.LogDebug("Button [{Name}] not found.", name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ClickButton({Name}) failed.", name);
        }
    }

    public void SendChatMessage(string message)
    {
        // TODO: find chat input box and send message
        _logger.LogInformation("[CHAT] {Msg}", message);
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

public record OfferedCard(string CardId, string CardName, int Quantity);
