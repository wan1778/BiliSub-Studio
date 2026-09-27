using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BiliSubStudio.App.Services;

// Discord Game IPC thuần (không gói ngoài): named pipe discord-ipc-0..9,
// handshake client_id rồi SET_ACTIVITY. Best-effort tuyệt đối: Discord tắt
// hay pipe lỗi thì báo false, không bao giờ ném ra ngoài làm hỏng app.
internal sealed class DiscordIpcClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private NamedPipeClientStream? _pipe;
    private bool _disposed;

    public bool Connected => _pipe is { IsConnected: true };

    public bool TryConnect(string clientId)
    {
        Disconnect();
        for (var index = 0; index < 10; index++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(1500);
                WriteFrame(pipe, 0, JsonSerializer.Serialize(new { v = 1, client_id = clientId }, Json));
                _pipe = pipe;
                return true;
            }
            catch
            {
                try { pipe.Dispose(); } catch { }
            }
        }
        return false;
    }

    public bool TrySendActivity(string activityJson)
    {
        var pipe = _pipe;
        if (pipe is not { IsConnected: true }) return false;
        try
        {
            var nonce = Guid.NewGuid().ToString("N");
            WriteFrame(pipe, 1, "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + Environment.ProcessId
                + ",\"activity\":" + activityJson + "},\"nonce\":\"" + nonce + "\"}");
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    public bool TryClearActivity() => TrySendActivity("null");

    private static void WriteFrame(NamedPipeClientStream pipe, int opcode, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var header = new byte[8];
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(body.Length).CopyTo(header, 4);
        pipe.Write(header, 0, header.Length);
        pipe.Write(body, 0, body.Length);
        pipe.Flush();
    }

    private void Disconnect()
    {
        var pipe = _pipe;
        _pipe = null;
        if (pipe is null) return;
        try { pipe.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
    }
}
