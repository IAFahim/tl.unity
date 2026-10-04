import json, pathlib, shutil, subprocess

root = pathlib.Path(__file__).resolve().parent.parent
target = root / 'artifacts/oracle'
target.mkdir(parents=True, exist_ok=True)
for source in sorted((root / 'tests/Fixtures').glob('*.json')):
    subprocess.run(['dotnet', str(root/'external/v3/tools/Tl.Bake/bin/Release/net10.0/Tl.Bake.dll'), str(source), str(target/(source.stem+'.tlb')), '--assembly', str(root/'tests/V3.Domain/bin/Release/net10.0/Game.Domain.dll')], check=True, capture_output=True)
outputs = {}
for kind in ['V3', 'Profile']:
    result = subprocess.run(['dotnet', str(root/f'tests/{kind}.Oracle/bin/Release/net10.0/{kind}.Oracle.dll'), str(target)], check=True, capture_output=True, text=True)
    outputs[kind] = json.loads(result.stdout)
    (target/(kind.lower()+'.json')).write_text(result.stdout)
assert outputs['V3'] == outputs['Profile'], 'Unity profile differs from pinned v3'
resources = root/'tests/Tl.V3.Project/Assets/Tests/Resources'
resources.mkdir(parents=True, exist_ok=True)
for source in target.glob('*.tlb'):
    shutil.copyfile(source, resources/(source.stem+'.bytes'))
(resources/'oracle.json').write_text(json.dumps(outputs['V3'], separators=(',', ':')))
print(f"Exact v3/profile parity: {len(outputs['V3'])} fixture/results")
