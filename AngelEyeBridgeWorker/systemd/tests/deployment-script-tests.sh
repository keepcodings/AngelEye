#!/usr/bin/env bash
set -euo pipefail

SOURCE_ROOT="${1:-/src}"
DEPLOY_SCRIPT="${SOURCE_ROOT}/AngelEyeBridgeWorker/systemd/angel-eye-worker-deploy"
TEST_ROOT="$(mktemp -d /tmp/angel-deploy-test-XXXXXX)"
trap 'rm -rf "$TEST_ROOT"' EXIT
touch "${TEST_ROOT}/.angel-deploy-test-root"

export ANGEL_DEPLOY_TEST_MODE=1
export ANGEL_DEPLOY_TEST_ROOT="$TEST_ROOT"
export ANGEL_DEPLOY_HEALTH_ATTEMPTS=2
export ANGEL_DEPLOY_HEALTH_DELAY_SECONDS=0
export DEPLOY_STATE_ROOT="${TEST_ROOT}/fake-systemd"

BIN="${TEST_ROOT}/bin"
mkdir -p "$BIN" "$DEPLOY_STATE_ROOT"
export PATH="${BIN}:${PATH}"

cat > "${BIN}/systemctl" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
state="${DEPLOY_STATE_ROOT}"
mkdir -p "$state"
action="${1:-}"
case "$action" in
  is-active)
    [[ "$(<"${state}/active")" == "active" ]]
    ;;
  is-enabled)
    [[ "$(<"${state}/enabled")" == "enabled" ]]
    ;;
  stop)
    echo inactive > "${state}/active"
    echo $(( $(<"${state}/stop-count") + 1 )) > "${state}/stop-count"
    ;;
  start|restart)
    echo active > "${state}/active"
    echo $(( $(<"${state}/restart-count") + 1 )) > "${state}/restart-count"
    ;;
  enable)
    echo enabled > "${state}/enabled"
    ;;
  disable)
    echo disabled > "${state}/enabled"
    if [[ "${2:-}" == "--now" ]]; then
      echo inactive > "${state}/active"
    fi
    ;;
  *)
    echo "unexpected systemctl action: $*" >&2
    exit 2
    ;;
esac
SH
chmod +x "${BIN}/systemctl"

cat > "${BIN}/curl" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
root="${ANGEL_DEPLOY_TEST_ROOT}"
current="${root}/opt/angel-eye-bridge/current/.release-manifest.json"
version=""
if [[ -f "$current" ]]; then
  version="$(python3 - "$current" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as stream:
    print(json.load(stream)["version"])
PY
)"
fi
fail_version=""
[[ -f "${root}/health-fail-version" ]] && fail_version="$(<"${root}/health-fail-version")"
if [[ -n "$fail_version" && "$version" == "$fail_version" ]]; then
  exit 22
fi
printf '{"status":"ok"}\n'
SH
chmod +x "${BIN}/curl"

mkdir -p \
  "${TEST_ROOT}/etc/angel-eye-bridge" \
  "${TEST_ROOT}/etc/angel-eye-deploy" \
  "${TEST_ROOT}/var/lib/angel-eye-bridge" \
  "${TEST_ROOT}/opt/angel-eye-bridge/releases/1.3.0-old"

cat > "${TEST_ROOT}/etc/angel-eye-bridge/appsettings.json" <<'JSON'
{
  "bridge": {
    "instanceName": "telebet-29",
    "environment": "QA",
    "role": "Primary"
  },
  "health": { "port": 18080 }
}
JSON
printf 'state-before\n' > "${TEST_ROOT}/var/lib/angel-eye-bridge/bridge-state.json"
printf 'sqlite-before\n' > "${TEST_ROOT}/var/lib/angel-eye-bridge/bridge-events.sqlite"
cat > "${TEST_ROOT}/opt/angel-eye-bridge/releases/1.3.0-old/.release-manifest.json" <<'JSON'
{"version":"1.3.0"}
JSON
printf '#!/bin/sh\nexit 0\n' > \
  "${TEST_ROOT}/opt/angel-eye-bridge/releases/1.3.0-old/angel-eye-bridge"
chmod +x "${TEST_ROOT}/opt/angel-eye-bridge/releases/1.3.0-old/angel-eye-bridge"
ln -s "${TEST_ROOT}/opt/angel-eye-bridge/releases/1.3.0-old" \
  "${TEST_ROOT}/opt/angel-eye-bridge/current"

echo active > "${DEPLOY_STATE_ROOT}/active"
echo enabled > "${DEPLOY_STATE_ROOT}/enabled"
echo 0 > "${DEPLOY_STATE_ROOT}/stop-count"
echo 0 > "${DEPLOY_STATE_ROOT}/restart-count"

openssl req -x509 -newkey rsa:2048 -nodes \
  -subj "/CN=Angel Worker Release Test" \
  -keyout "${TEST_ROOT}/signing.key" \
  -out "${TEST_ROOT}/etc/angel-eye-deploy/release-signing.pem" \
  -days 1 >/dev/null 2>&1

run_deploy() {
  "$DEPLOY_SCRIPT" "$@"
}

manifest_version() {
  python3 - "$1" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as stream:
    print(json.load(stream)["version"])
PY
}

make_bundle() {
  local deployment_id="$1"
  local version="$2"
  local fail_check="${3:-false}"
  local staging="${TEST_ROOT}/var/tmp/angel-eye-deploy/${deployment_id}"
  local build="${TEST_ROOT}/build-${deployment_id}"
  mkdir -p "$staging" "$build"
  cat > "${build}/angel-eye-bridge" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
directory="$(cd "$(dirname "$0")" && pwd)"
[[ ! -f "${directory}/fail-check" ]]
SH
  chmod +x "${build}/angel-eye-bridge"
  [[ "$fail_check" == "true" ]] && touch "${build}/fail-check"
  printf 'worker-%s\n' "$version" > "${build}/angel-eye-bridge.dll"
  tar -czf "${staging}/release.tar.gz" -C "$build" .
  local digest
  digest="$(sha256sum "${staging}/release.tar.gz" | awk '{print $1}')"
  python3 - "${staging}/release-manifest.json" "$version" "$digest" <<'PY'
import json
import sys
path, version, digest = sys.argv[1:4]
with open(path, "w", encoding="utf-8") as stream:
    json.dump({
        "schemaVersion": 1,
        "version": version,
        "buildCommit": "a14aed3",
        "targetRuntime": "linux-x64",
        "artifactFile": "release.tar.gz",
        "artifactSha256": digest,
        "createdUtc": "2026-07-23T06:00:00Z"
    }, stream, separators=(",", ":"))
PY
  openssl cms -sign -binary \
    -in "${staging}/release-manifest.json" \
    -signer "${TEST_ROOT}/etc/angel-eye-deploy/release-signing.pem" \
    -inkey "${TEST_ROOT}/signing.key" \
    -outform DER \
    -out "${staging}/release-manifest.p7s" \
    -nosmimecap >/dev/null 2>&1
}

assert_current_version() {
  local expected="$1"
  local actual
  actual="$(manifest_version "${TEST_ROOT}/opt/angel-eye-bridge/current/.release-manifest.json")"
  [[ "$actual" == "$expected" ]] || {
    echo "expected current version $expected, got $actual" >&2
    exit 1
  }
}

ID_OK="4f97045b-ec5d-42c8-8c82-b45c97a1fa92"
ID_TAMPER="5a87045b-ec5d-42c8-8c82-b45c97a1fa93"
ID_CONFIG="6b97045b-ec5d-42c8-8c82-b45c97a1fa94"
ID_HEALTH="7c97045b-ec5d-42c8-8c82-b45c97a1fa95"
ID_STANDBY="8d97045b-ec5d-42c8-8c82-b45c97a1fa96"

run_deploy preflight "$ID_OK" | grep -q '"stage":"staging"'
make_bundle "$ID_OK" "1.4.0"
run_deploy preflight "$ID_OK" | grep -q '"status":"succeeded"'
CONFIG_HASH="$(sha256sum "${TEST_ROOT}/etc/angel-eye-bridge/appsettings.json")"
STATE_HASH="$(sha256sum "${TEST_ROOT}/var/lib/angel-eye-bridge/bridge-state.json")"
run_deploy install "$ID_OK" | grep -q '"stage":"health"'
assert_current_version "1.4.0"
[[ "$(sha256sum "${TEST_ROOT}/etc/angel-eye-bridge/appsettings.json")" == "$CONFIG_HASH" ]]
[[ "$(sha256sum "${TEST_ROOT}/var/lib/angel-eye-bridge/bridge-state.json")" == "$STATE_HASH" ]]
run_deploy status | grep -q '"currentVersion":"1.4.0"'
run_deploy status | grep -q '"stage":"health"'
run_deploy status | grep -q '"lastResult":"succeeded"'
grep -q '"target":"telebet-29"' \
  "${TEST_ROOT}/var/log/angel-eye-bridge/deployments/${ID_OK}.jsonl"
grep -Eq '"operator":"[^"]+"' \
  "${TEST_ROOT}/var/log/angel-eye-bridge/deployments/${ID_OK}.jsonl"
grep -Eq '"artifactSha256":"[0-9a-f]{64}"' \
  "${TEST_ROOT}/var/log/angel-eye-bridge/deployments/${ID_OK}.jsonl"

STOP_BEFORE="$(<"${DEPLOY_STATE_ROOT}/stop-count")"
make_bundle "$ID_TAMPER" "1.4.1"
printf 'tamper\n' >> \
  "${TEST_ROOT}/var/tmp/angel-eye-deploy/${ID_TAMPER}/release.tar.gz"
if run_deploy preflight "$ID_TAMPER" >/dev/null 2>&1; then
  echo "tampered bundle was accepted" >&2
  exit 1
fi
[[ "$(<"${DEPLOY_STATE_ROOT}/stop-count")" == "$STOP_BEFORE" ]]
assert_current_version "1.4.0"

make_bundle "$ID_CONFIG" "1.4.2" true
if run_deploy preflight "$ID_CONFIG" >/dev/null 2>&1; then
  echo "config-invalid bundle was accepted" >&2
  exit 1
fi
[[ "$(<"${DEPLOY_STATE_ROOT}/stop-count")" == "$STOP_BEFORE" ]]
assert_current_version "1.4.0"

make_bundle "$ID_HEALTH" "1.5.0"
run_deploy preflight "$ID_HEALTH" >/dev/null
echo "1.5.0" > "${TEST_ROOT}/health-fail-version"
set +e
run_deploy install "$ID_HEALTH" > "${TEST_ROOT}/health-install.out"
HEALTH_EXIT=$?
set -e
[[ "$HEALTH_EXIT" == "70" ]]
grep -q '"stage":"rollback","status":"succeeded"' "${TEST_ROOT}/health-install.out"
assert_current_version "1.4.0"
rm "${TEST_ROOT}/health-fail-version"

python3 - "${TEST_ROOT}/etc/angel-eye-bridge/appsettings.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as stream:
    value = json.load(stream)
value["bridge"] = {
    "instanceName": "telebet-31",
    "environment": "Production",
    "role": "Standby"
}
with open(path, "w", encoding="utf-8") as stream:
    json.dump(value, stream)
PY
echo active > "${DEPLOY_STATE_ROOT}/active"
echo enabled > "${DEPLOY_STATE_ROOT}/enabled"
make_bundle "$ID_STANDBY" "1.6.0"
run_deploy preflight "$ID_STANDBY" >/dev/null
run_deploy install "$ID_STANDBY" | grep -q 'standby release installed'
assert_current_version "1.6.0"
[[ "$(<"${DEPLOY_STATE_ROOT}/active")" == "inactive" ]]
[[ "$(<"${DEPLOY_STATE_ROOT}/enabled")" == "disabled" ]]

for id in "$ID_OK" "$ID_TAMPER" "$ID_CONFIG" "$ID_HEALTH" "$ID_STANDBY"; do
  [[ -s "${TEST_ROOT}/var/log/angel-eye-bridge/deployments/${id}.jsonl" ]]
done

echo "deployment-script-tests: PASS"
