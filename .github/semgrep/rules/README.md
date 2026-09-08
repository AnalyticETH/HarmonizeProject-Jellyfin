# Reviewed Semgrep registry snapshots

These files are reviewed snapshots of the official Semgrep registry rulesets used
by the blocking security workflows:

- `default.yml`: `https://semgrep.dev/c/p/default`
- `javascript.yml`: `https://semgrep.dev/c/p/javascript`
- `python.yml`: `https://semgrep.dev/c/p/python`

They were captured on 2026-08-29 for Semgrep `1.175.0`. The workflows verify
each file with a SHA-256 digest before validating or scanning it. Keeping the
snapshots in the repository makes a run reproducible and prevents a mutable
registry alias or CDN response from changing the security policy mid-run.

When refreshing a ruleset, download the official URL, replace only the matching
snapshot, update the corresponding workflow digest in both blocking workflows,
run `node scripts/validate-semgrep-lock.mjs`, and review the complete hosted
security scan before pushing the intentional update to `main`.
