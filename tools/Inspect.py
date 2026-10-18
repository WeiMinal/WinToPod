#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Inspect the shipped PE/bundle bytes without executing Windows code."""
from pathlib import Path
import hashlib
import json
import struct
import sys
import zipfile
import zlib

ROOT = Path(__file__).resolve().parent.parent


def pe_layout(data):
    pe, = struct.unpack_from('<I', data, 0x3c)
    assert data[:2] == b'MZ' and data[pe:pe + 4] == b'PE\0\0'
    count, = struct.unpack_from('<H', data, pe + 6)
    optional_size, = struct.unpack_from('<H', data, pe + 20)
    optional = pe + 24
    sections = [struct.unpack_from('<IIII', data, optional + optional_size + i * 40 + 8) for i in range(count)]
    def offset(address):
        for virtual_size, base, raw_size, raw in sections:
            if base <= address < base + max(virtual_size, raw_size):
                return raw + address - base
        raise AssertionError('Unmapped PE address')
    return pe, optional, offset


def resources(data):
    _, optional, offset = pe_layout(data)
    rva, = struct.unpack_from('<I', data, optional + 112 + 16)
    root = offset(rva); result = {}
    def visit(start, path):
        named, ids = struct.unpack_from('<HH', data, start + 12)
        for i in range(named + ids):
            name, value = struct.unpack_from('<II', data, start + 16 + i * 8)
            child = path + [name]
            if value & 0x80000000:
                visit(root + (value & 0x7fffffff), child)
            else:
                address, size = struct.unpack_from('<II', data, root + value)
                at = offset(address); result.setdefault(child[0], []).append(data[at:at + size])
    visit(root, [])
    return result


def bundle(data):
    signature = bytes.fromhex('8b1202b96a612038727b930214d7a03213f5b9e6efae3318ee3b2dce24b36aae')
    marker = data.index(signature); header, = struct.unpack_from('<q', data, marker - 8)
    major, minor, count = struct.unpack_from('<IIi', data, header)
    assert major == 6 and minor == 0 and 0 < count < 10000
    cursor = header + 12
    def string():
        nonlocal cursor
        length = shift = 0
        while shift <= 28:
            value = data[cursor]; cursor += 1; length |= (value & 127) << shift
            if value < 128: break
            shift += 7
        assert length < 8192 and cursor + length <= len(data)
        value = data[cursor:cursor + length].decode(); cursor += length
        return value
    string(); cursor += 40
    names = set(); assemblies = {}
    for _ in range(count):
        offset, size, compressed = struct.unpack_from('<qqq', data, cursor); cursor += 25
        name = string(); names.add(name)
        assert 0 <= offset < header and 0 <= size <= 256 * 1024 * 1024 and 0 <= compressed
        assert offset + (compressed or size) <= header
        if name in {'WinToPod.dll', 'WinToPod.AirPlay.dll', 'WinToPod.Core.dll'}:
            value = data[offset:offset + (compressed or size)]
            if compressed: value = zlib.decompress(value, -15)
            assert len(value) == size; assemblies[name] = value
    return names, assemblies


def inspect(path):
    data = Path(path).read_bytes()
    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as archive:
            assert archive.testzip() is None
            exes = [n for n in archive.namelist() if n.endswith('.exe')]
            assert len(exes) == 1; data = archive.read(exes[0])
    pe, optional, _ = pe_layout(data)
    assert struct.unpack_from('<H', data, pe + 4)[0] == 0x8664
    assert struct.unpack_from('<H', data, optional + 68)[0] == 2
    native = resources(data); icon = (ROOT / 'src/WinToPod.Desktop/Assets/Brand/WinToPod.ico').read_bytes()
    frame_count, = struct.unpack_from('<H', icon, 4); sizes = []
    assert len(native[3]) == frame_count
    for i in range(frame_count):
        at = 6 + 16 * i; size, offset = struct.unpack_from('<II', icon, at + 8)
        assert icon[offset:offset + size] in native[3]
        sizes.append(icon[at] or 256)
    version = native[16][0]; fixed = version.index(struct.pack('<I', 0xFEEF04BD))
    _, _, file_ms, file_ls, product_ms, product_ls = struct.unpack_from('<6I', version, fixed)
    assert (file_ms, file_ls, product_ms, product_ls) == (0x10000, 0, 0x10000, 0), 'PE version is not 1.0.0.0'
    names, assemblies = bundle(data)
    assert set(assemblies) == {'WinToPod.dll', 'WinToPod.AirPlay.dll', 'WinToPod.Core.dll'}
    assert {'PresentationFramework.Fluent.dll', 'WindowsFormsIntegration.dll', 'System.Security.Cryptography.ProtectedData.dll'} <= names
    assert not any(n.endswith('.pdb') for n in names)
    desktop = assemblies['WinToPod.dll']; brand = ROOT / 'src/WinToPod.Desktop/Assets/Brand'
    embedded = [p for p in brand.iterdir() if p.suffix in {'.ico', '.png'} and not p.name.endswith('-source.png')]
    assert all(p.read_bytes() in desktop for p in embedded), 'Compiled theme assets differ from source'
    assert all(name in desktop for name in [b'BrandAssets', b'FluentDeviceView', b'ProcessAudioCapture', b'DesktopPairingStore'])
    assert b'DiagnosticRedactor' in assemblies['WinToPod.Core.dll']
    assert all(needle not in assembly and needle.decode().encode('utf-16-le') not in assembly
               for needle in [str(ROOT).encode(), b'WinToPod-app-128.png', b'ConnectionCoordinator', b'VolumeScale', b'HeaderImage'] for assembly in assemblies.values())
    return {'passed': True, 'product_version': '1.0', 'pe_file_version': '1.0.0.0', 'architecture': 'win-x64',
            'exe_bytes': len(data), 'exe_sha256': hashlib.sha256(data).hexdigest(), 'transparent_icon_sizes': sizes,
            'embedded_brand_files': len(embedded), 'self_contained_runtime': True, 'symbols_bundled': False,
            'assembly_sha256': {n: hashlib.sha256(v).hexdigest() for n, v in assemblies.items()},
            'windows_execution_tested_by_this_inspection': False}


if __name__ == '__main__':
    print(json.dumps(inspect(sys.argv[1]), indent=2))
