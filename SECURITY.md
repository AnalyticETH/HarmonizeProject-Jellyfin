# Security Policy

## Supported versions

The latest `1.5.x` release is supported with security fixes. Older releases should be upgraded before troubleshooting a security report.

## Reporting a vulnerability

Please report suspected vulnerabilities privately through a GitHub Security Advisory for this repository. Do not open a public issue or include credentials, bridge keys, access tokens, or other sensitive data in a report.

Include the affected release, reproduction steps, impact, and any suggested mitigation. Reports are triaged against the plugin code, its NuGet dependencies, and the GitHub Actions workflows.

## Automated coverage

Every push and pull request runs the named self-hosted CI runner with:

- NuGet vulnerability auditing through `dotnet list package --vulnerable --include-transitive`
- Blocking Gitleaks history scanning with a redacted JSON artifact
- Blocking Semgrep static analysis with the explicit `p/default` ruleset and a JSON artifact
- Immutable commit-SHA references for third-party GitHub Actions
