#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Check the public tree before packaging. Local build output is never selected."""
from pathlib import Path
import json
import re
import sys

ROOT = Path(__file__).resolve().parent.parent
EXCLUDED = {'.git', 'bin', 'obj', 'artifacts', 'node_modules', '__pycache__', 'TestResults', '.vs', '.vscode', '.idea', '.venv', '.pytest_cache', '.DS_Store', 'Thumbs.db'}
ROOT_FILES = {'README.md', 'LICENSE', 'CHANGELOG.md', 'THIRD-PARTY.md', 'Directory.Build.props', 'WinToPod.slnx', 'global.json', '.gitignore', '.gitattributes', '.editorconfig'}
ROOT_FOLDERS = {'.github', 'src', 'tests', 'tools', 'browser', 'docs', 'licenses', 'packaging'}


def public_files():
    return sorted(p for p in ROOT.rglob('*') if p.is_file() and not any(part in EXCLUDED for part in p.relative_to(ROOT).parts))


def check():
    files = public_files(); problems = []
    for path in ROOT.iterdir():
        if path.name in EXCLUDED:
            continue
        if path.name not in (ROOT_FOLDERS if path.is_dir() else ROOT_FILES):
            problems.append('Unexpected root item: ' + path.name)
    for path in files:
        rel = path.relative_to(ROOT).as_posix()
        if path.is_symlink(): problems.append('Symlink: ' + rel)
        if path.suffix.lower() in {'.zip', '.exe', '.dll', '.pdb', '.log', '.dat', '.key', '.pem', '.pfx', '.obj', '.res'}:
            problems.append('Private or generated file: ' + rel)
        if path.name in {'desktop-settings.json', 'playback-credentials.dat', 'paired-receivers.dat'}:
            problems.append('Local user state: ' + rel)
        if path.suffix.lower() in {'.cs', '.csproj', '.props', '.py', '.mjs', '.js', '.md', '.json', '.ps1', '.html', '.yml'}:
            text = path.read_text(encoding='utf-8')
            if re.search(r'/(?:workspace/scratch|Users)/[A-Za-z0-9_-]+/', text):
                problems.append('Absolute local path: ' + rel)
            if re.search(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----', text):
                problems.append('Private key: ' + rel)
            if re.search(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})', text):
                problems.append('Access token: ' + rel)
            if (rel.startswith('src/WinToPod.WinUI/') or rel.startswith('docs/')) and re.search(r'WinUI Demo|DemoApplication|20261027-settings-scroll', text):
                problems.append('Private/development marker: ' + rel)
    props = (ROOT / 'Directory.Build.props').read_text(encoding='utf-8')
    if '<Version>1.1.0</Version>' not in props: problems.append('Product version differs from V1.1')
    manifest = json.loads((ROOT / 'browser/WinToPod.VideoSync/manifest.json').read_text(encoding='utf-8'))
    if manifest['version'] != '1.1.0': problems.append('Companion version differs from V1.1')
    for name in manifest['icons'].values():
        if not (ROOT / 'browser/WinToPod.VideoSync' / name).is_file(): problems.append('Missing browser icon: ' + name)
    result = {'passed': not problems, 'public_files': len(files), 'problems': problems}
    print(json.dumps(result, indent=2))
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    sys.exit(check())
