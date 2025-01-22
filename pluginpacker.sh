pluginDir="plugin"
fullName="BTCPayServer.Plugins.BareBitcoin"

if [ ! -d ../btcpayserver/BTCPayServer.PluginPacker/build-tools/PluginPacker ]; then
  # Create plugin packer
  cd ../btcpayserver/BTCPayServer.PluginPacker
  mkdir -p build-tools/PluginPacker
  dotnet build -c Release -o build-tools/PluginPacker
  rm -rf build-tools/btcpayserver
  cd -
fi

cd $pluginDir
dotnet publish -c Release -o "tmp/publish"
../../btcpayserver/BTCPayServer.PluginPacker/build-tools/PluginPacker/BTCPayServer.PluginPacker "tmp/publish" "$fullName" "tmp/publish-package"
mkdir -p tmp/out
cp tmp/publish-package/*/*/* tmp/out
rm tmp/out/SHA256SUMS.asc tmp/out/SHA256SUMS

echo "Plugin file ready at: $pluginDir/tmp/out/"
echo "Upload the .btcpay file from this directory to BTCPay Server's plugin section"
