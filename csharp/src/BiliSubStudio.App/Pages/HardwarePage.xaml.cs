using BiliSubStudio.Core.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BiliSubStudio.App.Pages;

public sealed partial class HardwarePage : Page
{
    private readonly BiliSubApplication _application;
    public HardwarePage(BiliSubApplication application) { _application = application; InitializeComponent(); Loaded += (_, _) => RenderSnapshot(); }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RenderSnapshot();
    private void RenderSnapshot() { var hardware = _application.Hardware.Snapshot(); CpuText.Text = $"CPU: {hardware.CpuName} · {hardware.LogicalProcessors} luồng"; MemoryText.Text = $"RAM khả dụng: {hardware.MemoryBytes / 1024d / 1024 / 1024:0.0} GiB"; GpuText.Text = hardware.NvidiaDetected ? $"GPU: {hardware.GpuName} · {hardware.CudaDriver} · {hardware.VramBytes / 1024d / 1024 / 1024:0.0} GiB VRAM" : "GPU: không phát hiện NVIDIA CUDA"; var tools = _application.Tools.Status; ToolsText.Text = $"yt-dlp: {(tools.YtDlpReady ? "Ready" : "thiếu")} · FFmpeg: {(tools.FfmpegReady ? "Ready" : "thiếu")} · FFprobe: {(tools.FfprobeReady ? "Ready" : "thiếu")}"; }
    private async void PrepareTools_Click(object sender, RoutedEventArgs e) { try { SetBusy(true); ResultText.Text = "Đang tải công cụ portable..."; await _application.Tools.EnsureYtDlpAsync(CancellationToken.None); await _application.Tools.EnsureFfmpegAsync(CancellationToken.None); RenderSnapshot(); ResultText.Text = "Công cụ đã sẵn sàng."; } catch (Exception error) { ResultText.Text = error.Message; } finally { SetBusy(false); } }
    private async void PrepareOcr_Click(object sender, RoutedEventArgs e) { try { SetBusy(true); ResultText.Text = "Đang chuẩn bị OCR Auto..."; var status = await _application.PrepareOcrAsync("auto", CancellationToken.None); ResultText.Text = $"OCR {status.ActiveMode} Ready · {status.Workers} worker"; } catch (Exception error) { ResultText.Text = error.Message; } finally { SetBusy(false); } }
    private async void Benchmark_Click(object sender, RoutedEventArgs e) { try { SetBusy(true); ResultText.Text = "Đang benchmark CPU và RAM..."; var result = await _application.Hardware.BenchmarkAsync(CancellationToken.None); ResultText.Text = $"CPU SHA-256: {result.CpuMegabytesPerSecond:0} MiB/s · RAM copy: {result.MemoryMegabytesPerSecond:0} MiB/s · đề xuất tối đa {result.RecommendedOcrLanes} OCR lane"; } catch (Exception error) { ResultText.Text = error.Message; } finally { SetBusy(false); } }

    private void SetBusy(bool busy)
    {
        Progress.IsIndeterminate = busy;
        RefreshButton.IsEnabled = !busy;
        PrepareToolsButton.IsEnabled = !busy;
        PrepareOcrButton.IsEnabled = !busy;
        BenchmarkButton.IsEnabled = !busy;
    }
}
