# Compose Unity Docker Image

This repository builds Linux and Windows Docker images for installing Unity editors and running Unity project builds and tests through [slothsoft/unity](https://github.com/Faulo/slothsoft-unity).

## Image Contents

Both image variants provide:

- Unity Hub 3.12.1.
- The `compose-unity` command backed by Composer and `slothsoft/unity`.
- Node.js and npm.
- The itch.io Butler client.
- SteamCMD.
- Blender, with its release series selected by the `BLENDER_SERIES` build argument.
- PHP and Composer.
- A .NET SDK.
- Git, curl, and archive utilities.

The Linux image additionally includes DocFX, Mono, Xvfb, XFCE, and a VNC server. The Windows image uses the full Windows container image so Unity packages can use desktop WinRT APIs. Its `OS_BASE` build argument defaults to `1809` and also accepts `20H2`; the available .NET Framework runtime comes from the selected Windows base. Both Windows variants include the supported Visual Studio 2019 Build Tools servicing baseline. Visual Studio 2022 Build Tools are not used because their MSBuild assemblies are incompatible with the .NET Framework build host used by `dotnet format` and DocFX. Both variants also include native launchers for `compose-unity` and Unity Hub, and machine-wide `.blend`/`.fbx` associations that invoke `blender.exe` directly for Unity's Blender-to-FBX import workflow.

## Repository Layout

Build inputs are organized by ownership:

- `common/` contains shared cross-platform launcher and sidecar source.
- `linux/` contains the Linux Dockerfile and machine identity.
- `windows/` contains the Windows Dockerfile, Hub launcher and patch, Chocolatey package, and PHP extension configuration.

Both Docker builds use the repository root as build context. Both images create their Composer project during the build, require `slothsoft/unity`, and configure the process timeout from `UNITY_TIMEOUT`.

## Docker Contexts

The local development setup uses two explicitly named Docker contexts:

- `linux` targets the Linux container host.
- `windows` targets the Windows container host.

Specify the context for direct Docker commands:

```text
docker --context linux info
docker --context windows info
```

The first daemon must report `OSType: linux`; the second must report `OSType: windows`.

## Configuration

Project-specific script configuration lives in `.env`:

```dotenv
DOCKER_IMAGE=compose-unity
DOCKER_TEST_ARGS="--env UNITY_CREDENTIALS_USR --env UNITY_CREDENTIALS_PSW --env EMAIL_CREDENTIALS_USR --env EMAIL_CREDENTIALS_PSW"
DOCKER_TEST_ARGS_LINUX="-v \"unity-binaries:/root/Unity\""
DOCKER_TEST_ARGS_WINDOWS="-v \"unity-binaries:C:/Program Files/Unity/Hub/Editor\""
DOCKER_TEST_ARGS_WINDOWS_GPU="--isolation process --device \"class/5B45201D-F2F2-4F3B-85BB-30FF1F953599\" --env UNITY_NO_GRAPHICS=0"
DOCKER_TEST_CMD="compose-unity exec unity-empty-project test 2021.3.45f1"
```

`DOCKER_IMAGE` names the image. Scripts tag the result as `tmp/compose-unity:latest`.

The test configuration:

- Forwards Unity and email credentials from the host environment.
- Mounts the `unity-binaries` named volume at the platform-specific Unity editor directory.
- Adds process isolation, DirectX device passthrough, and `UNITY_NO_GRAPHICS=0` when the Windows GPU profile is selected.
- Creates an empty project using Unity `2021.3.45f1`.

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

The Windows build script resolves to:

```text
docker --context windows build --tag tmp/compose-unity:latest --file windows/Dockerfile .
```

To build the Windows 20H2 variant explicitly, add `--build-arg OS_BASE=20H2`.

GitHub Actions publishes the Windows variants as `latest-windows-20H2` and
`latest-windows-1809`. The `latest` manifest lists 20H2 before 1809 so newer
Hyper-V-capable hosts prefer 20H2 while 1809 hosts fall back to their compatible
image.

The Linux test script reconstructs:

```text
docker --context linux run --rm --env UNITY_CREDENTIALS_USR --env UNITY_CREDENTIALS_PSW --env EMAIL_CREDENTIALS_USR --env EMAIL_CREDENTIALS_PSW -v "unity-binaries:/root/Unity" tmp/compose-unity:latest compose-unity exec unity-empty-project test 2021.3.45f1
```

The Windows test script reconstructs:

```text
docker --context windows run --rm --env UNITY_CREDENTIALS_USR --env UNITY_CREDENTIALS_PSW --env EMAIL_CREDENTIALS_USR --env EMAIL_CREDENTIALS_PSW -v "unity-binaries:C:/Program Files/Unity/Hub/Editor" tmp/compose-unity:latest compose-unity exec unity-empty-project test 2021.3.45f1
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

The Dockerfiles also declare Unity, Unity Hub, cache, configuration, and licensing directories as volumes. The Linux image includes VNC tooling for interactive licensing setup.

`linux/machine-id` supplies a stable Linux machine identity used by the image. Changes to that file, credential forwarding, editor paths, or licensing volumes can invalidate persisted licensing and should be made deliberately.

## Cross-Platform Command

Both images expose the same `compose-unity` command:

```text
compose-unity exec unity-empty-project test 2021.3.45f1
```

Both images compile the shared C# source in `common/` into native launchers named `compose-unity` and `compose-unity-sidecar`. The launcher invokes Composer without an intermediate shell, preserves attached input, output, and exit status, and registers each call with the sidecar.

## Long-Running Sidecar

With no explicit command, the image starts `compose-unity-sidecar` as PID 1. Start a Linux sidecar with persistent editor and Unity state volumes:

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
docker exec unity compose-unity-sidecar status
docker inspect --format '{{.State.Health.Status}}' unity
```

Logs contain sanitized `READY`, `START`, `END`, `FAILED`, `CANCELLED`, `TIMEOUT`, and `ORPHANED` summaries. Full Unity output remains attached to the originating `docker exec` call. Health checks are offline and remain healthy while builds are active.

An explicit command overrides the default sidecar command, preserving one-off usage:

```text
docker run --rm faulo/compose-unity:latest compose-unity exec unity-build ...
```

The Windows image also compiles a Unity Hub launcher that adapts headless command-line arguments for Windows containers. Its embedded Hub runtime is patched to retry interrupted downloads and launch Editor installers directly instead of waiting for unavailable UAC interaction.

Windows containers do not provide the interactive desktop shell expected by
`FindExecutableW` and `ShellExecuteExW`. The image replaces both 64-bit and
32-bit `SHELL32.dll` with compatibility proxies that preserve all original
exports while resolving registered file associations through `SHLWAPI` and
launching executables through `CreateProcessW`. The original Microsoft DLLs
remain beside the proxies as `shell32real.dll` and handle all other shell APIs.
