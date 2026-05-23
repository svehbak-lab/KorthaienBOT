namespace MtgoBot.Core.Trading;

/// <summary>
/// Tracks how many times each card has sold today (in-memory only).
/// Resets when the bot process restarts (nightly at 05:30).
///
/// Each sale of a card bumps its sell price up by 5%, capped at +30%.
/// Buy prices are never affected.
/// </summary>
public class IntradayVelocityTracker
{
    private readonly Dictionary<string, int> _salesCount = new();
    private readonly object _lock = new();

    private const decimal UpliftPerSale = 0.05m;
    private const decimal MaxUplift     = 0.30m;

    public void RecordSale(string cardId, int quantity = 1)
    {
        lock (_lock)
        {
            _salesCount.TryGetValue(cardId, out var current);
            _salesCount[cardId] = current + quantity;
        }
    }

    public decimal GetSellMultiplier(string cardId)
    {
        lock (_lock)
        {
            if (!_salesCount.TryGetValue(cardId, out var count) || count == 0)
                return 1.0m;
            var uplift = Math.Min(MaxUplift, (count - 1) * UpliftPerSale);
            return 1.0m + uplift;
        }
    }

    public decimal ApplyUplift(string cardId, decimal baseSellPrice)
    {
        var multiplier = GetSellMultiplier(cardId);
        return Math.Round(baseSellPrice * multiplier, 4);
    }

    public int GetSalesCount(string cardId)
    {
        lock (_lock)
        {
            _salesCount.TryGetValue(cardId, out var count);
            return count;
        }
    }

    public IReadOnlyDictionary<string, int> GetAllSales() => _salesCount;

    public void Reset()
    {
        lock (_lock) { _salesCount.Clear(); }
    }
}
