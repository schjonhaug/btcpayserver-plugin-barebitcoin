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
    *)
      if [ -n "$package" ]; then echo "Pass a single plugin package." >&2; usage; exit 1; fi
      package="$arg"
      ;;
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
    echo "$name is not set; export it or add it to $envFile." >&2
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

# Prints the HTTP status and redirect target and stores the body in $page.
# Redirects are not followed, so a successful form post shows up as a 302.
request() {
  curl "${curlOptions[@]}" --output "$page" --write-out '%{http_code} %{redirect_url}\n' "$@"
}

# A form post only succeeded if BTCPay redirects to the page it returns to on
# success. A lost session redirects to /login instead, without doing anything.
expect_redirect() {
  local action="$1" expectedPath="$2" status location path
  read -r status location
  path="${location#"$baseUrl"}"
  path="${path%%\?*}"
  if [ "$status" != 302 ] || [ "$path" != "$expectedPath" ]; then
    echo "$action failed (HTTP $status${location:+, redirected to $location})." >&2
    exit 1
  fi
}

# Prints the version BTCPay lists for the plugin, from the authenticated plugins page.
installed_version() {
  request "$baseUrl/server/plugins" >/dev/null || return 0
  perl -0ne 'if (/id="\Q'"$identifier"'\E".*?<\/h4>.*?<h5[^>]*>\s*([0-9]+(?:\.[0-9]+){1,3})/s) { print "$1\n" }' "$page"
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

# BTCPay takes the plugin identifier from the file name. The packer writes the
# manifest next to the package, which tells which version should end up loaded.
identifier="$(basename "$package" .btcpay)"
expectedVersion=""
if [ -f "$package.json" ]; then
  expectedVersion="$(perl -MJSON::PP -0ne 'print decode_json($_)->{Version} // ""' "$package.json")"
fi

echo "Uploading $(basename "$package")${expectedVersion:+ (version $expectedVersion)}"
request "$baseUrl/server/plugins/upload" \
  --form "__RequestVerificationToken=$token" \
  --form "files=@$package;filename=$(basename "$package");type=application/octet-stream" |
  expect_redirect "Upload" /server/plugins

if [ "$restart" = false ]; then
  echo "Uploaded. Restart BTCPay Server to load the plugin."
  exit 0
fi

echo "Restarting BTCPay Server"
request "$baseUrl/server/maintenance" \
  --data-urlencode "__RequestVerificationToken=$token" \
  --data-urlencode "command=soft-restart" |
  expect_redirect "Restart request" /server/maintenance

health() {
  curl "${curlOptions[@]}" --max-time 5 --output /dev/null --write-out '%{http_code}' "$baseUrl/api/v1/health" 2>/dev/null || true
}

# BTCPay stops a few seconds after the request. Until it has been seen down, a
# healthy response still comes from the process running the old plugin.
deadline=$((SECONDS + 60))
until [ "$(health)" != 200 ]; do
  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "BTCPay Server did not restart within 60 seconds; the uploaded plugin is not loaded yet." >&2
    exit 1
  fi
  sleep 1
done

deadline=$((SECONDS + 240))
until [ "$(health)" = 200 ]; do
  if [ "$SECONDS" -ge "$deadline" ]; then
    echo "BTCPay Server did not come back within 4 minutes." >&2
    exit 1
  fi
  sleep 2
done

if [ -z "$expectedVersion" ]; then
  echo "BTCPay Server is back up. No manifest next to the package, so the loaded version was not checked."
  exit 0
fi
loadedVersion="$(installed_version)"
if [ "$loadedVersion" != "$expectedVersion" ]; then
  echo "BTCPay Server is back up, but lists $identifier as ${loadedVersion:-not installed} instead of $expectedVersion." >&2
  exit 1
fi
echo "BTCPay Server is back up with $identifier $loadedVersion."
