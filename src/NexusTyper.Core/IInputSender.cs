namespace NexusTyper.Core;

public interface IInputSender
{
    Task SendUnicodeAsync(char value, TimeSpan? holdDuration, CancellationToken cancellationToken);

    Task SendEnterAsync(CancellationToken cancellationToken);
}
