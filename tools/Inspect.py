#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Inspect the actual unpackaged WinUI distribution without executing it."""
from pathlib import Path
import hashlib
import json
import struct
import sys
import xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parent.parent

def pe(data):
    assert data[:2] == b'MZ', 'Missing PE signature'
    at, = struct.unpack_from('<I', data, 0x3c)
    assert data[at:at+4] == b'PE\0\0'
    assert struct.unpack_from('<H', data, at+4)[0] == 0x8664, 'Expected x64 executable'
    assert struct.unpack_from('<H', data, at+24+68)[0] == 2, 'Expected GUI executable'

def inspect(stage):
    stage = Path(stage); app = stage/'app'
    expected = ET.parse(ROOT/'Directory.Build.props').findtext('.//FileVersion')
    for path in [stage/'WinToPod.exe', app/'WinToPod.exe']:
        data=path.read_bytes(); pe(data)
        signature=struct.pack('<I',0xFEEF04BD); at=data.index(signature)
        major,minor,build,revision=map(int,expected.split('.'))
        assert struct.unpack_from('<II',data,at+8)==((major<<16)|minor,(build<<16)|revision), 'PE version mismatch: '+str(path)
    for name in ['WinToPod.dll','WinToPod.Core.dll','WinToPod.AirPlay.dll','Microsoft.UI.Xaml.Internal.dll','Microsoft.UI.Xaml.Controls.dll','coreclr.dll','hostfxr.dll','WinToPod.runtimeconfig.json','WinToPod.deps.json','resources.pri','Microsoft.UI.Xaml.Controls.pri']:
        assert (app/name).is_file(), 'Missing runtime dependency: '+name
    assert (app/'resources.pri').read_bytes()==(app/'Microsoft.UI.Xaml.Controls.pri').read_bytes(), 'Theme index mismatch'
    runtime=json.loads((app/'WinToPod.runtimeconfig.json').read_text())['runtimeOptions']
    assert runtime['includedFrameworks'], 'Runtime must be self-contained'
    files=[p for p in stage.rglob('*') if p.is_file()]
    banned={'.pdb','.log','.dat','.key','.pem','.pfx','.zip','.obj','.res'}
    assert not [str(p.relative_to(stage)) for p in files if p.suffix.lower() in banned], 'Private/generated files in runtime'
    assert not any(p.name in {'desktop-settings.json','paired-receivers.dat','playback-credentials.dat'} for p in files)
    for name in ['WinToPod.dll','WinToPod.Core.dll','WinToPod.AirPlay.dll']:
        data=(app/name).read_bytes()
        for text in [str(ROOT),'WinUI Demo','DemoApplication','20261027-settings-scroll']:
            assert text.encode() not in data and text.encode('utf-16-le') not in data, 'Local/development marker: '+name
    assert not any(p.name.startswith(('PresentationFramework','System.Windows.Forms','WindowsFormsIntegration')) for p in files), 'Retired frontend runtime'
    return dict(passed=True, version='1.1', architecture='win-x64', file_version=expected,
        self_contained=True, files=len(files), windows_execution_tested_by_this_inspection=False,
        assembly_sha256={n:hashlib.sha256((app/n).read_bytes()).hexdigest() for n in ['WinToPod.dll','WinToPod.Core.dll','WinToPod.AirPlay.dll']})
if __name__=='__main__': print(json.dumps(inspect(sys.argv[1]),indent=2))
