using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BiliSubStudio.Core.Ocr;

internal sealed class OcrWorkerClient : IAsyncDisposable
{
    private const int StderrTailLimit = 16 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);
    // Private UTF-8 JSON pipe, never HTML: keep base64 '+' literal instead of '\u002B'.
    private static readonly JsonSerializerOptions RequestJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly OcrRuntime _runtime;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private readonly object _stderrSync = new();
    private readonly StringBuilder _stderrTail = new();
    private Task? _stderrCompletion;
    private long _requestId;
    // Tuổi worker (số ảnh inference đã phục vụ thành công, không tính probe): dùng cho
    // recycle chủ động chống suy giảm theo giờ + pháp y treo (biết worker chết lúc bao nhiêu tuổi).
    private long _imagesServed;

    public OcrWorkerClient(OcrRuntime runtime) => _runtime = runtime;
    public bool IsAlive => _process is { HasExited: false };
    internal long ImagesServed => Interlocked.Read(ref _imagesServed);
    internal void ResetAge() => Interlocked.Exchange(ref _imagesServed, 0);
    public string Kind => _runtime.Kind;
    internal OcrRuntime Runtime => _runtime;

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
        lock (_stderrSync) _stderrTail.Clear();
        _stderrCompletion = PumpStderrAsync(_process.StandardError);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(StartupTimeout);
        try
        {
            while (true)
            {
                var line = await _output.ReadLineAsync(startup.Token);
                if (line is null) throw await ClosedExceptionAsync("OCR worker đóng trước khi Ready.");
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { NoteProtocolNoise(line); continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var typeNode)) continue;
                    var type = typeNode.GetString();
                    if (string.Equals(type, "fatal", StringComparison.Ordinal))
                    {
                        var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;
                        Kill();
                        throw await ClosedExceptionAsync(string.IsNullOrWhiteSpace(error) ? "OCR worker báo lỗi nghiêm trọng khi khởi động." : error);
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

    public async Task<OcrResult> RunAsync(string imageBase64, CancellationToken cancellationToken, bool recoverShortBlank = false, string? activeShortText = null, Action<OcrPerfSample>? onPerf = null)
    {
        if (string.IsNullOrWhiteSpace(imageBase64)) throw new ArgumentException("Ảnh OCR rỗng.", nameof(imageBase64));
        var payload = new Dictionary<string, object?>
        {
            ["image_base64"] = imageBase64,
            ["recover_short_blank"] = recoverShortBlank,
            ["active_short_text"] = activeShortText,
        };
        ApplyAmbientStyleFilter(payload);
        var result = await RunRequestAsync(
            payload,
            root =>
            {
                var sample = ParsePerf(root);
                if (sample is not null) onPerf?.Invoke(sample);
                return ParseResult(root);
            },
            cancellationToken);
        Interlocked.Increment(ref _imagesServed);
        return result;
    }

    public async Task<IReadOnlyList<OcrResult>> RunBatchAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken,
        bool recoverShortBlank = false,
        string? activeShortText = null,
        Action<OcrPerfSample>? onPerf = null)
    {
        // Giới hạn theo worker ĐANG CHẠY (parity 15/9: worker 27/8 từ chối batch >4),
        // không theo protocol repo mới (8). Batch 5-8 trước đây nổ ở worker thành
        // OcrRecognitionException ở window/refinement — fail ngay client cho rõ.
        if (imageBase64.Count is < 1 || imageBase64.Count > OcrRequestBatcher.WorkerBatchLimit || imageBase64.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Batch OCR phải có từ 1 đến {OcrRequestBatcher.WorkerBatchLimit} ảnh hợp lệ.", nameof(imageBase64));
        var payload = new Dictionary<string, object?>
        {
            ["images_base64"] = imageBase64,
            ["recover_short_blank"] = recoverShortBlank,
            ["active_short_text"] = activeShortText,
        };
        ApplyAmbientStyleFilter(payload);
        var results = await RunRequestAsync(
            payload,
            root =>
            {
                if (!root.TryGetProperty("ok", out var okNode) || !okNode.GetBoolean()
                    || !root.TryGetProperty("results", out var resultsNode)
                    || resultsNode.ValueKind != JsonValueKind.Array)
                {
                    var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;
                    throw new InvalidDataException(error ?? "OCR worker trả batch không hợp lệ.");
                }
                var parsed = resultsNode.EnumerateArray().Select(ParseResult).ToArray();
                if (parsed.Length != imageBase64.Count)
                    throw new InvalidDataException($"OCR worker trả {parsed.Length}/{imageBase64.Count} kết quả batch.");
                var sample = ParsePerf(root);
                if (sample is not null) onPerf?.Invoke(sample);
                return parsed;
            },
            cancellationToken);
        Interlocked.Add(ref _imagesServed, imageBase64.Count);
        return results;
    }

    // D.1 det/rec split: worker moi kem perf_ms (det/rec that do). Worker cu thieu
    // -> null, bo qua am tham (khong fail request vi thieu so do).
    private static OcrPerfSample? ParsePerf(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("perf_ms", out var node) || node.ValueKind != JsonValueKind.Object)
                return null;
            static double GetDouble(JsonElement obj, string name)
                => obj.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0 ? d : 0;
            static int GetInt(JsonElement obj, string name)
                => obj.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) && i >= 0 ? i : 0;
            return new OcrPerfSample(GetDouble(node, "det_ms"), GetDouble(node, "rec_ms"),
                GetInt(node, "det_boxes"), GetInt(node, "rec_crops"),
                GetInt(node, "cache_hits"), GetInt(node, "images"));
        }
        catch { return null; }
    }

    // Mức lọc tách chữ khỏi nền cho lượt quét ("medium"|"strong"), scanner tự đo
    // lúc bắt đầu rồi gán 1 lần. Mỗi lượt quét GÁN LẠI từ đầu (off -> null)
    // nên không rò sang lượt khác.
    internal static readonly AsyncLocal<string?> AmbientStyleFilter = new();

    private static void ApplyAmbientStyleFilter(Dictionary<string, object?> payload)
    {
        if (AmbientStyleFilter.Value is "medium" or "strong")
            payload["style_filter"] = AmbientStyleFilter.Value;
    }

    // Đo kiểu sub (style probe): worker trả điểm từng khung ROI. Worker cũ không
    // có op -> lỗi -> caller fail-open về "off".
    public async Task<IReadOnlyList<OcrSubtitleStyle.FrameScore>> ProbeSubtitleStyleAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken)
    {
        if (imageBase64.Count is < 2 or > 65 || imageBase64.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Style probe phải có từ 2 đến 65 ảnh hợp lệ.", nameof(imageBase64));
        return await RunRequestAsync(
            new Dictionary<string, object?> { ["style_probe_images_base64"] = imageBase64 },
            root =>
            {
                if (!root.TryGetProperty("ok", out var okNode) || !okNode.GetBoolean()
                    || !root.TryGetProperty("style_scores", out var scoresNode)
                    || scoresNode.ValueKind != JsonValueKind.Array)
                {
                    var error = root.TryGetProperty("error", out var errorNode) ? errorNode.GetString() : null;
                    throw new InvalidDataException(error ?? "OCR worker trả style probe không hợp lệ.");
                }
                var scores = new List<OcrSubtitleStyle.FrameScore>(imageBase64.Count);
                foreach (var item in scoresNode.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("bright", out var brightNode)
                        || !brightNode.TryGetDouble(out var bright)
                        || !double.IsFinite(bright) || bright is < 0 or > 1)
                        throw new InvalidDataException("OCR worker trả điểm style hỏng.");
                    double? ring = null;
                    if (item.TryGetProperty("ring", out var ringNode)
                        && ringNode.ValueKind == JsonValueKind.Number
                        && ringNode.TryGetDouble(out var ringValue)
                        && double.IsFinite(ringValue) && ringValue >= 0)
                        ring = ringValue;
                    scores.Add(new OcrSubtitleStyle.FrameScore(bright, ring));
                }
                if (scores.Count != imageBase64.Count)
                    throw new InvalidDataException($"OCR worker trả {scores.Count}/{imageBase64.Count} điểm style.");
                return (IReadOnlyList<OcrSubtitleStyle.FrameScore>)scores;
            },
            cancellationToken);
    }

    public async Task<IReadOnlyList<double>> ProbeVisualChangesAsync(
        IReadOnlyList<string> imageBase64,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeVisualChangesExAsync(imageBase64, cancellationToken);
        return probe.ChangeScores;
    }

    // E.3 shadow: doc them band_change_scores + edge_density NEU CO (worker moi).
    // Worker cu khong co -> null, shadow vang mat, production chay nhu cu.
    public async Task<VisualProbeResult> ProbeVisualChangesExAsync(
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
                return new VisualProbeResult(
                    scores,
                    ReadOptionalScores(root, "band_change_scores", imageBase64.Count - 1),
                    ReadOptionalScores(root, "edge_density", imageBase64.Count));
            },
            cancellationToken);
    }

    private static IReadOnlyList<double>? ReadOptionalScores(JsonElement root, string name, int expected)
    {
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Array)
            return null;
        var values = node.EnumerateArray().Select(score => score.GetDouble()).ToArray();
        if (values.Length != expected || values.Any(score => !double.IsFinite(score) || score is < 0 or > 1))
            return null;
        return values;
    }

    private async Task<T> RunRequestAsync<T>(
        IReadOnlyDictionary<string, object?> payload,
        Func<JsonElement, T> parse,
        CancellationToken cancellationToken)
    {
        try { await _requestGate.WaitAsync(cancellationToken); }
        catch (ObjectDisposedException)
        {
            // Worker bi huy giua chung (resize/teardown): loi phuc hoi duoc de lane
            // retry qua ReplaceWorkerAsync, thay vi crash voi ObjectDisposed kho hieu.
            throw new OcrWorkerUnavailableException("OCR worker bi huy trong luc teardown; retry tu checkpoint an toan.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var requestToken = timeout.Token;
        try
        {
            if (!IsAlive || _input is null || _output is null) throw new InvalidOperationException("OCR worker chưa sẵn sàng.");
            var id = Interlocked.Increment(ref _requestId);
            var requestFields = payload.ToDictionary(pair => pair.Key, pair => pair.Value);
            requestFields["id"] = id;
            var request = JsonSerializer.Serialize(requestFields, RequestJsonOptions);
            await _input.WriteLineAsync(request.AsMemory(), requestToken);
            await _input.FlushAsync(requestToken);
            
            using var registration = requestToken.Register(() => Kill());
            while (true)
            {
                var line = await _output.ReadLineAsync(requestToken);
                if (line is null) throw await ClosedExceptionAsync("OCR worker đóng khi đang xử lý.");
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                // Paddle/PaddleOCR có thể in diagnostic thẳng ra STDOUT (thay vì stderr):
                // trước đây vứt hẳn (continue) nên worker gào đúng nguyên nhân mà C# vẫn chờ
                // tới 90s rồi giết oan. Giữ lại vào đuôi pháp y để ca treo tới có tang chứng.
                catch (JsonException) { NoteProtocolNoise(line); continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("type", out var typeNode) && string.Equals(typeNode.GetString(), "fatal", StringComparison.Ordinal))
                    {
                        var fatal = root.TryGetProperty("error", out var fatalNode) ? fatalNode.GetString() : null;
                        Kill();
                        throw await ClosedExceptionAsync(string.IsNullOrWhiteSpace(fatal) ? "OCR worker báo lỗi nghiêm trọng." : fatal);
                    }
                    if (!root.TryGetProperty("id", out var idNode) || !idNode.TryGetInt64(out var responseId) || responseId != id) continue;
                    return parse(root);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(BuildTimeoutMessage(payload));
        }
        finally { _requestGate.Release(); }
    }

    // Phap y treo worker (vu lane chet hang loat): timeout 90s truoc day giet worker
    // ma khong ghi gi -> khong bao gio biet treo o batch may anh, op gi, stderr gi.
    // Gio loi mang theo: loai op + so anh + ~KB payload + tuoi worker + song/chet + PID + duoi stderr/stdout-noise.
    private string BuildTimeoutMessage(IReadOnlyDictionary<string, object?> payload)
    {
        var (op, images, bytes) = DescribePayload(payload);
        var served = Interlocked.Read(ref _imagesServed);
        int? pid;
        bool alive;
        int? exitCode = null;
        try
        {
            var process = _process;
            alive = process is { HasExited: false };
            pid = process?.Id;
            if (process is { HasExited: true }) exitCode = process.ExitCode;
        }
        catch { pid = null; alive = false; }
        string tail;
        lock (_stderrSync) tail = Compact(_stderrTail.ToString());
        return $"OCR worker khong phan hoi trong {RequestTimeout.TotalSeconds:0} giay ({op} {images} anh ~{bytes / 1024}KB, worker {served} anh, {(alive ? "con song luc giet" : "da chet truoc khi giet")}"
            + (pid is null ? string.Empty : $", PID={pid}")
            + (exitCode is null ? string.Empty : $", exit={exitCode}")
            + "); worker da duoc dung de tranh treo tac vu."
            + (tail.Length == 0 ? string.Empty : $" stderr/stdout cuoi: {tail}");
    }

    internal static (string Op, int Images, long Bytes) DescribePayload(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.TryGetValue("images_base64", out var batch) && batch is System.Collections.IList batchList)
            return ("batch", batchList.Count, SumLengths(batchList));
        if (payload.TryGetValue("probe_images_base64", out var probe) && probe is System.Collections.IList probeList)
            return ("probe", probeList.Count, SumLengths(probeList));
        if (payload.TryGetValue("style_probe_images_base64", out var style) && style is System.Collections.IList styleList)
            return ("style-probe", styleList.Count, SumLengths(styleList));
        if (payload.TryGetValue("image_base64", out var single) && single is string singleText)
            return ("single", 1, singleText.Length);
        return ("single", 1, 0);

        static long SumLengths(System.Collections.IList list)
        {
            long total = 0;
            foreach (var item in list)
                if (item is string text)
                    total += text.Length;
            return total;
        }
    }

    // Giũ rác stdout-protocol vào cùng đuôi stderr (chung giới hạn 16KB, cắt dòng dài):
    // Paddle traceback/OOM in ra stdout là tang chứng treo quý nhất, trước đây bị vứt.
    private void NoteProtocolNoise(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (line.Length > 4096) line = line[^4096..];
        lock (_stderrSync)
        {
            _stderrTail.Append("[stdout] ");
            _stderrTail.Append(line.Trim());
            _stderrTail.Append('\n');
            if (_stderrTail.Length > StderrTailLimit)
                _stderrTail.Remove(0, _stderrTail.Length - StderrTailLimit);
        }
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
            throw new InvalidDataException($"OCR worker không xác nhận đúng {OcrInstaller.DetectionModel} + {OcrInstaller.RecognitionModel}.");
        var device = root.TryGetProperty("device", out var deviceNode) ? deviceNode.GetString() ?? string.Empty : string.Empty;
        if (!device.StartsWith(_runtime.Kind, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OCR worker chạy sai thiết bị yêu cầu.");
    }

    public async Task StopAsync()
    {
        // Ghi chu quan trong: KHONG kiem tra flag _disposed o day. DisposeAsync set
        // flag TRUOC khi goi StopAsync, nen check o day se lam DisposeAsync thanh no-op
        // (worker khong bao gio duoc dung -> leak + test bat duoc). Tinh goi thua
        // vo hai do DisposeAsync dam bao (flag), StopAsync cu goi lai vo hai.
        Kill();
        try { await _requestGate.WaitAsync(); }
        catch (ObjectDisposedException) { return; }
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
                if (_stderrCompletion is not null)
                {
                    try { await _stderrCompletion.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
                }
                _process.Dispose();
            }
            _process = null;
            _input = null;
            _output = null;
            _stderrCompletion = null;
        }
        finally { _requestGate.Release(); }
        if (stopFailure is not null) throw stopFailure;
    }

    private void Kill()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
    }

    private async Task PumpStderrAsync(StreamReader reader)
    {
        var buffer = new char[2048];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            if (read == 0) return;
            lock (_stderrSync)
            {
                _stderrTail.Append(buffer, 0, read);
                if (_stderrTail.Length > StderrTailLimit)
                    _stderrTail.Remove(0, _stderrTail.Length - StderrTailLimit);
            }
        }
    }

    private async Task<OcrWorkerUnavailableException> ClosedExceptionAsync(string message)
    {
        var process = _process;
        var pid = process?.Id;
        if (process is not null)
        {
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        if (_stderrCompletion is not null)
        {
            try { await _stderrCompletion.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }
        int? exitCode = null;
        try { if (process is { HasExited: true }) exitCode = process.ExitCode; } catch { }
        string stderr;
        lock (_stderrSync) stderr = Compact(_stderrTail.ToString());
        var diagnostic = $"{message} Thiết bị={Kind.ToUpperInvariant()}"
            + (pid is null ? string.Empty : $", PID={pid}")
            + (exitCode is null ? string.Empty : $", exit={exitCode}")
            + (stderr.Length == 0 ? string.Empty : $". stderr: {stderr}");
        return new OcrWorkerUnavailableException(diagnostic);
    }

    private static string Compact(string value)
    {
        var text = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length > 1200 ? text[^1200..] : text;
    }

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        // Gọi 2 lần phải an toàn: lane-finally (OcrManager) và teardown pool có thể
        // cùng hủy một worker (vu double-dispose lam hong test recovery).
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await StopAsync(); }
        finally { _requestGate.Dispose(); }
    }
}

internal sealed class OcrWorkerUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
