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

            // Find all Custom elements inside the trade window
            var allItems = tradeWindow.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));

            // Parse card rows from column elements.
            // Each card row has elements with Name like:
            //   "Cardslot: Flamebraider, Column Display Index: 6"  (card name)
            //   "Cardslot: Flamebraider, Column Display Index: 12" (set code)
            // We group by card slot name and extract name (col 6) and set (col 12).

            var playerOffers = new List<OfferedCard>();
            var botOffers    = new List<OfferedCard>();

            if (allItems != null)
            {
                var windowRect = tradeWindow.Current.BoundingRectangle;
                double midX = windowRect.Left + windowRect.Width / 2;

                // Group elements by their slot key (everything before ", Column Display Index:")
                var slotGroups = new Dictionary<string, Dictionary<int, (string text, double x)>>();

                foreach (AutomationElement item in allItems)
                {
                    try
                    {
                        var rect = item.Current.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width < 5 || rect.Height < 5) continue;

                        string name = item.Current.Name ?? "";
                        if (!name.Contains("Column Display Index:")) continue;

                        // Parse: "Cardslot: <cardname>, Column Display Index: <N>"
                        int colIdx = name.LastIndexOf(", Column Display Index:", StringComparison.Ordinal);
                        if (colIdx < 0) continue;

                        string slotPart = name.Substring(0, colIdx).Trim();
                        string colPart  = name.Substring(colIdx + ", Column Display Index:".Length).Trim();

                        if (!int.TryParse(colPart, out int col)) continue;

                        // Extract card name from "Cardslot: <name>"
                        string slotName = slotPart.StartsWith("Cardslot:", StringComparison.OrdinalIgnoreCase)
                            ? slotPart.Substring("Cardslot:".Length).Trim()
                            : slotPart;

                        // Use slot+x position as key to group columns of same row
                        string groupKey = $"{slotName}|{(rect.Left < midX ? "L" : "R")}|{rect.Top:F0}";

                        if (!slotGroups.ContainsKey(groupKey))
                            slotGroups[groupKey] = new Dictionary<int, (string, double)>();

                        slotGroups[groupKey][col] = (slotName, rect.Left);
                    }
                    catch { }
                }

                // Build OfferedCard from each slot group
                foreach (var kvp in slotGroups)
                {
                    try
                    {
                        var cols = kvp.Value;
                        if (cols.Count == 0) continue;

                        // Column 6 = card name, column 12 = set code (from AccessibilityInsights)
                        string cardName = cols.ContainsKey(6)  ? cols[6].text  :
                                          cols.ContainsKey(5)  ? cols[5].text  :
                                          cols.First().Value.text;

                        string setCode  = cols.ContainsKey(12) ? cols[12].text :
                                          cols.ContainsKey(11) ? cols[11].text : "";

                        if (string.IsNullOrWhiteSpace(cardName)) continue;

                        bool isTix = cardName.Contains("Event Ticket") || cardName.Contains("Ticket");
                        // CardId = "name|set" so the trade engine can look up by both
                        string cardId = isTix ? "EVENT_TICKET" : $"{cardName}|{setCode}";

                        double xPos = cols.First().Value.Item2;
                        var card = new OfferedCard(cardId, cardName, 1, setCode);

                        if (xPos < midX)
                            playerOffers.Add(card);
                        else
                            botOffers.Add(card);
                    }
                    catch { }
                }
            }

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

public record OfferedCard(string CardId, string CardName, int Quantity, string SetCode = "");
