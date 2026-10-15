"""Read bytes actually retained in a minidump, never disk-image fallbacks."""
import argparse
import json
import mmap
import struct
from pathlib import Path

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dump', type=Path, required=True)
    parser.add_argument('--address', action='append', required=True)
    parser.add_argument('--size', type=int, default=64)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.size <= 4096:
        parser.error('size must be 1..4096')
    with args.dump.open('rb') as stream, mmap.mmap(stream.fileno(), 0, access=mmap.ACCESS_READ) as data:
        if data[:4] != b'MDMP':
            raise ValueError('Expected minidump')
        count, directory = struct.unpack_from('<II', data, 8)
        if count > 256 or directory + count * 12 > len(data):
            raise ValueError('Invalid stream directory')
        ranges = []
        for index in range(count):
            kind, size, rva = struct.unpack_from('<III', data, directory + index * 12)
            if rva + size > len(data):
                raise ValueError('Stream exceeds dump')
            if kind == 9:
                entries, cursor = struct.unpack_from('<QQ', data, rva)
                if 16 + entries * 16 > size:
                    raise ValueError('Invalid Memory64 descriptors')
                for entry in range(entries):
                    address, length = struct.unpack_from('<QQ', data, rva + 16 + entry * 16)
                    ranges.append((address, length, cursor))
                    cursor += length
                if cursor > len(data):
                    raise ValueError('Memory64 bytes exceed dump')
            elif kind == 5:
                entries = struct.unpack_from('<I', data, rva)[0]
                if 4 + entries * 16 > size:
                    raise ValueError('Invalid MemoryList descriptors')
                for entry in range(entries):
                    address, length, offset = struct.unpack_from('<QII', data, rva + 4 + entry * 16)
                    if offset + length > len(data):
                        raise ValueError('MemoryList bytes exceed dump')
                    ranges.append((address, length, offset))
        result = {'dump': str(args.dump), 'ranges': len(ranges), 'reads': []}
        for value in args.address:
            requested = int(value, 0)
            record = {'address': hex(requested), 'size': args.size, 'present': False}
            for address, length, offset in ranges:
                if address <= requested and requested + args.size <= address + length:
                    start = offset + requested - address
                    record.update(present=True, fileOffset=start,
                                  hex=data[start:start + args.size].hex(' '))
                    break
            result['reads'].append(record)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))

if __name__ == '__main__':
    main()
