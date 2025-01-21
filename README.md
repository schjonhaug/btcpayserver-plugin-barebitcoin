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
   - IP Whitelist: 170.75.160.202
   - Permissions: Select both Read and Write permissions

3. After creation, you'll receive:
   - Public Key
   - Secret Key

Make sure to securely store these credentials as the secret key will only be shown once.

### 2. Lightning Connection Setup

After obtaining your API keys, use the provided `barebitcoin-lightning-connection-setup.sh` script to select your Bitcoin account and generate the connection configuration:

1. Make the script executable:
   ```shell
   chmod +x barebitcoin-lightning-connection-setup.sh
   ```

2. Run the script with your API keys:
   ```shell
   ./barebitcoin-lightning-connection-setup.sh "your-public-key" "your-secret-key"
   ```

3. The script will:
   - List all your available Bitcoin accounts with their balances
   - Let you select which account to use
   - Generate the connection configuration string needed for BTCPay Server

4. Copy the generated configuration string - you'll need this to complete the setup in BTCPay Server.

Example output:
```
Available accounts:
1) acc_01HQ1MA3YFJE4358XK5AN2MZTH (Balance: 0.01186317 BTC)
2) acc_01HQ2FF8Z6M3XGKB0HMZQRK45K (Balance: 0 BTC)
3) acc_01HQ6E808YMS3KB2Z9R72BQGHT (Balance: 0.00000071 BTC)

Select account number (1-3):

Connection configuration for your custom Lightning node:
type=barebitcoin;private-key=your-secret-key;public-key=your-public-key;account-id=selected-account-id
```

This setup process only needs to be done once when initially configuring the plugin.


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
}' > ../btcpayserver/appsettings.dev.json
```

Finally, also in the cloned BTCPay Server, run:

```shell
