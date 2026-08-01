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

The Linux image additionally includes DocFX, Mono, Xvfb, XFCE, and a VNC server. The Windows image uses the full Windows container image so Unity packages can use desktop WinRT APIs. Its `OS_BASE` build argument defaults to `1809` and also accepts `20H2`; the available .NET Framework runtime comes from the selected Windows base. The 20H2 variant includes Visual Studio Build Tools with MSBuild, while the 1809 variant uses the .NET SDK's MSBuild fallback to remain compatible with its older .NET Framework runtime. Both include native launchers for `compose-unity` and Unity Hub, and machine-wide `.blend`/`.fbx` associations that invoke `blender.exe` directly for Unity's Blender-to-FBX import workflow.

## Repository Layout

Build inputs are organized by ownership:

- `linux/` contains the self-contained Linux build context, Dockerfile, launcher, and machine identity.
- `windows/` contains the self-contained Windows build context, Dockerfile, launchers, Hub patch, Chocolatey package, and PHP extension configuration.

Each Docker build uses its platform directory as the build context. Both images create their Composer project during the build, require `slothsoft/unity`, and configure the process timeout from `UNITY_TIMEOUT`.

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
DOCKER_TEST_CMD="compose-unity exec unity-empty-project test 2021.3.45f1"
```

`DOCKER_IMAGE` names the image. Scripts tag the result as `tmp/compose-unity:latest`.

The test configuration:

- Forwards Unity and email credentials from the host environment.
- Mounts the `unity-binaries` named volume at the platform-specific Unity editor directory.
- Creates an empty project using Unity `2021.3.45f1`.

Credential values are forwarded at runtime and are not stored in `.env` or baked into the image.

## Batch Scripts

The OS-specific scripts are intended to be launched from Windows Explorer and pause before closing:

```text
docker-build-linux.bat
docker-build-windows.bat
docker-test-linux.bat
docker-test-windows.bat
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
docker --context linux build --tag tmp/compose-unity:latest --file linux/Dockerfile linux
```

The Windows build script resolves to:

```text
docker --context windows build --tag tmp/compose-unity:latest --file windows/Dockerfile windows
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

Linux installs `linux/compose-unity` as a shell launcher. Windows compiles `windows/compose-unity.cs` into `C:/Windows/compose-unity.exe`, which invokes Composer without requiring `cmd /C`.

The Windows image also compiles a Unity Hub launcher that adapts headless command-line arguments for Windows containers. Its embedded Hub runtime is patched to retry interrupted downloads and launch Editor installers directly instead of waiting for unavailable UAC interaction.

Windows containers do not provide the interactive desktop shell expected by
`FindExecutableW` and `ShellExecuteExW`. The image replaces both 64-bit and
32-bit `SHELL32.dll` with compatibility proxies that preserve all original
exports while resolving registered file associations through `SHLWAPI` and
launching executables through `CreateProcessW`. The original Microsoft DLLs
remain beside the proxies as `shell32real.dll` and handle all other shell APIs.
