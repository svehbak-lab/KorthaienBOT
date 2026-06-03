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
            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null)
                return null;

            string windowName = tradeWindow.Current.Name ?? "";
            string playerName = windowName.StartsWith("Trade: ", StringComparison.OrdinalIgnoreCase)
                ? windowName.Substring(7).Trim().ToLowerInvariant()
                : windowName.ToLowerInvariant();

            _logger.LogDebug("Trade window found: [{Name}]", windowName);

            var allItems = tradeWindow.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));

            var playerOffers = new List<OfferedCard>();
            var botOffers    = new List<OfferedCard>();

            if (allItems != null)
            {
                var windowRect = tradeWindow.Current.BoundingRectangle;
                double midX = windowRect.Left + windowRect.Width / 2;

                var slotGroups = new Dictionary<string, Dictionary<int, (string text, double x)>>();

                foreach (AutomationElement item in allItems)
                {
                    try
                    {
                        var rect = item.Current.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width < 5 || rect.Height < 5) continue;

                        string name = item.Current.Name ?? "";
                        if (!name.Contains("Column Display Index:")) continue;

                        int colIdx = name.LastIndexOf(", Column Display Index:", StringComparison.Ordinal);
                        if (colIdx < 0) continue;

                        string slotPart = name.Substring(0, colIdx).Trim();
                        string colPart  = name.Substring(colIdx + ", Column Display Index:".Length).Trim();

                        if (!int.TryParse(colPart, out int col)) continue;

                        string slotName = slotPart.StartsWith("Cardslot:", StringComparison.OrdinalIgnoreCase)
                            ? slotPart.Substring("Cardslot:".Length).Trim()
                            : slotPart;

                        string groupKey = $"{slotName}|{(rect.Left < midX ? "L" : "R")}|{rect.Top:F0}";

                        if (!slotGroups.ContainsKey(groupKey))
                            slotGroups[groupKey] = new Dictionary<int, (string, double)>();

                        slotGroups[groupKey][col] = (slotName, rect.Left);
                    }
                    catch { }
                }

                foreach (var kvp in slotGroups)
                {
                    try
                    {
                        var cols = kvp.Value;
                        if (cols.Count == 0) continue;

                        string cardName = cols.ContainsKey(6)  ? cols[6].text  :
                                          cols.ContainsKey(5)  ? cols[5].text  :
                                          cols.First().Value.text;

                        string setCode  = cols.ContainsKey(12) ? cols[12].text :
                                          cols.ContainsKey(11) ? cols[11].text : "";

                        if (string.IsNullOrWhiteSpace(cardName)) continue;

                        bool isTix = cardName.Contains("Event Ticket") || cardName.Contains("Ticket");
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
    // Binder search box
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Types text into the binder search box (AutomationId: 50004).
    /// Clears existing text first.
    /// </summary>
    public bool TypeInSearchBox(string text)
    {
        try
        {
            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null) return false;

            // Find the search/filter Edit box in the binder area
            var searchBox = tradeWindow.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true)));

            if (searchBox == null)
            {
                _logger.LogDebug("Search box (50004) not found.");
                return false;
            }

            // Focus and clear
            searchBox.SetFocus();
            Thread.Sleep(100);

            if (searchBox.TryGetCurrentPattern(ValuePattern.Pattern, out var patternObj)
                && patternObj is ValuePattern valuePattern)
            {
                valuePattern.SetValue(text);
                _logger.LogDebug("Search box set to: {Text}", text);
                Thread.Sleep(300); // wait for results to filter
                return true;
            }

            // Fallback: use keyboard
            searchBox.SetFocus();
            Thread.Sleep(100);
            System.Windows.Forms.SendKeys.SendWait("^a"); // select all
            System.Windows.Forms.SendKeys.SendWait("{DELETE}");
            System.Windows.Forms.SendKeys.SendWait(text);
            Thread.Sleep(300);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TypeInSearchBox failed.");
            return false;
        }
    }

    /// <summary>Clears the binder search box.</summary>
    public void ClearSearchBox()
    {
        TypeInSearchBox("");
    }

    // ─────────────────────────────────────────────────────────────────
    // Adding items to bot's side of trade window
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds TIX to the bot's side of the trade window by:
    /// 1. Typing "Event Ticket" in the search box
    /// 2. Double-clicking the first TIX result
    /// 3. Repeating for each TIX needed
    /// </summary>
    public bool AddTixToBotSide(int quantity)
    {
        if (quantity <= 0) return true;

        try
        {
            _logger.LogInformation("Adding {Qty} TIX to bot's side...", quantity);

            // Search for Event Ticket in binder
            if (!TypeInSearchBox("Event Ticket"))
                return false;

            Thread.Sleep(400);

            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null) return false;

            for (int i = 0; i < quantity; i++)
            {
                var tixElement = FindCardInBinder(tradeWindow, "Event Ticket");
                if (tixElement == null)
                {
                    _logger.LogWarning("TIX element not found in binder at slot {I}.", i);
                    break;
                }

                DoubleClickElement(tixElement);
                Thread.Sleep(200);
            }

            ClearSearchBox();
            _logger.LogInformation("✅ Added {Qty} TIX to bot's side.", quantity);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AddTixToBotSide failed.");
            return false;
        }
    }

    /// <summary>
    /// Adds a specific card from the customer's binder to the bot's side.
    /// Searches by card name, double-clicks first match.
    /// </summary>
    public bool AddCardToBotSide(string cardName, int quantity = 1)
    {
        if (quantity <= 0) return true;

        try
        {
            _logger.LogInformation("Adding {Qty}x {Card} to bot's side...", quantity, cardName);

            if (!TypeInSearchBox(cardName))
                return false;

            Thread.Sleep(400);

            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null) return false;

            for (int i = 0; i < quantity; i++)
            {
                var cardElement = FindCardInBinder(tradeWindow, cardName);
                if (cardElement == null)
                {
                    _logger.LogWarning("Card {Card} not found in binder at slot {I}.", cardName, i);
                    break;
                }

                DoubleClickElement(cardElement);
                Thread.Sleep(200);
            }

            ClearSearchBox();
            _logger.LogInformation("✅ Added {Qty}x {Card} to bot's side.", quantity, cardName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AddCardToBotSide({Card}) failed.", cardName);
            return false;
        }
    }

    /// <summary>
    /// Finds a card element in the binder (top area of trade window) by name.
    /// Looks for "CardSlot: {cardName}" Custom elements.
    /// </summary>
    private AutomationElement? FindCardInBinder(AutomationElement tradeWindow, string cardName)
    {
        try
        {
            var allCustom = tradeWindow.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));

            if (allCustom == null) return null;

            // Binder area: top portion of trade window (above the "You Will Receive" panels)
            // Based on observed BoundingRectangle: binder is at t=80, b=720
            var windowRect = tradeWindow.Current.BoundingRectangle;
            double binderBottom = windowRect.Top + (windowRect.Height * 0.65); // approx top 65%

            foreach (AutomationElement el in allCustom)
            {
                try
                {
                    var rect = el.Current.BoundingRectangle;
                    if (rect.IsEmpty) continue;

                    // Must be in binder area (top portion)
                    if (rect.Top > binderBottom) continue;

                    string name = el.Current.Name ?? "";
                    if (name.StartsWith("Cardslot:", StringComparison.OrdinalIgnoreCase)
                        && name.Contains(cardName, StringComparison.OrdinalIgnoreCase)
                        && name.Contains("Column Display Index:"))
                    {
                        return el;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FindCardInBinder failed.");
        }
        return null;
    }

    /// <summary>
    /// Double-clicks an AutomationElement using mouse simulation.
    /// </summary>
    private void DoubleClickElement(AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            int x = (int)(rect.Left + rect.Width / 2);
            int y = (int)(rect.Top + rect.Height / 2);

            // Move mouse and double-click
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
            Thread.Sleep(50);

            // Use mouse_event via P/Invoke for reliable double-click
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);

            _logger.LogDebug("Double-clicked element at ({X},{Y}): {Name}", x, y,
                element.Current.Name?.Substring(0, Math.Min(40, element.Current.Name.Length)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DoubleClickElement failed.");
        }
    }

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    // ─────────────────────────────────────────────────────────────────
    // UI interactions
    // ─────────────────────────────────────────────────────────────────

    public void AcceptTradeRequest()
    {
        try
        {
            var desktop = AutomationElement.RootElement;

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
