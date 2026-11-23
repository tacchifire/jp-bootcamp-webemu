#!/usr/bin/env python3
"""Convert UF2 flash blocks to a flat binary, filling gaps with 0xFF."""
import argparse
from pathlib import Path
import struct

BLOCK = 512
MAGIC_START = (0x0A324655, 0x9E5D5157)
MAGIC_END = 0x0AB16F30
FLAG_NOT_MAIN_FLASH = 0x1
FLAG_FILE_CONTAINER = 0x1000
FLAG_FAMILY_ID = 0x2000
RP2040_FAMILY_ID = 0xE48BFF56
RP2040_FLASH = (0x10000000, 0x200000)  # flash0 in emu/platform/rp2040.repl


def parse(data):
    """Validate the UF2 framing; return sorted (address, payload) flash blocks."""
    if not data or len(data) % BLOCK:
        raise ValueError('UF2 must contain complete 512-byte blocks')
    blocks = []
    for offset in range(0, len(data), BLOCK):
        magic0, magic1, flags, address, size, _, _, _ = struct.unpack_from(
            '<8I', data, offset)
        if (magic0, magic1) != MAGIC_START:
            raise ValueError(f'Invalid UF2 header magic at offset {offset}')
        if struct.unpack_from('<I', data, offset + 508)[0] != MAGIC_END:
            raise ValueError(f'Invalid UF2 end magic at offset {offset}')
        if not 0 < size <= 476 or address + size > 2**32:
            raise ValueError(f'Invalid UF2 payload at offset {offset}')
        if flags & FLAG_NOT_MAIN_FLASH:  # These blocks do not belong in the BIN.
            continue
        if flags & FLAG_FILE_CONTAINER:
            raise ValueError('UF2 file-container blocks are not flash images')
        blocks.append((address, data[offset + 32:offset + 32 + size]))
    if not blocks:
        raise ValueError('UF2 contains no flash payload')
    blocks.sort(key=lambda block: block[0])
    end = blocks[0][0]
    for address, payload in blocks:
        if address < end:
            raise ValueError('Overlapping UF2 payloads')
        end = address + len(payload)
    return blocks


def check_rp2040(data):
    """Stricter checks for images the emulator loads at the RP2040 flash base.

    Beyond parse(): every block's numbering must be complete (catches truncated
    files), a family ID, when present, must be RP2040's, and the payload must
    start at the flash base and fit in the emulated 2 MB flash.
    """
    blocks = parse(data)
    numbering = {}
    families = set()
    for offset in range(0, len(data), BLOCK):
        _, _, flags, _, _, block_no, num_blocks, family = struct.unpack_from(
            '<8I', data, offset)
        if block_no >= num_blocks:
            raise ValueError(f'Invalid UF2 block number at offset {offset}')
        numbering.setdefault(num_blocks, set()).add(block_no)
        if flags & FLAG_FAMILY_ID:
            families.add(family)
    for num_blocks, seen in numbering.items():
        if len(seen) != num_blocks:
            raise ValueError(f'UF2 is incomplete: {len(seen)} of {num_blocks} blocks present')
    if families - {RP2040_FAMILY_ID}:
        raise ValueError('UF2 family is not RP2040: '
                         + ', '.join(f'0x{f:08x}' for f in sorted(families)))
    start, size = RP2040_FLASH
    base = blocks[0][0]
    end = blocks[-1][0] + len(blocks[-1][1])
    if base != start:
        raise ValueError(f'UF2 starts at 0x{base:08x}, not at the flash base 0x{start:08x}')
    if end > start + size:
        raise ValueError(f'UF2 ends at 0x{end:08x}, beyond the 2 MB flash')
    return {'blocks': len(data) // BLOCK, 'flash_blocks': len(blocks),
            'base': base, 'end': end, 'family_id': sorted(families)}


def convert(source, destination):
    blocks = parse(source.read_bytes())
    base = blocks[0][0]
    # Stream gaps to avoid allocating memory proportional to address span.
    with destination.open('wb') as output:
        end = base
        for address, payload in blocks:
            gap = address - end
            while gap:
                count = min(gap, 65536)
                output.write(b'\xff' * count)
                gap -= count
            output.write(payload)
            end = address + len(payload)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    try:
        convert(args.input, args.output)
    except (OSError, ValueError) as exc:
        parser.exit(1, f'UF2 conversion failed: {exc}\n')


if __name__ == '__main__':
    main()
