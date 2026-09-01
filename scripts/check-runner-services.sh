#!/bin/sh
set -eu

# Keep this list explicit. A newly provisioned runner must be added here and
# to the self-hosted runner runbook before it is considered production-ready.
status=0
for unit in \
    actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service \
    actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-runtime.service \
    actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service \
    actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-dependabot.service
do
    if systemctl is-enabled --quiet "$unit"; then
        enabled=enabled
    else
        enabled=disabled
        status=1
    fi

    if systemctl is-active --quiet "$unit"; then
        active=active
    else
        active=inactive
        status=1
    fi

    printf '%s enabled=%s active=%s\n' "$unit" "$enabled" "$active"
done

if [ "$status" -ne 0 ]; then
    echo "One or more Harmonize self-hosted runner services is not enabled and active." >&2
    exit "$status"
fi

echo "All four Harmonize self-hosted runner services are enabled and active."
