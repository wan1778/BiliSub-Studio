namespace BiliSubStudio.Core.Editor;

/// <summary>
/// Một đoạn video trong dải thời gian ghép. Mỗi clip giữ đường dẫn nguồn và
/// khoảng [Start, End] tính trong chính file nguồn đó; vị trí trên dải thời
/// gian ghép là tổng thời lượng của các clip đứng trước nó.
/// </summary>
public sealed record EditorTimelineClip(
    string Id,
    string SourcePath,
    double Start,
    double End,
    string Label)
{
    public double Duration => Math.Max(0, End - Start);
}

public sealed record EditorTimelineProject(IReadOnlyList<EditorTimelineClip> Clips);
