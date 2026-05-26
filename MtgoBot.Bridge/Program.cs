using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using MTGOSDK.API.Trade;
using MTGOSDK.API.Trade.Enums;

/// <summary>
/// MtgoBot.Bridge — net10.0-windows process that wraps MTGOSDK.
/// Communicates with MtgoBot.Client via named pipe "KorthaienBotBridge".
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
            "READ"   => HandleRead(),
            "CHAT"   => HandleChat(req.Message ?? ""),
            "SUBMIT" => HandleSubmit(),
            "ACCEPT" => HandleAccept(),
            _ => JsonConvert.SerializeObject(new { error = $"Unknown command: {req.Cmd}" })
        };
    }

    static string HandleRead()
    {
        try
        {
            var trade = TradeManager.CurrentTrade;
            if (trade == null)
                return JsonConvert.SerializeObject(new { snapshot = (object?)null });

            // Opponent name
            string playerName = "unknown";
            try { playerName = trade.TradePartner?.Name ?? "unknown"; } catch { }

            // Cards player offered
            var playerOffers = new List<CardDto>();
            try
            {
                foreach (var item in trade.PartnerTradedItems.CollectionItems)
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

            // Cards bot offered
            var botOffers = new List<CardDto>();
            try
            {
                foreach (var item in trade.TradedItems.CollectionItems)
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

            // Both sides submitted = ApprovalReceivedBoth or beyond
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
        Console.WriteLine($"[Bridge] CHAT: {message}");
        // TODO: wire to MTGOSDK Chat API
        return JsonConvert.SerializeObject(new { ok = true });
    }

    static string HandleSubmit()
    {
        Console.WriteLine("[Bridge] SUBMIT");
        // TODO: implement via MTGOSDK or Win32
        return JsonConvert.SerializeObject(new { ok = true });
    }

    static string HandleAccept()
    {
        Console.WriteLine("[Bridge] ACCEPT");
        // TODO: implement via MTGOSDK or Win32
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
    [JsonProperty("isOpen")]        public bool             IsOpen        { get; set; }
    [JsonProperty("playerName")]    public string           PlayerName    { get; set; } = "";
    [JsonProperty("playerOffers")]  public List<CardDto>    PlayerOffers  { get; set; } = new();
    [JsonProperty("botOffers")]     public List<CardDto>    BotOffers     { get; set; } = new();
    [JsonProperty("bothSubmitted")] public bool             BothSubmitted { get; set; }
}
