using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using MTGOSDK.API.Trade;
using MTGOSDK.API.Trade.Enums;

class Program
{
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    const uint BM_CLICK = 0x00F5;

    static void Main(string[] args)
    {
        Console.WriteLine("[Bridge] MtgoBot.Bridge starting...");

        try
        {
            TradeManager.TradeStarted += (sender, e) =>
                Console.WriteLine("[Bridge] TradeStarted event fired!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bridge] TradeStarted hook failed: {ex.Message}");
        }

        while (true)
        {
            try
            {
                // Create pipe with open security so any user/process can connect
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));

                using var pipe = NamedPipeServerStreamAcl.Create(
                    "KorthaienBotBridge",
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None,
                    4096, 4096,
                    security);

                Console.WriteLine("[Bridge] Waiting for bot client to connect...");
                pipe.WaitForConnection();
                Console.WriteLine("[Bridge] Bot client connected.");

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, true) { AutoFlush = true };

                while (pipe.IsConnected)
                {
                    try
                    {
                        var line = reader.ReadLine();
                        if (line == null) break;
                        Console.WriteLine($"[Bridge] CMD: {line}");
                        var request = JsonConvert.DeserializeObject<BridgeRequest>(line);
                        if (request == null) continue;
                        var response = HandleCommand(request);
                        writer.WriteLine(response);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Bridge] Error: {ex.Message}");
                        try { writer.WriteLine(JsonConvert.SerializeObject(new { error = ex.Message })); } catch { }
                    }
                }
                Console.WriteLine("[Bridge] Client disconnected.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Bridge] Pipe error: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    static string HandleCommand(BridgeRequest req)
    {
        return (req.Cmd?.ToUpperInvariant()) switch
        {
            "READ"           => HandleRead(),
            "ACCEPT_REQUEST" => HandleAcceptTradeRequest(),
            "CHAT"           => HandleChat(req.Message ?? ""),
            "SUBMIT"         => HandleSubmit(),
            "ACCEPT"         => HandleAccept(),
            _ => JsonConvert.SerializeObject(new { error = $"Unknown: {req.Cmd}" })
        };
    }

    static string HandleAcceptTradeRequest()
    {
        try
        {
            var mtgoProcs = System.Diagnostics.Process.GetProcessesByName("MTGO");
            if (mtgoProcs.Length == 0)
                return JsonConvert.SerializeObject(new { ok = false, error = "MTGO not running" });

            bool clicked = false;
            EnumChildWindows(mtgoProcs[0].MainWindowHandle, (hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                var sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                var text = sb.ToString();
                if (text.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                {
                    PostMessage(hWnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine($"[Bridge] Clicked Accept button.");
                    clicked = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return JsonConvert.SerializeObject(new { ok = clicked });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bridge] ACCEPT_REQUEST error: {ex.Message}");
            return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
        }
    }

    static string HandleRead()
    {
        try
        {
            var trade = TradeManager.CurrentTrade;
            if (trade == null)
                return JsonConvert.SerializeObject(new { snapshot = (object?)null });

            string playerName = "unknown";
            try { playerName = trade.TradePartner?.Name ?? "unknown"; } catch { }

            var playerOffers = new List<CardDto>();
            try
            {
                foreach (var item in trade.PartnerTradedItems.CollectionItems)
                    try { playerOffers.Add(new CardDto { CardId = item.IsTicket ? "EVENT_TICKET" : item.Id.ToString(), CardName = item.Name ?? "Unknown", Quantity = 1 }); }
                    catch { }
            }
            catch { }

            var botOffers = new List<CardDto>();
            try
            {
                foreach (var item in trade.TradedItems.CollectionItems)
                    try { botOffers.Add(new CardDto { CardId = item.IsTicket ? "EVENT_TICKET" : item.Id.ToString(), CardName = item.Name ?? "Unknown", Quantity = 1 }); }
                    catch { }
            }
            catch { }

            bool bothSubmitted = false;
            try
            {
                var state = trade.State;
                bothSubmitted = state == TradeState.ApprovalReceivedBoth
                             || state == TradeState.ApprovalSubmittedLocal
                             || state == TradeState.ApprovalSubmittedOther
                             || trade.IsAccepted;
            }
            catch { }

            return JsonConvert.SerializeObject(new { snapshot = new SnapshotDto
            {
                IsOpen = true, PlayerName = playerName,
                PlayerOffers = playerOffers, BotOffers = botOffers,
                BothSubmitted = bothSubmitted
            }});
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bridge] READ error: {ex.Message}");
            return JsonConvert.SerializeObject(new { snapshot = (object?)null });
        }
    }

    static string HandleChat(string message)
    {
        Console.WriteLine($"[Bridge] CHAT: {message}");
        return JsonConvert.SerializeObject(new { ok = true });
    }

    static string HandleSubmit()
    {
        Console.WriteLine("[Bridge] SUBMIT");
        return JsonConvert.SerializeObject(new { ok = true });
    }

    static string HandleAccept()
    {
        Console.WriteLine("[Bridge] ACCEPT");
        return JsonConvert.SerializeObject(new { ok = true });
    }
}

class BridgeRequest
{
    [JsonProperty("cmd")]     public string? Cmd     { get; set; }
    [JsonProperty("message")] public string? Message { get; set; }
}

class CardDto
{
    [JsonProperty("cardId")]   public string CardId   { get; set; } = "";
    [JsonProperty("cardName")] public string CardName { get; set; } = "";
    [JsonProperty("quantity")] public int    Quantity { get; set; } = 1;
}

class SnapshotDto
{
    [JsonProperty("isOpen")]        public bool          IsOpen        { get; set; }
    [JsonProperty("playerName")]    public string        PlayerName    { get; set; } = "";
    [JsonProperty("playerOffers")]  public List<CardDto> PlayerOffers  { get; set; } = new();
    [JsonProperty("botOffers")]     public List<CardDto> BotOffers     { get; set; } = new();
    [JsonProperty("bothSubmitted")] public bool          BothSubmitted { get; set; }
}
