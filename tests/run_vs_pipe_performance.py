"""Alternate current/baseline real Windows pipe calls against an owned fixture."""
import argparse
import importlib.util
import json
from pathlib import Path
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import test_vs_pipe_reads as fixture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--baseline-ref', required=True)
    parser.add_argument('--samples', type=int, default=20)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != 'win32' or not 3 <= args.samples <= 100:
        parser.error('Windows and 3..100 samples required')
    directory = ROOT / '.tmp/performance' / str(time.time_ns())
    directory.mkdir(parents=True)
    old_path = directory / 'vs_bridge_before.py'
    old_path.write_bytes(subprocess.check_output(
        ['git', 'show', args.baseline_ref + ':mcp_server/vs_bridge.py'], cwd=ROOT))
    spec = importlib.util.spec_from_file_location('mcp_server.vs_bridge_before', old_path)
    old = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(old)
    current = fixture.vs_bridge
    rows = {'before': [], 'after': []}
    try:
        for i in range(args.samples):
            for label in (('before', 'after') if i % 2 == 0 else ('after', 'before')):
                fixture.vs_bridge = old if label == 'before' else current
                start = time.perf_counter()
                value = fixture.PipeReadTests().exchange('fast')
                assert value == {'text': '\u7ebf\u7a0b\U0001f680'}, value
                rows[label].append((time.perf_counter() - start) * 1000)
    finally:
        fixture.vs_bridge = current
    report = {'evidence': 'owned immediate-response Windows pipe; not VS debugger latency',
              'baselineRef': args.baseline_ref, 'pairedAlternatingOrder': True,
              'operations': {k: {'samples': len(v), 'medianMs': round(statistics.median(v), 3),
                                 'p95Ms': round(sorted(v)[int((len(v) - 1) * .95)], 3),
                                 'rawMs': v} for k, v in rows.items()}}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({k: {n: v for n, v in row.items() if n != 'rawMs'}
                      for k, row in report['operations'].items()}, indent=2))


if __name__ == '__main__':
    main()
