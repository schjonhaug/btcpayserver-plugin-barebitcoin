#!/usr/bin/env bash
set -euo pipefail

# Uploads a .btcpay package to a BTCPay Server test instance and restarts it, the
# same way Server Settings > Plugins does. BTCPay has no Greenfield API for plugin
# management, so this signs in as an admin and posts the UI forms.
#
# Reads these from the environment, or from the env file in BTCPAY_TEST_ENV
# (default ~/.config/btcpay-test/env):
#   BTCPAY_URL              for example https://btcpay.example.com
#   BTCPAY_ADMIN_EMAIL      a server admin without two-factor authentication
#   BTCPAY_ADMIN_PASSWORD
# Optional:
#   BTCPAY_INSECURE=1       skip TLS verification, for self-signed instances
#   BTCPAY_RESOLVE          passed to curl --resolve, for example host:port:ip

usage() {
  echo "Usage: $0 [path/to/plugin.btcpay] [--no-restart]" >&2
}

scriptDir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" >/dev/null && pwd -P)"
repoRoot="$(git -C "$scriptDir" rev-parse --show-toplevel)"

package=""
restart=true
for arg in "$@"; do
  case "$arg" in
    --no-restart) restart=false ;;
    -h|--help) usage; exit 0 ;;
    -*) echo "Unknown option: $arg" >&2; usage; exit 1 ;;
    *) package="$arg" ;;
  esac
done
package="${package:-$repoRoot/plugin/tmp/out/BTCPayServer.Plugins.BareBitcoin.btcpay}"
if [ ! -f "$package" ]; then
  echo "No plugin package at $package. Build one first, see pluginpacker.sh." >&2
  exit 1
fi

envFile="${BTCPAY_TEST_ENV:-$HOME/.config/btcpay-test/env}"
if [ -f "$envFile" ]; then
  # shellcheck disable=SC1090
  source "$envFile"
fi
for name in BTCPAY_URL BTCPAY_ADMIN_EMAIL BTCPAY_ADMIN_PASSWORD; do
  if [ -z "${!name:-}" ]; then
    echo "$name is not set; add it to $envFile." >&2
    exit 1
  fi
done
baseUrl="${BTCPAY_URL%/}"

workDir="$(mktemp -d)"
chmod 700 "$workDir"
trap 'rm -rf "$workDir"' EXIT
cookies="$workDir/cookies"
page="$workDir/page.html"

curlOptions=(--silent --show-error --max-time 60 --cookie "$cookies" --cookie-jar "$cookies")
if [ "${BTCPAY_INSECURE:-0}" = 1 ]; then curlOptions+=(--insecure); fi
if [ -n "${BTCPAY_RESOLVE:-}" ]; then curlOptions+=(--resolve "$BTCPAY_RESOLVE"); fi

# Every form carries an antiforgery token bound to the session cookie.
antiforgery_token() {
  perl -ne 'if (/name="__RequestVerificationToken"[^>]*value="([^"]+)"/) { print "$1\n"; exit }' "$page"
}

# Prints the HTTP status and stores the body in $page; redirects are not followed,
# so a successful form post shows up as a 302.
request() {
  curl "${curlOptions[@]}" --output "$page" --write-out '%{http_code} %{redirect_url}\n' "$@"
}

echo "Signing in to $baseUrl as $BTCPAY_ADMIN_EMAIL"
request "$baseUrl/login" >/dev/null
token="$(antiforgery_token)"
[ -n "$token" ] || { echo "The login page has no antiforgery token." >&2; exit 1; }
# The password goes through stdin, so it never appears in the process list.
read -r status location < <(printf '%s' "$BTCPAY_ADMIN_PASSWORD" | request "$baseUrl/login" \
  --data-urlencode "__RequestVerificationToken=$token" \
  --data-urlencode "Email=$BTCPAY_ADMIN_EMAIL" \
  --data-urlencode "Method=Password" \
  --data-urlencode "Password@-")
if [ "$status" != 302 ] || [[ "$location" == */login* ]]; then
  echo "Sign-in failed (HTTP $status). Check the credentials; accounts with two-factor authentication are not supported." >&2
  exit 1
fi

status="$(request "$baseUrl/server/plugins" | cut -d' ' -f1)"
token="$(antiforgery_token)"
if [ "$status" != 200 ] || [ -z "$token" ]; then
  echo "Could not open Server Settings > Plugins (HTTP $status); the account must be a server admin." >&2
  exit 1
fi

# BTCPay takes the plugin identifier from the file name.
echo "Uploading $(basename "$package")"
status="$(request "$baseUrl/server/plugins/upload" \
  --form "__RequestVerificationToken=$token" \
  --form "files=@$package;filename=$(basename "$package");type=application/octet-stream" | cut -d' ' -f1)"
[ "$status" = 302 ] || { echo "Upload failed (HTTP $status)." >&2; exit 1; }

if [ "$restart" = false ]; then
  echo "Uploaded. Restart BTCPay Server to load the plugin."
  exit 0
fi

echo "Restarting BTCPay Server"
status="$(request "$baseUrl/server/maintenance" \
  --data-urlencode "__RequestVerificationToken=$token" \
  --data-urlencode "command=soft-restart" | cut -d' ' -f1)"
[ "$status" = 302 ] || [ "$status" = 200 ] || { echo "Restart request failed (HTTP $status)." >&2; exit 1; }

# The restart starts a few seconds after the request, so wait for the server to
# go down before waiting for it to come back.
health() {
  curl "${curlOptions[@]}" --max-time 5 --output /dev/null --write-out '%{http_code}' "$baseUrl/api/v1/health" 2>/dev/null || true
}
for _ in $(seq 1 30); do
  [ "$(health)" = 200 ] || break
  sleep 1
done
for _ in $(seq 1 120); do
  if [ "$(health)" = 200 ]; then
    echo "BTCPay Server is back up with the uploaded plugin."
    exit 0
  fi
  sleep 2
done
echo "BTCPay Server did not come back within 4 minutes." >&2
exit 1
