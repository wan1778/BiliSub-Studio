using System.Text.Json;

namespace BiliSubStudio.Core.Editor;

// ASR-only persistence. Never delete the destination to work around a Windows
// sharing violation, or overwrite its contents in place with a partial JSON.
internal static class AsrCheckpointFile
{
    private static readonly int[] RetryDelaysMilliseconds = [100, 200, 400, 800, 1000];

    internal static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions json,
        CancellationToken cancellationToken, Action<string>? warning = null)
    {
        path = Path.GetFullPath(path);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        var prepared = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            // Retain a complete flushed snapshot if publication is denied or
            // cancelled. Partial serialization is never a recovery checkpoint.
            prepared = true;
            await PublishAsync(temporary, path, cancellationToken, warning);
        }
        catch (OperationCanceledException)
        {
            if (prepared && File.Exists(temporary))
                warning?.Invoke($"Đã hủy lưu checkpoint; giữ bản ghi hoàn chỉnh để phục hồi: {temporary}");
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var retained = prepared && File.Exists(temporary)
                ? $" Bản mới đã ghi đầy đủ, giữ tại: {temporary}."
                : " Không có bản mới đã xác nhận ghi đầy đủ.";
            throw new IOException($"Không thể lưu checkpoint Whisper: {path}. "
                + $"Đích: {DescribePath(path)}; dự phòng: {path}.bak ({DescribePath(path + ".bak")}). "
                + $"Lỗi {error.GetType().Name}, HRESULT=0x{error.HResult:X8}: {error.Message}."
                + retained + " Không xóa checkpoint để ép ghi. Kiểm tra ứng dụng đang giữ file, quyền ghi/thay thế file "
                + "và thông báo bảo vệ thư mục của Windows; không cần cài lại CUDA/model.", error);
        }
        finally
        {
            if (!prepared)
            {
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    warning?.Invoke($"Không dọn được file checkpoint ghi dở: {temporary}; HRESULT=0x{error.HResult:X8}.");
                }
            }
        }
    }

    private static async Task PublishAsync(string temporary, string path, CancellationToken cancellationToken,
        Action<string>? warning)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path))
                    File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: false);
                else
                    File.Move(temporary, path); // First write; never overwrite a racing writer blindly.
                return;
            }
            catch (Exception error) when (attempt < RetryDelaysMilliseconds.Length
                && IsRetryable(error) && File.Exists(temporary) && !IsProtectedTarget(path) && !IsProtectedTarget(path + ".bak"))
            {
                if (attempt == 0)
                    warning?.Invoke($"Windows đang từ chối thay checkpoint Whisper: {path}; HRESULT=0x{error.HResult:X8}. "
                        + "Đang thử lại có giới hạn, giữ checkpoint trước đó.");
                await Task.Delay(RetryDelaysMilliseconds[attempt], cancellationToken);
            }
        }
    }

    // 1175 leaves both original names intact. Do not blindly retry 1176/1177,
    // where ReplaceFile has special recovery/metadata semantics.
    private static bool IsRetryable(Exception error) => error is UnauthorizedAccessException
        || error is IOException && (error.HResult & 0xffff) is 2 or 5 or 32 or 33 or 80 or 183 or 1175;

    private static bool IsProtectedTarget(string path)
    {
        try { return (File.GetAttributes(path) & (FileAttributes.ReadOnly | FileAttributes.Directory)) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }

    private static string DescribePath(string path)
    {
        try { return File.GetAttributes(path).ToString(); }
        catch (FileNotFoundException) { return "không tồn tại"; }
        catch (DirectoryNotFoundException) { return "không có thư mục"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"không đọc được thuộc tính, HRESULT=0x{error.HResult:X8}";
        }
    }
}
