using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BiliSubStudio.Core.Ocr;

internal sealed class OcrWorkerClient : IAsyncDisposable
{
    private readonly OcrRuntime _runtime;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private Task<string>? _stderr;
    private long _requestId;

    public OcrWorkerClient(OcrRuntime runtime) => _runtime = runtime;
    public bool IsAlive => _process is { HasExited: false };
    public string Kind => _runtime.Kind;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsAlive) return;
        var start = new ProcessStartInfo(_runtime.Python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(_runtime.Worker);
        start.ArgumentList.Add("--model-cache");
        start.ArgumentList.Add(_runtime.Models);
        start.ArgumentList.Add("--device");
        start.ArgumentList.Add(_runtime.Device);
        start.Environment["PADDLE_PDX_CACHE_HOME"] = _runtime.Models;
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        _process = new Process { StartInfo = start };
        if (!_process.Start()) throw new InvalidOperationException("Không khởi động được PaddleOCR worker.");
        _input = _process.StandardInput;
        _output = _process.StandardOutput;
        // The startup token only controls the Ready handshake. Keep draining stderr for
        // the whole worker lifetime so a noisy native runtime cannot fill the pipe and hang.
        _stderr = _process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var readyLine = await _output.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromMinutes(3), cancellationToken)
                ?? throw new EndOfStreamException("OCR worker đóng trước khi Ready.");
            ValidateReady(readyLine);
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task<OcrResult> RunAsync(string imageBase64, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (!IsAlive || _input is null || _output is null) throw new InvalidOperationException("OCR worker chưa sẵn sàng.");
            var id = Interlocked.Increment(ref _requestId);
            var request = JsonSerializer.Serialize(new { id, image_base64 = imageBase64 });
            await _input.WriteLineAsync(request.AsMemory(), cancellationToken);
            await _input.FlushAsync(cancellationToken);
            using var registration = cancellationToken.Register(() => Kill());
            while (true)
            {
                var line = await _output.ReadLineAsync(cancellationToken)
                    ?? throw new EndOfStreamException("OCR worker đóng khi đang xử lý.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idNode) || idNode.GetInt64() != id) continue;
                var ok = root.TryGetProperty("ok", out var okNode) && okNode.GetBoolean();
                var detected = root.TryGetProperty("detected", out var detectedNode) && detectedNode.GetBoolean();
                var text = root.TryGetProperty("text", out var textNode) ? textNode.GetString() ?? string.Empty : string.Empty;
                var confidence = root.TryGetProperty("confidence", out var confidenceNode) ? confidenceNode.GetDouble() : 0;
                var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;
                var lines = new List<OcrLine>();
                if (root.TryGetProperty("lines", out var linesNode) && linesNode.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in linesNode.EnumerateArray())
                    {
                        var box = item.TryGetProperty("box", out var boxNode) && boxNode.ValueKind == JsonValueKind.Array
                            ? boxNode.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];
                        lines.Add(new OcrLine(
                            item.TryGetProperty("text", out var lineText) ? lineText.GetString() ?? string.Empty : string.Empty,
                            item.TryGetProperty("confidence", out var lineConfidence) ? lineConfidence.GetDouble() : 0,
                            box));
                    }
                }
                return new OcrResult(ok, detected, text, confidence, lines, error);
            }
        }
        finally { _requestGate.Release(); }
    }

    private void ValidateReady(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "ready") throw new InvalidDataException("OCR worker không trả Ready.");
        if (root.TryGetProperty("error", out var error) && !string.IsNullOrWhiteSpace(error.GetString())) throw new InvalidOperationException(error.GetString());
        var models = root.TryGetProperty("models", out var modelsNode)
            ? modelsNode.EnumerateArray().Select(x => x.GetString()).ToArray() : [];
        if (!models.Contains(OcrInstaller.DetectionModel) || !models.Contains(OcrInstaller.RecognitionModel))
            throw new InvalidDataException("OCR worker không xác nhận đúng PP-OCRv6 Small detection + recognition.");
        var device = root.TryGetProperty("device", out var deviceNode) ? deviceNode.GetString() ?? string.Empty : string.Empty;
        if (!device.StartsWith(_runtime.Kind, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OCR worker chạy sai thiết bị yêu cầu.");
    }

    public async Task StopAsync()
    {
        Kill();
        await _requestGate.WaitAsync();
        try
        {
            if (_process is not null)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                if (_stderr is not null)
                {
                    try { _ = await _stderr.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                }
                _process.Dispose();
            }
            _process = null;
            _input = null;
            _output = null;
            _stderr = null;
        }
        finally { _requestGate.Release(); }
    }

    private void Kill()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _requestGate.Dispose();
    }
}
