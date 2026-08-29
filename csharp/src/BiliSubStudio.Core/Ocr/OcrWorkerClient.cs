using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BiliSubStudio.Core.Ocr;

internal sealed class OcrWorkerClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
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
        // Keep draining stderr for the whole worker lifetime so a noisy native runtime
        // cannot fill the pipe and hang. stdout is the JSON protocol, but Paddle/PaddleOCR
        // may still emit diagnostic text there; protocol readers deliberately ignore it.
        _stderr = _process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(StartupTimeout);
        try
        {
            while (true)
            {
                var line = await _output.ReadLineAsync(startup.Token)
                    ?? throw new EndOfStreamException("OCR worker đóng trước khi Ready.");
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var typeNode)) continue;
                    var type = typeNode.GetString();
                    if (string.Equals(type, "fatal", StringComparison.Ordinal))
                    {
                        var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "OCR worker báo lỗi nghiêm trọng khi khởi động." : error);
                    }
                    if (!string.Equals(type, "ready", StringComparison.Ordinal)) continue;
                }
                ValidateReady(line);
                break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync();
            throw new TimeoutException($"OCR worker không Ready trong {StartupTimeout.TotalMinutes:0} phút; worker đã được dừng.");
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    public async Task<OcrResult> RunAsync(string imageBase64, CancellationToken cancellationToken, bool recoverShortBlank = false, string? activeShortText = null)
    {
        if (string.IsNullOrWhiteSpace(imageBase64)) throw new ArgumentException("Ảnh OCR rỗng.", nameof(imageBase64));
        return await RunRequestAsync(
            new Dictionary<string, object?>
            {
                ["image_base64"] = imageBase64,
                ["recover_short_blank"] = recoverShortBlank,
                ["active_short_text"] = activeShortText,
            },
            ParseResult,
            cancellationToken);
    }

    public async Task<IReadOnlyList<OcrResult>> RunBatchAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken,
        bool recoverShortBlank = false,
        string? activeShortText = null)
    {
        if (imageBase64.Count is < 1 or > 4 || imageBase64.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Batch OCR phải có từ 1 đến 4 ảnh hợp lệ.", nameof(imageBase64));
        return await RunRequestAsync(
            new Dictionary<string, object?>
            {
                ["images_base64"] = imageBase64,
                ["recover_short_blank"] = recoverShortBlank,
                ["active_short_text"] = activeShortText,
            },
            root =>
            {
                if (!root.TryGetProperty("ok", out var okNode) || !okNode.GetBoolean()
                    || !root.TryGetProperty("results", out var resultsNode)
                    || resultsNode.ValueKind != JsonValueKind.Array)
                {
                    var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;
                    throw new InvalidDataException(error ?? "OCR worker trả batch không hợp lệ.");
                }
                var results = resultsNode.EnumerateArray().Select(ParseResult).ToArray();
                if (results.Length != imageBase64.Count)
                    throw new InvalidDataException($"OCR worker trả {results.Length}/{imageBase64.Count} kết quả batch.");
                return results;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<double>> ProbeVisualChangesAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken)
    {
        if (imageBase64.Count is < 2 or > 65 || imageBase64.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Probe biến đổi phải có từ 2 đến 65 ảnh hợp lệ.", nameof(imageBase64));
        return await RunRequestAsync(
            new Dictionary<string, object?> { ["probe_images_base64"] = imageBase64 },
            root =>
            {
                if (!root.TryGetProperty("ok", out var okNode) || !okNode.GetBoolean()
                    || !root.TryGetProperty("change_scores", out var scoresNode)
                    || scoresNode.ValueKind != JsonValueKind.Array)
                {
                    var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;
                    throw new InvalidDataException(error ?? "OCR worker trả probe biến đổi không hợp lệ.");
                }
                var scores = scoresNode.EnumerateArray().Select(score => score.GetDouble()).ToArray();
                if (scores.Length != imageBase64.Count - 1
                    || scores.Any(score => !double.IsFinite(score) || score is < 0 or > 1))
                    throw new InvalidDataException($"OCR worker trả {scores.Length}/{imageBase64.Count - 1} điểm biến đổi hợp lệ.");
                return scores;
            },
            cancellationToken);
    }

    private async Task<T> RunRequestAsync<T>(
        IReadOnlyDictionary<string, object?> payload,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var requestToken = timeout.Token;
        try
        {
            if (!IsAlive || _input is null || _output is null) throw new InvalidOperationException("OCR worker chưa sẵn sàng.");
            var id = Interlocked.Increment(ref _requestId);
            var requestFields = payload.ToDictionary(pair => pair.Key, pair => pair.Value);
            requestFields["id"] = id;
            var request = JsonSerializer.Serialize(requestFields);
            await _input.WriteLineAsync(request.AsMemory(), requestToken);
            await _input.FlushAsync(requestToken);
            using var registration = requestToken.Register(() => Kill());
            while (true)
            {
                var line = await _output.ReadLineAsync(requestToken)
                    ?? throw new EndOfStreamException("OCR worker đóng khi đang xử lý.");
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("type", out var typeNode) && string.Equals(typeNode.GetString(), "fatal", StringComparison.Ordinal))
                    {
                        var fatal = root.TryGetProperty("error", out var fatalNode) ? fatalNode.GetString() : null;
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(fatal) ? "OCR worker báo lỗi nghiêm trọng." : fatal);
                    }
                    if (!root.TryGetProperty("id", out var idNode) || !idNode.TryGetInt64(out var responseId) || responseId != id) continue;
                    return parse(root);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"OCR worker không phản hồi trong {RequestTimeout.TotalSeconds:0} giây; worker đã được dừng để tránh treo tác vụ.");
        }
        finally { _requestGate.Release(); }
    }

    private static OcrResult ParseResult(JsonElement root)
    {
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
                    ? boxNode.EnumerateArray().Select(x => x.TryGetInt32(out var coordinate) ? coordinate : checked((int)Math.Round(x.GetDouble()))).ToArray()
                    : [];
                lines.Add(new OcrLine(
                    item.TryGetProperty("text", out var lineText) ? lineText.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("confidence", out var lineConfidence) ? lineConfidence.GetDouble() : 0,
                    box));
            }
        }
        return new OcrResult(ok, detected, text, confidence, lines, error);
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
        Exception? stopFailure = null;
        try
        {
            if (_process is not null)
            {
                try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                if (IsAlive)
                {
                    Kill();
                    try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
                }
                if (IsAlive)
                    stopFailure = new IOException($"Không dừng được OCR Python worker PID {_process.Id} và cây tiến trình con.");
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
        if (stopFailure is not null) throw stopFailure;
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
