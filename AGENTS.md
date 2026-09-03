# AGENTS.md

Shared instructions for coding agents. Project-specific information is kept in [README.md](README.md), read it before non-trivial changes.

## Docker

### Repository and environment

Repository builds Linux and Windows variants of one Docker image. Shared build inputs live in `common/`; platform inputs live in `linux/` and `windows/`. Both builds use repository root as build context.

Always pass `--context linux` or `--context windows` to Docker commands. Never rely on active context. Root `.env` is authoritative for image name, test args, and test command. Use `docker context ls` to discover other daemons available via network.

Only images under disposable `tmp/` namespace may be built, tagged, overwritten, or removed. Treat every other image as read-only.

### Batch entry points

Root batch scripts are interactive Windows Explorer entry points and pause for output visibility. For agent automation, construct Docker commands directly instead of invoking pausing batch files.

### Build and validation

Builds are generally large, network-dependent, and may require matching Windows host. Report skipped targets and concrete reasons. Preserve checksum verification, download validation, native exit-code checks, and explicit error handling.

### Documentation and style

Keep Dockerfile comments focused on non-obvious reasons. Preserve file shell: PowerShell for Windows Dockerfile, POSIX shell for Linux Dockerfile, batch for `.bat`. Keep tool versions and checksums near constrained installation logic. Update `README.md` when image contents, prerequisites, or public commands change.

### Release

When release operations are authorized, the complete release cycle is:

#### Phase 1: Design Contract

1. Review the tests in `.jenkins/Jenkinsfile.groovy` and update them as needed so that they expect the feature to be written.
2. Commit and push the tests.
3. Run this plugin's job in `https://ci.slothsoft.net/job/jenkins/` via MCP and with `DOCKER_NAMESPACE` set to `faulo` and watch its complete console log.
4. If the integration tests pass, repeat from step 1 to make them show the defect.
5. After integration tests fail, move on to the next phase.

#### Phase 2: Implementation

1. Implement the feature using the local test suite to unit test as applicable.
2. Build candidate images (using the `tmp` namespace) on docker context `dende` (Windows) and `garl` (Linux).
3. Run this image's job in `https://ci.slothsoft.net/job/jenkins/` via MCP and with `DOCKER_NAMESPACE` set to `tmp` and watch its complete console log.
4. If the integration tests fail, repeat from step 1 to fix the issue.
5. After integration tests pass, move on to the next phase.

#### Phase 3: Shipping

1. Commit and push the changes, then watch the GitHub CI image build.
2. If GitHub CI fails, fix the issue, then repeat from step 1.
3. After GitHub CI passes, pull the newly-built images (now in the `faulo` namespace) on docker context `dende` and `garl`.
4. Run this plugin's job in `https://ci.slothsoft.net/job/jenkins/` via MCP and with `DOCKER_NAMESPACE` set to `faulo` and watch its complete console log.
5. If any post-push check or final integration test fails, fix the issue and repeat the full cycle from Phase 1.

If the design contract changes at any point, repeat from Phase 1, step 1.

## General

### Meta commands

These short messages have special handling when they appear alone in a user message:

- `ping`: Reply with `pong`.
- `.`: Reply with `.`.
- `?`: Continue the previous response or task after an interruption.
- `ticket <URI>`: Read the linked ticket and all comments through the available integration. Inspect the project, reproduce the current behavior, and run relevant checks as needed. Then explain the request, project context, reproducibility, risks, and a proposed implementation plan. Do not edit files, change remote state, commit, or push until the user approves the approach.
- `can you <x>?` is a question about your knowledge, capabilities or permissions. It is not an instruction to perform `x`.

### Compatibility

Follow semantic versioning. Preserve backward compatibility for public APIs unless the task explicitly permits a breaking change.

### Project conventions

`.editorconfig` is authoritative. Never edit `.editorconfig` unless expressly instructed by the user.

### Git

Git mutations are forbidden by default. Agents may use read-only inspection commands such as `git status`, `git log`, `git diff`, `git show`, `git blame`, and `git branch --list` without additional permission.

An agent may perform Git mutations only after the user explicitly opts in. Permission is limited to the operations and task the user authorized; do not treat prior authorization as standing permission for later mutations.

When Git mutations are authorized:

- The user is responsible for choosing the branch. Verify the current branch and working-tree status before editing and again before creating commits.
- Treat all unknown local changes as user work. Do not overwrite, stage, commit, restore, or otherwise alter them.
- Keep commits small and cohesive.
- Format agent-authored commits according to Conventional Commits 1.0.0: `<type>[optional scope]: <description>`.
- When working from a ticket, include the ticket key and URL in the commit footer.
- Before committing, read the configured Git author name and email. Keep the configured email, append the agent name once, in brackets to the configured author name (e.g. `Daniel Schulz (Codex)`), and pass that identity explicitly with `git commit --author`. Do not modify repository or global Git configuration.
- Do not force-push, amend, rebase, reset, or discard changes unless the user explicitly requests that specific operation.

### CI Infrastructure

Jenkins runs at `https://ci.slothsoft.net/`. Infrastructure reads are always permitted; triggering builds or making any other change requires explicit user consent.

Use these access methods:

- `ssh ci.slothsoft.net <command>` invokes the Jenkins CLI directly; it does not open a host shell. Useful read-only commands include `who-am-i`, `list-jobs`, and `console`.
- `ssh <host>` opens that server's shell.
- `docker --context <host> ...` talks to that server's Docker daemon. Always name the context explicitly.

The three CI servers are:

- `groke`: Ubuntu; runs the Jenkins controller and a Jenkins agent in the `agents_jenkins-agent` container. SSH alias and Docker context: `groke`.
- `garl`: Ubuntu; runs a Jenkins agent in the `agents_jenkins-agent` container, connected to the controller on `groke`. SSH alias and Docker context: `garl`.
- `dende`: Windows Server 2019; runs a Jenkins agent in the `agents_jenkins-agent` container, connected to the controller on `groke`. SSH alias and Docker context: `dende`.
