# Bare Bitcoin Plugin for BTCPay Server

Integrate your [Bare Bitcoin](https://barebitcoin.no) account with BTCPay Server. This plugin allows you to receive Lightning payments directly to your Bare Bitcoin account and view your current balance.

## Features

- **Receive Lightning payments** — Accept bitcoin payments over Lightning Network directly to your Bare Bitcoin account
- **Balance display** — View your current Bare Bitcoin balance within BTCPay Server

## Limitations

Sending payments over Lightning Network is not yet supported.

## Installation

1. In BTCPay Server, go to **Server Settings > Plugins**
2. Search for "Bare Bitcoin"
3. Click **Install**
4. Restart BTCPay Server when prompted

## Setup

### 1. Create API Keys

1. Log in to your Bare Bitcoin account
2. Navigate to [API Key Creation](https://barebitcoin.no/innlogget/profil/nokler/opprett)
3. Create a new key with:
   - **Name:** A descriptive name (e.g., "BTCPay Server")
   - **Permissions:** Select both **Read** and **Receive**
4. Save your Public Key and Secret Key securely — the secret key is only shown once

### 2. Generate Connection String

Use the provided script to generate your BTCPay Server connection string:

1. Ensure [Node.js](https://nodejs.org) is installed
2. Run:
   ```shell
   node barebitcoin-lightning-connection-setup.js
   ```
3. Enter your Public Key and Secret Key when prompted
4. Select which Bitcoin account to use (if you have multiple)
5. Copy the generated connection string

### 3. Configure BTCPay Server

1. In BTCPay Server, go to your store's **Lightning** settings
2. Select **Bare Bitcoin** as the Lightning connection type
3. Paste your connection string
4. Save

## Development

### Prerequisites

Install the [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). The plugin and tests target `net10.0`.

This branch targets BTCPay Server `2.3.9` and newer. For BTCPay Server `2.3.8` and older, keep using an older plugin release.

Use one of these BTCPay Server source layouts:

- Clone BTCPay Server adjacent to this repository for local development.
- Or initialize the `submodules/btcpayserver` submodule, which is what Plugin Builder uses.

Adjacent checkout:

```shell
git clone https://github.com/btcpayserver/btcpayserver.git
```

Submodule checkout:

```shell
git submodule update --init --recursive
```

### Build

Build BTCPay Server if you are using the adjacent checkout:

```shell
dotnet build ../btcpayserver/BTCPayServer/BTCPayServer.csproj
```

Add the plugin to the BTCPay Server solution:

```shell
cd btcpayserver
dotnet sln add ../btcpayserver-plugin-barebitcoin/plugin -s Plugins
```

Build the plugin:

```shell
dotnet build ../btcpayserver-plugin-barebitcoin/plugin/BTCPayServer.Plugins.BareBitcoin.csproj
```

### Run Locally

Configure BTCPay Server to load the plugin:

```shell
echo '{
  "DEBUG_PLUGINS": "<absolute-path-to>/plugin/bin/Debug/net10.0/BTCPayServer.Plugins.BareBitcoin.dll"
}' > BTCPayServer/appsettings.dev.json
```

Start the development environment:

```shell
cd BTCPayServer.Tests
docker-compose up dev
```

Launch BTCPay Server (e.g., via VS Code's ".NET Core Launch (web)") and navigate to https://localhost:14142

## Public Release

To publish a new public plugin build for BTCPay Server, use the Plugin Builder:

- URL: https://plugin-builder.btcpayserver.org/
- Public plugin page: https://plugin-builder.btcpayserver.org/public/plugins/barebitcoin

Prepare a reproducible release tag with:

```shell
./pluginpacker.sh 2.0.1
```

The script updates the plugin version, runs tests, commits the version bump, creates and pushes the release tag, optionally creates a local `.btcpay` package, and prints the Plugin Builder form values.

Create a new Plugin Builder build with:

1. Git repository: `https://github.com/schjonhaug/btcpayserver-plugin-barebitcoin`
2. Git branch or tag: the release tag, for example `v2.0.1`
3. Directory to the plugin's project: `plugin`
4. Dotnet build configuration: `Release`

Every new Plugin Builder build starts as a **pre-release**. Pre-release builds are not visible to BTCPay Server instances unless the admin has explicitly enabled pre-release plugins.

Use the pre-release stage to install and test the plugin on your own BTCPay Server instance before promoting it. Once verified, release the build in the Plugin Builder UI to make it available to all users.

The GitHub release alone does not publish the plugin to the public BTCPay plugin directory.

Before building a release, keep the tracked `submodules/btcpayserver` checkout aligned with the BTCPay Server version declared in the plugin dependency metadata. Plugin Builder uses the submodule layout, so an outdated submodule can surface transitive BTCPay dependency warnings even when local adjacent-checkout builds are clean.

To update it, replace `v2.3.9` with the BTCPay Server version required by `plugin/BareBitcoinPlugin.cs`:

```shell
git submodule update --init submodules/btcpayserver
git -C submodules/btcpayserver fetch --tags
git -C submodules/btcpayserver checkout v2.3.9
git add submodules/btcpayserver
```
