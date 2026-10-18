# Compose Unity for Linux and Windows containers

[![Docker](https://github.com/Faulo/docker-compose-unity/actions/workflows/docker.yml/badge.svg?branch=main)](https://github.com/Faulo/docker-compose-unity/actions/workflows/docker.yml) [![Docker image updated](https://img.shields.io/docker/last-updated/faulo/compose-unity/latest)](https://hub.docker.com/r/faulo/compose-unity/tags?name=latest) [![License: MIT](https://img.shields.io/github/license/Faulo/docker-compose-unity)](LICENSE) [![Status: retired](https://img.shields.io/badge/status-retired-inactive)](#migration)

**This project is retired.** Use
[`faulo/unity`](https://github.com/Faulo/docker-unity) for Unity editor
installation, builds, tests, and the Unity MCP server. Use
[`faulo/ci-tools`](https://github.com/Faulo/docker-ci-tools) for Steam and itch.io
publishing tools.

The legacy [`faulo/compose-unity`](https://hub.docker.com/r/faulo/compose-unity)
image combines Unity tooling and distribution utilities in one Linux or Windows
container. This repository remains a reference for existing consumers.

## Migration

Choose the replacement for the work your container performs:

| Workload | Replacement | Public command |
| --- | --- | --- |
| Unity editor installation, project builds, and tests | `faulo/unity:latest` | `unity` |
| Unity sidecar and MCP server | `faulo/unity:latest` | `unity-sidecar` |
| Steam and itch.io publishing | `faulo/ci-tools:latest` | `steamcmd`, `butler`, `steam-buildfile`, `steam-login` |

The replacements separate Unity execution from build distribution. Pipelines
that perform both tasks should use the appropriate image for each step.

Review the replacement project's documentation before changing an existing
deployment. Commands, environment variables, and persistent state need to be
mapped explicitly; changing only the image name is insufficient.

For example, the legacy command:

```text
compose-unity exec unity-empty-project test 2021.3.45f2
```

becomes this command inside the Unity image:

```text
unity empty-project test 2021.3.45f2
```

The Unity image starts `unity-sidecar` by default and enables its MCP server
with `UNITY_MCP=1`. CI tools provide direct publishing commands without a Unity
editor or licensing setup. See the
[Unity documentation](https://github.com/Faulo/docker-unity#readme) and
[CI tools documentation](https://github.com/Faulo/docker-ci-tools#readme) for
credentials, volumes, and complete usage examples.

## Legacy images

| Tag | Platform |
| --- | --- |
| `latest` | Linux amd64 and Windows amd64 |
| `latest-linux`, `latest-linux-amd64` | Linux amd64 |
| `latest-windows`, `latest-windows-ltsc2019` | Full Windows LTSC 2019 amd64 |

Docker selects the platform matching the container engine when you use
`latest`. Windows containers require a compatible Windows host. For an
existing deployment that needs an exact legacy image, pin its digest.

Retirement applies to the product even if a legacy image is rebuilt or its
last-updated badge changes. New deployments should use the replacements above.

## Legacy tests and source

The repository retains the shared launcher and sidecar under
[`common/ComposeUnity`](common/ComposeUnity), platform Dockerfiles under
[`linux`](linux) and [`windows`](windows), and automated tests.

Run the unit suite from the repository root with the .NET 9 SDK:

```text
dotnet test common/ComposeUnity.Tests/ComposeUnity.Tests.csproj --configuration Release
```

The suite covers command routing, configuration, project inspection, Unity
test-report parsing, stream handling, and state management. Separate daemon and
image integration suites exercise Docker workers, mounts, command execution,
and installed tools.

## Support

Report migration issues in the relevant replacement project's issue tracker:
[Unity](https://github.com/Faulo/docker-unity/issues) or
[CI tools](https://github.com/Faulo/docker-ci-tools/issues).

## License

The original code, Dockerfiles, scripts, and documentation are licensed under
the [MIT License](LICENSE). Unity, Steam, and other bundled software retain
their respective licenses and account requirements.
