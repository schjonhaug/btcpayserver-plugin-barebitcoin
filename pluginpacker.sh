#!/usr/bin/env bash
set -euo pipefail

pluginDir="plugin"
projectFile="$pluginDir/BTCPayServer.Plugins.BareBitcoin.csproj"
fullName="BTCPayServer.Plugins.BareBitcoin"
repoUrl="https://github.com/schjonhaug/btcpayserver-plugin-barebitcoin"
pluginBuilderUrl="https://plugin-builder.btcpayserver.org/plugins/barebitcoin/create"
pluginPackerDir="../btcpayserver/BTCPayServer.PluginPacker"
pluginPackerOut="$pluginPackerDir/build-tools/PluginPacker"

usage() {
  cat <<EOF
Usage: $0 <version> [--no-push] [--no-package]

Creates a reproducible Plugin Builder pre-release candidate:
  1. validates master and a clean worktree
  2. updates $projectFile
  3. runs tests
  4. commits "Release v<version>"
  5. creates tag v<version>
  6. pushes master and tag unless --no-push is passed
  7. creates a local .btcpay package unless --no-package is passed
  8. prints the Plugin Builder form values

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

currentVersion="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$projectFile" | head -1)"
if [ -z "$currentVersion" ]; then
  echo "Could not find <Version> in $projectFile." >&2
  exit 1
fi

echo "Preparing release $tag from current version $currentVersion"

perl -0pi -e "s:<Version>[^<]+</Version>:<Version>$version</Version>:" "$projectFile"

dotnet test BTCPayServer.Plugins.Tests/BTCPayServer.Plugins.Tests.csproj -c Release

git add "$projectFile"
git commit -m "Release $tag"
git tag "$tag"

if [ "$pushRelease" = true ]; then
  git push origin master
  git push origin "$tag"
else
  echo "Skipping push because --no-push was passed."
fi

if [ "$packageLocal" = true ]; then
  rm -rf "$pluginPackerOut"

  pushd "$pluginPackerDir"
    mkdir -p build-tools/PluginPacker
    dotnet build -c Release -o build-tools/PluginPacker
    rm -rf build-tools/btcpayserver
  popd

  pushd "$pluginDir"
    rm -rf tmp/publish tmp/publish-package tmp/out
    dotnet publish -c Release -o "tmp/publish"
    ../../btcpayserver/BTCPayServer.PluginPacker/build-tools/PluginPacker/BTCPayServer.PluginPacker "tmp/publish" "$fullName" "tmp/publish-package"
    mkdir -p tmp/out
    cp tmp/publish-package/*/*/* tmp/out
    rm -f tmp/out/SHA256SUMS.asc tmp/out/SHA256SUMS
  popd

  echo "Plugin file ready at: $pluginDir/tmp/out/"
else
  echo "Skipping local .btcpay package because --no-package was passed."
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
