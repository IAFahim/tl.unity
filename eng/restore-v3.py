import hashlib, os, pathlib, shutil, subprocess, time

root=pathlib.Path(__file__).resolve().parent.parent
cache=root/'artifacts/nuget-cache'
cache.mkdir(parents=True,exist_ok=True)
backup=root/'artifacts/replaced-packages'
backup.mkdir(parents=True,exist_ok=True)
def digest(path): return hashlib.sha256(path.read_bytes()).digest()
for name,assembly,build in [('Tl.Runtime.Unity','Tl.Core','src/Tl.Runtime.Unity'),('Tl.Sample.Game.Domain','Game.Domain','samples/Game.Domain')]:
    package=f'{name}.3.0.1-preview.1'
    cached=cache/(package+'.nupkg')
    packed=root/'artifacts/packages'/(package+'.nupkg')
    if cached.exists() and digest(cached)!=digest(packed): shutil.move(str(cached),str(backup/(cached.name+'.'+str(time.time_ns()))))
    installed=root/'tests/Tl.V3.Project/Assets/Packages'/package
    dll=installed/f'lib/netstandard2.1/{assembly}.dll'
    built=root/build/f'bin/Release/netstandard2.1/{assembly}.dll'
    if installed.exists() and (not dll.exists() or digest(dll)!=digest(built)):
        shutil.move(str(installed),str(backup/(package+'.'+str(time.time_ns()))))
subprocess.run(['dotnet','nugetforunity','restore',str(root/'tests/Tl.V3.Project')],check=True,env={**os.environ,'NuGetCachePath':str(cache)})
for name,assembly,build in [('Tl.Runtime.Unity','Tl.Core','src/Tl.Runtime.Unity'),('Tl.Sample.Game.Domain','Game.Domain','samples/Game.Domain')]:
    installed=root/f'tests/Tl.V3.Project/Assets/Packages/{name}.3.0.1-preview.1/lib/netstandard2.1/{assembly}.dll'
    assert digest(installed)==digest(root/build/f'bin/Release/netstandard2.1/{assembly}.dll'),f'NuGetForUnity restored stale {name}'
