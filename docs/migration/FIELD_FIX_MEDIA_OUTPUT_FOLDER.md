# Field fix: explicit media output folder

Status: field-QA pending. This document is a focused migration/field-test contract and does not promote a release.

## Requirement

The production `Tải media` page must never silently download into the app-owned default `.../BiliSub Studio/Downloads` directory.

- The page must visibly show the selected output directory.
- A native Windows `Chọn thư mục…` control must be available on the same page.
- If the persisted config still equals the app-owned bootstrap Downloads directory, the UI treats output as **not selected** and keeps the media Start action disabled.
- A user-selected directory is validated for write access and persisted for the next session.
- Video and subtitle phases of the bundled media job receive the exact same selected directory.
- If selection is cancelled, invalid, missing, or not writable, no media job is started.

## Call map

```text
VideoPage [Chọn thư mục…]
  -> existing IFolderPickerService.PickFolderAsync
  -> FolderPickerService (native Windows FolderPicker)
  -> BiliSubApplication.Settings.SetOutputDirectoryAsync
      -> Directory.CreateDirectory
      -> temporary write probe
      -> JsonConfigStore.UpdateAsync(OutputDirectory)
  -> VideoPage.OutputPathBox

VideoPage [Tải video + phụ đề]
  -> revalidate/persist OutputPathBox via SetOutputDirectoryAsync
  -> VideoDownloadRequest(OutputDirectory = exact selected path)
  -> BiliSubApplication.StartVideo
  -> bundled video phase + bundled subtitle phase
  -> both phases use the same request OutputDirectory
```

## Impact boundary

This fix changes only the media-page output-folder UX/composition and settings output-directory validation. It does **not** change Range download/resume, video transport, subtitle parsing/export, OCR, Editor, authentication, or updater semantics.

## Real-machine field gate

1. On a fresh/default config, `Tải media` shows `Chưa chọn thư mục lưu`; Start remains disabled even after metadata succeeds.
2. `Chọn thư mục…` opens the native Windows folder picker.
3. Cancelling the picker does not start a job and does not overwrite the saved path.
4. Choosing a writable folder shows that exact path and persists it.
5. After metadata succeeds, Start becomes enabled only when a subtitle track and an explicit output folder are both present.
6. The resulting video and subtitle files both appear in the selected folder.
7. Reopen the app: the previously selected path is visible and reused, while still remaining changeable before the next download.
8. A non-writable/invalid folder must fail before job creation with a visible error.
