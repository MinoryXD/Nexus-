using System.ComponentModel;
using System.Runtime.InteropServices;
using NexusTyper.Core;

namespace NexusTyper.App;

public sealed class WindowsInputSender : IInputSender
{
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const ushort VirtualKeyReturn = 0x0D;

    public async Task SendUnicodeAsync(char value, TimeSpan? holdDuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Send(CreateInput(scan: value, flags: KeyeventfUnicode));
        try
        {
            if (holdDuration is { } duration && duration > TimeSpan.Zero)
            {
                await Task.Delay(duration, cancellationToken);
            }
        }
        finally
        {
            Send(CreateInput(scan: value, flags: KeyeventfUnicode | KeyeventfKeyup));
        }
    }

    public Task SendEnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputs = new[]
        {
            CreateInput(virtualKey: VirtualKeyReturn, flags: 0),
            CreateInput(virtualKey: VirtualKeyReturn, flags: KeyeventfKeyup),
        };
        Send(inputs);
        return Task.CompletedTask;
    }

    private static void Send(Input input) => Send([input]);

    private static void Send(Input[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не смогла отправить ввод. Проверьте, что NexusTyper и целевое приложение запущены с одинаковыми правами.");
        }
    }

    private static Input CreateInput(ushort virtualKey = 0, ushort scan = 0, uint flags = 0) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = virtualKey,
                Scan = scan,
                Flags = flags,
            },
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, Input[] inputs, int sizeOfInput);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }
}
