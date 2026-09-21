"""Run the OCR gold corpus against the working tree, optionally comparing HEAD."""

import argparse
import datetime
import json
import os
import pathlib
import shutil
import subprocess
import sys


SCRIPT = pathlib.Path(__file__).resolve()
DEFAULT_REPO = SCRIPT.parents[2]


def run(command, cwd, allow_quality_failure=False):
    print("+ " + subprocess.list2cmdline([str(part) for part in command]), flush=True)
    result = subprocess.run([str(part) for part in command], cwd=cwd, check=False)
    if result.returncode == 2:
        raise InfrastructureError(f"infrastructure command exited 2: {command[0]}")
    if result.returncode and not allow_quality_failure:
        raise RuntimeError(f"command exited {result.returncode}: {command[0]}")
    return result.returncode


def capture(command, cwd):
    result = subprocess.run([str(part) for part in command], cwd=cwd, text=True,
                            encoding="utf-8", errors="replace", stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, check=False)
    if result.returncode:
        raise InfrastructureError(f"command failed ({result.returncode}): {' '.join(map(str, command))}\n{result.stderr}")
    return result.stdout.strip()


def is_within(child, parent):
    try:
        child.resolve().relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def copy_runtime(seed, destination):
    source = seed / "Tools" if (seed / "Tools").is_dir() else seed
    if not source.is_dir():
        raise InfrastructureError(f"runtime seed has no Tools directory: {seed}")
    target = destination / "app" / "Tools"
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source, target, dirs_exist_ok=True, copy_function=shutil.copy2)


def harness_command(args, repo, scratch, report, label, revision, artifacts, core_project=None, worker=None):
    project = repo / "csharp" / "tests" / "BiliSubStudio.OcrGoldHarness" / "BiliSubStudio.OcrGoldHarness.csproj"
    command = ["dotnet", "run", "--project", project, "--configuration", "Release",
               "--artifacts-path", artifacts]
    if core_project is not None:
        command.append(f"-p:CoreProjectPath={core_project}")
    if worker is not None:
        command.append(f"-p:OcrWorkerPath={worker}")
    command += ["--", "--manifest", args.manifest, "--scratch", scratch, "--report", report,
                "--label", label, "--revision", revision, "--timeout-seconds", str(args.timeout_seconds)]
    if args.ffprobe:
        command += ["--ffprobe", pathlib.Path(args.ffprobe).resolve()]
    return command


def metric_deltas(candidate, head):
    head_cases = {case["id"]: case for case in head["cases"]}
    output = []
    for candidate_case in candidate["cases"]:
        case_id = candidate_case["id"]
        if case_id not in head_cases:
            raise InfrastructureError(f"HEAD report is missing case '{case_id}'")
        left = candidate_case["score"]["metrics"]
        right = head_cases[case_id]["score"]["metrics"]
        output.append({
            "id": case_id,
            "candidate_status": candidate_case["status"],
            "head_status": head_cases[case_id]["status"],
            "candidate_minus_head": {
                "cue_recall": left["cue_recall"] - right["cue_recall"],
                "cue_precision": left["cue_precision"] - right["cue_precision"],
                "cer": left["cer"] - right["cer"],
                "mean_onset_error_seconds": nullable_delta(left["mean_onset_error_seconds"], right["mean_onset_error_seconds"]),
                "mean_offset_error_seconds": nullable_delta(left["mean_offset_error_seconds"], right["mean_offset_error_seconds"]),
                "decoded_frames": candidate_case["production_scan"]["frames"] - head_cases[case_id]["production_scan"]["frames"],
            },
        })
    return output


def nullable_delta(left, right):
    return None if left is None or right is None else left - right


def load_report(path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise InfrastructureError(f"cannot read harness report {path}: {error}") from error


def write_json(path, value):
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


class InfrastructureError(RuntimeError):
    pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=pathlib.Path, default=DEFAULT_REPO)
    parser.add_argument("--manifest", type=pathlib.Path,
                        default=DEFAULT_REPO / "csharp" / "tests" / "BiliSubStudio.OcrGoldHarness" / "fixtures" / "manifest.json")
    parser.add_argument("--scratch", type=pathlib.Path, required=True,
                        help="external directory for app state, builds, worktree, and reports")
    parser.add_argument("--runtime-seed", type=pathlib.Path,
                        help="optional installed app root or Tools directory copied into scratch")
    parser.add_argument("--ffprobe", help="optional explicit ffprobe; otherwise app-managed ffprobe is used")
    parser.add_argument("--compare-head", action="store_true")
    parser.add_argument("--timeout-seconds", type=int, default=7200)
    parser.add_argument("--keep-head-worktree", action="store_true")
    args = parser.parse_args()
    args.repo = args.repo.resolve()
    args.manifest = args.manifest.resolve()
    args.scratch = args.scratch.resolve()
    if args.runtime_seed:
        args.runtime_seed = args.runtime_seed.resolve()
    if not (args.repo / ".git").exists():
        raise SystemExit(f"not a git repository: {args.repo}")
    if is_within(args.scratch, args.repo):
        raise SystemExit(f"--scratch must be outside the repository: {args.scratch}")
    if args.timeout_seconds < 1 or args.timeout_seconds > 86400:
        raise SystemExit("--timeout-seconds must be in [1, 86400]")
    args.scratch.mkdir(parents=True, exist_ok=True)

    candidate_scratch = args.scratch / "candidate"
    candidate_report = candidate_scratch / "report.json"
    candidate_artifacts = args.scratch / "dotnet-candidate"
    if args.runtime_seed:
        copy_runtime(args.runtime_seed, candidate_scratch)
    head_commit = capture(["git", "rev-parse", "HEAD"], args.repo)
    dirty = bool(capture(["git", "status", "--porcelain"], args.repo))
    candidate_revision = head_commit + ("+working-tree" if dirty else "")
    candidate_exit = run(harness_command(args, args.repo, candidate_scratch, candidate_report,
                                          "candidate", candidate_revision, candidate_artifacts),
                         args.repo, allow_quality_failure=True)
    candidate = load_report(candidate_report)
    if not args.compare_head:
        print(json.dumps({"mode": "candidate-only", "status": candidate.get("status"),
                          "report": str(candidate_report)}))
        return candidate_exit

    head_worktree = args.scratch / "head-worktree"
    if head_worktree.exists():
        raise InfrastructureError(f"HEAD worktree destination already exists: {head_worktree}")
    head_scratch = args.scratch / "head"
    head_report = head_scratch / "report.json"
    head_artifacts = args.scratch / "dotnet-head"
    worktree_added = False
    try:
        run(["git", "worktree", "add", "--detach", head_worktree, "HEAD"], args.repo)
        worktree_added = True
        if args.runtime_seed:
            copy_runtime(args.runtime_seed, head_scratch)
        core_project = head_worktree / "csharp" / "src" / "BiliSubStudio.Core" / "BiliSubStudio.Core.csproj"
        worker = head_worktree / "internal" / "ocr" / "worker.py"
        head_exit = run(harness_command(args, args.repo, head_scratch, head_report, "head", head_commit,
                                        head_artifacts, core_project, worker), args.repo, allow_quality_failure=True)
        head = load_report(head_report)
        comparison = {
            "schema_version": 1,
            "status": candidate.get("status", "error"),
            "created_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
            "candidate_report": str(candidate_report),
            "head_report": str(head_report),
            "candidate_revision": candidate_revision,
            "head_revision": head_commit,
            "candidate_exit_code": candidate_exit,
            "head_exit_code": head_exit,
            "case_deltas": metric_deltas(candidate, head),
            "note": "Deltas are diagnostic; manifest quality thresholds gate candidate status.",
        }
        comparison_path = args.scratch / "comparison-report.json"
        write_json(comparison_path, comparison)
        print(json.dumps({"mode": "candidate-vs-head", "status": comparison["status"],
                          "report": str(comparison_path)}))
        return candidate_exit
    finally:
        if worktree_added and not args.keep_head_worktree:
            run(["git", "worktree", "remove", "--force", head_worktree], args.repo)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except InfrastructureError as error:
        print(f"INFRASTRUCTURE ERROR: {error}", file=sys.stderr)
        sys.exit(2)
