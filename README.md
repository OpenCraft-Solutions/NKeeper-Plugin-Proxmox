# NKeeper Proxmox VE plugin

The official `proxmox` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: Proxmox VE nodes and their clusters' virtual machines, ported from 1.x's Proxmox support. It lives in a repository of its own and is released on its own (NKeeper's `docs/architecture.md` §9). Its design is §7.38 there.

## What it does

It contributes the `proxmox` service type (a node's SSH, port 22) and a **Virtual machines** tab for hosts that have it. The tab is virt-core's guests tab (§7.36), which this plugin depends on:
- every QEMU virtual machine of the node's cluster (not templates), with its state and the node it runs on;
- their power actions: start, shut down, reboot, pause, resume, hibernate, force off and reset, through `pvesh`, on whichever node the VM runs;
- a VM's VNC console in the same tab, through `qm vncproxy`, as Proxmox's own web console reaches it. A VM on another node is reached over the cluster's own SSH between nodes.

It reaches the node as 1.x did: over SSH, which core opens and signs in to with the service's credential, so the plugin never sees a password. `pvesh` and `qm` need root: sign in as root, or as a user who may use `sudo` without a password. It needs SDK 1.12 (`>=1.12.0 <2.0.0`), for a streaming command's input.

**Not yet:** containers (LXC), SPICE consoles, and linking a guest to its host in NKeeper's inventory.

The plugin's ID is `proxmox`, and it is on by default: it is signed with NKeeper's official key, and installed from NKeeper's plugin catalog.

## Layout

```text
backend/        the provider (ProxmoxProvider.cs), the plugin, nkeeper-plugin.json (the manifest), the project
frontend/       the session view: virt-core's guests tab, told it's Proxmox VE
tests/          the provider's tests, on a stand-in for core's SSH connection and Proxmox's output
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK and virt-core's package, never core's source: `NKeeper.Abstractions`, `NKeeper.Plugin.Build` and `NKeeper.Plugins.Virtualization` (NuGet), and `@opencraft-solutions/nkeeper-sdk` (npm, imported as `@nkeeper/sdk`), all from GitHub Packages.

## Building

You need the .NET 10 SDK (see `global.json`) and Node.js 24 (see `.nvmrc`). GitHub Packages needs a token even for public packages; give the build one that can read packages, in the environment, never in the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
(cd frontend && npm ci && npm run lint && npm run typecheck)
dotnet build NKeeper.Plugins.Proxmox.slnx --configuration Release
dotnet test NKeeper.Plugins.Proxmox.slnx --configuration Release
```

The build packs `proxmox-<version>.nkp` into `artifacts/plugins/`, signed with a development key it creates under `.dev/keys/`. Load it in a development NKeeper beside virt-core's package.

## Releasing

Raise `version` in `backend/nkeeper-plugin.json` (and `frontend/package.json`), merge to `main`, and push a tag `v<version>`. The release workflow builds and tests, then, in the protected `release` environment that needs an owner's approval, packs the package signed with the official key and publishes a GitHub release with it and its SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
