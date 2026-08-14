# Compose Unity Docker Image

This repository builds Linux and Windows Docker images for installing Unity editors and running Unity project builds and tests through [slothsoft/unity](https://github.com/Faulo/slothsoft-unity).

## Image Contents

Both image variants provide:

- Unity Hub 3.12.1.
- The `compose-unity` command backed by Composer and `slothsoft/unity`.
- Node.js and npm.
- The itch.io Butler client.
- SteamCMD.
- Blender 4.5.
- PHP 8.2 and Composer 2.
- .NET SDK 9.
- DocFX.
- Git, curl, and archive utilities.

The Windows image additionally provides PowerShell 7.6.4 as `pwsh` and the
Visual C++ 2010, 2013, and 2015-2022 runtimes required by Unity's native editor
libraries.

The Linux image additionally includes the x64 SteamCMD client, Xvfb, and ICU for
the .NET-based Unity Licensing Client. The Windows image uses the full Windows
container image so Unity packages can use desktop WinRT APIs. Its `OS_BASE`
build argument defaults to `1809` and also accepts `20H2`; the available .NET
Framework runtime comes from the selected Windows base. Both Windows variants
include the supported Visual Studio 2019 Build Tools servicing baseline. Visual
Studio 2022 Build Tools are not used because their MSBuild assemblies are
incompatible with the .NET Framework build host used by `dotnet format` and
DocFX. Both variants also include native launchers for `compose-unity` and Unity
Hub, and machine-wide `.blend`/`.fbx` associations that invoke `blender.exe`
directly for Unity's Blender-to-FBX import workflow.

## Repository Layout

Build inputs are organized by ownership:

- `common/ComposeUnity/` contains the shared cross-platform launcher and
  sidecar source, targeting .NET 9.0 by default.
- `common/ComposeUnity.Tests/` contains daemon-free unit tests and their Unity
  project fixtures.
- `common/ComposeUnity.IntegrationTests/` contains the disposable Docker
  end-to-end test for the MCP `project_info` tool.
- `linux/` contains the Linux Dockerfile and machine identity.
- `windows/` contains the Windows Dockerfile, Hub launcher and patch, Chocolatey package, and PHP extension configuration.

Both Docker builds use the repository root as build context. Both images create their Composer project during the build, require `slothsoft/unity`, and configure the process timeout from `UNITY_TIMEOUT`.

Both images default `SLOTHSOFT_UNITY_VERSION` to `2.21` and require the
corresponding compatible Composer release range, `^2.21`. Override the build
argument to select a different compatible minor release.

## Docker Contexts

The local development setup assumes two explicitly named Docker contexts:

- `linux` should point to a docker host running linux containers.
- `windows` should point to a docker host running windows containers.

## Configuration

Project-specific script configuration lives in `.env`:

```dotenv
DOCKER_IMAGE=compose-unity
DOCKER_TEST_ARGS="--env UNITY_CREDENTIALS_USR --env UNITY_CREDENTIALS_PSW --env EMAIL_CREDENTIALS_USR --env EMAIL_CREDENTIALS_PSW"
DOCKER_TEST_ARGS_LINUX="-v \"unity-binaries:/root/Unity\""
DOCKER_TEST_ARGS_WINDOWS="-v \"unity-binaries:C:/Program Files/Unity/Hub/Editor\""
DOCKER_TEST_ARGS_WINDOWS_GPU="--isolation process --device \"class/5B45201D-F2F2-4F3B-85BB-30FF1F953599\" --env UNITY_NO_GRAPHICS=0"
DOCKER_TEST_CMD="compose-unity exec unity-empty-project test"
DOCKER_TEST_VERSIONS="2019.4.41f2 2020.3.49f1 2021.3.45f2 2022.3.62f3 6000.0.81f1"
```

`DOCKER_IMAGE` names the image. Scripts tag the result as `tmp/compose-unity:latest`.

The test configuration:

- Forwards Unity and email credentials from the host environment.
- Mounts the `unity-binaries` named volume at the platform-specific Unity editor directory.
- Adds process isolation, DirectX device passthrough, and `UNITY_NO_GRAPHICS=0` when the Windows GPU profile is selected.
- Creates an empty project with the latest pinned LTS patch from each major
  Unity line starting with 2019: `2019.4.41f2`, `2020.3.49f1`,
  `2021.3.45f2`, `2022.3.62f3`, and `6000.0.81f1`.
- Runs the same version sequence on Linux and Windows and stops at the first
  failed editor installation or project creation.

Credential values are forwarded at runtime and are not stored in `.env` or baked into the image.

## Batch Scripts

The OS-specific scripts are intended to be launched from Windows Explorer and pause before closing:

```text
docker-build-linux.bat
docker-build-windows.bat
docker-test-linux.bat
docker-test-windows.bat
docker-test-windows-gpu.bat
```

They delegate to the shared scripts with `linux` or `windows` as the first argument. The shared scripts can also be called directly:

```bat
docker-build.bat linux
docker-build.bat windows
docker-test.bat linux
docker-test.bat windows
```

Calling `docker-build.bat` or `docker-test.bat` without an argument omits `--context` and derives the container OS from the active Docker daemon.

## Reconstructed Commands

The Linux build script resolves to:

```text
docker --context linux build --tag tmp/compose-unity:latest --file linux/Dockerfile .
```

The Linux image installs Node.js 24, .NET SDK 9.0, and `slothsoft/unity ^2.21`
by default. Override them with the `NODE_VERSION`, `DOTNET_VERSION`, and
`SLOTHSOFT_UNITY_VERSION` build arguments; the Node.js and .NET selections are
also available under the same names inside the resulting image:

```text
docker --context linux build --build-arg NODE_VERSION=24 --build-arg DOTNET_VERSION=9.0 --build-arg SLOTHSOFT_UNITY_VERSION=2.21 --tag tmp/compose-unity:latest --file linux/Dockerfile .
```

The Windows build script resolves to:

```text
docker --context windows build --tag tmp/compose-unity:latest --file windows/Dockerfile .
```

The Windows image installs .NET SDK 9.0 and `slothsoft/unity ^2.21` by default.
Override them with the `DOTNET_VERSION` and `SLOTHSOFT_UNITY_VERSION` build
arguments; the .NET selection is also available under the same name inside the
resulting image:

```text
docker --context windows build --build-arg DOTNET_VERSION=9.0 --build-arg SLOTHSOFT_UNITY_VERSION=2.21 --tag tmp/compose-unity:latest --file windows/Dockerfile .
```

To build the Windows 20H2 variant explicitly, add `--build-arg OS_BASE=20H2`.

GitHub Actions publishes the Windows variants as `latest-windows-20H2` and
`latest-windows-1809`. The `latest` manifest lists 20H2 before 1809 so newer
Hyper-V-capable hosts prefer 20H2 while 1809 hosts fall back to their compatible
image.

For each version in `DOCKER_TEST_VERSIONS`, the Linux test script reconstructs:

```text
docker --context linux run --rm --env UNITY_CREDENTIALS_USR --env UNITY_CREDENTIALS_PSW --env EMAIL_CREDENTIALS_USR --env EMAIL_CREDENTIALS_PSW -v "unity-binaries:/root/Unity" tmp/compose-unity:latest compose-unity exec unity-empty-project test VERSION
```

For each version in `DOCKER_TEST_VERSIONS`, the Windows test script reconstructs:

```text
docker --context windows run --rm --env UNITY_CREDENTIALS_USR --env UNITY_CREDENTIALS_PSW --env EMAIL_CREDENTIALS_USR --env EMAIL_CREDENTIALS_PSW -v "unity-binaries:C:/Program Files/Unity/Hub/Editor" tmp/compose-unity:latest compose-unity exec unity-empty-project test VERSION
```

The batch scripts load the quoted values from `.env`, remove the surrounding quotes, and decode `\"` before passing the arguments to Docker.

`docker-test-windows-gpu.bat` adds the Windows GPU profile from
`DOCKER_TEST_ARGS_WINDOWS_GPU`. The `windows` context must target a host that
can run the selected Windows image with process isolation and GPU passthrough.

## Windows GPU Acceleration

The Windows image defaults to `UNITY_NO_GRAPHICS=1` so it remains usable on
GPU-less and Hyper-V-isolated hosts. Opt in to DirectX GPU acceleration by
using process isolation, exposing the DirectX device interface class, and
setting `UNITY_NO_GRAPHICS=0`:

```text
docker --context windows run --rm --isolation process `
  --device "class/5B45201D-F2F2-4F3B-85BB-30FF1F953599" `
  --env UNITY_NO_GRAPHICS=0 `
  tmp/compose-unity:latest `
  compose-unity exec unity-empty-project test 2021.3.45f1
```

Equivalent Compose service configuration:

```yaml
services:
  compose-unity:
    image: faulo/compose-unity:latest
    isolation: process
    environment:
      UNITY_NO_GRAPHICS: 0
    devices:
      - "class/5B45201D-F2F2-4F3B-85BB-30FF1F953599"
```

The host and image must meet Microsoft's
[Windows container GPU prerequisites](https://learn.microsoft.com/en-us/virtualization/windowscontainers/deploy-containers/gpu-acceleration),
including compatible Windows versions, Docker 19.03 or newer, and a WDDM 2.5
or newer display driver. GPU acceleration is unavailable with Hyper-V
isolation. Process isolation also requires a host compatible with the selected
`1809` or `20H2` image variant.

A successful Unity launch logs `GfxDevice: creating device client` followed by
the Direct3D version, hardware renderer, VRAM, and driver. Unity Editor system
requirements still apply independently; newer Editor releases may not support
the Windows versions used by these image variants.

## Volumes and Licensing

The named `unity-binaries` volume persists downloaded Unity editors:

- Linux: `/root/Unity`
- Windows: `C:/Program Files/Unity/Hub/Editor`

SteamCMD state can be persisted at:

- Linux: `/root/Steam`
- Windows: `C:/steam`

On Windows, the `steamcmd` command seeds Valve's standalone installer into an
empty `C:/steam` volume, then runs and updates SteamCMD there. On both
platforms, reusing the Steam volume preserves updater downloads and login
configuration across containers. The Dockerfiles also declare Unity, Unity
Hub, cache, configuration, licensing, and SteamCMD state directories as
volumes.

`linux/machine-id` supplies a stable Linux machine identity used by the image. Changes to that file, credential forwarding, editor paths, or licensing volumes can invalidate persisted licensing and should be made deliberately.

## Cross-Platform Command

Both images expose the same `compose-unity` command, shown here with one of the
pinned test versions:

```text
compose-unity exec unity-empty-project test 2021.3.45f2
```

Both images compile the shared C# source in `common/ComposeUnity/` into one
canonical native launcher named `compose-unity`. The launcher invokes Composer
without an intermediate shell, preserves attached input, output, and exit
status, and registers each call with the sidecar.

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

Set `COMPOSE_UNITY_MCP=1` to run the sidecar supervisor and an official ASP.NET
Core Streamable HTTP MCP server together. The endpoint is fixed at `/mcp` on
container port `8080`. An unset value or `0` keeps the existing sidecar-only
behavior; every other value is a startup error.

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

The server advertises exactly three tools:

- `project_info` validates a Docker-host project path and returns its normalized
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

Pass `projectRoot` as a path understood by the Docker daemon. Docker Desktop
users can pass a natural Windows host path such as
`C:\Users\name\projects\game` to a Linux sidecar; the sidecar sends it to the
daemon unchanged and uses the daemon-reported bind source after validation.
The path must contain `Assets`, `Packages`, and `ProjectSettings` directories.

One worker is retained for each normalized project and immutable controller
image. Only the three project directories are bind-mounted writable; the
worker's `Library` remains in its container layer so imports survive later
calls and stay specific to that image and container OS. Workers inherit known
Unity editor, cache, configuration, licensing, and Steam volumes, applicable
resource limits and GPU device configuration, and only these environment
variables when present: `UNITY_NO_GRAPHICS`, `UNITY_ACCELERATOR_ENDPOINT`,
`UNITY_ACCELERATOR_PARAMS`, `UNITY_LOGGING`, `UNITY_EMPTY_MANIFEST`,
`UNITY_CREDENTIALS_USR`, `UNITY_CREDENTIALS_PSW`, `EMAIL_CREDENTIALS_USR`,
`EMAIL_CREDENTIALS_PSW`, and `COMPOSE_UNITY_CALL_TIMEOUT`. The Docker socket or
named pipe is never passed to workers.

Calls use a FIFO lane per project. A daemon-wide project lock also prevents
overlapping Unity operations from duplicate sidecars or different image
revisions. Sidecar shutdown stops accepting MCP calls and gracefully stops any
worker currently executing one, which invokes the existing Unity process-tree
shutdown policy while retaining the worker container and its imported
`Library` for restart.

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
in production, but does not install Composer, PHP, Unity, or the other production
image tools. The suite verifies project probing, MCP calls, argument boundaries,
Docker output and exit codes, JUnit results, retained worker reuse, project
mounts, and exclusion of the Docker endpoint from workers. The Windows fixture
uses the official .NET 9 Nano Server 1809 image for LTSC 2019 hosts.

The MCP `project_info` path has a separate end-to-end test. It builds a small
disposable image under the permitted `tmp/` namespace, starts a sidecar against
the explicitly selected local Docker context, verifies the advertised tool set,
and calls `project_info` against the checked-in project fixture:

```powershell
./common/ComposeUnity.IntegrationTests/project-info.ps1 -Context linux
./common/ComposeUnity.IntegrationTests/project-info.ps1 -Context windows `
    -Image tmp/compose-unity:latest -SkipBuild
```

The lightweight integration Dockerfile is Linux-only. The Windows command
reuses a previously built production image so it also validates the real
Windows launcher and image configuration. GitHub Actions runs the daemon-free
suite and Linux end-to-end test before image builds. Full Unity invocation
remains in the local image test scripts because editor downloads and licensing
make it unsuitable for the lightweight CI harness.

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
