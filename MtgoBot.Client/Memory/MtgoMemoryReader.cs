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
    private AutomationElement? _cachedTradeWindow;
    private AutomationElement? _botOfferGrid;
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

    /// <summary>
    /// Reads ALL card names in the bot's "You Will Receive" panel (left side),
    /// scrolling through the list to capture cards beyond the visible area.
    /// One distinct card name = one copy (quantity isn't readable from the panel).
    /// </summary>
    public List<string> ReadAllBotOfferNames()
    {
        var names = new List<string>();
        var seen = new HashSet<string>();

        try
        {
            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null) return names;
            ForegroundMtgo();

            var windowRect = tradeWindow.Current.BoundingRectangle;
            double midX     = windowRect.Left + windowRect.Width / 2;
            double offerTop = windowRect.Top + windowRect.Height * 0.68;

            // Find the bot's (left) DataGrid once so cell scans are scoped to it
            // instead of the whole window (which includes the huge binder).
            _botOfferGrid = FindBotOfferGrid(tradeWindow, midX, offerTop);

            // Position the cursor over the middle of the bot's (left) offer panel so
            // mouse-wheel events scroll THAT list.
            int panelCx = (int)(windowRect.Left + windowRect.Width * 0.25);
            int panelCy = (int)(windowRect.Top + windowRect.Height * 0.85);

            // Park the cursor once.
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(panelCx, panelCy);
            Thread.Sleep(60);

            int stableRounds = 0;
            for (int iteration = 0; iteration < 60 && stableRounds < 2; iteration++)
            {
                int before = seen.Count;
                CollectVisibleBotNames(tradeWindow, midX, offerTop, names, seen);

                if (seen.Count == before) stableRounds++;
                else stableRounds = 0;

                // Scroll ~6 rows per step (2 wheel clicks). Visible window is ~10 rows,
                // so this overlaps and never skips cards, while still moving fast.
                mouse_event(MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(-WHEEL_DELTA * 2)), 0);
                Thread.Sleep(90);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadAllBotOfferNames failed.");
        }

        _logger.LogInformation("Read {Count} card names from bot offer panel.", names.Count);
        return names;
    }

    /// <summary>Finds the bot's (left) offer DataGrid so scans can be scoped to it.</summary>
    private AutomationElement? FindBotOfferGrid(AutomationElement tradeWindow, double midX, double offerTop)
    {
        try
        {
            var grids = tradeWindow.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.DataGrid));
            foreach (AutomationElement g in grids)
            {
                try
                {
                    var r = g.Current.BoundingRectangle;
                    if (r.IsEmpty) continue;
                    if (r.Left < midX && r.Top > offerTop - 120) return g;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    private void CollectVisibleBotNames(
        AutomationElement tradeWindow, double midX, double offerTop,
        List<string> names, HashSet<string> seen)
    {
        var cacheRequest = new CacheRequest();
        cacheRequest.Add(AutomationElement.NameProperty);
        cacheRequest.Add(AutomationElement.BoundingRectangleProperty);
        cacheRequest.TreeScope = TreeScope.Element | TreeScope.Descendants;

        AutomationElementCollection items;
        AutomationElement scanRoot = _botOfferGrid ?? tradeWindow;
        using (cacheRequest.Activate())
        {
            items = scanRoot.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));
        }
        if (items == null) return;

        var rowsByY = new Dictionary<string, string>();
        foreach (AutomationElement item in items)
        {
            try
            {
                var rect = item.Cached.BoundingRectangle;
                if (rect.IsEmpty || rect.Top < offerTop) continue;
                if (rect.Left >= midX) continue; // bot side = left only

                string raw = item.Cached.Name ?? "";
                if (!raw.Contains("Column Display Index:")) continue;

                int ci = raw.LastIndexOf(", Column Display Index:", StringComparison.Ordinal);
                if (ci < 0) continue;
                string slotPart = raw.Substring(0, ci).Trim();
                string slotName = slotPart.StartsWith("Item: CardSlot:", StringComparison.OrdinalIgnoreCase)
                    ? slotPart.Substring("Item: CardSlot:".Length).Trim()
                    : (slotPart.StartsWith("CardSlot:", StringComparison.OrdinalIgnoreCase)
                        ? slotPart.Substring("CardSlot:".Length).Trim()
                        : slotPart);

                slotName = CollapseDoubledName(slotName);
                if (string.IsNullOrWhiteSpace(slotName)) continue;

                rowsByY[$"{rect.Top:F0}"] = slotName;
            }
            catch { }
        }

        foreach (var kv in rowsByY)
        {
            if (seen.Add(kv.Value))
                names.Add(kv.Value);
        }
    }

    private static string CollapseDoubledName(string s)
    {
        s = s.Trim();
        if (s.Length % 2 == 1)
        {
            int mid = s.Length / 2;
            if (s[mid] == ' ')
            {
                string a = s.Substring(0, mid).Trim();
                string b = s.Substring(mid + 1).Trim();
                if (a == b) return a;
            }
        }
        return s;
    }

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

            // PERFORMANCE: batch all property reads into ONE cross-process call.
            // Without caching, every .Name / .BoundingRectangle below is a separate
            // round-trip to MTGO — thousands of them — which made each tick take
            // many seconds. With a CacheRequest we fetch everything at once.
            var cacheRequest = new CacheRequest();
            cacheRequest.Add(AutomationElement.NameProperty);
            cacheRequest.Add(AutomationElement.BoundingRectangleProperty);
            cacheRequest.TreeScope = TreeScope.Element | TreeScope.Descendants;

            AutomationElementCollection allItems;
            using (cacheRequest.Activate())
            {
                allItems = tradeWindow.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom));
            }

            var playerOffers = new List<OfferedCard>();
            var botOffers    = new List<OfferedCard>();

            if (allItems != null)
            {
                var windowRect = tradeWindow.Current.BoundingRectangle;
                double midX = windowRect.Left + windowRect.Width / 2;
                // The two offer panels occupy roughly the bottom third of the trade
                // window; the binder fills the top. Compute the boundary from the
                // window's own geometry so it adapts to different resolutions.
                double offerTop = windowRect.Top + windowRect.Height * 0.68;

                var slotGroups = new Dictionary<string, Dictionary<int, (string text, double x)>>();

                foreach (AutomationElement item in allItems)
                {
                    try
                    {
                        // Use Cached.* — no cross-process call, reads from the snapshot.
                        var rect = item.Cached.BoundingRectangle;
                        if (rect.IsEmpty || rect.Width < 5 || rect.Height < 5) continue;

                        // Only the two offer panels (bottom of the window) matter.
                        if (rect.Top < offerTop) continue;

                        string name = item.Cached.Name ?? "";
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

                        // Panel layout: the bot's "You Will Receive" is on the LEFT,
                        // the customer's "[name] Will Receive" is on the RIGHT.
                        if (xPos < midX)
                            botOffers.Add(card);
                        else
                            playerOffers.Add(card);
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
        catch (System.Windows.Automation.ElementNotAvailableException)
        {
            // The cached trade window went stale (e.g. previous trade closed).
            // Clear the cache so the next call re-finds a fresh window.
            _cachedTradeWindow = null;
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadTradeWindow failed.");
            return null;
        }
    }

    private AutomationElement? FindTradeWindow()
    {
        // Reuse the cached window if it is still alive — avoids re-scanning the
        // desktop on every call (ReadTradeWindow, SendChatMessage, etc. all call this).
        if (_cachedTradeWindow != null)
        {
            try
            {
                string cachedName = _cachedTradeWindow.Current.Name ?? "";
                if (cachedName.StartsWith("Trade: ", StringComparison.OrdinalIgnoreCase))
                    return _cachedTradeWindow;
            }
            catch { /* stale — fall through and re-find */ }
            _cachedTradeWindow = null;
        }

        try
        {
            var searchRoot = AutomationElement.RootElement;

            // Trade windows are top-level windows, so search direct CHILDREN only.
            var windows = searchRoot.FindAll(
                TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

            if (windows != null)
            {
                foreach (AutomationElement el in windows)
                {
                    try
                    {
                        string name = el.Current.Name ?? "";
                        if (name.StartsWith("Trade: ", StringComparison.OrdinalIgnoreCase))
                        {
                            _cachedTradeWindow = el;
                            return el;
                        }
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
    /// Finds an Edit control in the trade window by horizontal position.
    /// leftSide=true → binder search box (X &lt; 400).
    /// leftSide=false → chat input box (X &gt; 1500).
    /// </summary>
    private AutomationElement? FindEditControl(AutomationElement tradeWindow, bool leftSide)
    {
        try
        {
            var edits = tradeWindow.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));

            if (edits == null) return null;

            foreach (AutomationElement edit in edits)
            {
                try
                {
                    var rect = edit.Current.BoundingRectangle;
                    if (rect.IsEmpty) continue;

                    if (leftSide && rect.Left < 400) return edit;
                    if (!leftSide && rect.Left > 1200) return edit;
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FindEditControl failed.");
        }
        return null;
    }

    /// <summary>
    /// Types text into the binder search box (left Edit control).
    /// Clears existing text first.
    /// </summary>
    public bool TypeInSearchBox(string text)
    {
        try
        {
            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null) return false;

            // Find the search/filter Edit box — it is on the LEFT side of the window (X < 400).
            // The chat input box is also an Edit control but on the far RIGHT (X > 1500).
            var searchBox = FindEditControl(tradeWindow, leftSide: true);
            if (searchBox == null)
            {
                _logger.LogDebug("Search box (left Edit) not found.");
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
    private const uint MOUSEEVENTF_WHEEL    = 0x0800;
    private const int  WHEEL_DELTA          = 120;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
    private const byte VK_RETURN = 0x0D;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Puts text on the clipboard (STA thread) for paste operations.</summary>
    private static void SetClipboardText(string text)
    {
        var t = new Thread(() =>
        {
            try { System.Windows.Forms.Clipboard.SetText(text); } catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

    /// <summary>
    /// Brings the MTGO process's main window to the foreground so mouse/keyboard
    /// simulation lands on it. Essential for unattended operation — without this,
    /// the bot only works when MTGO is already the active window.
    /// </summary>
    public void ForegroundMtgo()
    {
        try
        {
            if (_mtgoProcess == null || _mtgoProcess.HasExited) return;
            IntPtr h = _mtgoProcess.MainWindowHandle;
            if (h != IntPtr.Zero)
            {
                ShowWindow(h, SW_RESTORE);
                SetForegroundWindow(h);
                Thread.Sleep(150);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ForegroundMtgo failed.");
        }
    }

    /// <summary>Presses and releases Enter via low-level keybd_event (avoids SendKeys bug).</summary>
    private static void PressEnter()
    {
        keybd_event(VK_RETURN, 0, 0, 0);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, 0);
    }

    // ─────────────────────────────────────────────────────────────────
    // Deck import (Search Tools → Import Deck → file picker)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Imports a .dek file into the current trade via Search Tools → Import Deck.
    /// MTGO auto-adds matching cards (by CatID) from the customer binder to the bot side.
    /// Returns true if the import dialog flow was driven successfully.
    /// </summary>
    public bool ImportDeck(string dekFilePath)
    {
        try
        {
            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null)
            {
                _logger.LogWarning("ImportDeck: no trade window.");
                return false;
            }
            ForegroundMtgo();

            // 1. Click "Search Tools" (Text control — use mouse click) and wait for
            //    the "Import Deck" button to appear. The click sometimes misses or the
            //    dialog is slow, so retry the whole open a few times.
            var desktop = AutomationElement.RootElement;
            AutomationElement? importBtn = null;

            for (int openAttempt = 0; openAttempt < 3 && importBtn == null; openAttempt++)
            {
                var searchTools = tradeWindow.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Search Tools"));

                if (searchTools == null)
                {
                    _logger.LogWarning("ImportDeck: 'Search Tools' not found.");
                    return false;
                }

                ClickElement(searchTools);
                Thread.Sleep(1000);

                // Poll for the Import Deck button (up to ~3s per open attempt).
                for (int attempt = 0; attempt < 6 && importBtn == null; attempt++)
                {
                    importBtn = desktop.FindFirst(
                        TreeScope.Descendants,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.NameProperty, "Import Deck"),
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                    if (importBtn == null) Thread.Sleep(500);
                }

                if (importBtn == null)
                    _logger.LogDebug("ImportDeck: dialog not open after attempt {N}, retrying.", openAttempt + 1);
            }

            if (importBtn == null)
            {
                _logger.LogWarning("ImportDeck: 'Import Deck' button not found after retries.");
                return false;
            }

            if (importBtn.TryGetCurrentPattern(InvokePattern.Pattern, out var invObj)
                && invObj is InvokePattern inv)
            {
                inv.Invoke();
            }
            else
            {
                ClickElement(importBtn);
            }
            Thread.Sleep(1200); // wait for Windows file picker

            // 3. Type the full path into the file picker and confirm.
            //    The file name Edit field has focus by default in the Open dialog.
            if (!TypeIntoFilePicker(dekFilePath))
            {
                _logger.LogWarning("ImportDeck: could not drive file picker.");
                return false;
            }

            _logger.LogInformation("✅ ImportDeck triggered for {Path}", dekFilePath);
            Thread.Sleep(2000); // let MTGO process the deck and show any warning

            // MTGO shows a "cards not found" warning listing every card the customer
            // lacks (most of the 1520). Dismiss it by clicking OK so the matched
            // cards commit to the trade and the dialog stops covering the window.
            DismissWarningDialog();
            Thread.Sleep(800);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImportDeck failed.");
            return false;
        }
    }

    /// <summary>
    /// Finds and clicks the OK button on MTGO's post-import warning dialog
    /// ("The following cards ... were not found"). Safe to call when no dialog exists.
    /// </summary>
    private void DismissWarningDialog()
    {
        try
        {
            var desktop = AutomationElement.RootElement;
            // Try a few times — the dialog can take a moment to render.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var okBtn = desktop.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.NameProperty, "OK"),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));

                if (okBtn != null)
                {
                    if (okBtn.TryGetCurrentPattern(InvokePattern.Pattern, out var iObj)
                        && iObj is InvokePattern ip)
                        ip.Invoke();
                    else
                        ClickElement(okBtn);
                    _logger.LogInformation("Dismissed import warning dialog (OK).");
                    return;
                }
                Thread.Sleep(400);
            }
            _logger.LogDebug("No warning dialog to dismiss.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DismissWarningDialog failed.");
        }
    }

    /// <summary>
    /// Drives the standard Windows 'Open' file dialog: types the path into the
    /// file name box and presses Enter.
    /// </summary>
    private bool TypeIntoFilePicker(string fullPath)
    {
        try
        {
            var desktop = AutomationElement.RootElement;
            Thread.Sleep(500); // let the dialog fully open

            // The file name field is an Edit control named "File name:".
            var fileNameEdit = desktop.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, "File name:")));

            if (fileNameEdit == null)
            {
                // Some locales/styles wrap it in a ComboBox — try finding any Edit in a window
                // whose name suggests an open dialog.
                fileNameEdit = desktop.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true)));
            }

            if (fileNameEdit == null)
            {
                _logger.LogWarning("File picker: filename Edit not found.");
                return false;
            }

            fileNameEdit.SetFocus();
            Thread.Sleep(150);

            bool valueSet = false;
            if (fileNameEdit.TryGetCurrentPattern(ValuePattern.Pattern, out var vObj)
                && vObj is ValuePattern vp)
            {
                try
                {
                    vp.SetValue(fullPath);
                    Thread.Sleep(200);
                    string readBack = "";
                    try { readBack = vp.Current.Value ?? ""; } catch { }
                    _logger.LogInformation("File picker: filename field now = '{Val}'", readBack);
                    valueSet = !string.IsNullOrEmpty(readBack);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "ValuePattern.SetValue failed."); }
            }

            // Fallback: clipboard paste (reliable when ValuePattern/SendKeys fail).
            if (!valueSet)
            {
                _logger.LogDebug("File picker: using clipboard paste fallback.");
                SetClipboardText(fullPath);
                fileNameEdit.SetFocus();
                Thread.Sleep(150);
                // Ctrl+V
                keybd_event(VK_CONTROL, 0, 0, 0);
                keybd_event(VK_V, 0, 0, 0);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
                Thread.Sleep(200);
            }
            Thread.Sleep(300);

            // Click the "Open" button.
            var openBtn = desktop.FindFirst(
                TreeScope.Descendants,
                new AndCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Open"),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));

            if (openBtn != null
                && openBtn.TryGetCurrentPattern(InvokePattern.Pattern, out var iObj)
                && iObj is InvokePattern ip)
            {
                ip.Invoke();
                _logger.LogDebug("File picker: clicked Open.");
            }
            else
            {
                // Fallback: press Enter to confirm.
                System.Windows.Forms.SendKeys.SendWait("{ENTER}");
                _logger.LogDebug("File picker: pressed Enter (Open button not found).");
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TypeIntoFilePicker failed.");
            return false;
        }
    }

    /// <summary>Single left-click at an element center via mouse simulation.</summary>
    private void ClickElement(AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            int x = (int)(rect.Left + rect.Width / 2);
            int y = (int)(rect.Top + rect.Height / 2);
            System.Windows.Forms.Cursor.Position = new System.Drawing.Point(x, y);
            Thread.Sleep(50);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ClickElement failed.");
        }
    }

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

    /// <summary>
    /// Sends a chat message: types into the chat input box (right Edit control),
    /// then clicks the "Send" text control. Clicking Send is far more reliable
    /// than simulating Enter, which fails in this UI-Automation context.
    /// </summary>
    public void SendChatMessage(string message)
    {
        try
        {
            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null)
            {
                _logger.LogDebug("[CHAT] No trade window — message not sent: {Msg}", message);
                return;
            }

            var chatBox = FindEditControl(tradeWindow, leftSide: false);
            if (chatBox == null)
            {
                _logger.LogDebug("[CHAT] Chat box not found — message not sent: {Msg}", message);
                return;
            }

            ForegroundMtgo();
            chatBox.SetFocus();
            Thread.Sleep(150);

            // Put the text in via ValuePattern.
            if (chatBox.TryGetCurrentPattern(ValuePattern.Pattern, out var vObj)
                && vObj is ValuePattern vp)
            {
                vp.SetValue(message);
            }
            else
            {
                _logger.LogDebug("[CHAT] chat box has no ValuePattern — cannot set text: {Msg}", message);
                return;
            }
            Thread.Sleep(250);

            // Click the "Send" text control (it does not support Invoke, so mouse-click).
            var sendBtn = tradeWindow.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "Send"));

            if (sendBtn != null)
            {
                // Click Send, wait, and verify the chat box cleared (= message sent).
                // If it didn't clear, click once more.
                ClickElement(sendBtn);
                Thread.Sleep(400);

                bool sent = false;
                try
                {
                    string remaining = vp.Current.Value ?? "";
                    sent = string.IsNullOrEmpty(remaining);
                }
                catch { }

                if (!sent)
                {
                    ClickElement(sendBtn);
                    Thread.Sleep(300);
                }
                _logger.LogInformation("[CHAT] {Msg}", message);
            }
            else
            {
                // Fallback: focus + Enter via keybd_event.
                chatBox.SetFocus();
                Thread.Sleep(100);
                PressEnter();
                _logger.LogInformation("[CHAT] {Msg} (via Enter — Send button not found)", message);
            }
            Thread.Sleep(100);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SendChatMessage failed.");
        }
    }

    /// <summary>Escapes characters that have special meaning to SendKeys.</summary>
    private static string EscapeForSendKeys(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            if ("+^%~(){}[]".IndexOf(c) >= 0)
                sb.Append('{').Append(c).Append('}');
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Reads the most recent chat messages from the trade window chat panel.
    /// Returns the raw text of visible chat lines (newest last).
    /// </summary>
    public List<string> ReadChatMessages()
    {
        var messages = new List<string>();
        try
        {
            var tradeWindow = FindTradeWindow();
            if (tradeWindow == null) return messages;

            // Chat messages are Text elements on the right side of the window (X > 1500)
            var texts = tradeWindow.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));

            if (texts == null) return messages;

            foreach (AutomationElement t in texts)
            {
                try
                {
                    var rect = t.Current.BoundingRectangle;
                    if (rect.IsEmpty) continue;
                    if (rect.Left < 1400) continue; // chat panel is on the right

                    string txt = t.Current.Name ?? "";
                    if (!string.IsNullOrWhiteSpace(txt))
                        messages.Add(txt);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ReadChatMessages failed.");
        }
        return messages;
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
