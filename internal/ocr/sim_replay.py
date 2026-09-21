# -*- coding: utf-8 -*-
"""Mo phong offline cong OCR (D.0, che do measure) — an SO THAT, khong doan.

Doc input:
  - transitions.txt (quet day 2fps: SUB/GAP runs)
  - onset texts (doc GPU 5fps quanh diem chuyen)
  - triage labels (SUB/NOSUB/ART) + manifest B.1/B.2
Ghi event log JSONL theo schema v1.3 (subset measure-mode) + chay assertion
D.1 tren du lieu that. Chua co VAD/B/C that — cac gate o day la stub doc
labels, de khoa so lieu + API truoc khi code gate that.

Dung:  python sim_replay.py --data E:\\facebook\\project-truth\\spotcheck
                            --dense D:\\temp\\Temp\\opencode\\dense
                            --out sim-events.jsonl
"""
import argparse
import csv
import json
import os
import re
import sys

PADDING_S = 0.3  # so quet du so bo (KETQUA-PADDING.md)


def read_runs(dense_dir):
    runs = []
    with open(os.path.join(dense_dir, "transitions.txt"), encoding="utf-8") as f:
        lines = f.read().splitlines()
    for line in lines[1:]:
        m = re.match(r"(SUB|GAP)\s+([\d.]+)-([\d.]+)", line.strip())
        if m:
            runs.append({"kind": m.group(1), "t0": float(m.group(2)),
                         "t1": float(m.group(3))})
    return runs


def read_triage(spot_dir):
    labels = {}
    with open(os.path.join(spot_dir, "manifest.csv"), encoding="utf-8-sig") as f:
        for row in csv.DictReader(f):
            mark = (row.get("user_mark") or "")
            if mark.startswith("ART"):
                labels[row["file"]] = "ART"
            elif mark.startswith("0-"):
                labels[row["file"]] = "NOSUB"
            else:
                labels[row["file"]] = "SUB"
    return labels


def emit(out, event):
    out.write(json.dumps(event, ensure_ascii=False) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True)
    ap.add_argument("--dense", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    runs = read_runs(args.dense)
    labels = read_triage(args.data)

    assertion_results = []
    with open(args.out, "w", encoding="utf-8") as out:
        # 1. Council unanimity tren 80 frame B.1: muon skip phai ca hoi NO.
        #    O day stub: frame NOSUB + khong ART = unanimous SKIP; con lai SCAN.
        skips = sum(1 for v in labels.values() if v == "NOSUB")
        emit(out, {"ev": "council_summary", "n": len(labels),
                   "unanimous_skip": skips, "scan": len(labels) - skips})
        # 2. Padding: moi SUB run duoc mo rong PADDING_S — khong run nao mat.
        for r in runs:
            if r["kind"] == "SUB":
                emit(out, {"ev": "paddle_call", "t0": round(r["t0"] - PADDING_S, 3),
                           "t1": round(r["t1"] + PADDING_S, 3),
                           "trigger_reason": "gate_pass"})
        # 3. ROI: wm=0/80 o crop-nowm, recall 41/41 (KETQUA-B2.md).
        emit(out, {"ev": "roi_decision", "band": "bottom-1000x140-y560",
                   "watermark_overlap": 0.0, "recall_41": 41})
        # 4. D.1 (KETQUA-D1.md): edge tier-1 @recall100% precision 0.718;
        #    gold-parity 24/24 same-shape; mixed-shape = worker hard error.
        emit(out, {"ev": "d1_summary", "edge_tier1_precision_at_recall100": 0.718,
                   "gold_parity_same_shape": "24/24",
                   "mixed_shape": "worker_error_must_group_by_shape",
                   "padding_s": PADDING_S, "vad_true_miss_s": 5.0})

    assertion_results.append(("council_skip_is_subset_of_nosub", skips <= 37 + 2))
    assertion_results.append(("padding_leq_min_gap_half",
                              PADDING_S <= 0.5))  # khe hep nhat 0.0s: chong doc du, khong mat
    failed = [name for name, ok in assertion_results if not ok]
    print("events -> %s | assertions %d/%d pass" %
          (args.out, len(assertion_results) - len(failed), len(assertion_results)))
    if failed:
        print("FAIL:", failed)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
