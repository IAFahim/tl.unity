# Unity CI runner

The Unity jobs (`com.tl.unity EditMode + PlayMode` and `Unity Mono + EditMode + PlayMode`) run on one self-hosted runner. Without it they stay `queued`.

| | |
|---|---|
| Runner name | `tl-unity-licensed-linux` |
| Labels | `self-hosted`, `Linux`, `X64`, `unity` |
| Host | the owner's Linux desktop (hostname `os`, user `os`), the one with the licensed Unity 7000.0.0a16 under `~/Unity/Hub/Editor` |
| Runner folder | `~/actions-runner-tl-unity` |
| Service | `actions.runner.IAFahim-tl.unity.tl-unity-licensed-linux.service` (systemd, runs as `os`, starts on boot) |
| Environment | `~/actions-runner-tl-unity/.env` and `.path` (`DOTNET_ROOT=$HOME/.dotnet` and `~/.dotnet` first on `PATH`: the mise `dotnet` wrapper finds no SDK and the v3 gate fails with "No .NET SDKs were found") |

## Is it online

```
gh api repos/IAFahim/tl.unity/actions/runners --jq '.runners[]|{name,status,busy}'
```

## Restart it

```
sudo systemctl restart actions.runner.IAFahim-tl.unity.tl-unity-licensed-linux.service
journalctl -u actions.runner.IAFahim-tl.unity.tl-unity-licensed-linux.service -n 50 --no-pager
```

The runner log of the last error is `~/actions-runner-tl-unity/_diag/Runner_*.log`. If the Unity job fails at the licence, open the Editor once on that machine and sign in.

## Install it again (new machine or lost registration)

```
mkdir ~/actions-runner-tl-unity && cd ~/actions-runner-tl-unity
curl -fsSL -o r.tgz https://github.com/actions/runner/releases/download/v2.338.2/actions-runner-linux-x64-2.338.2.tar.gz && tar xzf r.tgz && rm r.tgz
./config.sh --unattended --url https://github.com/IAFahim/tl.unity --token "$(gh api -X POST repos/IAFahim/tl.unity/actions/runners/registration-token --jq .token)" --name tl-unity-licensed-linux --labels unity --replace
printf 'DOTNET_ROOT=%s/.dotnet\nUNITY_NO_AUTH_BROKER=1\n' "$HOME" >> .env
sudo ./svc.sh install "$USER" && sudo ./svc.sh start
```

Add `$HOME/.dotnet` to the front of the line in `.path` before starting the service.

Fork pull requests do not run here: the workflows guard the Unity jobs by repository, and the repo setting `fork-pr-contributor-approval` is `all_external_contributors`.
