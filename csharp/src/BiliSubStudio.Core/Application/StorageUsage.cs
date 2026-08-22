namespace BiliSubStudio.Core.Application;

public sealed record StorageUsage(long Data, long Tools, long Ocr, long Temp, long Cache)
{
    public long Total => Data + Tools + Temp + Cache;
}
