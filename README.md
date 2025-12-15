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

Clone BTCPay Server adjacent to this repository:

```shell
git clone https://github.com/btcpayserver/btcpayserver.git
```

### Build

Build the BTCPay Server submodule:

```shell
dotnet build submodules/btcpayserver
```

Add the plugin to the BTCPay Server solution:

```shell
cd btcpayserver
dotnet sln add ../barebitcoin-btcpayserver-plugin/plugin -s Plugins
```

Build the plugin:

```shell
dotnet build ../barebitcoin-btcpayserver-plugin/plugin
```

### Run Locally

Configure BTCPay Server to load the plugin:

```shell
echo '{
  "DEBUG_PLUGINS": "<absolute-path-to>/plugin/bin/Debug/net8.0/BTCPayServer.Plugins.BareBitcoin.dll"
}' > BTCPayServer/appsettings.dev.json
```

Start the development environment:

```shell
cd BTCPayServer.Tests
docker-compose up dev
```

Launch BTCPay Server (e.g., via VS Code's ".NET Core Launch (web)") and navigate to https://localhost:14142
