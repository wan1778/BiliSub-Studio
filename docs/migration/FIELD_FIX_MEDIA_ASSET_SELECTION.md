# Field fix — Tải media: mặc định đủ bộ + tải riêng

## User contract

Trang `Tải media` có ba lựa chọn tùy chọn: `Video`, `Thumbnail`, `Phụ đề`.

- Không chọn mục nào: tải bộ đầy đủ `Video + Thumbnail + Phụ đề nếu nguồn có`.
- Chọn một hoặc nhiều mục: chỉ tải đúng các mục đã chọn.
- Thumbnail/phụ đề được chọn nhưng nguồn không có: bỏ qua mục đó, ghi cảnh báo rõ ràng; không tự tải một asset khác để thay thế.
- Video được chọn: giữ toàn bộ long-media safeguards hiện có (highest accessible quality ordering, output-drive cache/resume, free-space preflight, Range/fallback resume).
- Legacy caller không bật `MediaBundle`: `StartVideo` vẫn tải video như trước; `BundleVideo=true` là mặc định tương thích ngược.

## UI hierarchy

`VideoPage` được tách thành bốn khối thao tác chính và một khối tiến độ:

1. Nguồn Bilibili.
2. Nội dung cần tải.
3. Chất lượng và định dạng.
4. Nơi lưu và bắt đầu.
5. Tiến độ / nhật ký.

Mục tiêu là giảm mật độ chữ/control trong một card duy nhất và làm rõ thứ tự thao tác.

## Call map

```text
VideoPage.LoadMetadata_Click
  -> BiliSubApplication.GetMetadataAsync
  -> YtDlpResolver.GetMetadataAsync
  -> VideoMetadata(qualities, subtitles, thumbnail)

VideoPage.Start_Click
  -> đọc VideoAssetCheckBox / ThumbnailAssetCheckBox / SubtitleAssetCheckBox
  -> nếu không checkbox nào được chọn:
       downloadVideo = true
       downloadThumbnail = true
       downloadSubtitle = true
  -> nếu có checkbox được chọn:
       mỗi download* phản ánh đúng checkbox tương ứng
  -> VideoDownloadRequest(
       MediaBundle=true,
       BundleVideo=downloadVideo,
       BundleThumbnail=downloadThumbnail,
       BundleSubtitleIfAvailable=downloadSubtitle)
  -> BiliSubApplication.StartVideo
       -> bundledVideo / bundledThumbnail / bundledSubtitle
       -> chỉ chạy phase tương ứng với asset được yêu cầu
       -> parent AppJob tổng hợp progress/log/cancel
```

## Impact boundary

Files changed for this behavior:

- `csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/VideoPage.xaml.cs`
- `csharp/src/BiliSubStudio.Core/Video/VideoModels.cs`
- `csharp/src/BiliSubStudio.Core/Application/BiliSubApplication.cs`
- `csharp/scripts/verify_media_bundle_contract.py`

Not changed:

- `RangeDownloader.cs`
- `VideoDownloadService.cs`
- `SubtitleService.cs`
- OCR
- Editor
- updater

Thus the low-level long-video transport/resume implementation is unchanged; this checkpoint changes asset selection/orchestration and media-page presentation only.

## Required gate

`verify_media_bundle_contract.py` must fail if any of these regressions return:

- no-selection no longer means all three assets;
- only Thumbnail/Sub still triggers video phase;
- separate asset controls disappear;
- legacy video caller loses default video behavior;
- long-media/highest-quality/resume contracts regress.

Windows compile, WinUI startup/layout, installer packaging and real-machine field QA remain mandatory after this source gate.
