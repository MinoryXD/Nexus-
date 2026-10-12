namespace NexusTyper.Core;

public sealed class TypingSession
{
    private readonly IInputSender _inputSender;
    private readonly TimeSpan _countdown;
    private int _operationActive;

    public TypingSession(IInputSender inputSender, TimeSpan? countdown = null)
    {
        _inputSender = inputSender ?? throw new ArgumentNullException(nameof(inputSender));
        _countdown = countdown ?? TimeSpan.FromSeconds(3);
    }

    public async Task StartAsync(
        TypingOptions options,
        Action<TyperState, string> report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(report);

        if (Interlocked.Exchange(ref _operationActive, 1) != 0)
        {
            throw new InvalidOperationException("Набор уже выполняется.");
        }

        try
        {
            await RunAsync(options, report, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report(TyperState.Stopped, "Остановлено.");
        }
        catch (Exception exception)
        {
            report(TyperState.Error, $"Ошибка ввода: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _operationActive, 0);
        }
    }

    private async Task RunAsync(
        TypingOptions options,
        Action<TyperState, string> report,
        CancellationToken cancellationToken)
    {
        var seconds = (int)Math.Ceiling(_countdown.TotalSeconds);
        for (var remaining = seconds; remaining > 0; remaining--)
        {
            report(TyperState.Countdown, $"Старт через {remaining} сек. Переключитесь в нужное окно.");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        report(TyperState.Typing, "Набор текста...");
        for (var index = 0; index < options.Text.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = options.Text[index];
            if (value is '\r' or '\n')
            {
                await _inputSender.SendEnterAsync(cancellationToken);
                if (value == '\r' && index + 1 < options.Text.Length && options.Text[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                await _inputSender.SendUnicodeAsync(value, options.HoldDuration, cancellationToken);
            }

            if (index < options.Text.Length - 1)
            {
                var delayMilliseconds = options.Gap.TotalMilliseconds;
                if (value == ' ')
                {
                    delayMilliseconds += options.SpacePauseMilliseconds;
                }

                if (options.JitterPercent > 0 && delayMilliseconds > 0)
                {
                    var variation = (Random.Shared.NextDouble() * 2 - 1) * options.JitterPercent / 100d;
                    delayMilliseconds *= 1 + variation;
                }

                if (delayMilliseconds > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds), cancellationToken);
                }
            }
        }

        if (options.SendEnter)
        {
            await _inputSender.SendEnterAsync(cancellationToken);
        }

        report(TyperState.Completed, "Набор завершён.");
    }
}
