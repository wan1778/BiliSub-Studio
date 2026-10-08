using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Application;

public interface IStorageUsageReader
{
    Task<StorageUsage> ReadAsync(AppPaths paths, CancellationToken cancellationToken = default);

    // Vụ kẹt nút Tải media 05/10: quét đệ quy ~900k file (Tools + Cache
    // voice) tốn hàng chục phút, mọi Set* settings đều đứng đợi. Đường
    // settings chỉ cần số hiển thị: lấy số cũ trả ngay + quét nền.
    bool TryGetLast(out StorageUsage usage);
    void RefreshInBackground(AppPaths paths);
}
