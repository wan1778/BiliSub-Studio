using BiliSubStudio.Core.Jobs;

namespace BiliSubStudio.App.ViewModels;

public sealed class MediaQueueItem : BindableBase
{
    private string _stateText = "Đang chờ";
    private string _detail = "Đã thêm vào hàng đợi.";
    private double _progress;
    private bool _canCancel = true;
    private bool _done;
    private string _statusCode = "queued";

    public MediaQueueItem(int number, string jobId, string title, string url, string outputDirectory)
    {
        Number = number;
        JobId = jobId;
        Title = string.IsNullOrWhiteSpace(title) ? url : title;
        Url = url;
        OutputDirectory = outputDirectory;
    }

    public int Number { get; }
    public string JobId { get; }
    public string Title { get; }
    public string Url { get; }
    public string OutputDirectory { get; }
    public string DisplayTitle => $"{Number}. {Title}";
    public string StateText { get => _stateText; private set => SetProperty(ref _stateText, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool CanCancel { get => _canCancel; private set => SetProperty(ref _canCancel, value); }
    public bool Done { get => _done; private set => SetProperty(ref _done, value); }
    public string StatusCode { get => _statusCode; private set => SetProperty(ref _statusCode, value); }

    public void Apply(JobSnapshot snapshot)
    {
        StatusCode = snapshot.Status;
        Progress = Math.Clamp(snapshot.Progress, 0, 100);
        Detail = snapshot.Message;
        Done = snapshot.Done;
        CanCancel = !snapshot.Done && !string.Equals(snapshot.Status, "cancelling", StringComparison.OrdinalIgnoreCase);
        StateText = snapshot.Status switch
        {
            "queued" => "Đang chờ",
            "starting" => "Đang chuẩn bị",
            "cancelling" => "Đang hủy",
            "cancelled" => "Đã hủy",
            "done" => "Hoàn tất",
            "error" => "Có lỗi",
            _ => snapshot.Done ? "Hoàn tất" : "Đang tải",
        };
    }
}
