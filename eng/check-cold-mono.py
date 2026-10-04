import json
import os
import pathlib
import shutil
import subprocess

root = pathlib.Path(__file__).resolve().parent.parent
configured = os.environ.get('TL_MONO')
unity_mono = pathlib.Path.home() / 'Unity/Hub/Editor/6000.7.0a6/Editor/Data/MonoBleedingEdge/bin/mono'
mono = configured or (str(unity_mono) if unity_mono.is_file() else shutil.which('mono'))
if not mono:
    raise SystemExit('Install Mono or set TL_MONO to the installed Unity Mono executable.')
executable = root / 'tests/Cold.Mono/bin/Release/netstandard2.1/Cold.Mono.dll'
fixture = root / 'artifacts/oracle/recoil.tlb'
results = []
for mode in ['cold', 'prepared']:
    result = subprocess.run([mono, str(executable), str(fixture), mode], capture_output=True, text=True)
    (root / f'artifacts/tests/mono-{mode}.log').write_text(result.stdout + result.stderr)
    if result.returncode:
        raise SystemExit(result.stdout + result.stderr)
    results.append({'mode': mode, 'exitCode': result.returncode, 'result': result.stdout.strip()})
    print(result.stdout.strip())
version = subprocess.check_output([mono, '--version'], text=True).splitlines()[0]
(root / 'artifacts/tests/cold-mono.json').write_text(json.dumps({'mono': version, 'freshProcesses': results}, indent=2) + '\n')
