from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise SystemExit(f"FAIL: {message}")


log_code = read("csharp/src/BiliSubStudio.Core/Diagnostics/ApplicationLog.cs")
redactor_code = read("csharp/src/BiliSubStudio.Core/Diagnostics/LogRedactor.cs")
job_code = read("csharp/src/BiliSubStudio.Core/Jobs/AppJob.cs")
bug_code = read("csharp/src/BiliSubStudio.Core/Maintenance/BugReportService.cs")
test_code = read("csharp/tests/BiliSubStudio.Core.ContractTests/ApplicationLoggingContract.cs")

for marker in (
    "public sealed class ApplicationLog : IDisposable",
    "public event Action<AppLogEntry>? EntryAdded",
    "public AppLogEntry Info(string source, string message, string? jobId = null)",
    "public AppLogEntry Warning(string source, string message, string? jobId = null)",
    "public AppLogEntry Error(string source, string message, string? jobId = null)",
    "public IReadOnlyList<AppLogEntry> Snapshot(long afterSequence = 0)",
    "public AppLogEntry Error(string source, string message, Exception exception, string? jobId = null)",
    "Channel<LogWriteItem>",
    "SingleReader = true, SingleWriter = false",
    "MaxGenerations = 5",
    "LogRedactor.Redact(Normalize(message))",
    "_writeQueue.Writer.TryWrite(new LogWriteItem(line, null))",
    "File.AppendAllTextAsync",
    "public void Flush(TimeSpan? timeout = null)",
    "private async Task WriterLoopAsync()",
    "for (var generation = MaxGenerations - 1; generation >= 1; generation--)",
):
    require(marker in log_code, f"logging upgrade missing: {marker}")

require("File.AppendAllText(" not in log_code, "ApplicationLog must not restore synchronous File.AppendAllText")
require(log_code.index("_writeQueue.Writer.TryWrite(new LogWriteItem(line, null))") < log_code.index("var subscribers = EntryAdded"),
        "disk queue enqueue must happen before EntryAdded publication")
require("_applicationLog?.Error(Kind, finalMessage, error, Id);" in job_code,
        "AppJob.Finish must publish the full exception to ApplicationLog")
require("LogRedactor.Redact(note)" in bug_code and "LogRedactor.Redact(x.Value)" in bug_code,
        "BugReportService must reuse LogRedactor")
require("GeneratedRegex" not in bug_code, "BugReportService must not keep a duplicate secret-regex set")
require("public static partial class LogRedactor" in redactor_code, "shared LogRedactor type is missing")
for marker in ("SESSDATA", "bili_jct", "DedeUserID", "authorization", "token", "cookie", "C:\\\\Users", "sessdata"):
    require(marker in redactor_code, f"LogRedactor coverage missing: {marker}")
require("AppDomain.CurrentDomain.ProcessExit += OnProcessExit" in log_code
        and "AppDomain.CurrentDomain.ProcessExit -= OnProcessExit" in log_code,
        "ApplicationLog must self-flush on normal process exit without UI call-site changes")
for marker in ("Cookie: SESSDATA=secret", "token=abc", "C:\\Users\\Alice", "auth=hidden", "ThrowNestedFailure"):
    require(marker in test_code, f"logging runtime regression fixture missing: {marker}")

print("PASS: redacted queued ApplicationLog / full job exception / five-generation rotation / shutdown flush contracts")
