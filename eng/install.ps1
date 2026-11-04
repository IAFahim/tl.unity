[CmdletBinding()]
param(
    [string]$Project = (Get-Location).Path,
    [string]$Tag = "",
    [string]$From = ""
)
$ErrorActionPreference = "Stop"

$repo = if ($env:TL_REPO) { $env:TL_REPO } else { "IAFahim/tl.unity" }
function Fail($message) { throw "tl install: $message" }

if (-not (Test-Path (Join-Path $Project "ProjectSettings/ProjectVersion.txt"))) {
    Fail "$Project is not a Unity project (no ProjectSettings/ProjectVersion.txt)"
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("tl-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if ($From) {
        Copy-Item (Join-Path $From "Tl.Runtime.Unity.*.nupkg"), (Join-Path $From "SHA256SUMS") $work
        $Tag = "local"
    } elseif ((Get-Command gh -ErrorAction SilentlyContinue) -and (gh auth status 2>$null; $LASTEXITCODE -eq 0)) {
        if (-not $Tag) { $Tag = (gh release list -R $repo --limit 1 --json tagName -q '.[0].tagName') }
        if (-not $Tag) { Fail "$repo has no releases" }
        gh release download $Tag -R $repo -p 'Tl.Runtime.Unity.*.nupkg' -p SHA256SUMS -D $work
        if ($LASTEXITCODE -ne 0) { Fail "release $Tag download failed" }
    } else {
        $api = if ($Tag) { "https://api.github.com/repos/$repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$repo/releases?per_page=1" }
        try { $release = Invoke-RestMethod -Uri $api -Headers @{ Accept = "application/vnd.github+json" } }
        catch { Fail "no release found at $api (private repository? run gh auth login)" }
        if ($release -is [array]) { $release = $release[0] }
        if (-not $release) { Fail "$repo has no releases" }
        $Tag = $release.tag_name
        $asset = $release.assets | Where-Object { $_.name -like "Tl.Runtime.Unity.*.nupkg" } | Select-Object -First 1
        if (-not $asset) { Fail "release $Tag has no Tl.Runtime.Unity nupkg asset" }
        $base = "https://github.com/$repo/releases/download/$Tag"
        Invoke-WebRequest -Uri "$base/$($asset.name)" -OutFile (Join-Path $work $asset.name)
        Invoke-WebRequest -Uri "$base/SHA256SUMS" -OutFile (Join-Path $work "SHA256SUMS")
    }

    $package = Get-ChildItem $work -Filter "Tl.Runtime.Unity.*.nupkg" | Select-Object -First 1
    $expected = (Get-Content (Join-Path $work "SHA256SUMS") | Where-Object { $_ -match [regex]::Escape($package.Name) + '$' } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
    $actual = (Get-FileHash $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected -or $expected.ToLowerInvariant() -ne $actual) { Fail "checksum mismatch for $($package.Name)" }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $entry = $zip.GetEntry("lib/netstandard2.1/Tl.Core.dll")
        if (-not $entry) { Fail "$($package.Name) has no lib/netstandard2.1/Tl.Core.dll" }
        $target = Join-Path $Project "Assets/Plugins/Tl"
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $target "Tl.Core.dll"), $true)
    } finally { $zip.Dispose() }

    $version = $package.Name -replace '^Tl\.Runtime\.Unity\.', '' -replace '\.nupkg$', ''
    $sha = (Get-FileHash (Join-Path $target "Tl.Core.dll") -Algorithm SHA256).Hash.ToLowerInvariant()
    $receipt = '{{"package":"Tl.Runtime.Unity","version":"{0}","release":"{1}","source":"{2}","sha256":"{3}"}}' -f $version, $Tag, $repo, $sha
    Set-Content -Path (Join-Path $target "tl-install.json") -Value $receipt -NoNewline:$false
    Write-Host "installed Tl.Runtime.Unity $version (release $Tag) into $target"
    Write-Host "reopen or refocus the Unity Editor to compile; reference Tl.Core.dll from asmdefs with overrideReferences."
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
