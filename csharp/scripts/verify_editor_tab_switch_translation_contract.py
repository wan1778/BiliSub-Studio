from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
app = (ROOT / "csharp/src/BiliSubStudio.App/App.xaml.cs").read_text(encoding="utf-8")
cue = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs").read_text(encoding="utf-8")
bootstrap = (ROOT / "csharp/src/BiliSubStudio.App/Pages/EditorPage.ParityBootstrap.cs").read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit("FAIL: " + message)


require('OperationAbortedHResult = unchecked((int)0x80004004)' in app,
        'WinUI E_ABORT constant is missing')
require('MainWindow is not null && args.Exception.HResult == OperationAbortedHResult' in app,
        'runtime E_ABORT must be handled only after the main window exists')
require('StartupDiagnostics.WriteException("winui-operation-aborted", args.Exception);' in app,
        'runtime E_ABORT must still be persisted to diagnostics')
abort_pos = app.index('args.Exception.HResult == OperationAbortedHResult')
fatal_pos = app.index('StartupDiagnostics.ShowFatalError("winui-unhandled"')
require(abort_pos < fatal_pos and 'return;' in app[abort_pos:fatal_pos],
        'runtime E_ABORT must return before fatal dialog/Exit')

translate_start = cue.index('private async Task TranslateAllWithManualStateAsync()')
translate_end = cue.index('private async void SubtitleRetranslateCue_Click', translate_start)
translate = cue[translate_start:translate_end]
for marker in (
    'var translationProjectSnapshot = ProjectSnapshot();',
    'if (!IsLoaded || _subtitleSource is null',
    'if (!IsLoaded) return 0;',
    'await _editorTabLifecycleGate.WaitAsync();',
    'if (IsLoaded && !snapshot.Done',
    'await PersistEditorProjectAsync(_project);',
    'if (IsLoaded)\n                    {\n                        TranslationProgress.Value = 100;',
    'if (IsLoaded)\n            {\n                RefreshEditorActions();',
):
    require(marker in translate, 'tab-safe Vietsub path missing: ' + marker)
require('AttachSubtitleToProject(result.OutputPath);' not in translate,
        'hidden-tab finalization must not read XAML controls through AttachSubtitleToProject')
require('await SaveProjectNowAsync();' not in translate,
        'hidden-tab finalization must persist captured project state without reading XAML controls')

for marker in (
    'if (_subtitleSource is not null)',
    'RenderSubtitleCueList();',
    'UpdateSubtitleSummary();',
    'if (_translationJobId is not null)',
    'TranslationProgress.Value = snapshot.Progress;',
    'Vietsub hoàn tất · {_subtitleSource.Cues.Count:N0} câu.',
):
    require(marker in bootstrap, 'Editor Loaded resync missing: ' + marker)

print('PASS: Vietsub continues across tab unload without touching hidden Editor XAML; runtime E_ABORT is non-fatal')
