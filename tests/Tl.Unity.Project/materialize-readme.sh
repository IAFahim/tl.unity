#!/usr/bin/env bash
set -euo pipefail

[[ "$#" -eq 1 ]] || { printf '%s\n' "usage: materialize-readme.sh <output-directory>" >&2; exit 2; }
project_dir="$(cd "$(dirname "$0")" && pwd)"
repository_dir="$(cd "$project_dir/../.." && pwd)"
output_dir="$1"
source_dir="$output_dir/Source"
generated_dir="$output_dir/Generated"
mkdir -p "$source_dir" "$generated_dir"

python3 - "$repository_dir/src/Tl.Unity/README.md" "$source_dir" <<'PY'
import hashlib
import pathlib
import sys

readme = pathlib.Path(sys.argv[1]).read_text(encoding="utf-8")
output = pathlib.Path(sys.argv[2])
marker = "```csharp\n"
blocks = []
position = 0
while True:
    start = readme.find(marker, position)
    if start < 0:
        break
    start += len(marker)
    end = readme.find("\n```", start)
    if end < 0:
        raise SystemExit("unterminated C# block in Tl.Unity README")
    blocks.append(readme[start:end])
    position = end + 4
if len(blocks) != 3:
    raise SystemExit(f"expected exactly three C# blocks in Tl.Unity README, found {len(blocks)}")
(output / "DomainJobs.cs").write_text(blocks[0] + "\n", encoding="utf-8", newline="\n")
(output / "Combat.tl").write_text(blocks[1] + "\n", encoding="utf-8", newline="\n")
(output / "ReadmeEcs.cs").write_text(blocks[2] + "\n", encoding="utf-8", newline="\n")
with (output.parent / "receipt.txt").open("w", encoding="utf-8", newline="\n") as stream:
    for name, block in (("domain", blocks[0]), ("authoring", blocks[1]), ("ecs", blocks[2])):
        stream.write(f"{name}-sha256\t{hashlib.sha256(block.encode()).hexdigest()}\n")
PY
cat >"$output_dir/Tl.Unity.ReadmeReceipt.asmdef" <<'JSON'
{
  "name": "Tl.Unity.ReadmeReceipt",
  "references": [
    "Tl.Unity",
    "Unity.Burst",
    "Unity.Collections",
    "Unity.Entities",
    "Unity.Mathematics"
  ],
  "allowUnsafeCode": true
}
JSON

cat >"$source_dir/Timeline.Authoring.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <Nullable>enable</Nullable>
    <TlGenBackend>unity-entities</TlGenBackend>
    <TlGenOutput>$generated_dir</TlGenOutput>
    <TlGenAssembly>$repository_dir/src/Tl.Gen.CSharp/bin/Release/net10.0/Tl.Gen.CSharp.dll</TlGenAssembly>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="DomainJobs.cs" />
    <Compile Include="Combat.tl" />
    <ProjectReference Include="$repository_dir/src/Tl.Core/Tl.Core.csproj" />
  </ItemGroup>
  <Import Project="$repository_dir/src/Tl.Gen.CSharp/build/Tl.Gen.CSharp.targets" />
</Project>
XML

dotnet msbuild "$source_dir/Timeline.Authoring.csproj" -restore -t:TlGenExport -p:Configuration=Release -p:NuGetAudit=false
