#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Preserve raw output per existing scenario; never change its assertions.

All simulators share loopback PTP ports, so execute them sequentially. A failing
scenario does not suppress later scenarios. Exit nonzero if any command fails.
"""
import argparse
import ast
import json
import os
from pathlib import Path
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--drift', action='store_true', help='Also run the five virtual 8-hour real-loop measurements')
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    output = args.output.resolve()
    if output.exists():
        parser.error('Use a new output directory to preserve earlier raw results.')
    output.mkdir(parents=True)
    assembly = 'src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll'
    commands = [
        ('build', [args.dotnet, 'build', 'src/WinToPod.Checks', '-c', 'Release', '-m:1']),
        ('checks', [args.dotnet, 'run', '--project', 'src/WinToPod.Checks', '-c', 'Release', '--no-build']),
        ('source', ['python', 'tools/CheckSource.py']),
        ('automatic', [args.dotnet, assembly, '--test-automatic-connection']),
        ('video', [args.dotnet, assembly, '--test-video-sync']),
    ]
    for script in ['receiver_simulator', 'native_pair_simulator', 'playback_digest_simulator', 'stability_simulator']:
        tree = ast.parse((root / 'tests' / (script + '.py')).read_text(encoding='utf-8'))
        # Read the unchanged script's explicit mode list instead of maintaining
        # a second list that could silently omit a future scenario.
        lists = [node for node in ast.walk(tree) if isinstance(node, ast.List)
                 and len(node.elts) >= 8 and all(isinstance(v, ast.Constant) and isinstance(v.value, str) for v in node.elts)]
        modes = ast.literal_eval(max(lists, key=lambda node: len(node.elts)))
        for mode in modes:
            commands.append((script + '--' + mode, ['python', 'tests/' + script + '.py', args.dotnet, assembly, mode]))
    if args.drift:
        commands.append(('drift', [args.dotnet, assembly, '--base01-drift']))
    results = []
    for name, command in commands:
        started = time.monotonic()
        with (output / (name + '.stdout')).open('wb') as out, (output / (name + '.stderr')).open('wb') as err:
            result = subprocess.run(command, cwd=root, env={**os.environ, 'WINTOPOD_SOAK_SECONDS': '120'}, stdout=out, stderr=err)
        results.append(dict(name=name, command=command, returncode=result.returncode,
                            seconds=round(time.monotonic() - started, 3), soak_seconds=120))
        (output / 'results.json').write_text(json.dumps(results, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        print(f'{name}: exit={result.returncode}', flush=True)
        if name == 'build' and result.returncode:
            break  # Never claim stale binaries checked a failed build.
    return int(any(result['returncode'] for result in results))


if __name__ == '__main__':
    raise SystemExit(main())
