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
    static void Main(string[] args)
    {
        Console.WriteLine("[Bridge] MtgoBot.Bridge starting (net48)...");

        while (true)
        {
            try
            {
                var security = new PipeSecurity();
                security.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    PipeAccessRights.FullControl,
                    AccessControlType.Allow));

                var pipe = new NamedPipeServerStream(
                    "KorthaienBotBridge",
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None,
                    4096, 4096,
                    security);

                Console.WriteLine("[Bridge] Waiting for bot client...");
                pipe.WaitForConnection();
                Console.WriteLine("[Bridge] Bot client connected.");

                var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                var writer = new StreamWriter(pipe, Encoding.UTF8, 4096, true);
                writer.AutoFlush = true;

                while (pipe.IsConnected)
                {
                    try
                    {
                        string line = reader.ReadLine();
                        if (line == null) break;
                        Console.WriteLine("[Bridge] CMD: " + line);
                        var request = JsonConvert.DeserializeObject<BridgeRequest>(line);
                        if (request == null) continue;
                        string response = HandleCommand(request);
                        writer.WriteLine(response);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[Bridge] Error: " + ex.Message);
                        try { writer.WriteLine(JsonConvert.SerializeObject(new { error = ex.Message })); }
                        catch { }
                    }
                }

                Console.WriteLine("[Bridge] Client disconnected.");
                reader.Dispose();
                writer.Dispose();
                pipe.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Bridge] Pipe error: " + ex.Message);
                Thread.Sleep(1000);
            }
        }
    }

    static string HandleCommand(BridgeRequest req)
    {
        if (req.Cmd == null) return JsonConvert.SerializeObject(new { error = "no cmd" });
        switch (req.Cmd.ToUpperInvariant())
        {
            case "READ":           return HandleRead();
            case "ACCEPT_REQUEST": return JsonConvert.SerializeObject(new { ok = false, note = "manual accept required" });
            case "CHAT":           return HandleChat(req.Message ?? "");
            case "SUBMIT":         return HandleSubmit();
            case "ACCEPT":         return HandleAccept();
            default:               return JsonConvert.SerializeObject(new { error = "Unknown: " + req.Cmd });
        }
    }

    static string HandleRead()
    {
        try
        {
            var trade = TradeManager.CurrentTrade;
            if (trade == null)
                return JsonConvert.SerializeObject(new { snapshot = (object)null });

            string playerName = "unknown";
            try { playerName = trade.TradePartner != null ? trade.TradePartner.Name ?? "unknown" : "unknown"; }
            catch { }

            var playerOffers = new List<CardDto>();
            try
            {
                var items = trade.PartnerTradedItems;
                if (items != null)
                {
                    foreach (var item in items.CollectionItems)
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
            }
            catch { }

            var botOffers = new List<CardDto>();
            try
            {
                var items = trade.TradedItems;
                if (items != null)
                {
                    foreach (var item in items.CollectionItems)
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

            Console.WriteLine("[Bridge] Trade open: player=" + playerName +
                " playerOffers=" + playerOffers.Count +
                " botOffers=" + botOffers.Count +
                " submitted=" + bothSubmitted);

            var snapshot = new SnapshotDto
            {
                IsOpen        = true,
                PlayerName    = playerName,
                PlayerOffers  = playerOffers,
                BotOffers     = botOffers,
                BothSubmitted = bothSubmitted
            };

            return JsonConvert.SerializeObject(new { snapshot = snapshot });
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Bridge] READ error: " + ex.GetType().Name + ": " + ex.Message);
            if (ex.InnerException != null)
                Console.WriteLine("[Bridge] Inner: " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message);
            return JsonConvert.SerializeObject(new { snapshot = (object)null });
        }
    }

    static string HandleChat(string message)
    {
        Console.WriteLine("[Bridge] CHAT: " + message);
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
    [JsonProperty("cmd")]     public string Cmd     { get; set; }
    [JsonProperty("message")] public string Message { get; set; }
}

class CardDto
{
    [JsonProperty("cardId")]   public string CardId   { get; set; }
    [JsonProperty("cardName")] public string CardName { get; set; }
    [JsonProperty("quantity")] public int    Quantity { get; set; }
}

class SnapshotDto
{
    [JsonProperty("isOpen")]        public bool          IsOpen        { get; set; }
    [JsonProperty("playerName")]    public string        PlayerName    { get; set; }
    [JsonProperty("playerOffers")]  public List<CardDto> PlayerOffers  { get; set; }
    [JsonProperty("botOffers")]     public List<CardDto> BotOffers     { get; set; }
    [JsonProperty("bothSubmitted")] public bool          BothSubmitted { get; set; }
}
