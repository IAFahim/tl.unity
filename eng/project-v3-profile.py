import hashlib
import json
import pathlib
import re
import subprocess
import sys

source = pathlib.Path(sys.argv[1]).resolve()
target = pathlib.Path(sys.argv[2])
target.mkdir(parents=True, exist_ok=True)
pin = 'a464bfe95e158fb79e97f7b0848bd8e8d9c79b7b'
actual = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
if actual != pin:
    raise SystemExit('Unity profile requires exact v3 source pin ' + pin + '; found ' + actual)
core = source / 'src/Tl.Core'
inputs = {}


def read(name):
    text = (core / name).read_text()
    inputs[name] = hashlib.sha256(text.encode()).hexdigest()
    return text


def section(text, declaration):
    first = text.index(declaration)
    tokens = re.finditer(r'//[^\n]*|/\*[\s\S]*?\*/|@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\x27(?:\\.|[^\x27\\])*\x27|=>|[{};]', text[first:])
    depth = 0
    expression = False
    for token in tokens:
        value = token.group()
        if value == '=>' and depth == 0:
            expression = True
        elif value == '{':
            depth += 1
        elif value == '}':
            depth -= 1
            if depth == 0 and not expression:
                return text[first:first + token.end()]
        elif value == ';' and expression and depth == 0:
            return text[first:first + token.end()]
    raise ValueError('Unterminated declaration ' + declaration)


def compatible(text):
    text = re.sub(r'^\s*///.*\n', '', text, flags=re.M)
    text = re.sub(r'where (\w+) : allows ref struct', '', text)
    text = text.replace('MethodImplOptions.AggressiveOptimization', '(MethodImplOptions)512')
    text = re.sub(r'ArgumentNullException.ThrowIfNull\((\w+)\);', r'if (\1 is null) throw new ArgumentNullException(nameof(\1));', text)
    text = text.replace('ObjectDisposedException.ThrowIf(_disposed, this);', 'if (_disposed) throw new ObjectDisposedException(GetType().Name);')
    text = text.replace('float.IsFinite(value)', '(!float.IsNaN(value) && !float.IsInfinity(value))')
    return text


def write(name, text):
    path = target / name
    value = compatible(text) + '\n'
    if not path.exists() or path.read_text() != value:
        path.write_text(value)


for name in ['Data.cs', 'Hooks.cs', 'InternTable.cs', 'MeasuredLanes.cs', 'BankView.cs', 'Playback.cs']:
    text = read(name)
    if name == 'Data.cs':
        text = text.replace('internal static TimelineAsset LoadAsset(ReadOnlySpan<byte> baked) => Of(Load(baked));', '''internal static TimelineAsset LoadAsset(ReadOnlySpan<byte> baked) => Of(Load(baked));
    public static TimelineAsset LoadOwned(ReadOnlySpan<byte> baked)
    {
        return Of(Load(baked));
    }''')
    if name == 'InternTable.cs':
        text = text.replace('Volatile.Read(ref _graveyard) != 0', 'Volatile.Read(ref _graveyard) != IntPtr.Zero')
        text = text.replace('while (node != 0)', 'while (node != IntPtr.Zero)')
        text = text.replace('Volatile.Write(ref _graveyard, 0)', 'Volatile.Write(ref _graveyard, IntPtr.Zero)')
    write(name, text)

compiled = read('Compiled.cs').replace('public readonly ref struct Frame', 'public readonly unsafe ref struct Frame')
compiled = compiled.replace('private readonly ref readonly TTrack _track;', 'private readonly TTrack* _track;')
compiled = compiled.replace('private readonly ref readonly TClip _clip;', 'private readonly TClip* _clip;')
compiled = compiled.replace('_track = ref track;', '_track = (TTrack*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref global::System.Runtime.CompilerServices.Unsafe.AsRef(in track));')
compiled = compiled.replace('_clip = ref clip;', '_clip = (TClip*)global::System.Runtime.CompilerServices.Unsafe.AsPointer(ref global::System.Runtime.CompilerServices.Unsafe.AsRef(in clip));')
compiled = compiled.replace('=> ref _track;', '=> ref *_track;').replace('=> ref _clip;', '=> ref *_clip;')
write('Compiled.cs', compiled)

lane = read('Lane.cs')
write('LaneEncoding.cs', 'using System.Runtime.CompilerServices;\nnamespace Tl;\n' + section(lane, 'internal static class LaneMovement') + '\n' + section(lane, 'internal static unsafe class LaneEncoding'))
bank = read('TimelineSet.cs')
write('LaneBlockPlan.cs', 'namespace Tl;\n' + section(bank, 'internal sealed unsafe class LaneBlockPlan'))
builder = section(bank, 'SlotView* BakeBlock(').replace('CollectionsMarshal.AsSpan(plan.Segments)', 'plan.Segments.ToArray().AsSpan()').replace('CollectionsMarshal.AsSpan(plan.Dense)', 'plan.Dense.ToArray().AsSpan()')
builder = builder.replace('SlotView* BakeBlock(', 'internal static SlotView* BakeBlock(')
builder = re.sub(r'^\s*_(blockCount|headerTotal|tableTotal)[^\n]*\n', '', builder, flags=re.M)
write('BankBuilder.cs', 'using System.Runtime.InteropServices;\nnamespace Tl;\ninternal static unsafe class BankBuilder { const int BlockHeaderBytes = 72;\n' + builder + '\n}')

pair = read('TimelinePair.cs')
parts = ['internal unsafe struct MemoLane', 'public unsafe ref struct ColumnSet']
write('Columns.cs', 'using System.Diagnostics.CodeAnalysis;\nusing System.Runtime.CompilerServices;\nusing System.Runtime.InteropServices;\nnamespace Tl;\n' + '\n'.join(section(pair, name) for name in parts))
methods = [
    'public static bool TrySample(',
    'public static TClip SampleClip(',
    'public static void Apply<TIndex, TPosition>(ReadOnlySpan<TIndex> indices, ReadOnlySpan<TPosition> positions, bool forward, in ColumnSet caller)',
    'static void ApplyLive(',
]
extra = '\n'.join(section(pair, name) for name in methods)
for declaration in ['static void CheckSizes<TIndex, TPosition, TEffect>', 'static void CheckSizes<TIndex, TPosition>', 'static void ThrowColumnSizes(', 'static void ThrowColumnRowMismatch(', 'static void ThrowMemoRow(', 'static void ThrowLiveFrame(', 'static void* CellOf(']:
    if declaration in pair:
        extra += '\n' + section(pair, declaration)
write('LiveDispatch.cs', 'using System.Runtime.CompilerServices;\nusing System.Runtime.InteropServices;\nnamespace Tl;\npublic static unsafe partial class Timeline<TTrack,TClip> where TTrack:unmanaged,IBlend<TClip> where TClip:unmanaged {\n' + extra + '\n}')
(target / 'source-manifest.json').write_text(json.dumps({'pin': pin, 'sha256': inputs}, indent=2) + '\n')
