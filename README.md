# Bare Bitcoin Plugin for BTCPay Server

This plugin enables Bare Bitcoin functionality in BTCPay Server, allowing you to interact with Bitcoin in its purest form without any additional layers or complexity.

## Current Limitations

Currently, the plugin only supports receiving bitcoin over Lightning Network. Sending payments over Lightning Network is not yet supported.

## Future Plans

- Add support for sending bitcoin over Lightning Network

## First Time Setup

### 1. API Key Creation

To use the BareBitcoin API, you need to create API keys:

1. Navigate to [BareBitcoin API Key Creation](https://barebitcoin.no/innlogget/profil/nokler/opprett)
2. Fill in the following details:
   - Name: Give your API key a descriptive name (e.g., "Enogtjue")
   - Permissions: Select both Read and Receive permissions

3. After creation, you'll receive:
   - Public Key
   - Secret Key

Make sure to securely store these credentials as the secret key will only be shown once.

### 2. Lightning Connection Setup

After obtaining your API keys, use the provided `barebitcoin-lightning-connection-setup.js` script to select your Bitcoin account and generate the connection configuration:

1. Make sure you have Node.js installed on your system

2. Run the script with Node.js:
   ```shell
   node barebitcoin-lightning-connection-setup.js
   ```

3. When prompted, enter your:
   - Public key
   - Secret key

4. The script will:
   - List all your available Bitcoin accounts with their balances
   - Let you select which account to use (if you have multiple accounts)
   - Generate the connection configuration string needed for BTCPay Server

5. Copy the generated configuration string - you'll need this to complete the setup in BTCPay Server.

Example output:


## Local development

First, build BTCPay Server found in the submodule:

```shell
dotnet build submodules/btcpayserver
```

Then, in an adjacent folder to this repo, clone BTCPay Server:

```shell
git clone https://github.com/btcpayserver/btcpayserver.git
```

Then, add the plugin to the cloned BTCPay:

```shell
# Enter the forked BTCPay Server repository
cd btcpayserver

# Add your plugin to the solution
dotnet sln add ../barebitcoin-btcpayserver-plugin/plugin -s Plugins
```

Build the plugin:

```shell
dotnet build plugin
```

Find the the absolut path of `BTCPayServer.Plugins.BareBitcoin.dll`:

```shell
find . -name "BTCPayServer.Plugins.BareBitcoin.dll"
```

Finnaly, to make sure the plugin is included in every run:

```shell
echo '{
  "DEBUG_PLUGINS": "/Users/andreas/Developer/enogtjue/BB-plugin/barebitcoin-btcpayserver-plugin/plugin/bin/Debug/net8.0/BTCPayServer.Plugins.BareBitcoin.dll"
}' > ../btcpayserver/BTCPayServer/appsettings.dev.json
```

Finally, also in the cloned BTCPay Server, run:

```shell
cd btcpayserver/BTCPayServer.Tests
docker-compose up dev
```

Then, start the BTCPay Server using for example VSCode (.NET Core Launch (web))

Finally, navigate to https://localhost:14142

## Compilation of binaries

```shell
./pluginpacker.sh
```

This creates a file in `plugin/tmp/out/BTCPayServer.Plugins.BareBitcoin.btcpay` which then should be uploaded to BTCPay in the plugins section.