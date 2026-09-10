#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "$0")" && pwd)"
repository_dir="$(cd "$project_dir/../.." && pwd)"
fixture_dir="$project_dir/Assets/Samples/GeneratedJobs"
output_dir="$fixture_dir/Generated"
reference_list="$(mktemp)"
trap 'rm -f "$reference_list"' EXIT

dotnet build "$repository_dir/src/Tl.Core/Tl.Core.csproj" -c Release -m:1 -p:NuGetAudit=false >/dev/null
dotnet build "$repository_dir/src/Tl.Gen.CSharp/Tl.Gen.CSharp.csproj" -c Release -m:1 -p:NuGetAudit=false >/dev/null

runtime_dir="$(dotnet --list-runtimes | awk '$1 == "Microsoft.NETCore.App" { path=$3; gsub(/[][]/, "", path); found=path "/" $2 } END { print found }')"
find "$runtime_dir" -maxdepth 1 -type f -name '*.dll' -print | sort >"$reference_list"
printf '%s\n' "$repository_dir/src/Tl.Core/bin/Release/net10.0/Tl.Core.dll" >>"$reference_list"

dotnet "$repository_dir/src/Tl.Gen.CSharp/bin/Release/net10.0/Tl.Gen.CSharp.dll" \
  --compile \
  --backend unity-entities \
  --output "$output_dir" \
  --source "$fixture_dir/Operations.cs" \
  --source "$fixture_dir/Catalog.tl" \
  --source "$fixture_dir/UnityContracts.tl" \
  --reference-list "$reference_list"
