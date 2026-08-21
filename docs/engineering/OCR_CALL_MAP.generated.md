# OCR generated symbol call map

> GENERATED FROM CURRENT `internal/ocr` SOURCE by `scripts/generate_ocr_call_map.go`. Do not hand-edit.
> Every production Go function in `internal/ocr` is listed with its source location and direct call expressions. `--check` blocks a stale map.

## Ownership boundary

```text
web OCR controls
  -> preview probe: MP4 H.264/HEVC/AV1 -> direct <video> attempt -> ocrDirectPlaybackReady -> idle Play/Mute; video.onerror -> FFmpeg-frame fallback with Play/Mute disabled
  -> internal/api OCR handlers
     -> internal/ocr Manager / Scanner
        -> device detection + CPU/GPU runtime installer + private PaddleOCR worker(s)
        -> RC13 parallel segment coordinator + bounded FFmpeg/NVDEC lanes + sparse visual gate + shared dynamic OCR worker pool + lane-local subtitle trackers + strict Chinese-only cue normalization + core/boundary reconciliation + schema-4 pause/resume checkpoint (schema 3 retained for legacy)
```

Production OCR Go functions: **183**

| Function | Source | Direct calls |
|---|---|---|
| `normalizeDeviceMode` | `internal/ocr/device.go:25` | `fmt.Errorf`, `strings.ToLower`, `strings.TrimSpace` |
| `detectNVIDIAGPU` | `internal/ocr/device.go:41` | `detectNVIDIAGPUPlatform` |
| `gpuWheelForCUDADriver` | `internal/ocr/device.go:45` | _leaf / external state only_ |
| `formatCUDADriverVersion` | `internal/ocr/device.go:60` | `fmt.Sprintf` |
| `detectNVIDIAGPUPlatform` | `internal/ocr/gpu_probe_other.go:7` | _leaf / external state only_ |
| `probeNVIDIAResourcesPlatform` | `internal/ocr/gpu_probe_other.go:11` | _leaf / external state only_ |
| `detectNVIDIAGPUPlatform` | `internal/ocr/gpu_probe_windows.go:40` | `Error`, `bytesBeforeNUL`, `ctx.Done`, `ctx.Err`, `cuDeviceGet.Call`, `cuDeviceGetCount.Call`, `cuDeviceGetName.Call`, `cuDriverGetVersion.Call`, `cuInit.Call`, `cuda.FindProc`, `cuda.Release`, `fmt.Sprintf`, `formatCUDADriverVersion`, `gpuWheelForCUDADriver`, `int`, `loadNVML`, `nvml.close`, `nvmlDriverVersion`, `string`, `syscall.LoadDLL`, `uintptr`, `unsafe.Pointer` |
| `probeNVIDIAResourcesPlatform` | `internal/ocr/gpu_probe_windows.go:113` | `ctx.Done`, `float64`, `lib.close`, `lib.deviceGetCount.Call`, `lib.deviceGetHandle.Call`, `lib.deviceMemory.Call`, `lib.deviceUtilization.Call`, `loadNVML`, `uintptr`, `unsafe.Pointer` |
| `loadNVML` | `internal/ocr/gpu_probe_windows.go:153` | `dll.Release`, `fmt.Errorf`, `lib.bind`, `lib.init.Call`, `nvmlCandidates`, `syscall.LoadDLL` |
| `nvmlLibrary.bind` | `internal/ocr/gpu_probe_windows.go:181` | `n.dll.FindProc` |
| `nvmlLibrary.close` | `internal/ocr/gpu_probe_windows.go:210` | `n.dll.Release`, `n.shutdown.Call` |
| `nvmlDriverVersion` | `internal/ocr/gpu_probe_windows.go:220` | `bytesBeforeNUL`, `n.systemDriver.Call`, `string`, `strings.TrimSpace`, `uintptr`, `unsafe.Pointer` |
| `nvmlCandidates` | `internal/ocr/gpu_probe_windows.go:236` | `add`, `filepath.Join`, `os.Getenv`, `strings.ToLower`, `strings.TrimSpace` |
| `bytesBeforeNUL` | `internal/ocr/gpu_probe_windows.go:262` | _leaf / external state only_ |
| `runtimeSpec` | `internal/ocr/install.go:67` | `errors.New`, `fmt.Errorf`, `strings.TrimSpace` |
| `expectedInstallManifest` | `internal/ocr/install.go:85` | `hex.EncodeToString`, `runtimeSpec`, `sha256.Sum256` |
| `installPaths` | `internal/ocr/install.go:100` | `filepath.Join`, `runtimeSpec` |
| `Manager.ensureInstalled` | `internal/ocr/install.go:115` | `fmt.Errorf`, `installPaths`, `m.installManagedRuntime`, `validateInstall` |
| `Manager.installManagedRuntime` | `internal/ocr/install.go:132` | `filepath.Join`, `installPaths`, `m.ensureUV`, `managedRuntimeEnv`, `os.MkdirAll`, `runInstallerCommand`, `runtimeSpec`, `writeInstallManifest`, `writeWorker` |
| `managedRuntimeEnv` | `internal/ocr/install.go:176` | `filepath.Join`, `os.Environ` |
| `runInstallerCommand` | `internal/ocr/install.go:189` | `cmd.CombinedOutput`, `exec.CommandContext`, `fmt.Errorf`, `proc.Hide`, `string`, `strings.Join`, `strings.TrimSpace` |
| `Manager.ensureUV` | `internal/ocr/install.go:203` | `downloadFile`, `filepath.Join`, `fmt.Errorf`, `os.MkdirAll`, `os.Remove`, `os.Stat`, `st.IsDir`, `st.Size`, `unzipNamedFile`, `verifySHA256File` |
| `downloadFile` | `internal/ocr/install.go:231` | `f.Close`, `f.Sync`, `fmt.Errorf`, `http.DefaultClient.Do`, `http.NewRequestWithContext`, `io.Copy`, `os.Create`, `os.Remove`, `os.Rename`, `req.Header.Set`, `resp.Body.Close` |
| `verifySHA256File` | `internal/ocr/install.go:266` | `errors.New`, `f.Close`, `fmt.Errorf`, `h.Sum`, `hex.EncodeToString`, `io.Copy`, `os.Open`, `os.ReadFile`, `sha256.New`, `string`, `strings.EqualFold`, `strings.Fields` |
| `unzipNamedFile` | `internal/ocr/install.go:291` | `f.Open`, `filepath.Base`, `fmt.Errorf`, `io.Copy`, `os.Create`, `os.Remove`, `os.Rename`, `out.Close`, `out.Sync`, `r.Close`, `rc.Close`, `strings.EqualFold`, `zip.OpenReader` |
| `writeWorker` | `internal/ocr/install.go:330` | `filepath.Join`, `os.Rename`, `os.WriteFile` |
| `runtimeManifestPath` | `internal/ocr/install.go:339` | `filepath.Join` |
| `writeInstallManifest` | `internal/ocr/install.go:343` | `expectedInstallManifest`, `filepath.Dir`, `json.MarshalIndent`, `os.MkdirAll`, `os.Rename`, `os.WriteFile`, `runtimeManifestPath` |
| `validateInstall` | `internal/ocr/install.go:363` | `errors.New`, `expectedInstallManifest`, `fmt.Errorf`, `hex.EncodeToString`, `installPaths`, `json.Unmarshal`, `os.MkdirAll`, `os.ReadFile`, `os.Stat`, `runtimeManifestPath`, `sha256.Sum256`, `st.IsDir`, `st.Size` |
| `Manager.cleanupLegacy` | `internal/ocr/install.go:406` | `errors.Is`, `filepath.Clean`, `filepath.Dir`, `filepath.Join`, `os.Remove`, `os.RemoveAll`, `os.Stat` |
| `Manager.resetRuntimeForRepair` | `internal/ocr/install.go:423` | `filepath.Join`, `os.RemoveAll` |
| `New` | `internal/ocr/manager.go:139` | _leaf / external state only_ |
| `Manager.ConfigureDevice` | `internal/ocr/manager.go:147` | `m.mu.Lock`, `m.mu.Unlock`, `m.stopWorkers`, `normalizeDeviceMode` |
| `Manager.RefreshCapabilities` | `internal/ocr/manager.go:170` | `detectNVIDIAGPU`, `m.mu.Lock`, `m.mu.Unlock` |
| `Manager.Status` | `internal/ocr/manager.go:189` | `fmt.Sprintf`, `m.allWorkersLocked`, `m.mu.Lock`, `m.mu.Unlock`, `sort.Strings`, `strings.Join`, `w.status` |
| `Manager.allWorkersLocked` | `internal/ocr/manager.go:239` | `appendKind` |
| `Manager.Ensure` | `internal/ocr/manager.go:256` | `gpuErr.Error`, `gpuUnavailableError`, `m.RefreshCapabilities`, `m.cleanupLegacy`, `m.ensureMu.Lock`, `m.ensureMu.Unlock`, `m.fail`, `m.mu.Lock`, `m.mu.Unlock`, `m.readyForLocked`, `m.startMode`, `m.stopWorkers`, `strings.TrimSpace` |
| `gpuUnavailableError` | `internal/ocr/manager.go:319` | `errors.New`, `strings.TrimSpace` |
| `Manager.readyForLocked` | `internal/ocr/manager.go:326` | _leaf / external state only_ |
| `Manager.startMode` | `internal/ocr/manager.go:336` | `fmt.Errorf`, `m.mu.Lock`, `m.mu.Unlock`, `m.startRuntimeWithRepair`, `m.stopWorkers`, `startOne` |
| `Manager.startRuntimeWithRepair` | `internal/ocr/manager.go:385` | `m.ensureInstalled`, `m.resetRuntimeForRepair`, `m.workerExited`, `start`, `w.start`, `w.stop` |
| `Manager.workerExited` | `internal/ocr/manager.go:409` | `err.Error`, `m.mu.Lock`, `m.mu.Unlock` |
| `Manager.fail` | `internal/ocr/manager.go:460` | `err.Error`, `m.mu.Lock`, `m.mu.Unlock` |
| `Manager.Stop` | `internal/ocr/manager.go:468` | `m.mu.Lock`, `m.mu.Unlock`, `m.stopWorkers` |
| `Manager.stopWorkers` | `internal/ocr/manager.go:481` | `m.mu.Lock`, `m.mu.Unlock`, `w.stop` |
| `Manager.Remove` | `internal/ocr/manager.go:509` | `filepath.Clean`, `filepath.Dir`, `filepath.Join`, `m.Stop`, `os.RemoveAll` |
| `Manager.Parallelism` | `internal/ocr/manager.go:521` | `m.ActiveScanWorkers` |
| `Manager.ActiveScanWorkers` | `internal/ocr/manager.go:525` | `m.allWorkersLocked`, `m.mu.Lock`, `m.mu.Unlock` |
| `Manager.BatchCapable` | `internal/ocr/manager.go:543` | `m.mu.Lock`, `m.mu.Unlock` |
| `Manager.RunBatch` | `internal/ocr/manager.go:549` | `errors.Is`, `errors.New`, `fmt.Errorf`, `m.Ensure`, `m.acquireWorker`, `m.mu.Lock`, `m.mu.Unlock`, `m.releaseWorker`, `runErr.Error`, `strings.TrimSpace`, `w.run`, `w.runBatch` |
| `Manager.Run` | `internal/ocr/manager.go:589` | `errors.Is`, `errors.New`, `m.Ensure`, `m.acquireWorker`, `m.mu.Lock`, `m.mu.Unlock`, `m.releaseWorker`, `runErr.Error`, `strings.TrimSpace`, `w.run` |
| `Manager.ConfigureScanWorkers` | `internal/ocr/manager.go:611` | `errors.New`, `fmt.Errorf`, `m.ActiveScanWorkers`, `m.Ensure`, `m.mu.Lock`, `m.mu.Unlock`, `m.resizeWorkerKind` |
| `Manager.ResetScanWorkers` | `internal/ocr/manager.go:658` | `fmt.Errorf`, `m.ConfigureScanWorkers`, `m.Stop` |
| `Manager.resizeWorkerKind` | `internal/ocr/manager.go:668` | `ctx.Done`, `ctx.Err`, `fmt.Errorf`, `m.allWorkersLocked`, `m.mu.Lock`, `m.mu.Unlock`, `m.startRuntimeExisting`, `worker.stop` |
| `Manager.startRuntimeExisting` | `internal/ocr/manager.go:715` | `m.ensureInstalled`, `m.workerExited`, `w.start`, `w.stop` |
| `Manager.acquireWorker` | `internal/ocr/manager.go:729` | `ctx.Done`, `ctx.Err`, `errors.New`, `m.allWorkersLocked`, `m.mu.Lock`, `m.mu.Unlock`, `strings.TrimSpace` |
| `Manager.releaseWorker` | `internal/ocr/manager.go:773` | `m.mu.Lock`, `m.mu.Unlock` |
| `workerClient.start` | `internal/ocr/manager.go:792` | `cmd.Process.Kill`, `cmd.Start`, `cmd.StderrPipe`, `cmd.StdinPipe`, `cmd.StdoutPipe`, `cmd.Wait`, `ctx.Done`, `ctx.Err`, `err.Error`, `exec.Command`, `fmt.Errorf`, `io.ReadAll`, `onExit`, `os.Environ`, `parseWorkerReady`, `proc.Hide`, `scanLines`, `string`, `strings.TrimSpace`, `time.NewTimer`, `timer.Stop`, `w.mu.Lock`, `w.mu.Unlock` |
| `workerClient.stop` | `internal/ocr/manager.go:901` | `cmd.Process.Kill`, `w.mu.Lock`, `w.mu.Unlock` |
| `workerClient.status` | `internal/ocr/manager.go:918` | `w.mu.Lock`, `w.mu.Unlock` |
| `workerClient.abort` | `internal/ocr/manager.go:927` | `cmd.Process.Kill`, `err.Error`, `w.mu.Lock`, `w.mu.Unlock` |
| `workerClient.run` | `internal/ocr/manager.go:939` | `ctx.Done`, `ctx.Err`, `errors.New`, `fmt.Errorf`, `json.Marshal`, `parseWorkerResult`, `stdin.Write`, `time.NewTimer`, `timer.Stop`, `w.abort`, `w.mu.Lock`, `w.mu.Unlock`, `w.runMu.Lock`, `w.runMu.Unlock` |
| `workerClient.runBatch` | `internal/ocr/manager.go:999` | `ctx.Done`, `ctx.Err`, `errors.New`, `fmt.Errorf`, `json.Marshal`, `parseWorkerBatchResult`, `stdin.Write`, `time.NewTimer`, `timer.Stop`, `w.abort`, `w.mu.Lock`, `w.mu.Unlock`, `w.runMu.Lock`, `w.runMu.Unlock` |
| `scanLines` | `internal/ocr/manager.go:1058` | `bufio.NewScanner`, `sc.Buffer`, `sc.Scan`, `sc.Text`, `strings.TrimSpace` |
| `parseWorkerReady` | `internal/ocr/manager.go:1071` | `errors.New`, `fmt.Errorf`, `json.Unmarshal`, `strings.HasPrefix` |
| `parseWorkerResult` | `internal/ocr/manager.go:1099` | `errors.New`, `json.Unmarshal` |
| `parseWorkerBatchResult` | `internal/ocr/manager.go:1116` | `errors.New`, `json.Unmarshal`, `strings.TrimSpace` |
| `normalizeScanBatch` | `internal/ocr/scanner.go:162` | `fmt.Errorf`, `strings.ToLower`, `strings.TrimSpace` |
| `explicitScanBatchSize` | `internal/ocr/scanner.go:175` | _leaf / external state only_ |
| `ocrBatchCapable` | `internal/ocr/scanner.go:186` | `runner.BatchCapable` |
| `benchmarkScanBatch` | `internal/ocr/scanner.go:191` | `fmt.Errorf`, `runner.BatchCapable`, `runner.RunBatch`, `time.Duration`, `time.Now`, `time.Since` |
| `scanModeFor` | `internal/ocr/scanner.go:226` | `strings.ToLower`, `strings.TrimSpace` |
| `scanFilter` | `internal/ocr/scanner.go:251` | `fmt.Sprintf` |
| `scanFFmpegArgs` | `internal/ocr/scanner.go:265` | `scanFFmpegArgsRange` |
| `scanFFmpegArgsRange` | `internal/ocr/scanner.go:269` | `fmt.Sprintf` |
| `compactFFmpegError` | `internal/ocr/scanner.go:288` | `strings.Fields`, `strings.Join`, `strings.TrimSpace` |
| `probeNVDEC` | `internal/ocr/scanner.go:296` | `cancel`, `cmd.Run`, `compactFFmpegError`, `context.WithTimeout`, `err.Error`, `errors.Is`, `exec.CommandContext`, `fmt.Sprintf`, `probeCtx.Err`, `proc.Hide`, `scanFilter`, `stderr.String` |
| `newScanFrameReader` | `internal/ocr/scanner.go:338` | `ctx.Done`, `io.ReadFull`, `time.Now`, `time.Since` |
| `scanFrameReader.next` | `internal/ocr/scanner.go:366` | `ctx.Done`, `ctx.Err`, `time.NewTimer`, `timer.Stop` |
| `Scanner.Run` | `internal/ocr/scanner.go:393` | `benchDuration.Seconds`, `explicitScanParallelism`, `fmt.Errorf`, `job.Logf`, `job.SetResult`, `legacyCheckpointAvailable`, `maxParallelismForDuration`, `newParallelCheckpointSession`, `normalizeScanParallelism`, `s.run`, `s.runParallel`, `s.selectAutoParallelism`, `strings.TrimSpace` |
| `Scanner.run` | `internal/ocr/scanner.go:457` | `Add`, `NormalizeChineseSubtitleText`, `Seconds`, `batchBenchmarkDuration.Seconds`, `benchDuration.Seconds`, `benchmarkScanBatch`, `boolInt`, `buildLiveScanResult`, `captureSafeState`, `cfg.OnProgress`, `cfg.OnSafeState`, `cfg.PauseRequested`, `checkpoint.MaybeSaveWithStats`, `checkpoint.Remove`, `checkpoint.SaveNowWithStats`, `checkpointStats`, `cmd.Process.Kill`, `cmd.Start`, `cmd.StderrPipe`, `cmd.StdoutPipe`, `cmd.Wait`, `commitCandidate`, `commitVisualFrame`, `compactFFmpegError`, `context.WithCancel`, `ctx.Err`, `edgeSignatureActivity`, `edgeSignatureDiff`, `encodeDuration.Seconds`, `errors.Is`, `errors.New`, `exec.CommandContext`, `explicitScanBatchSize`, `float64`, `fmt.Errorf`, `fmt.Sprintf`, `frameCancel`, `framePipelineDuration.Seconds`, `frameReader.next`, `io.LimitReader`, `io.ReadAll`, `job.Logf`, `job.PauseRequested`, `job.Set`, `job.SetResult`, `lastPublish.IsZero`, `makeEdgeSignature`, `math.Max`, `math.Min`, `maybeSave`, `newScanCheckpointSession`, `newScanFrameReader`, `newScanOCRCandidate`, `newSubtitleTracker`, `nextEvaluatedFrame`, `normalizeScanBatch`, `normalizeScanRegion`, `now.Sub`, `ocrBatchCapable`, `ocrDuration.Seconds`, `ocrRunnerParallelism`, `os.Stat`, `probeNVDEC`, `proc.Hide`, `publish`, `runScanOCRCandidates`, `s.run`, `saveNow`, `scanClock`, `scanFFmpegArgsRange`, `scanFilter`, `scanModeFor`, `shouldRunOCR`, `st.IsDir`, `st.Size`, `string`, `strings.TrimSpace`, `time.Duration`, `time.Now`, `time.Since`, `time.Until`, `tracker.Active`, `tracker.CanCheckpoint`, `tracker.CanVisualConfirm`, `tracker.CanVisualConfirmEmpty`, `tracker.ConfirmVisual`, `tracker.ConfirmVisualEmpty`, `tracker.Cues`, `tracker.ExtendActiveVisual`, `tracker.Finish`, `tracker.HasActive`, `tracker.NeedsConfirmation`, `tracker.Observe`, `tracker.Restore`, `tryPause`, `visualBlankStable`, `visualDuration.Seconds`, `visualFrameStable`, `waitErr.Error` |
| `ocrRunnerParallelism` | `internal/ocr/scanner.go:1052` | `p.Parallelism` |
| `newScanOCRCandidate` | `internal/ocr/scanner.go:1067` | `framePNGBase64`, `time.Now`, `time.Since` |
| `runScanOCRCandidates` | `internal/ocr/scanner.go:1086` | `engine.Run`, `fmt.Errorf`, `framePNGBase64`, `ocrRunnerParallelism`, `runBatch`, `runSingle`, `runner.BatchCapable`, `runner.RunBatch`, `strings.TrimSpace`, `time.Now`, `time.Since`, `wg.Add`, `wg.Done`, `wg.Wait` |
| `buildLiveScanResult` | `internal/ocr/scanner.go:1237` | `batchBenchmarkDuration.Seconds`, `boolInt`, `encodeDuration.Seconds`, `float64`, `framePipelineDuration.Seconds`, `ocrDuration.Seconds`, `visualDuration.Seconds` |
| `CaptureFramePNGBase64` | `internal/ocr/scanner.go:1297` | `cmd.Output`, `errors.New`, `exec.CommandContext`, `fmt.Errorf`, `fmt.Sprintf`, `framePNGBase64`, `math.IsInf`, `math.IsNaN`, `normalizeScanRegion`, `os.Stat`, `proc.Hide`, `st.IsDir`, `st.Size`, `strings.TrimSpace` |
| `normalizeScanRegion` | `internal/ocr/scanner.go:1340` | `errors.New` |
| `makeEdgeSignature` | `internal/ocr/scanner.go:1370` | `subtitleLikePixel` |
| `subtitleLikePixel` | `internal/ocr/scanner.go:1399` | `int`, `lumAt`, `maxInt`, `minInt` |
| `edgeSignatureActivity` | `internal/ocr/scanner.go:1421` | `float64` |
| `visualFrameStable` | `internal/ocr/scanner.go:1436` | _leaf / external state only_ |
| `visualBlankStable` | `internal/ocr/scanner.go:1445` | _leaf / external state only_ |
| `shouldRunOCR` | `internal/ocr/scanner.go:1452` | _leaf / external state only_ |
| `edgeSignatureDiff` | `internal/ocr/scanner.go:1467` | `float64` |
| `lumAt` | `internal/ocr/scanner.go:1489` | `int` |
| `framePNGBase64` | `internal/ocr/scanner.go:1494` | `base64.StdEncoding.EncodeToString`, `byte`, `clampInt`, `enc.Encode`, `errors.New`, `image.NewNRGBA`, `image.Rect`, `int`, `out.Bytes` |
| `cleanScanText` | `internal/ocr/scanner.go:1516` | `strings.Fields`, `strings.Join`, `strings.ReplaceAll`, `strings.TrimSpace` |
| `comparableScanText` | `internal/ocr/scanner.go:1520` | `cleanScanText`, `repl.Replace`, `strings.NewReplacer` |
| `scanSimilarity` | `internal/ocr/scanner.go:1526` | `comparableScanText`, `float64`, `minInt` |
| `scanClock` | `internal/ocr/scanner.go:1559` | `fmt.Sprintf`, `int64`, `math.IsInf`, `math.IsNaN` |
| `clampInt` | `internal/ocr/scanner.go:1573` | _leaf / external state only_ |
| `absInt` | `internal/ocr/scanner.go:1583` | _leaf / external state only_ |
| `minInt` | `internal/ocr/scanner.go:1590` | _leaf / external state only_ |
| `maxInt` | `internal/ocr/scanner.go:1600` | _leaf / external state only_ |
| `boolInt` | `internal/ocr/scanner.go:1610` | _leaf / external state only_ |
| `scanMode.String` | `internal/ocr/scanner.go:1619` | `strconv.FormatFloat` |
| `InspectCheckpoint` | `internal/ocr/scanner_checkpoint.go:66` | `boolInt`, `math.Max`, `math.Min`, `parallelCheckpointInfo`, `readParallelScanCheckpoint`, `readScanCheckpoint`, `scanCheckpointFile`, `scanCheckpointKey`, `scanParallelCheckpointKey`, `strings.TrimSpace` |
| `legacyCheckpointAvailable` | `internal/ocr/scanner_checkpoint.go:121` | `errors.Is`, `os.Stat`, `readScanCheckpoint`, `scanCheckpointFile`, `scanCheckpointKey`, `scanParallelCheckpointKey`, `strings.TrimSpace` |
| `RemoveCheckpoint` | `internal/ocr/scanner_checkpoint.go:152` | `errors.Is`, `os.Remove`, `scanCheckpointFile`, `scanCheckpointKeyForSchema`, `strings.TrimSpace` |
| `scanCheckpointKey` | `internal/ocr/scanner_checkpoint.go:171` | `scanCheckpointKeyForSchema` |
| `scanCheckpointKeyForSchema` | `internal/ocr/scanner_checkpoint.go:175` | `UnixNano`, `errors.New`, `filepath.Abs`, `filepath.Clean`, `hex.EncodeToString`, `json.Marshal`, `normalizeScanRegion`, `os.Stat`, `scanModeFor`, `sha256.Sum256`, `st.IsDir`, `st.ModTime`, `st.Size`, `strings.ToLower`, `strings.TrimSpace` |
| `scanCheckpointFile` | `internal/ocr/scanner_checkpoint.go:218` | `filepath.Join` |
| `writeScanCheckpoint` | `internal/ocr/scanner_checkpoint.go:222` | `errors.New`, `f.Close`, `f.Sync`, `f.Write`, `filepath.Dir`, `json.Marshal`, `math.IsInf`, `math.IsNaN`, `os.MkdirAll`, `os.OpenFile`, `os.Remove`, `os.Rename`, `strings.TrimSpace` |
| `readScanCheckpoint` | `internal/ocr/scanner_checkpoint.go:258` | `errors.Is`, `errors.New`, `json.Unmarshal`, `math.IsInf`, `math.IsNaN`, `os.ReadFile` |
| `newScanCheckpointSession` | `internal/ocr/scanner_checkpoint.go:288` | `math.Floor`, `math.IsInf`, `math.IsNaN`, `os.Remove`, `readScanCheckpoint`, `scanCheckpointFile`, `scanCheckpointKey`, `strings.TrimSpace` |
| `scanCheckpointSession.MaybeSave` | `internal/ocr/scanner_checkpoint.go:314` | `s.MaybeSaveWithStats` |
| `scanCheckpointSession.MaybeSaveWithStats` | `internal/ocr/scanner_checkpoint.go:318` | `math.Floor`, `s.save`, `tracker.CanCheckpoint` |
| `scanCheckpointSession.SaveNow` | `internal/ocr/scanner_checkpoint.go:329` | `s.SaveNowWithStats` |
| `scanCheckpointSession.SaveNowWithStats` | `internal/ocr/scanner_checkpoint.go:333` | `s.save`, `tracker.CanCheckpoint` |
| `scanCheckpointSession.Remove` | `internal/ocr/scanner_checkpoint.go:340` | `errors.Is`, `os.Remove` |
| `scanCheckpointSession.save` | `internal/ocr/scanner_checkpoint.go:351` | `tracker.Active`, `tracker.Cues`, `writeScanCheckpoint` |
| `scanParallelCheckpointKey` | `internal/ocr/scanner_checkpoint.go:387` | `scanCheckpointKeyForSchema` |
| `newParallelCheckpointSession` | `internal/ocr/scanner_checkpoint.go:391` | `os.Remove`, `readParallelScanCheckpoint`, `scanCheckpointFile`, `scanParallelCheckpointKey`, `strings.TrimSpace` |
| `parallelCheckpointSession.Save` | `internal/ocr/scanner_checkpoint.go:409` | `writeParallelScanCheckpoint` |
| `parallelCheckpointSession.Remove` | `internal/ocr/scanner_checkpoint.go:418` | `errors.Is`, `os.Remove` |
| `writeParallelScanCheckpoint` | `internal/ocr/scanner_checkpoint.go:429` | `errors.New`, `f.Close`, `f.Sync`, `f.Write`, `filepath.Dir`, `json.Marshal`, `math.IsInf`, `math.IsNaN`, `os.MkdirAll`, `os.OpenFile`, `os.Remove`, `os.Rename`, `strings.TrimSpace` |
| `readParallelScanCheckpoint` | `internal/ocr/scanner_checkpoint.go:470` | `errors.Is`, `errors.New`, `json.Unmarshal`, `os.ReadFile` |
| `laneCheckpointState` | `internal/ocr/scanner_checkpoint.go:496` | _leaf / external state only_ |
| `updateLaneCheckpointState` | `internal/ocr/scanner_checkpoint.go:500` | _leaf / external state only_ |
| `parallelCheckpointInfo` | `internal/ocr/scanner_checkpoint.go:516` | `contiguousCompletedFrontier`, `math.Max`, `math.Min`, `recentCuesAtOrBefore`, `reconcileSegmentCues` |
| `normalizeScanParallelism` | `internal/ocr/scanner_parallel.go:74` | `fmt.Errorf`, `strconv.Atoi`, `strconv.Itoa`, `strings.ToLower`, `strings.TrimSpace` |
| `explicitScanParallelism` | `internal/ocr/scanner_parallel.go:89` | `strconv.Atoi`, `strings.TrimSpace` |
| `maxParallelismForDuration` | `internal/ocr/scanner_parallel.go:103` | `int`, `math.Floor`, `math.IsInf`, `math.IsNaN` |
| `buildScanSegments` | `internal/ocr/scanner_parallel.go:117` | `errors.New`, `float64`, `fmt.Errorf`, `math.IsInf`, `math.IsNaN`, `math.Max`, `math.Min`, `maxParallelismForDuration` |
| `cueOwnedBySegment` | `internal/ocr/scanner_parallel.go:144` | _leaf / external state only_ |
| `collectParallelBenchmarkOutcomes` | `internal/ocr/scanner_parallel.go:175` | `ctx.Done`, `ctx.Err` |
| `configureAutoWorkerLevel` | `internal/ocr/scanner_parallel.go:193` | `cancel`, `context.WithTimeout`, `pool.ConfigureScanWorkers` |
| `restoreAutoWorkerPool` | `internal/ocr/scanner_parallel.go:202` | `cancel`, `context.WithTimeout`, `fmt.Errorf`, `pool.ConfigureScanWorkers`, `resetCancel`, `resetter.ResetScanWorkers` |
| `Scanner.runParallel` | `internal/ocr/scanner_parallel.go:238` | `Seconds`, `aggregateParallelScanResult`, `buildScanSegments`, `cancel`, `checkpointMu.Lock`, `checkpointMu.Unlock`, `checkpointSession.Remove`, `checkpointSession.Save`, `checkpointStatsFromScanResult`, `context.Background`, `context.WithCancel`, `context.WithTimeout`, `contiguousCompletedFrontier`, `errors.Is`, `errors.New`, `float64`, `fmt.Errorf`, `fmt.Sprintf`, `job.Logf`, `job.PauseRequested`, `job.Set`, `job.SetResult`, `laneCheckpointState`, `lastCheckpointWrite.IsZero`, `math.IsInf`, `math.IsNaN`, `math.Max`, `math.Min`, `newParallelCheckpointSession`, `pool.ConfigureScanWorkers`, `progressMu.Lock`, `progressMu.Unlock`, `publish`, `recentCuesAtOrBefore`, `reconcileSegmentCues`, `s.run`, `scanResultFromParallelCheckpointLane`, `time.Now`, `time.Since`, `updateLaneCheckpointState`, `writeParallelCheckpoint` |
| `checkpointStatsFromScanResult` | `internal/ocr/scanner_parallel.go:542` | _leaf / external state only_ |
| `scanResultFromParallelCheckpointLane` | `internal/ocr/scanner_parallel.go:551` | _leaf / external state only_ |
| `aggregateParallelScanResult` | `internal/ocr/scanner_parallel.go:560` | `float64`, `reconcileSegmentCues`, `scanResultFromParallelCheckpointLane` |
| `contiguousCompletedFrontier` | `internal/ocr/scanner_parallel.go:608` | `math.Max`, `math.Min` |
| `recentCues` | `internal/ocr/scanner_parallel.go:624` | _leaf / external state only_ |
| `recentCuesAtOrBefore` | `internal/ocr/scanner_parallel.go:631` | `recentCues` |
| `reconcileSegmentCues` | `internal/ocr/scanner_parallel.go:644` | `NormalizeChineseSubtitleText`, `cueOwnedBySegment`, `scanSimilarity`, `sort.SliceStable`, `strings.TrimSpace` |
| `Scanner.selectAutoParallelism` | `internal/ocr/scanner_parallel.go:708` | `configureAutoWorkerLevel`, `elapsed.Seconds`, `errors.Is`, `evaluateAutoResourceGate`, `fmt.Sprintf`, `formatAutoResourceSnapshot`, `job.Logf`, `job.Set`, `maxParallelismForDuration`, `probe`, `restoreAutoWorkerPool`, `s.autoResourceProbe`, `s.benchmarkParallelLevel`, `time.Now`, `time.Since` |
| `Scanner.benchmarkParallelLevel` | `internal/ocr/scanner_parallel.go:823` | `Seconds`, `benchCtx.Done`, `cancel`, `collectParallelBenchmarkOutcomes`, `context.WithTimeout`, `errors.New`, `float64`, `math.Max`, `math.Min`, `s.autoResourceProbe`, `s.run`, `startAutoResourceSampler`, `stopSampler`, `time.Now`, `time.Since` |
| `Scanner.autoResourceProbe` | `internal/ocr/scanner_resource.go:42` | _leaf / external state only_ |
| `probeAutoResources` | `internal/ocr/scanner_resource.go:49` | `probeNVIDIAResources`, `probePlatformResources` |
| `probeNVIDIAResources` | `internal/ocr/scanner_resource.go:64` | `probeNVIDIAResourcesPlatform` |
| `mergeAutoResourcePeak` | `internal/ocr/scanner_resource.go:68` | _leaf / external state only_ |
| `startAutoResourceSampler` | `internal/ocr/scanner_resource.go:112` | `cancel`, `context.WithCancel`, `float64`, `mergeAutoResourcePeak`, `mu.Lock`, `mu.Unlock`, `probe`, `sampleCtx.Done`, `ticker.Stop`, `time.NewTicker` |
| `autoResourceSafetyMargin` | `internal/ocr/scanner_resource.go:169` | `float64`, `uint64` |
| `evaluateAutoResourceGate` | `internal/ocr/scanner_resource.go:177` | `autoResourceSafetyMargin`, `bytesToGiB`, `bytesToMiB`, `float64`, `fmt.Sprintf`, `formatAutoResourceSnapshot`, `minUint64`, `uint64` |
| `formatAutoResourceSnapshot` | `internal/ocr/scanner_resource.go:244` | `bytesToGiB`, `bytesToMiB`, `fmt.Sprintf`, `strings.Join`, `uint64` |
| `minUint64` | `internal/ocr/scanner_resource.go:268` | _leaf / external state only_ |
| `bytesToGiB` | `internal/ocr/scanner_resource.go:275` | `float64` |
| `bytesToMiB` | `internal/ocr/scanner_resource.go:276` | `float64` |
| `probePlatformResources` | `internal/ocr/scanner_resource_other.go:10` | _leaf / external state only_ |
| `probePlatformResources` | `internal/ocr/scanner_resource_windows.go:35` | `ctx.Done`, `float64`, `procGlobalMemoryStatusEx.Call`, `readSystemTimes`, `time.NewTimer`, `timer.Stop`, `uint32`, `uintptr`, `unsafe.Pointer`, `unsafe.Sizeof` |
| `readSystemTimes` | `internal/ocr/scanner_resource_windows.go:68` | `procGetSystemTimes.Call`, `toUint64`, `uint64`, `uintptr`, `unsafe.Pointer` |
| `newSubtitleTracker` | `internal/ocr/scanner_tracker.go:27` | _leaf / external state only_ |
| `subtitleTracker.Observe` | `internal/ocr/scanner_tracker.go:31` | `NormalizeChineseSubtitleText`, `cleanScanText`, `math.Max`, `requiredSubtitleConfirmations`, `scanSimilarity`, `t.frameSpan`, `t.observeEmpty`, `t.promoteCandidate` |
| `subtitleTracker.NeedsConfirmation` | `internal/ocr/scanner_tracker.go:84` | _leaf / external state only_ |
| `subtitleTracker.CanVisualConfirm` | `internal/ocr/scanner_tracker.go:88` | `isShortASCIIText` |
| `subtitleTracker.ConfirmVisual` | `internal/ocr/scanner_tracker.go:100` | `math.Max`, `t.CanVisualConfirm`, `t.promoteCandidate` |
| `subtitleTracker.CanVisualConfirmEmpty` | `internal/ocr/scanner_tracker.go:113` | _leaf / external state only_ |
| `subtitleTracker.ConfirmVisualEmpty` | `internal/ocr/scanner_tracker.go:117` | `t.CanVisualConfirmEmpty`, `t.observeEmpty` |
| `subtitleTracker.ExtendActiveVisual` | `internal/ocr/scanner_tracker.go:125` | `math.Max`, `t.frameSpan` |
| `subtitleTracker.HasActive` | `internal/ocr/scanner_tracker.go:133` | _leaf / external state only_ |
| `subtitleTracker.CanCheckpoint` | `internal/ocr/scanner_tracker.go:137` | _leaf / external state only_ |
| `subtitleTracker.Restore` | `internal/ocr/scanner_tracker.go:141` | `NormalizeChineseSubtitleText` |
| `subtitleTracker.Finish` | `internal/ocr/scanner_tracker.go:168` | `t.commitActive`, `t.frameSpan` |
| `subtitleTracker.Cues` | `internal/ocr/scanner_tracker.go:184` | _leaf / external state only_ |
| `subtitleTracker.Active` | `internal/ocr/scanner_tracker.go:191` | _leaf / external state only_ |
| `subtitleTracker.observeEmpty` | `internal/ocr/scanner_tracker.go:199` | `t.commitActive`, `t.frameSpan` |
| `subtitleTracker.promoteCandidate` | `internal/ocr/scanner_tracker.go:224` | `t.commitActive`, `t.frameSpan` |
| `subtitleTracker.commitActive` | `internal/ocr/scanner_tracker.go:241` | `NormalizeChineseSubtitleText` |
| `subtitleTracker.frameSpan` | `internal/ocr/scanner_tracker.go:265` | _leaf / external state only_ |
| `requiredSubtitleConfirmations` | `internal/ocr/scanner_tracker.go:272` | `isShortASCIIText`, `strings.TrimSpace` |
| `NormalizeChineseSubtitleText` | `internal/ocr/scanner_tracker.go:293` | `cleanScanText`, `strings.Contains`, `strings.ReplaceAll`, `unicode.Is`, `unicode.IsLetter` |
| `isShortASCIIText` | `internal/ocr/scanner_tracker.go:323` | `strings.TrimSpace`, `unicode.IsDigit`, `unicode.IsLetter`, `unicode.IsPunct`, `unicode.IsSpace` |
