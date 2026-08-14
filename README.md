# Compose Unity Docker Image

This repository builds Linux and Windows Docker images for installing Unity editors and running Unity project builds and tests through [slothsoft/unity](https://github.com/Faulo/slothsoft-unity).

## Image Contents

### Common

Both image variants provide:

- Unity Hub 3.12.1.
- The native `compose-unity` launcher, backed by Composer 2 and
  `slothsoft/unity`.
- A long-running sidecar with health checks and process-tree cleanup.
- An optional ASP.NET Core Streamable HTTP MCP server for inspecting projects,
  running tests, executing editor methods, and building and serving WebGL.
- Node.js and npm.
- The itch.io Butler client.
- SteamCMD.
- Blender 4.5.
- PHP 8.2.
- .NET SDK 9 and DocFX.
- Git and Git LFS.
- Python 3.
- FFmpeg, curl, and archive utilities.

The following build arguments are shared by both variants:

| Argument | Default | Purpose |
| --- | --- | --- |
| `BLENDER_SERIES` | `4.5` | Selects the Blender release series. |
| `DOTNET_VERSION` | `9.0` | Selects the .NET SDK feature version used by the image and native launcher. |
| `NODE_VERSION` | `24` | Selects the installed Node.js major release. |
| `SLOTHSOFT_UNITY_VERSION` | `2.22` | Selects the compatible `slothsoft/unity` release. |
| `UNITY_TIMEOUT` | `14400` | Sets Composer's process timeout in seconds for Unity commands. |

### Linux

The Linux image is based on Debian Bookworm Slim and additionally provides:

- Xvfb and X authentication tools for headless editor execution.
- ICU for the .NET-based Unity Licensing Client.
- OpenSSL 1.1 compatibility for Unity 2021.

Linux-specific build arguments are:

| Argument | Default | Purpose |
| --- | --- | --- |
| `DEBIAN_FRONTEND` | `noninteractive` | Keeps Debian package installation non-interactive during the build. |

### Windows

The Windows image uses the full Windows container base so Unity packages can
access desktop WinRT APIs. It additionally provides:

- Chocolatey.
- PowerShell 7.6.4 as `pwsh`.
- Visual Studio 2019 Build Tools and .NET Framework 4.7.1 reference
  assemblies.
- Visual C++ 2010, 2013, and 2015-2022 runtimes required by Unity's native
  editor libraries.
- Container-safe `.blend` and `.fbx` associations that invoke `blender.exe`
  directly, including a `SHELL32` compatibility proxy for mounted paths.

Windows-specific build arguments are:

| Argument | Default | Purpose |
| --- | --- | --- |
| `OS_BASE` | `1809` | Selects the Windows base; `1809` and `20H2` are supported. |
| `POWERSHELL_VERSION` | `7.6.4` | Selects the installed PowerShell release. |

## Repository Layout

Build inputs are organized by ownership:

- `common/ComposeUnity/` contains the shared cross-platform launcher and
  sidecar source, targeting .NET 9.0 by default.
- `common/ComposeUnity.Tests/` contains daemon-free unit tests and their Unity
  project fixtures.
- `common/ComposeUnity.DaemonTests/` contains opt-in NUnit tests against local
  Linux and Windows Docker daemons and a deterministic worker backend.
- `linux/` contains the Linux Dockerfile and machine identity.
- `windows/` contains the Windows Dockerfile, Hub launcher and patch, Chocolatey package, and PHP extension configuration.

Both Docker builds use the repository root as build context. Both images create their Composer project during the build, require `slothsoft/unity`, and configure the process timeout from `UNITY_TIMEOUT`.

The compose-unity tool stack is installed at `/compose-unity` in the Linux
image and `C:\compose-unity` in the Windows image. Runtime-owned subdirectories,
including the WebGL document root, live beneath these locations.

## Docker Contexts

The local development setup assumes two explicitly named Docker contexts:

- `linux` should point to a Docker host running Linux containers.
- `windows` should point to a Docker host running Windows containers.

## GPU Acceleration

Both images default to `UNITY_NO_GRAPHICS=1` so they remain usable on GPU-less
hosts. Expose a compatible host GPU and set `UNITY_NO_GRAPHICS=0` to allow
Unity to initialize a graphics device:

On Windows:

```yaml
services:
  compose-unity:
    image: faulo/compose-unity:latest
    environment:
      UNITY_NO_GRAPHICS: 0
    isolation: process
    devices:
      - "class/5B45201D-F2F2-4F3B-85BB-30FF1F953599"
```

On Linux:

```yaml
services:
  compose-unity:
    image: faulo/compose-unity:latest
    environment:
      UNITY_NO_GRAPHICS: 0
    gpus: all
```

See the platform documentation for
[Windows container GPU acceleration](https://learn.microsoft.com/en-us/virtualization/windowscontainers/deploy-containers/gpu-acceleration),
[Docker GPU access](https://docs.docker.com/engine/containers/gpu/), and the
[Compose `gpus` attribute](https://docs.docker.com/reference/compose-file/services/#gpus).

## Volumes and Licensing

Use the following named volumes to persist Unity editors, configuration, Hub
state, caches, and licensing data on Linux:

```yaml
volumes:
  - unity-binaries:/root/Unity
  - unity-config:/root/.config/unity3d
  - unity-hub:/root/.config/unityhub
  - unity-cache:/root/.cache/Unity
  - unity-license:/root/.local/share/unity3d
```

The equivalent Windows volume mappings are:

```yaml
volumes:
  - unity-binaries:C:/Program Files/Unity/Hub/Editor
  - unity-config:C:/Users/ContainerAdministrator/AppData/Roaming/Unity
  - unity-hub:C:/Users/ContainerAdministrator/AppData/Roaming/UnityHub
  - unity-cache:C:/Users/ContainerAdministrator/AppData/Local/Unity
  - unity-license:C:/ProgramData/Unity
```

SteamCMD state can be persisted at:

- Linux: `/root/Steam`
- Windows: `C:/steam`

On Windows, the `steamcmd` command seeds Valve's standalone installer into an
empty `C:/steam` volume, then runs and updates SteamCMD there. On both
platforms, reusing the Steam volume preserves updater downloads and login
configuration across containers. The Dockerfiles declare all Unity state paths
shown above and the SteamCMD state directory as volumes.

`linux/machine-id` supplies a stable Linux machine identity used by the image. Changes to that file, credential forwarding, editor paths, or licensing volumes can invalidate persisted licensing and should be made deliberately.

## Cross-Platform Command

Both images expose the same `compose-unity` command, shown here with one of the
pinned test versions:

```text
compose-unity exec unity-empty-project test 2021.3.45f2
```

Both images compile the shared C# source in `common/ComposeUnity/` into one
canonical native launcher named `compose-unity`. The launcher invokes Composer
without an intermediate shell, selects the installed
`/compose-unity/composer.json` or `C:\compose-unity\composer.json` through
Composer's `COMPOSER` environment variable, preserves the caller's working
directory, attached input, output, and exit status, and registers each call
with the sidecar. The image-level `COMPOSE_UNITY` command points to this native
launcher as well.

## Long-Running Sidecar

With no explicit command, the image starts `compose-unity sidecar` as PID 1.
Start a Linux sidecar with persistent editor and Unity state volumes:

```text
docker run -d --name unity \
  -v unity-binaries:/root/Unity \
  -v unity-config:/root/.config/unity3d \
  faulo/compose-unity:latest
```

Start a Windows sidecar with equivalent persistent volumes:

```text
docker run -d --name unity `
  -v "unity-binaries:C:/Program Files/Unity/Hub/Editor" `
  -v "unity-config:C:/Users/ContainerAdministrator/AppData/Roaming/Unity" `
  faulo/compose-unity:latest
```

Mount the project workspace when starting the container, then select it for each call:

```text
docker exec --workdir <workspace> unity compose-unity exec unity-build ...
```

Forward invocation-specific environment variables with `docker exec --env NAME` or set container-wide values with `docker run --env NAME`. Environment values and full argument lists are never written to sidecar state or logs.

Each invocation has a maximum duration controlled by `COMPOSE_UNITY_CALL_TIMEOUT`. The default is `86400` seconds (24 hours). A container-wide value may be overridden per call:

```text
docker exec --env COMPOSE_UNITY_CALL_TIMEOUT=7200 unity compose-unity exec unity-build ...
```

Set the value to `0` to disable the call limit. Invalid and negative values fail before Composer starts. A timed-out call terminates its Composer/Unity process tree and returns exit code `124`; the sidecar remains available and healthy.

Inspect lifecycle events, active calls, and Docker health:

```text
docker logs unity
docker exec unity compose-unity sidecar status
docker inspect --format '{{.State.Health.Status}}' unity
```

Logs contain sanitized `READY`, `START`, `END`, `FAILED`, `CANCELLED`, `TIMEOUT`, and `ORPHANED` summaries. Full Unity output remains attached to the originating `docker exec` call. Health checks are offline and remain healthy while builds are active.

An explicit command overrides the default sidecar command, preserving one-off usage:

```text
docker run --rm faulo/compose-unity:latest compose-unity exec unity-build ...
```

The former `compose-unity-sidecar` executable name remains as a deprecated
compatibility link so existing Compose deployments continue to start. New
deployments and probes should use the explicit `compose-unity sidecar ...`
form. The compatibility link may be removed in a future major release.

## Optional MCP Server

Both images default `COMPOSE_UNITY_MCP` to `0`. Set it to `1` to run the sidecar
supervisor and an official ASP.NET Core Streamable HTTP MCP server together.
The endpoint is fixed at `/mcp` on container port `8080`. An unset value or `0`
keeps the existing sidecar-only behavior. Every other value logs a warning and
leaves MCP disabled; only the exact value `1` enables it.

The MCP sidecar controls persistent Unity worker containers, so it must be able
to access the same local Docker Engine that runs it. Publish the endpoint only
on host loopback. A Linux Compose service can be configured as follows:

```yaml
services:
  unity:
    image: faulo/compose-unity:latest
    environment:
      COMPOSE_UNITY_MCP: "1"
      UNITY_CREDENTIALS_USR:
      UNITY_CREDENTIALS_PSW:
      EMAIL_CREDENTIALS_USR:
      EMAIL_CREDENTIALS_PSW:
    ports:
      - "127.0.0.1:1234:8080"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock
      - unity-binaries:/root/Unity
      - unity-config:/root/.config/unity3d

volumes:
  unity-binaries:
  unity-config:
```

The equivalent Windows container uses the Docker Engine named pipe and Windows
Unity state paths:

```yaml
services:
  unity:
    image: faulo/compose-unity:latest
    environment:
      COMPOSE_UNITY_MCP: "1"
      UNITY_CREDENTIALS_USR:
      UNITY_CREDENTIALS_PSW:
      EMAIL_CREDENTIALS_USR:
      EMAIL_CREDENTIALS_PSW:
    ports:
      - "127.0.0.1:1234:8080"
    volumes:
      - type: npipe
        source: '\\.\pipe\docker_engine'
        target: '\\.\pipe\docker_engine'
      - type: volume
        source: unity-binaries
        target: 'C:\Program Files\Unity\Hub\Editor'
      - type: volume
        source: unity-config
        target: 'C:\Users\ContainerAdministrator\AppData\Roaming\Unity'

volumes:
  unity-binaries:
  unity-config:
```

Configure Codex to connect over HTTP rather than launch a subprocess:

```toml
[mcp_servers.unity]
url = "http://127.0.0.1:1234/mcp"
tool_timeout_sec = 1800
```

The server advertises exactly four tools:

- `get_project_info` validates a Docker-host project path and returns its normalized
  root, company, product and editor versions, major code and rendering settings,
  input handling, and complete package manifest by reading the project's YAML
  and JSON files directly. It never installs or launches Unity. Unity defaults
  that are absent from the serialized project remain explicit as empty override
  maps rather than inferred values. `ProjectSettings/GraphicsSettings.asset`
  is optional; projects without it report the affected rendering settings as
  `Unknown`.
- `run_tests` accepts one or more Unity test modes. A valid JUnit report returns
  `outcome` (`passed` or `failed`), `exitCode`, total/passed/failure/error/skipped
  counts, duration, and up to 100 failure details with complete stack traces.
  `failuresTruncated` reports whether additional failures were omitted. Valid
  reports omit Unity output. If Unity does not produce a trustworthy report,
  the result contains `outcome: "error"`, `exitCode`, and `log`: the complete,
  unfiltered, untruncated stdout/stderr transcript in Docker frame order.
  Stream transitions are marked `[stdout]` and `[stderr]`.
- `execute_method` runs a fully qualified static editor method with
  argument boundaries preserved and asks Unity to quit after it returns.
- `build_and_serve_webgl` installs the selected editor's WebGL Build Support
  module, invokes
  `Slothsoft.UnityExtensions.Editor.Build.WebGL`, copies the successful build
  from the retained worker through the Docker Engine archive API, and returns a
  browser-ready URL on the MCP listener. The project must provide
  `net.slothsoft.unity-extensions`; the tool does not change package state.

Pass `projectRoot` as a path understood by the Docker daemon. Docker Desktop
users can pass a natural Windows host path such as
`C:\Users\name\projects\game` to a Linux sidecar; the sidecar sends it to the
daemon unchanged and uses the daemon-reported bind source after validation.
The path must contain `Assets`, `Packages`, and `ProjectSettings` directories.

One worker is retained for each normalized project, immutable controller image,
and effective worker configuration. Only the three project directories are
bind-mounted writable; the worker's `Library` remains in its container layer so
imports survive later calls and stay specific to that image and container OS.
Workers inherit known
Unity editor, cache, configuration, licensing, and Steam volumes, applicable
resource limits and GPU device configuration, and only these environment
variables when present: `UNITY_NO_GRAPHICS`, `UNITY_ACCELERATOR_ENDPOINT`,
`UNITY_ACCELERATOR_PARAMS`, `UNITY_LOGGING`, `UNITY_EMPTY_MANIFEST`,
`UNITY_CREDENTIALS_USR`, `UNITY_CREDENTIALS_PSW`, `EMAIL_CREDENTIALS_USR`,
`EMAIL_CREDENTIALS_PSW`, and `COMPOSE_UNITY_CALL_TIMEOUT`. The Docker socket or
named pipe is never passed to workers. A canonical fingerprint covers the image
ID, project identity, forwarded environment, inherited state mounts, resource
limits, isolation, and device configuration. A retained worker whose fingerprint
does not match is replaced before use.

Calls use a FIFO lane per project. A daemon-wide project lock also prevents
overlapping Unity operations from duplicate sidecars or different image
revisions. Sidecar shutdown stops accepting MCP calls and gracefully stops any
worker currently executing one, which invokes the existing Unity process-tree
shutdown policy while retaining the worker container and its imported
`Library` for restart.

### WebGL document root

The MCP listener exposes an htdocs-style tree at `/webgl/`. Project directories
use a readable slug derived from Unity's product name and contain builds named
with UTC timestamps such as `2026-08-14_18-42-07Z`. Directory browsing is
enabled, while an `index.html` file takes priority as the directory index. A
build URL therefore has this form:

```text
http://127.0.0.1:1234/webgl/example-game/2026-08-14_18-42-07Z/
```

The sidecar document-root paths are:

- Linux: `/compose-unity/webgl`
- Windows: `C:\compose-unity\webgl`

Without a mount, builds live in the sidecar's writable container layer and
disappear with that container. Mount the directory as tmpfs for explicitly
temporary storage. This Linux Compose fragment keeps WebGL output in memory:

```yaml
services:
  unity:
    tmpfs:
      - /compose-unity/webgl
```

Use a named volume when builds should survive sidecar replacement:

```yaml
services:
  unity:
    volumes:
      - unity-webgl:/compose-unity/webgl

volumes:
  unity-webgl:
```

The equivalent Windows volume target is `C:\compose-unity\webgl`. A bind mount
may be used instead when the host should manage or inspect the files directly.
The sidecar performs no
retention or pruning and serves whatever is already present after startup.
Interrupted transfers can leave visible partial timestamp directories; their
unreturned URLs are never reused.

The server supports Unity's uncompressed, gzip, Brotli, decompression-fallback,
and WebAssembly files with their required MIME types and content encodings. It
also supports streaming, conditional and range requests. Every `/webgl`
response supplies `Cross-Origin-Opener-Policy`,
`Cross-Origin-Embedder-Policy`, and `Cross-Origin-Resource-Policy` so threaded
Unity builds can use `SharedArrayBuffer` on the loopback origin.

MCP startup is included in sidecar readiness, `status`, and Docker health when
enabled. Startup fails if Docker Engine access or self-container inspection is
unavailable. The server accepts loopback `Host` and `Origin` values only; it
does not provide authentication and is not intended for remote publication.

## ComposeUnity Tests

Run the daemon-free test suite with the .NET 9 SDK:

```text
dotnet test common/ComposeUnity.Tests/ComposeUnity.Tests.csproj --configuration Release
```

The suite covers command routing and legacy compatibility, configuration and
sanitization, project probing (including optional graphics settings), Unity
JUnit result parsing, Docker stream framing, state persistence, path handling,
and per-project FIFO behavior.

Real-daemon tests live in a separate project because they build disposable
`tmp/compose-unity-daemon-tests` images and create containers, bind mounts,
execs, and MCP worker state on every available local daemon:

```text
dotnet test common/ComposeUnity.DaemonTests/ComposeUnity.DaemonTests.csproj --configuration Release
```

The fixtures use the explicitly named `linux` and `windows` Docker contexts,
verify the daemon-reported container OS, and skip each platform independently
when its context or daemon is unavailable. Only local named-pipe and Unix-socket
contexts are accepted.

The tests publish the current controller and a deterministic fake
`compose-unity` executable, then assemble a minimal image from the applicable
.NET runtime-deps base. The fake executable occupies the same path workers use
in production and delegates project probing to the real controller, but does
not install Composer, PHP, Unity, or the other production image tools. NUnit
drives Docker and MCP directly and verifies the advertised tools, real project
probing, argument boundaries, Docker output and exit codes, JUnit results,
retained worker reuse, project mounts, and exclusion of the Docker endpoint
from workers. The Windows fixture uses the official .NET 9 Nano Server 1809
image and respects the daemon's default isolation mode, matching fleet behavior.
The GitHub Actions Windows runner configures Hyper-V as the daemon default so
the LTSC 2019 container can run on Windows Server 2022. CI runs the daemon-free
suite plus Linux and Windows daemon fixtures before image builds. Full Unity
invocation remains in the local image test scripts because editor downloads and
licensing make it unsuitable for the lightweight CI harness.

The Windows image also compiles a Unity Hub launcher that adapts headless command-line arguments for Windows containers. Its embedded Hub runtime is patched to retry interrupted downloads and launch Editor installers directly instead of waiting for unavailable UAC interaction.

Windows containers do not provide the interactive desktop shell expected by
`FindExecutableW` and `ShellExecuteExW`. The image replaces both 64-bit and
32-bit `SHELL32.dll` with compatibility proxies that preserve all original
exports while resolving registered file associations through `SHLWAPI` and
launching executables through `CreateProcessW`. The original Microsoft DLLs
remain beside the proxies as `shell32real.dll` and handle all other shell APIs.
During the image build, each original DLL must have a valid Microsoft signature,
match the selected Windows build family and architecture, and expose exactly the
ordinal/name map expected by its proxy. This permits compatible monthly Windows
servicing updates without maintaining hashes for Microsoft binaries while still
rejecting tampered or export-incompatible replacements. The checked-in proxy
binaries remain protected by exact SHA-256 checksums.
