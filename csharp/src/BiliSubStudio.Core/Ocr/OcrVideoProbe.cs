namespace BiliSubStudio.Core.Ocr;

public sealed record VideoProbe(int Width, int Height, double TimeBaseSeconds, double FrameDurationSeconds, double AvgFps, double StartTimeSeconds, int TimeBaseNum, int TimeBaseDen, double StreamDurationSeconds = 0);
