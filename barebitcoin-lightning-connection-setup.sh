#!/bin/zsh

# Check if both arguments are provided
if [ $# -lt 2 ]; then
    echo "Error: Missing required arguments" >&2
    echo "Usage: $0 <public_key> <secret_key>" >&2
    exit 1
fi

public_key="$1"
secret_key="$2"

# Set up request components
method="GET"
path="/v1/user/bitcoin-accounts"
nonce=$(/bin/date +%s)

# Create nonce hash
nonce_hash=$(/usr/bin/printf "%s" "$nonce" | /opt/homebrew/bin/openssl dgst -sha256 -binary)

# Concatenate method, path, and nonce_hash
message=$(/usr/bin/printf "%s%s" "$method" "$path")
message_with_hash=$(/usr/bin/printf "%s%s" "$message" "$nonce_hash")

# Decode secret and create HMAC
secret_decoded=$(/usr/bin/printf "%s" "$secret_key" | /usr/bin/base64 -d)
hmac=$(/usr/bin/printf "%s" "$message_with_hash" | /opt/homebrew/bin/openssl dgst -sha256 -binary -mac HMAC -macopt key:"$secret_decoded" | /usr/bin/base64)

# Execute the curl command and store the response
response=$(/usr/bin/curl -s -X GET \
  https://api.bb.no/v1/user/bitcoin-accounts \
  -H "x-bb-api-hmac: $hmac" \
  -H "x-bb-api-key: $public_key" \
  -H "x-bb-api-nonce: $nonce")

# Extract and display accounts with numbers
echo "Available accounts:"
declare -A accounts
i=1
while IFS= read -r id; do
    if [ ! -z "$id" ]; then
        accounts[$i]=$id
        balance=$(/usr/bin/printf "%s" "$response" | /usr/bin/jq -r ".accounts[] | select(.id == \"$id\") | .availableBtc")
        echo "$i) $id (Balance: $balance BTC)"
        ((i++))
    fi
done < <(/usr/bin/printf "%s" "$response" | /usr/bin/jq -r '.accounts[].id')

# Prompt for account selection
echo "\nSelect account number (1-$((i-1))):"
read account_num

# Validate input
if [[ ! "$account_num" =~ ^[0-9]+$ ]] || [ "$account_num" -lt 1 ] || [ "$account_num" -gt $((i-1)) ]; then
    echo "Invalid selection" >&2
    exit 1
fi

# Get selected account ID
selected_account=${accounts[$account_num]}

# Output the final string with heading
echo "\nConnection configuration for your custom Lightning node:\n"
echo "type=barebitcoin;private-key=$secret_key;public-key=$public_key;account-id=$selected_account"
