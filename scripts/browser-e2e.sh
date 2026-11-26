#!/bin/sh
# Run from any directory. The caller supplies a browser-trusted localhost certificate.
set -eu
cd "$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
: "${CONTROLSYSTEM_E2E_CERT:=$PWD/.local/browser-e2e/localhost.pem}"
: "${CONTROLSYSTEM_E2E_KEY:=$PWD/.local/browser-e2e/localhost.key}"
: "${NODE_EXTRA_CA_CERTS:=${CONTROLSYSTEM_E2E_CA:-$CONTROLSYSTEM_E2E_CERT}}"
export CONTROLSYSTEM_E2E_CERT CONTROLSYSTEM_E2E_KEY NODE_EXTRA_CA_CERTS
if [ ! -r "$CONTROLSYSTEM_E2E_CERT" ] || [ ! -r "$CONTROLSYSTEM_E2E_KEY" ]; then
  echo 'Missing localhost TLS certificate/key. Follow docs/browser-e2e.md (TLS validation stays enabled).' >&2
  exit 2
fi
dotnet test tests/ControlSystem.BrowserTests/ControlSystem.BrowserTests.csproj "$@"
