#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Run every unchanged native-pair scenario with ClockAligned; preserve failures."""
import argparse
import ast
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    output = args.output.resolve()
    if output.exists():
        parser.error('Use a new output directory to preserve earlier results.')
    output.mkdir(parents=True)
    tree = ast.parse((root / 'tests/native_pair_simulator.py').read_text(encoding='utf-8'))
    lists = [n for n in ast.walk(tree) if isinstance(n, ast.List) and len(n.elts) >= 8
             and all(isinstance(v, ast.Constant) and isinstance(v.value, str) for v in n.elts)]
    modes = ast.literal_eval(max(lists, key=lambda n: len(n.elts)))
    commands = [('build', [args.dotnet, 'build', 'src/WinToPod.Checks', '-c', 'Release', '-m:1'])]
    commands += [(mode, [sys.executable, 'tests/native_pair_simulator.py', args.dotnet,
                         'src/WinToPod.Checks/bin/Release/net10.0/WinToPod.Checks.dll', mode]) for mode in modes]
    results = []
    for name, command in commands:
        with (output / (name + '.stdout')).open('wb') as out, (output / (name + '.stderr')).open('wb') as err:
            result = subprocess.run(command, cwd=root, env={**os.environ, 'WINTOPOD_CHECK_CLOCK_ALIGNED': '1'}, stdout=out, stderr=err)
        results.append(dict(name=name, returncode=result.returncode, timing='ClockAligned', real_homepod=False))
        (output / 'results.json').write_text(json.dumps(results, indent=2) + '\n', encoding='utf-8')
        print(f'{name}: exit={result.returncode}', flush=True)
        if name == 'build' and result.returncode:
            break
    return int(any(r['returncode'] for r in results))


if __name__ == '__main__':
    raise SystemExit(main())
