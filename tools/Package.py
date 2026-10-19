#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Create a V1.0 portable package and clean source archive; never publish remotely."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import zipfile
from CheckSource import ROOT, check, public_files
from Inspect import inspect


def checked_json(path):
    value = json.loads(path.read_text(encoding='utf-8-sig'))
    assert value.get('passed'), 'Checks did not pass: ' + path.name
    return value


def zip_atomic(path, members, compression):
    temporary = path.with_suffix('.building')
    with temporary.open('wb') as stream:
        with zipfile.ZipFile(stream, 'w', compression=compression, allowZip64=True) as archive:
            for source, name in members:
                archive.write(source, name)
        stream.flush(); os.fsync(stream.fileno())
    os.replace(temporary, path)
    with zipfile.ZipFile(path) as archive:
        assert archive.testzip() is None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=ROOT / 'artifacts/release')
    args = parser.parse_args(); output = args.output.resolve(); output.mkdir(parents=True, exist_ok=True)
    assert check() == 0, 'Public source check failed'
    executable = ROOT / 'artifacts/win-x64/WinToPod.exe'
    inspected = inspect(executable)
    validation = ROOT / 'artifacts/validation'; validation.mkdir(parents=True, exist_ok=True)
    result = re.search(r'RESULT (\d+)/(\d+) passed', (validation / 'checks.txt').read_text(encoding='utf-8-sig'))
    assert result and result[1] == result[2], 'Core checks missing or failed'
    automatic = checked_json(validation / 'automatic-connection.json')
    video = checked_json(validation / 'video-sync.json')
    capture = [json.loads(line) for line in (validation / 'capture.jsonl').read_text(encoding='utf-8-sig').splitlines() if line.startswith('{')]
    assert len(capture) == 3 and all(row['passed'] for row in capture), 'Capture/volume checks missing or failed'
    interop = checked_json(validation / 'capture-interop.json')
    suites = [('receiver', 'independent_simulated_receiver', 21), ('native-pair', 'native_pair_simulations', 39),
              ('playback-digest', 'playback_digest_simulations', 15), ('stability', 'stability_simulations', 8)]
    present = [(validation / (name + '.json')).is_file() for name, _, _ in suites]
    assert not any(present) or all(present), 'Protocol reports are incomplete'
    protocols = {}
    if all(present):
        for name, key, expected in suites:
            rows = json.loads((validation / (name + '.json')).read_text(encoding='utf-8-sig'))[key]
            assert len(rows) == expected and all(row['passed'] for row in rows), 'Protocol checks failed: ' + name
            protocols[name] = rows
    optional = {name: checked_json(validation / (name + '.json')) if (validation / (name + '.json')).exists() else None
                for name in ['video-companion', 'video-browser', 'windows-ui']}
    report = dict(product='WinToPod', version='V1.0', built_utc=datetime.now(timezone.utc).isoformat(),
        inspection=inspected, core_checks=int(result[1]), automatic_connection_checks=len(automatic['checks']),
        video_bridge_checks=len(video['checks']), capture_checks=capture, native_interface_checks=interop,
        protocol_simulations=protocols or None, optional_checks=optional,
        real_homepod_tested_for_this_build=False, actual_windows_capture_tested=False,
        actual_multi_monitor_dpi_tested=False, code_signed=False, remote_repository_pushed=False)
    report_path = validation / 'release.json'; report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    package = output / 'WinToPod-V1.0-win-x64.zip'; source = output / 'WinToPod-V1.0-source.zip'
    sums = output / 'SHA256SUMS.txt'
    exe_sum = validation / 'exe-sha256.txt'; exe_sum.write_text(inspected['exe_sha256'] + '  WinToPod.exe\n', encoding='utf-8')
    members = [(executable, 'WinToPod.exe'), (ROOT / 'docs/USAGE.md', '使用说明.md'), (ROOT / 'LICENSE', 'LICENSE'),
               (ROOT / 'CHANGELOG.md', 'CHANGELOG.md'), (ROOT / 'THIRD-PARTY.md', 'THIRD-PARTY.md'),
               (ROOT / 'docs/PRIVACY.md', 'docs/PRIVACY.md'), (report_path, 'docs/VALIDATION.json'), (exe_sum, 'SHA256SUMS.txt')]
    for folder, target in [('licenses', 'licenses'), ('browser/WinToPod.VideoSync', '浏览器音画补偿')]:
        base = ROOT / folder
        members.extend((p, target + '/' + p.relative_to(base).as_posix()) for p in sorted(base.rglob('*')) if p.is_file())
    zip_atomic(package, members, zipfile.ZIP_STORED)
    zip_atomic(source, [(p, 'WinToPod/' + p.relative_to(ROOT).as_posix()) for p in public_files()], zipfile.ZIP_DEFLATED)
    integrity = {p.name: {'bytes': p.stat().st_size, 'sha256': hashlib.sha256(p.read_bytes()).hexdigest()} for p in [package, source]}
    sums.write_text(''.join(row['sha256'] + '  ' + name + '\n' for name, row in integrity.items()), encoding='utf-8')
    (output / 'integrity.json').write_text(json.dumps(integrity, indent=2) + '\n', encoding='utf-8')
    shutil.copyfile(ROOT / 'docs/USAGE.md', output / 'WinToPod-V1.0-使用说明.md')
    print(json.dumps(integrity, indent=2))


if __name__ == '__main__':
    main()
