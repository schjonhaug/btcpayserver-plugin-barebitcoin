#!/usr/bin/env bash
set -euo pipefail

pluginDir="plugin"
projectFile="$pluginDir/BTCPayServer.Plugins.BareBitcoin.csproj"
fullName="BTCPayServer.Plugins.BareBitcoin"
repoUrl="https://github.com/schjonhaug/btcpayserver-plugin-barebitcoin"
pluginBuilderUrl="https://plugin-builder.btcpayserver.org/plugins/barebitcoin/create"

# The BTCPay Server sources live either in the tracked submodule, which is the
# layout Plugin Builder uses, or in an adjacent checkout. BTCPAYSERVER_DIR
# overrides both for checkouts kept elsewhere.
has_plugin_packer() {
  [ -f "$1/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj" ]
}

resolve_btcpayserver_dir() {
  local candidate
  # An explicit override is never silently ignored: a wrong value is a typo,
  # not a reason to package from a different checkout than the one asked for.
  if [ -n "${BTCPAYSERVER_DIR:-}" ]; then
    if ! has_plugin_packer "$BTCPAYSERVER_DIR"; then
      echo "BTCPAYSERVER_DIR=$BTCPAYSERVER_DIR has no BTCPayServer.PluginPacker project." >&2
      return 1
    fi
    (cd -- "$BTCPAYSERVER_DIR" >/dev/null && pwd -P) || return 1
    return 0
  fi

  for candidate in "submodules/btcpayserver" "../btcpayserver"; do
    if has_plugin_packer "$candidate"; then
      (cd -- "$candidate" >/dev/null && pwd -P) || return 1
      return 0
    fi
  done

  cat >&2 <<EOF
Could not find BTCPay Server sources for the local .btcpay package.
Looked for BTCPayServer.PluginPacker in:
  submodules/btcpayserver
  ../btcpayserver
Initialize the submodule with: git submodule update --init submodules/btcpayserver
Or set BTCPAYSERVER_DIR to a BTCPay Server checkout.
EOF
  return 1
}

usage() {
  cat <<EOF
Usage: $0 <version> [--no-push] [--no-package]

Creates a reproducible Plugin Builder pre-release candidate:
  1. validates master, a clean worktree, and the BTCPay Server sources
  2. updates $projectFile
  3. runs tests
  4. creates a local .btcpay package unless --no-package is passed
  5. commits "Release v<version>"
  6. creates tag v<version>
  7. pushes master and tag unless --no-push is passed
  8. prints the Plugin Builder form values

The local package needs BTCPay Server sources. The tracked
submodules/btcpayserver submodule and an adjacent ../btcpayserver checkout are
both found automatically; set BTCPAYSERVER_DIR for any other location.

Every Plugin Builder build starts as a pre-release. After validation, press
Release in the Plugin Builder UI to make that same build public.
EOF
}

version=""
pushRelease=true
packageLocal=true
while [ "$#" -gt 0 ]; do
  case "$1" in
    --no-push)
      pushRelease=false
      ;;
    --no-package)
      packageLocal=false
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    v*)
      echo "Pass the version without a leading v, for example: 2.0.1" >&2
      usage >&2
      exit 1
      ;;
    [0-9]*)
      if [ -n "$version" ]; then
        echo "Version was provided more than once." >&2
        usage >&2
        exit 1
      fi
      version="$1"
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
  shift
done

if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Version must be SemVer without a leading v, for example: 2.0.1" >&2
  usage >&2
  exit 1
fi

tag="v$version"

if [ "$(git rev-parse --abbrev-ref HEAD)" != "master" ]; then
  echo "Release must be run from master." >&2
  exit 1
fi

if [ -n "$(git status --porcelain)" ]; then
  echo "Worktree must be clean before release." >&2
  git status --short >&2
  exit 1
fi

if git rev-parse "$tag" >/dev/null 2>&1; then
  echo "Tag $tag already exists locally." >&2
  exit 1
fi

btcpayServerDir=""
if [ "$packageLocal" = true ]; then
  if ! btcpayServerDir="$(resolve_btcpayserver_dir)"; then
    echo "Or pass --no-package; Plugin Builder builds the package from the tag anyway." >&2
    exit 1
  fi
  echo "Using BTCPay Server sources at $btcpayServerDir"
fi

currentVersion="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$projectFile" | head -1)"
if [ -z "$currentVersion" ]; then
  echo "Could not find <Version> in $projectFile." >&2
  exit 1
fi

echo "Preparing release $tag from current version $currentVersion"

# Everything up to the commit can fail, and the version bump is already in the
# worktree by then. Restoring it keeps the clean-worktree check meaningful, so
# a failed run can be repeated once its cause is fixed.
projectFileCommitted=false
restore_project_file() {
  if [ "$projectFileCommitted" = false ]; then
    git checkout -- "$projectFile"
  fi
}
trap restore_project_file EXIT

perl -0pi -e "s:<Version>[^<]+</Version>:<Version>$version</Version>:" "$projectFile"

dotnet test --project BTCPayServer.Plugins.Tests/BTCPayServer.Plugins.Tests.csproj -c Release --minimum-expected-tests 1

# Packaging only reads the bumped project file, so it runs before the commit
# and tag. A packaging failure then leaves git untouched and the run can simply
# be repeated.
if [ "$packageLocal" = true ]; then
  # The packer is built outside the BTCPay Server checkout. Building into it
  # leaves untracked output behind, which fails the clean-worktree check on the
  # next release when that checkout is the tracked submodule.
  pluginPackerOut="$(pwd)/$pluginDir/tmp/pluginpacker"
  rm -rf "$pluginPackerOut"
  dotnet build "$btcpayServerDir/BTCPayServer.PluginPacker/BTCPayServer.PluginPacker.csproj" \
    -c Release -o "$pluginPackerOut"

  pushd "$pluginDir"
    rm -rf tmp/publish tmp/publish-package tmp/out
    dotnet publish -c Release -o "tmp/publish"
    dotnet "$pluginPackerOut/BTCPayServer.PluginPacker.dll" "tmp/publish" "$fullName" "tmp/publish-package"
    mkdir -p tmp/out
    cp tmp/publish-package/*/*/* tmp/out
    rm -f tmp/out/SHA256SUMS.asc tmp/out/SHA256SUMS
  popd

  echo "Plugin file ready at: $pluginDir/tmp/out/"
else
  echo "Skipping local .btcpay package because --no-package was passed."
fi

git add "$projectFile"
git commit -m "Release $tag"
projectFileCommitted=true
git tag "$tag"

if [ "$pushRelease" = true ]; then
  git push origin master
  git push origin "$tag"
else
  echo "Skipping push because --no-push was passed."
fi

echo
echo "Plugin Builder build page: $pluginBuilderUrl"
echo
echo "Plugin Builder fields:"
echo "  Git repository: $repoUrl"
echo "  Git branch or tag: $tag"
echo "  Directory to the plugin's project: $pluginDir"
echo "  Dotnet build configuration: Release"
echo
echo "This Plugin Builder build will start as a pre-release."
echo "Install it on your own BTCPay instance with pre-release plugins enabled."
echo "After validation, press Release in Plugin Builder to make $tag public."
