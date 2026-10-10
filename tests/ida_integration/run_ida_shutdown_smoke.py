"""Opt-in real stdio disconnect smoke: saved owned IDA exits without client polling."""
import argparse
import asyncio
import ctypes
import json
from pathlib import Path
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))


async def child(mode, server):
    from mcp_server import bootstrap
    bootstrap.ensure_deps()
    from mcp import ClientSession, StdioServerParameters
    from mcp.client.stdio import stdio_client
    from ida_backend.transport import tool_value
    params = StdioServerParameters(command=sys.executable, args=[str(server)])
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as client:
            await client.initialize()
            result = await client.call_tool('ida_open', {'input_path': 'C:/Windows/System32/version.dll', 'mode': mode})
            session = tool_value(result.model_dump(by_alias=True, exclude_none=True))
            print(json.dumps(session), flush=True)
            for _ in range(150):
                result = await client.call_tool('ida_session_status', {'session_id': session['sessionId']})
                session = tool_value(result.model_dump(by_alias=True, exclude_none=True))
                if session['state'] == 'ready':
                    break
                await asyncio.sleep(.2)
            assert session['state'] == 'ready', session
            print(json.dumps(session), flush=True)
            # No ida_close/status after this: closing stdio must reap the worker.


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mode', choices=['gui', 'headless'], default='gui')
    parser.add_argument('--child', action='store_true')
    parser.add_argument('--server', type=Path, default=ROOT / 'mcp_server/ida_server.py')
    args = parser.parse_args()
    if args.child:
        asyncio.run(child(args.mode, args.server))
        return
    directory = ROOT / '.tmp/ida-shutdown-smoke' / str(time.time_ns())
    directory.mkdir(parents=True)
    import os
    env = dict(os.environ, AIDB_IDA_WORK_ROOT=str(directory / 'sessions'))
    process = subprocess.Popen([sys.executable, '-X', 'utf8', str(Path(__file__)), '--child', '--mode', args.mode,
                                '--server', str(args.server)],
                               env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding='utf-8')
    handle = None
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.OpenProcess.argtypes = [ctypes.c_uint32, ctypes.c_bool, ctypes.c_uint32]
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.WaitForSingleObject.restype = ctypes.c_uint32
    kernel.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    evidence = {'mode': args.mode}
    try:
        first = process.stdout.readline()
        if not first:
            _, stderr = process.communicate(timeout=5)
            evidence['stderr'] = stderr
            raise RuntimeError(stderr)
        opened = json.loads(first)
        evidence['opened'] = opened
        handle = kernel.OpenProcess(0x00100001, False, opened['pid'])
        assert handle, ctypes.get_last_error()
        stdout, stderr = process.communicate(timeout=40)
        evidence['stderr'] = stderr
        evidence['clientExitCode'] = process.returncode
        assert process.returncode == 0, stderr
        ready = json.loads(stdout.strip())
        evidence['ready'] = ready
        # SDK disconnect cleanup may finish after the client subprocess exits.
        deadline = time.monotonic() + 10
        while kernel.WaitForSingleObject(handle, 0) == 258 and time.monotonic() < deadline:
            time.sleep(.1)
        evidence['workerExited'] = kernel.WaitForSingleObject(handle, 0) == 0
        evidence['savedDatabaseExists'] = Path(ready['identity']['idbPath']).is_file()
        assert evidence['workerExited'], 'Owned IDA leaked after stdio disconnect'
        assert evidence['savedDatabaseExists'], 'Database was not saved on disconnect'
        # Prove the final saved artifact opens in IDA, not just that it exists.
        from ida_backend.sessions import IdaSessions
        sys.path.insert(0, str(Path(__file__).parent))
        from run_ida_smoke import wait_ready, wait_exit
        manager = IdaSessions(root=directory / 'reopen')
        try:
            reopened = manager.open(ready['identity']['idbPath'], 'headless')
            evidence['reopened'] = wait_ready(manager, reopened['sessionId'])
            manager.close(reopened['sessionId'])
            wait_exit(manager, reopened['sessionId'])
        finally:
            manager.shutdown()
        evidence['passed'] = True
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait(timeout=5)
        if handle:
            if kernel.WaitForSingleObject(handle, 0) == 258:
                kernel.TerminateProcess(handle, 1)
                kernel.WaitForSingleObject(handle, 5000)
            kernel.CloseHandle(handle)
        path = directory / 'evidence.json'
        path.write_text(json.dumps(evidence, indent=2, ensure_ascii=False), encoding='utf-8')
        print(str(path), flush=True)


if __name__ == '__main__':
    main()
