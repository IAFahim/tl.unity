import os
import pathlib
import re
import shutil
import sys

root = pathlib.Path(__file__).resolve().parent.parent
hub = pathlib.Path(os.environ.get('TL_UNITY_EDITORS') or pathlib.Path.home() / 'Unity/Hub/Editor')


def pinned():
    text = (root / 'tests/Tl.V3.Project/ProjectSettings/ProjectVersion.txt').read_text()
    return re.search(r'm_EditorVersion:\s*(\S+)', text).group(1)


def order(version):
    return [(0, int(part)) if part.isdigit() else (1, part) for part in re.findall(r'\d+|[A-Za-z]+', version)]


def installed():
    if not hub.is_dir():
        return []
    return sorted((p.name for p in hub.iterdir() if (p / 'Editor/Unity').is_file()), key=order, reverse=True)


def version():
    versions = installed()
    for wanted in (os.environ.get('UNITY_EDITOR_VERSION'), pinned()):
        if wanted in versions:
            return wanted
    return versions[0] if versions else None


def editor():
    chosen = version()
    return str(hub / chosen / 'Editor/Unity') if chosen else None


def mono():
    configured = os.environ.get('TL_MONO')
    if configured:
        return configured
    chosen = version()
    bundled = hub / chosen / 'Editor/Data/MonoBleedingEdge/bin/mono' if chosen else None
    return str(bundled) if bundled and bundled.is_file() else shutil.which('mono')


if __name__ == '__main__':
    kind = sys.argv[1] if len(sys.argv) > 1 else ''
    finders = {'version': version, 'editor': editor, 'mono': mono}
    if kind not in finders:
        raise SystemExit('Use version, editor or mono.')
    found = finders[kind]()
    if not found:
        raise SystemExit(f'No Unity {kind} found. Install the editor pinned in tests/Tl.V3.Project/ProjectSettings/ProjectVersion.txt under {hub}, or set UNITY_EDITOR_VERSION, TL_UNITY_EDITORS or TL_MONO.')
    print(found)
