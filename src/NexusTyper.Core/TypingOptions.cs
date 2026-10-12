namespace NexusTyper.Core;

public sealed class TypingOptions
{
    private TypingOptions(string text, int frequency, double? autoHoldMilliseconds, bool sendEnter,
        double spacePauseMilliseconds, int jitterPercent)
    {
        Text = text;
        Frequency = frequency;
        AutoHoldMilliseconds = autoHoldMilliseconds;
        SendEnter = sendEnter;
        SpacePauseMilliseconds = spacePauseMilliseconds;
        JitterPercent = jitterPercent;
    }

    public string Text { get; }

    public int Frequency { get; }

    public double? AutoHoldMilliseconds { get; }

    public bool SendEnter { get; }

    public double SpacePauseMilliseconds { get; }

    public int JitterPercent { get; }

    public double CharactersPerSecond => Frequency / 12d;

    public TimeSpan Interval => TimeSpan.FromMilliseconds(12000d / Frequency);

    public TimeSpan? HoldDuration => AutoHoldMilliseconds is { } milliseconds
        ? TimeSpan.FromMilliseconds(milliseconds)
        : null;

    public TimeSpan Gap => TimeSpan.FromMilliseconds(Math.Max(0, Interval.TotalMilliseconds - (HoldDuration?.TotalMilliseconds ?? 0)));

    public static bool TryCreate(
        string? text,
        int frequency,
        out TypingOptions? options,
        out string? error,
        double? autoHoldMilliseconds = null,
        bool sendEnter = false,
        double spacePauseMilliseconds = 0,
        int jitterPercent = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            options = null;
            error = "Введите текст для набора.";
            return false;
        }

        if (frequency is < 1 or > 300)
        {
            options = null;
            error = "Частота должна быть от 1 до 300.";
            return false;
        }

        if (autoHoldMilliseconds is < 0.1 or > 1000)
        {
            options = null;
            error = "Время удержания должно быть от 0,1 до 1000 мс.";
            return false;
        }

        if (spacePauseMilliseconds is < 0 or > 1000)
        {
            options = null;
            error = "Пауза после пробела должна быть от 0 до 1000 мс.";
            return false;
        }

        if (jitterPercent is < 0 or > 50)
        {
            options = null;
            error = "Разброс задержки должен быть от 0 до 50%.";
            return false;
        }

        options = new TypingOptions(text, frequency, autoHoldMilliseconds, sendEnter,
            spacePauseMilliseconds, jitterPercent);
        error = null;
        return true;
    }
}
