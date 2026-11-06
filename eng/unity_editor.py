import json
import os
import pathlib
import re
import shutil
import subprocess
import sys

windows = sys.platform == 'win32'
exe = 'Editor/Unity.exe' if windows else 'Editor/Unity'
mono_exe = 'Editor/Data/MonoBleedingEdge/bin/' + ('mono.exe' if windows else 'mono')
module = 'windows-il2cpp' if windows else 'linux-il2cpp'


def hub():
    configured = os.environ.get('TL_UNITY_EDITORS')
    if configured:
        return pathlib.Path(configured)
    if windows:
        return pathlib.Path(os.environ.get('PROGRAMFILES', 'C:/Program Files')) / 'Unity/Hub/Editor'
    return pathlib.Path.home() / 'Unity/Hub/Editor'


def order(version):
    return [(0, int(part)) if part.isdigit() else (1, part) for part in re.findall(r'\d+|[A-Za-z]+', version)]


def cli():
    configured = os.environ.get('TL_UNITY_CLI')
    if configured:
        return configured
    found = shutil.which('unity')
    if found:
        return found
    local = pathlib.Path(os.environ.get('LOCALAPPDATA', '')) / 'Unity/bin/unity.exe' if windows else pathlib.Path.home() / '.local/bin/unity'
    return str(local) if local.is_file() else None


def from_cli():
    tool = cli()
    if not tool:
        return {}
    result = subprocess.run([tool, '--no-banner', 'editors', '--installed', '--json'], capture_output=True, text=True)
    try:
        rows = json.loads(result.stdout)['data']
    except (ValueError, KeyError):
        return {}
    return {row['version']: pathlib.Path(row['location']) for row in rows if pathlib.Path(row['location']).is_file()}


def from_hub():
    root = hub()
    if not root.is_dir():
        return {}
    return {p.name: p / exe for p in root.iterdir() if (p / exe).is_file()}


def installed():
    found = from_hub()
    found.update(from_cli())
    return found


def install():
    tool = cli()
    if not tool:
        return
    subprocess.run([tool, '--no-banner', 'install', '7', '--yes', '--accept-eula', '--non-interactive', '--module', module], check=True, stdout=sys.stderr)


def newest():
    found = installed()
    if not found:
        install()
        found = installed()
    if not found:
        return None
    return found[max(found, key=order)]


def editor():
    configured = os.environ.get('UNITY')
    if configured:
        return pathlib.Path(configured).as_posix()
    chosen = newest()
    return chosen.as_posix() if chosen else None


def version():
    chosen = editor()
    return pathlib.Path(chosen).parent.parent.name if chosen else None


def mono():
    configured = os.environ.get('TL_MONO')
    if configured:
        return configured
    chosen = editor()
    bundled = pathlib.Path(chosen).parent.parent / mono_exe if chosen else None
    return bundled.as_posix() if bundled and bundled.is_file() else shutil.which('mono')


if __name__ == '__main__':
    kind = sys.argv[1] if len(sys.argv) > 1 else ''
    finders = {'version': version, 'editor': editor, 'mono': mono, 'cli': cli}
    if kind not in finders:
        raise SystemExit('Use version, editor, mono or cli.')
    found = finders[kind]()
    if not found:
        raise SystemExit(f'No Unity {kind} found. Install the Unity CLI, or set UNITY, TL_UNITY_EDITORS, TL_MONO or TL_UNITY_CLI.')
    print(found)
