# AGENTS.md

Shared instructions for coding agents working in this repository. Keep project purpose, image contents, and user-facing usage notes in `README.md`.

## Meta Commands

These short messages have special handling when they appear alone in a user message:

- `ping`: Reply with `pong`.
- `.`: Reply with `.`.
- `?`: Continue the previous response or task after an interruption.
- `ticket <URI>`: Treat `<URI>` as a Jira ticket link. Read the ticket and all comments through MCP, inspect the repository, and build or test the image as needed. Then explain the ticket, relevant repository context, reproducibility, and a proposed implementation plan. Do not edit files, change remote state, commit, or push until the user approves the approach.

## Repository and Environment

This repository builds Linux and Windows Docker images for running Unity and its supporting tools.

The host provides a `docker` client in its shell. Do not assume a particular host operating system, shell, or additional host tooling.

Two Docker contexts are available:

- `linux` is the Linux container host.
- `windows` is the Windows container host.

Always pass `--context linux` or `--context windows` explicitly to every Docker command, including targeted builds, runs, inspections, and debugging commands. Never rely on the active or environment-selected Docker context.

The root `.env` is the authoritative project configuration:

- `DOCKER_IMAGE` names the build directory and image. The local image tag is `tmp/{DOCKER_IMAGE}:latest`.
- `DOCKER_TEST_ARGS` supplies shared project-specific `docker run` options.
- `DOCKER_TEST_ARGS_LINUX` and `DOCKER_TEST_ARGS_WINDOWS` supply container-OS-specific `docker run` options such as volume paths.
- `DOCKER_TEST_CMD` supplies the single command used to test the image.

Read these values using facilities available in the current shell. Decode surrounding dotenv quotes and escaped inner quotes before constructing commands. Do not print, commit, or embed the values of credentials forwarded by `DOCKER_TEST_ARGS`.

Keep Linux and Windows behavior aligned where practical. When a change intentionally applies to only one image, state why and validate that target explicitly.

Do not install persistent host dependencies to compensate for missing image dependencies. Dependencies required at build or runtime belong in the appropriate Dockerfile, package manifest, or embedded runtime configuration.

## Build and Validation

Construct Docker commands directly. Use these full build templates from the repository root:

```text
docker --context linux build --tag {IMAGE} --file {DOCKER_IMAGE}/Dockerfile.linux.dockerfile {DOCKER_IMAGE}
docker --context windows build --tag {IMAGE} --file {DOCKER_IMAGE}/Dockerfile.windows.dockerfile {DOCKER_IMAGE}
```

For a targeted build, add Docker options such as `--target`, `--build-arg`, `--no-cache`, or `--progress plain` to the matching command, but retain its explicit context, Dockerfile, tag, and build context.

Before expensive work, verify the intended daemon directly:

```text
docker --context linux info
docker --context windows info
```

The first command must report `linux`; the second must report `windows`.

Run the standard Linux container tests with:

```text
docker --context linux run --rm {DOCKER_TEST_ARGS} {DOCKER_TEST_ARGS_LINUX} {IMAGE} {DOCKER_TEST_CMD}
```

Run the standard Windows container tests with:

```text
docker --context windows run --rm {DOCKER_TEST_ARGS} {DOCKER_TEST_ARGS_WINDOWS} {IMAGE} {DOCKER_TEST_CMD}
```

Replace all `{...}` placeholders before executing a command. For targeted execution, keep the same explicit context and image but replace the container command with the smallest relevant probe. Prefer separate `docker run` invocations over host-specific compound-command quoting. Forward only the environment variables required by that probe.

A normal validation consists of building the image for the affected context and running the relevant container tests. Changes to shared behavior require both Linux and Windows validation when both hosts are available.

Builds are large, network-dependent, and may require a matching Windows container host. For documentation-only or narrowly scoped changes, use proportionate checks. If a full build or test cannot run, report the skipped target and the concrete reason.

Dockerfile `RUN` steps include tool/version smoke checks. Keep failures visible and preserve command exit codes. Do not weaken checksum verification, download validation, or explicit error handling merely to make a build pass.

When changing:

- Base images, package repositories, or pinned tools: confirm that the referenced image, package, archive, and architecture exist.
- Windows installation logic: preserve PowerShell's stop-on-error behavior and check native process exit codes where PowerShell would not do so automatically.
- Linux installation logic: retain cache cleanup in the same layer and use non-interactive package installation.
- `compose-unity` launchers or embedded runtime configuration: validate the command inside both affected image variants.
- Unity Hub patches or launcher code: validate the patched/compiled artifact during the image build and run the relevant container smoke test.

Do not modify `machine-id`, credential forwarding, published volumes, or Unity licensing paths casually; they affect persisted licensing and CI behavior.

## Git

Git mutations are forbidden by default. Agents may use read-only inspection commands such as `git status`, `git log`, `git diff`, `git show`, `git blame`, and `git branch --list` without additional permission.

An agent may perform Git mutations only after the user explicitly opts in. Permission is limited to the operations and task the user authorized; do not treat prior authorization as standing permission for later mutations.

When Git mutations are authorized:

- The user is responsible for choosing the branch. Verify the current branch and working-tree status before making edits and again before creating commits.
- Treat all unknown local changes as user work. Do not overwrite, stage, commit, restore, or otherwise alter them.
- Keep commits small and cohesive.
- Format agent-authored commits according to Conventional Commits 1.0.0: `<type>[optional scope]: <description>`.
- When working from a Jira ticket, include the ticket key and URL in the commit footer.
- Before committing, read the configured Git author name and email. Keep the configured email, append the agent name once to the configured author name, and pass that identity explicitly with `git commit --author`. Do not modify repository or global Git configuration.
- Do not force-push, amend, rebase, reset, or discard changes unless the user explicitly requests that specific operation.

## Documentation and Style

- Keep Dockerfile stages and comments focused on why a non-obvious installation or workaround is necessary.
- Preserve the existing shell for each file: PowerShell in the Windows Dockerfile, POSIX shell in the Linux Dockerfile, and batch syntax in `.bat` files.
- Keep tool versions and checksums near the installation logic they constrain.
- Update `README.md` when image contents, prerequisites, or user-facing commands change.
- Follow `.gitattributes` and the existing line-ending convention of each file.

## Agent Workflow

- Work from the repository root.
- Read `README.md` and the affected Dockerfile or script before making non-trivial changes.
- Inspect both OS implementations before changing shared image behavior.
- Prefer fast local inspection before starting an expensive image build.
- Keep edits within the requested task; do not perform unrelated dependency upgrades or refactoring.
- Use normal patch/edit tools for manual edits. Avoid shell write tricks that make changes hard to review.
- Do not use destructive cleanup commands or revert user work unless explicitly asked for that exact operation.
