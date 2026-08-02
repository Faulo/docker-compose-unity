# AGENTS.md

Shared instructions for coding agents. Project-specific information is kept in [README.md](README.md), read it before non-trivial changes.

## Docker

### Repository and environment

Repository builds Linux and Windows variants of one Docker image. Shared build
inputs live in `common/`; platform inputs live in `linux/` and `windows/`. Both
builds use repository root as build context.

Always pass `--context linux` or `--context windows` to Docker commands. Never
rely on active context. Root `.env` is authoritative for image name, test args,
and test command. Use `docker context ls` to discover other daemons available via network.

Only images under disposable `tmp/` namespace may be built, tagged,
overwritten, or removed. Treat every other image as read-only.

### Batch entry points

Root batch scripts are interactive Windows Explorer entry points and pause for
output visibility. For agent automation, construct Docker commands directly
instead of invoking pausing batch files.

### Build and validation

Builds are generally large, network-dependent, and may require matching Windows host.
Report skipped targets and concrete reasons. Preserve checksum verification,
download validation, native exit-code checks, and explicit error handling.

### Documentation and style

Keep Dockerfile comments focused on non-obvious reasons. Preserve file shell:
PowerShell for Windows Dockerfile, POSIX shell for Linux Dockerfile, batch for
`.bat`. Keep tool versions and checksums near constrained installation logic.
Update `README.md` when image contents, prerequisites, or public commands change.

## General

### Meta commands

These short messages have special handling when they appear alone in a user
message:

- `ping`: Reply with `pong`.
- `.`: Reply with `.`.
- `?`: Continue the previous response or task after an interruption.
- `ticket <URI>`: Read the linked ticket and all comments through the available
  integration. Inspect the project, reproduce the current behavior, and run
  relevant checks as needed. Then explain the request, project context,
  reproducibility, risks, and a proposed implementation plan. Do not edit
  files, change remote state, commit, or push until the user approves the
  approach.

### Compatibility

Follow semantic versioning. Preserve backward compatibility for public APIs
unless the task explicitly permits a breaking change.

### Project conventions

`.editorconfig` is authoritative.

### Git

Git mutations are forbidden by default. Agents may use read-only inspection
commands such as `git status`, `git log`, `git diff`, `git show`, `git blame`,
and `git branch --list` without additional permission.

An agent may perform Git mutations only after the user explicitly opts in.
Permission is limited to the operations and task the user authorized; do not
treat prior authorization as standing permission for later mutations.

When Git mutations are authorized:

- The user is responsible for choosing the branch. Verify the current branch
  and working-tree status before editing and again before creating commits.
- Treat all unknown local changes as user work. Do not overwrite, stage,
  commit, restore, or otherwise alter them.
- Keep commits small and cohesive.
- Format agent-authored commits according to Conventional Commits 1.0.0:
  `<type>[optional scope]: <description>`.
- When working from a Jira ticket, include the ticket key and URL in the commit
  footer.
- Before committing, read the configured Git author name and email. Keep the
  configured email, append the agent name once to the configured author name,
  and pass that identity explicitly with `git commit --author`. Do not modify
  repository or global Git configuration.
- Do not force-push, amend, rebase, reset, or discard changes unless the user
  explicitly requests that specific operation.
