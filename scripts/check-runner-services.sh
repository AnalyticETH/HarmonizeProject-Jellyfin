#!/bin/sh
set -eu

# Keep this list explicit. A newly provisioned runner must be added here and
# to the self-hosted runner runbook before it is considered production-ready.
status=0

check_property() {
    unit=$1
    property=$2
    expected=$3

    if actual=$(systemctl show "$unit" --property="$property" --value --no-pager 2>/dev/null); then
        :
    else
        actual='<unavailable>'
    fi

    if [ "$actual" != "$expected" ]; then
        printf '%s property=%s expected=%s actual=%s\n' \
            "$unit" "$property" "$expected" "$actual" >&2
        status=1
    fi
}

check_runner_service() {
    unit=$1
    expected_user=$2
    expected_group=$3
    expected_protect_home=$4

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

    # Verify the service identity and confinement as well as its lifecycle.
    # These values mirror the provisioned runner unit files and fail closed if
    # an operator weakens a unit without updating this checked-in contract.
    check_property "$unit" User "$expected_user"
    check_property "$unit" Group "$expected_group"
    check_property "$unit" NoNewPrivileges yes
    check_property "$unit" PrivateTmp yes
    check_property "$unit" PrivateDevices yes
    check_property "$unit" ProtectSystem strict
    check_property "$unit" ProtectHome "$expected_protect_home"
    check_property "$unit" UMask 0077
    check_property "$unit" LimitCORE 0

    printf '%s enabled=%s active=%s user=%s group=%s protect_home=%s\n' \
        "$unit" "$enabled" "$active" "$expected_user" "$expected_group" "$expected_protect_home"
}

check_runner_service \
    actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin.service \
    harmonize-runner harmonize-runner yes
check_runner_service \
    actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-runtime.service \
    harmonize-runtime-runner harmonize-runtime-runner tmpfs
check_runner_service \
    actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-release.service \
    harmonize-release-runner harmonize-release-runner yes
check_runner_service \
    actions.runner.AnalyticETH-HarmonizeProject-Jellyfin.harmonizeproject-jellyfin-dependabot.service \
    harmonize-dependabot-runner harmonize-dependabot-runner tmpfs

if [ "$status" -ne 0 ]; then
    echo "One or more Harmonize self-hosted runner services is not enabled, active, correctly owned, and confined." >&2
    exit "$status"
fi

echo "All four Harmonize self-hosted runner services are enabled, active, correctly owned, and confined."
