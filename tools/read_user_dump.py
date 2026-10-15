"""Read a user-mode dump on one owning DbgEng thread, without a kernel session.

Reuses the validated local DbgEng physical loader/ABI only. This is a standalone
user-dump entry and never invokes kernel_open_dump, Engine.open or kernel admission.
"""
import argparse
import ctypes as C
import json
import re
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from kernel_backend.engine import Engine, U32, U64, PTR, check

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dump', type=Path, required=True)
    parser.add_argument('--symbols', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--module', default='')
    parser.add_argument('--frame', type=int)
    parser.add_argument('--code-window', action='store_true')
    parser.add_argument('--object-type')
    parser.add_argument('--object-address', type=lambda value: int(value, 0))
    parser.add_argument('--field', action='append', default=[])
    args = parser.parse_args()
    dump = args.dump.resolve(strict=True)
    symbols = args.symbols.resolve(strict=True)
    with dump.open('rb') as stream:
        if stream.read(4) != b'MDMP':
            raise ValueError('Expected a Windows user minidump container')
    engine = Engine()
    try:
        check(engine.symbols.call(77, [C.c_wchar_p], str(symbols)), 'SetSymbolPathWide(local only)')
        check(engine.client.call(60, [C.c_wchar_p, U64], str(dump), 0), 'OpenDumpFileWide')
        check(engine.control.call(93, [U32, U32], 0, 0xffffffff), 'WaitForEvent(dump)')
        kind, qualifier = U32(), U32()
        check(engine.control.call(34, [C.POINTER(U32), C.POINTER(U32)],
                                  C.byref(kind), C.byref(qualifier)), 'GetDebuggeeType')
        if kind.value != 2 or qualifier.value < 1024:
            raise ValueError(f'Expected an independent user dump, got {kind.value}:{qualifier.value}')
        engine.output.reset()
        if args.module and not re.fullmatch(r'[A-Za-z0-9_]+', args.module):
            raise ValueError('module must be an exact module basename')
        reload_command = f'.reload /f {args.module}.dll; ' if args.module else ''
        module_command = f'lmv m {args.module}' if args.module else 'lm'
        commands = f'.symopt+0x10; {reload_command}.exr -1; .ecxr; kv; {module_command}; r'
        if args.frame is not None:
            if not 0 <= args.frame <= 64:
                raise ValueError('frame must be 0..64')
            commands += f'; .frame /r {args.frame}; dv /t'
        if args.code_window:
            commands += '; .ecxr; u @rip-20 L20; dps @rsp L40'
        if args.object_type:
            if (not args.module or not re.fullmatch(r'[A-Za-z0-9_:]+', args.object_type) or
                    not args.object_address or args.object_address < 0 or
                    any(not re.fullmatch(r'[A-Za-z0-9_]+', field) for field in args.field)):
                raise ValueError('Typed object requires exact module/type, positive address and field names')
            target = f'{args.module}!{args.object_type} 0x{args.object_address:x}'
            if args.field:
                commands += ''.join(f'; dt {target} {field}' for field in args.field)
            else:
                commands += f'; dt {target}'
        check(engine.control.call(66, [U32, C.c_char_p, U32], 0, commands.encode('ascii'), 0), 'Execute(read-only dump commands)')
        result = {'dump': str(dump), 'dumpBytes': dump.stat().st_size,
                  'enginePath': str(engine.path), 'userDebuggeeClass': kind.value,
                  'dumpQualifier': qualifier.value, 'symbolPath': str(symbols),
                  'commands': commands, **engine.output.result()}
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2), encoding='utf-8')
        print(json.dumps(result, indent=2), flush=True)
    finally:
        engine.close()

if __name__ == '__main__':
    main()
