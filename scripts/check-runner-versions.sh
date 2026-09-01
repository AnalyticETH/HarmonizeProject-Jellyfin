#!/bin/sh
set -eu

# The unauthenticated GitHub REST API is limited to 60 requests per hour per
# public IP. A daily host check must not fail just because another service
# exhausted that shared bucket, so resolve the latest release through GitHub's
# rate-limit-free /releases/latest redirect instead.
latest_url=$(curl --fail --silent --show-error --location --retry 3 --retry-all-errors \
    --max-time 30 --output /dev/null --write-out '%{url_effective}' \
    https://github.com/actions/runner/releases/latest)
latest_version=$(printf '%s\n' "$latest_url" \
    | sed -n 's#^https://github\.com/actions/runner/releases/tag/v\([0-9][0-9.]*\)$#\1#p')
if [ -z "$latest_version" ]; then
    echo "Could not determine the latest actions/runner release from $latest_url" >&2
    exit 1
fi

status=0
for runner_dir in \
    /var/lib/harmonize-runner/actions-runner \
    /var/lib/harmonize-release-runner/actions-runner \
    /var/lib/harmonize-dependabot-runner/actions-runner \
    /var/lib/harmonize-runtime-runner/actions-runner \
    /var/lib/harmonize-pr-runner/actions-runner
do
    installed_version=$(jq --exit-status --raw-output \
        '.libraries | keys[] | select(startswith("Runner.Listener/")) | split("/")[1]' \
        "$runner_dir/bin/Runner.Listener.deps.json")
    if [ "$installed_version" != "$latest_version" ]; then
        echo "OUTDATED: $runner_dir is $installed_version; upstream is $latest_version" >&2
        status=1
    else
        echo "CURRENT: $runner_dir is $installed_version"
    fi
done

exit "$status"
