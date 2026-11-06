import json
import pathlib
import subprocess

import unity_editor

root = pathlib.Path(__file__).resolve().parent.parent
mono = unity_editor.mono()
if not mono:
    raise SystemExit('Install a Unity editor under ~/Unity/Hub/Editor or set TL_MONO to the installed Unity Mono executable.')
print('Mono: ' + mono)
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
