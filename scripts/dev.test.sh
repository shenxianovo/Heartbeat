#!/usr/bin/env bash

set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf "${test_root}"' EXIT

mkdir -p "${test_root}/workspace/scripts" "${test_root}/bin"
cp "${repository_root}/scripts/dev.sh" "${test_root}/workspace/scripts/dev.sh"
: > "${test_root}/calls"

cat > "${test_root}/bin/docker" <<'EOF'
#!/usr/bin/env bash
printf 'docker' >> "${DEV_TEST_CALLS}"
printf ' <%s>' "$@" >> "${DEV_TEST_CALLS}"
printf '\n' >> "${DEV_TEST_CALLS}"
EOF

cat > "${test_root}/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
printf 'dotnet' >> "${DEV_TEST_CALLS}"
printf ' <%s>' "$@" >> "${DEV_TEST_CALLS}"
printf ' <target=%s> <display=%s>' "${HEARTBEAT_COLLECTOR_TARGET:-}" "${HEARTBEAT_COLLECTOR_DISPLAY_NAME:-}" >> "${DEV_TEST_CALLS}"
printf '\n' >> "${DEV_TEST_CALLS}"
EOF

cat > "${test_root}/bin/uname" <<'EOF'
#!/usr/bin/env bash
printf 'Darwin\n'
EOF

cat > "${test_root}/bin/curl" <<'EOF'
#!/usr/bin/env bash
case "${*: -1}" in
  */hub/v1/status) printf '401' ;;
  *) printf '200' ;;
esac
EOF

chmod +x "${test_root}/bin/docker" "${test_root}/bin/dotnet" \
  "${test_root}/bin/uname" "${test_root}/bin/curl"

run_dev() {
  PATH="${test_root}/bin:${PATH}" \
    DEV_TEST_CALLS="${test_root}/calls" \
    "${test_root}/workspace/scripts/dev.sh" "$@"
}

assert_calls() {
  local expected="$1"
  local actual
  actual="$(cat "${test_root}/calls")"
  if [[ "${actual}" != *"${expected}"* ]]; then
    printf 'Expected command fragment:\n%s\nActual calls:\n%s\n' "${expected}" "${actual}" >&2
    exit 1
  fi
}

clear_calls() {
  : > "${test_root}/calls"
}

run_dev up web
assert_calls '<up> <--build> <--detach> <db> <migrate> <api> <web>'
if grep -q '<hub>' "${test_root}/calls"; then
  printf 'Explicit web selection unexpectedly included hub.\n' >&2
  exit 1
fi
clear_calls

run_dev status db
assert_calls '<ps> <--all> <db>'
clear_calls

run_dev down hub
assert_calls '<rm> <--stop> <--force> <hub>'
clear_calls

if run_dev up hub >"${test_root}/stdout" 2>"${test_root}/stderr"; then
  printf 'Hub unexpectedly started without configuration.\n' >&2
  exit 1
fi
grep -q 'Run ./scripts/setup.sh first' "${test_root}/stderr"
if [[ -s "${test_root}/calls" ]]; then
  printf 'Hub preflight failure unexpectedly invoked Docker.\n' >&2
  exit 1
fi

cat > "${test_root}/configured.env" <<'EOF'
HEARTBEAT_API_KEY='api-key'
HEARTBEAT_OWNER_ID='019fc545-8f8b-7c32-a5ce-9850af3f62b1'
HEARTBEAT_HUB_TOKEN='a-separate-local-token-at-least-32-characters'
HEARTBEAT_COLLECTOR_TARGET='Mac\'Book'
HEARTBEAT_COLLECTOR_DISPLAY_NAME="Development\tMac"
EOF

run_dev up --release --env-file "${test_root}/configured.env" hub
assert_calls '<compose> <--project-directory> <'
assert_calls '<up> <--build> <--detach> <hub>'
if grep -q 'compose.dev.yaml' "${test_root}/calls"; then
  printf 'Release selection unexpectedly loaded compose.dev.yaml.\n' >&2
  exit 1
fi
clear_calls

run_dev up --env-file "${test_root}/configured.env" desktop
assert_calls '<up> <--build> <--detach> <hub>'
assert_calls 'dotnet <watch> <--project>'
assert_calls "<target=Mac'Book>"
assert_calls $'<display=Development\tMac>'
clear_calls

if run_dev up unknown >"${test_root}/stdout" 2>"${test_root}/stderr"; then
  printf 'Unknown service unexpectedly succeeded.\n' >&2
  exit 1
fi
grep -q 'Unknown option or service: unknown' "${test_root}/stderr"

printf 'dev.sh selection tests passed.\n'
