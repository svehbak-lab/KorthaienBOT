namespace MtgoBot.Client.Loop;

/// <summary>
/// Injects realistic human-like delays between bot actions.
///
/// Gemini: "Don't let it press buttons with 0ms delay. Add random pauses
/// (300–700ms) between each action so the behavior pattern isn't flagged
/// by automated anti-cheat systems."
///
/// All bot actions that touch the MTGO UI should call an appropriate
/// method here before executing. This is especially important for:
///   - Adding cards to the trade window
///   - Clicking Submit / Accept
///   - Sending chat messages
///   - Responding to window state changes
/// </summary>
public static class HumanBehavior
{
    private static readonly Random _rng = Random.Shared;

    /// <summary>
    /// Standard action delay: 300–700ms.
    /// Use between most UI interactions.
    /// </summary>
    public static Task PauseAsync()
        => Task.Delay(_rng.Next(300, 701));

    /// <summary>
    /// Thinking delay: 800–1500ms.
    /// Use before responding to a new trade or complex window change.
    /// Simulates a human reading the situation.
    /// </summary>
    public static Task ThinkAsync()
        => Task.Delay(_rng.Next(800, 1501));

    /// <summary>
    /// Typing delay: 40–120ms per character.
    /// Use when sending chat messages character-by-character (if SDK requires it).
    /// </summary>
    public static Task TypingDelayAsync(int charCount)
        => Task.Delay(_rng.Next(40, 121) * Math.Max(1, charCount / 5));

    /// <summary>
    /// Card-add delay: 200–500ms per card.
    /// Use when adding each card to the trade window in a batch.
    /// Adds natural variation — doesn't add all 100 cards at exactly the same rate.
    /// </summary>
    public static Task CardAddDelayAsync()
        => Task.Delay(_rng.Next(200, 501));

    /// <summary>
    /// Slow down occasionally to simulate distraction (every ~20 actions).
    /// 5% chance of a longer 1–3 second pause.
    /// </summary>
    public static Task MaybeDistractedAsync()
    {
        if (_rng.Next(100) < 5)
            return Task.Delay(_rng.Next(1000, 3001));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Jittered delay around a center point (±30%).
    /// Use when you have a specific target delay but want natural variation.
    /// </summary>
    public static Task JitteredDelayAsync(int targetMs)
    {
        int jitter = (int)(targetMs * 0.3);
        int actual = targetMs + _rng.Next(-jitter, jitter + 1);
        return Task.Delay(Math.Max(50, actual));
    }
}
