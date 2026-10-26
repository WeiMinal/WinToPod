#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Package only a fresh, inspected WinUI staging tree and clean public source."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import zipfile
from CheckSource import ROOT, check, public_files
from Inspect import inspect

def zip_atomic(path,members):
    tmp=path.with_suffix('.building')
    with tmp.open('wb') as stream:
        with zipfile.ZipFile(stream,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=6,allowZip64=True) as archive:
            for source,name in members: archive.write(source,name)
        stream.flush(); os.fsync(stream.fileno())
    os.replace(tmp,path)
    with zipfile.ZipFile(path) as archive: assert archive.testzip() is None

def checked_json(path):
    result=json.loads(path.read_text(encoding='utf-8-sig'))
    assert result.get('passed',result.get('Passed',False)), 'Failed checks: '+path.name
    return result

def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--output',type=Path,default=ROOT/'artifacts/release')
    args=parser.parse_args(); output=args.output.resolve(); output.mkdir(parents=True,exist_ok=True)
    assert check()==0
    stage=ROOT/'artifacts/win-x64'; validation=ROOT/'artifacts/validation'
    inspection=inspect(stage)
    text=(validation/'checks.txt').read_text(encoding='utf-8-sig')
    count=re.search(r'RESULT (\d+)/(\d+) passed',text)
    assert count and count[1]==count[2], 'Core checks missing/failed'
    reports={name:checked_json(validation/(name+'.json')) for name in ['automatic-connection','video-sync','capture-interop','output-routing','controller']}
    capture=[json.loads(line) for line in (validation/'capture.jsonl').read_text(encoding='utf-8-sig').splitlines() if line.startswith('{')]
    assert len(capture)==4 and all(row['passed'] for row in capture)
    windows=checked_json(validation/'windows-ui.json') if (validation/'windows-ui.json').exists() else None
    report=dict(product='WinToPod',version='V1.1',built_utc=datetime.now(timezone.utc).isoformat(),inspection=inspection,
        core_checks=int(count[1]),checks=reports,capture=capture,windows_ui=windows,
        real_homepod_tested_for_this_build=False,actual_windows_capture_tested=False,code_signed=False,
        full_stream_simulations='Not certified for V1.1; cloud simulations have triggered PTP/scheduling protection. See docs/TESTING.md.')
    report_path=validation/'release.json';report_path.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n')
    (stage/'app/VALIDATION.json').write_bytes(report_path.read_bytes())
    package=output/'WinToPod-V1.1-win-x64.zip';source=output/'WinToPod-V1.1-source.zip'
    zip_atomic(package,[(p,p.relative_to(stage).as_posix()) for p in sorted(stage.rglob('*')) if p.is_file()])
    zip_atomic(source,[(p,'WinToPod/'+p.relative_to(ROOT).as_posix()) for p in public_files()])
    sums={p.name:dict(bytes=p.stat().st_size,sha256=hashlib.sha256(p.read_bytes()).hexdigest()) for p in [package,source]}
    (output/'SHA256SUMS.txt').write_text(''.join(v['sha256']+'  '+k+'\n' for k,v in sums.items()))
    print(json.dumps(sums,indent=2))
if __name__=='__main__':main()
