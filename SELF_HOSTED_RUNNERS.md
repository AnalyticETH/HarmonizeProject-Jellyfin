# Retired self-hosted runner notes

The repository workflows now target the ephemeral GitHub-hosted `ubuntu-24.04`
runner. Pull requests, security scans, package creation, runtime smoke, and
release publication no longer select a repository-owned runner or share a host
Docker socket.

The former self-hosted services and registrations are historical infrastructure
only. Stopping or deregistering those services is an owner-controlled machine
operation and is not performed by repository changes. Do not restore them for
public pull-request execution.

The executable runner policy is enforced by:

- `.github/workflows/pull-request-validation.yml`
- `.github/workflows/dotnet-ci.yml`
- `.github/workflows/security-scan.yml`
- `scripts/validate-workflow-inventory.mjs`
- `scripts/test-workflow-contracts.mjs`

The hosted runner health workflow checks the required Node, Python, jq, Docker,
and pinned .NET SDK toolchain every 15 minutes. A successful local validation is
not remote Actions evidence; after these changes are committed, verify a real
fork pull request and a trusted `main` run before public launch.

Public production remains gated by physical Hue acceptance, upgrade/rollback,
native Windows/macOS parity, private security intake, licensing/source authority,
and the protected repository-visibility policy. See
[RELEASE_READINESS.md](RELEASE_READINESS.md).
