#!/usr/bin/env bash
set -euo pipefail

pluginDir="plugin"
fullName="BTCPayServer.Plugins.BareBitcoin"
pluginPackerDir="../btcpayserver/BTCPayServer.PluginPacker"
pluginPackerOut="$pluginPackerDir/build-tools/PluginPacker"
repoUrl="https://github.com/schjonhaug/btcpayserver-plugin-barebitcoin"

rm -rf "$pluginPackerOut"

# Create plugin packer
pushd "$pluginPackerDir"
  mkdir -p build-tools/PluginPacker
  dotnet build -c Release -o build-tools/PluginPacker
  rm -rf build-tools/btcpayserver
popd

cd "$pluginDir"
dotnet publish -c Release -o "tmp/publish"
../../btcpayserver/BTCPayServer.PluginPacker/build-tools/PluginPacker/BTCPayServer.PluginPacker "tmp/publish" "$fullName" "tmp/publish-package"
mkdir -p tmp/out
cp tmp/publish-package/*/*/* tmp/out
rm -f tmp/out/SHA256SUMS.asc tmp/out/SHA256SUMS

echo "Plugin file ready at: $pluginDir/tmp/out/"
echo "Plugin Builder build page: https://plugin-builder.btcpayserver.org/plugins/barebitcoin/create"
echo
echo "Plugin Builder fields:"
echo "  Git repository: $repoUrl"
echo "  Git branch or tag: master"
echo "  Directory to the plugin's project: $pluginDir"
echo "  Dotnet build configuration: Release"
echo
echo "For a pre-release validation build, prefer using the release tag instead of master once the version bump is committed and tagged."
echo "Upload the .btcpay file from $pluginDir/tmp/out/ only when installing manually on a BTCPay Server instance."
