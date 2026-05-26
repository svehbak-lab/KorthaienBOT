using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using MTGOSDK.API.Trade;

/// <summary>
/// MtgoBot.Bridge — net48 process that wraps MTGOSDK.
///
/// Runs as a separate process alongside MtgoBot.Client.
/// Communicates via a named pipe "KorthaienBotBridge".
///
/// Protocol (newline-delimited JSON):
///   Bot client sends:  { "cmd": "READ" }
///   Bridge responds:   { "snapshot": { ... } }  or  { "snapshot": null }
///
///   Bot client sends:  { "cmd": "CHAT", "message": "Hei!" }
///   Bridge responds:   { "ok": true }
///
///   Bot client sends:  { "cmd": "SUBMIT" }
///   Bridge responds:   { "ok": true }
///
///   Bot client sends:  { "cmd": "ACCEPT" }
///   Bridge responds:   { "ok": true }
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("[Bridge] MtgoBot.Bridge starting...");

        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    "KorthaienBotBridge",
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);

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

                        var request = JsonConvert.DeserializeObject<BridgeRequest>(line);
                        if (request == null) continue;

                        string response = HandleCommand(request);
                        writer.WriteLine(response);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Bridge] Error handling command: {ex.Message}");
                        try { writer.WriteLine(JsonConvert.SerializeObject(new { error = ex.Message })); }
                        catch { }
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
        switch (req.Cmd?.ToUpperInvariant())
        {
            case "READ":
                return HandleRead();

            case "CHAT":
                return HandleChat(req.Message ?? "");

            case "SUBMIT":
                return HandleSubmit();

            case "ACCEPT":
                return HandleAccept();

            default:
                return JsonConvert.SerializeObject(new { error = $"Unknown command: {req.Cmd}" });
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
            try { playerName = trade.TradePartner?.Poster?.Name ?? "unknown"; }
            catch { }

            var playerOffers = new System.Collections.Generic.List<CardDto>();
            var botOffers    = new System.Collections.Generic.List<CardDto>();

            try
            {
                foreach (var item in trade.PartnerTradedItems)
                {
                    try
                    {
                        playerOffers.Add(new CardDto
                        {
                            CardId   = item.IsTicket ? "EVENT_TICKET" : item.Id.ToString(),
                            CardName = item.Name ?? "Unknown",
                            Quantity = 1
                        });
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var item in trade.TradedItems)
                {
                    try
                    {
                        botOffers.Add(new CardDto
                        {
                            CardId   = item.IsTicket ? "EVENT_TICKET" : item.Id.ToString(),
                            CardName = item.Name ?? "Unknown",
                            Quantity = 1
                        });
                    }
                    catch { }
                }
            }
            catch { }

            bool bothSubmitted = false;
            try
            {
                bothSubmitted = trade.State == MTGOSDK.API.Trade.Enums.TradeState.BothConfirmed
                             || trade.IsAccepted;
            }
            catch { }

            var snapshot = new SnapshotDto
            {
                IsOpen        = true,
                PlayerName    = playerName,
                PlayerOffers  = playerOffers,
                BotOffers     = botOffers,
                BothSubmitted = bothSubmitted
            };

            return JsonConvert.SerializeObject(new { snapshot });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Bridge] READ error: {ex.Message}");
            return JsonConvert.SerializeObject(new { snapshot = (object?)null });
        }
    }

    static string HandleChat(string message)
    {
        try
        {
            // TODO: wire to MTGOSDK Chat API once we identify the correct channel
            // For now log it — chat will be handled via SendKeys as fallback
            Console.WriteLine($"[Bridge] CHAT: {message}");
            return JsonConvert.SerializeObject(new { ok = true });
        }
        catch (Exception ex)
        {
            return JsonConvert.SerializeObject(new { ok = false, error = ex.Message });
        }
    }

    static string HandleSubmit()
    {
        Console.WriteLine("[Bridge] SUBMIT requested");
        // TODO: implement via MTGOSDK or Win32 PostMessage
        return JsonConvert.SerializeObject(new { ok = true });
    }

    static string HandleAccept()
    {
        Console.WriteLine("[Bridge] ACCEPT requested");
        // TODO: implement via MTGOSDK or Win32 PostMessage
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
    [JsonProperty("isOpen")]        public bool   IsOpen        { get; set; }
    [JsonProperty("playerName")]    public string PlayerName    { get; set; } = "";
    [JsonProperty("playerOffers")]  public System.Collections.Generic.List<CardDto> PlayerOffers { get; set; } = new();
    [JsonProperty("botOffers")]     public System.Collections.Generic.List<CardDto> BotOffers    { get; set; } = new();
    [JsonProperty("bothSubmitted")] public bool   BothSubmitted { get; set; }
}
