"""Validate an already paused exact owned user dump; never launch/attach/resume."""
import argparse
import asyncio
import hashlib
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from mcp_server import bootstrap
bootstrap.ensure_deps()
from mcp_server import vs_bridge
import psutil

async def run(args):
    evidence = {'dump': str(args.dump.resolve()), 'steps': {}, 'success': False}
    instances = await vs_bridge.discover_instances()
    instance = next(item for item in instances if item.process_id == args.ide_pid)
    identity = instance.target_identity()
    assert int(identity['processStartTimeUtcTicks']) == args.ide_start_ticks
    assert identity['serverInstanceId'] == args.server_instance_id
    paths = sorted({item.path for item in psutil.Process(args.ide_pid).memory_maps()
                    if item.path.lower().endswith('vsdebuggermcp.dll')})
    assert len(paths) == 1, paths
    digest = hashlib.sha256(Path(paths[0]).read_bytes()).hexdigest()
    assert digest.lower() == args.extension_sha.lower(), digest
    evidence['identity'] = identity
    evidence['extension'] = {'path': paths[0], 'sha256': digest}
    bridge = vs_bridge.get_bridge()
    _, _, _, generation = await bridge.get_selection_snapshot()
    selected, _, _ = await bridge.try_select_instance(generation,
        target_identity=identity, registration_snapshot=instance.registration_snapshot())
    assert selected
    try:
        status = await bridge.call('get_debug_status', {}, 10)
        evidence['steps']['status'] = status
        assert status['isPaused'] and status['currentProcessId'] == 0, status
        assert Path(status['currentProcess']).resolve() == args.dump.resolve(), status
        stack = await bridge.call('get_callstack', {}, 10)
        evidence['steps']['callstack'] = stack
        assert stack['snapshotStable'] and stack['frames'] and stack['processId'] == 0, stack
        assert all(frame.get('context') is None for frame in stack['frames'])
        bundle = await bridge.call('capture_debug_bundle', {
            'includeLocals': True, 'maxDepth': 0, 'includeThreads': False,
            'includeModules': False, 'includeRegisters': False, 'includeOutput': False,
            'includeException': False, 'evaluationPolicy': 'engine_default'}, 15)
        evidence['steps']['bundle'] = bundle
        assert bundle['locals']['variables'], bundle
        assert bundle['locals'].get('context') is None, bundle
        after = await bridge.call('get_debug_status', {}, 10)
        evidence['steps']['statusAfter'] = after
        assert after['isPaused'] and after['currentProcessId'] == 0, after
        assert after['debugSessionGeneration'] == status['debugSessionGeneration']
        evidence['success'] = True
    finally:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(evidence, indent=2), encoding='utf-8')
    print(json.dumps({'success': evidence['success'], 'output': str(args.output),
                      'frameCount': len(evidence['steps']['callstack']['frames']),
                      'localsCount': len(evidence['steps']['bundle']['locals']['variables'])}))

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ide-pid', type=int, required=True)
    parser.add_argument('--ide-start-ticks', type=int, required=True)
    parser.add_argument('--server-instance-id', required=True)
    parser.add_argument('--extension-sha', required=True)
    parser.add_argument('--dump', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    asyncio.run(run(parser.parse_args()))
