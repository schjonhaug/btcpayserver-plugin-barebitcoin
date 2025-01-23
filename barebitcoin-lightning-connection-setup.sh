#!/bin/zsh

# Remove argument check and replace with prompts
echo "Please enter your public key:"
read public_key

echo "Please enter your secret key:"
read secret_key

# Validate inputs
if [ -z "$public_key" ] || [ -z "$secret_key" ]; then
    echo "Error: Both public key and secret key are required" >&2
    exit 1
fi

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

# Execute the curl command and store the response, including HTTP status code
response=$(/usr/bin/curl -s -w "\n%{http_code}" -X GET \
  https://api.bb.no/v1/user/bitcoin-accounts \
  -H "x-bb-api-hmac: $hmac" \
  -H "x-bb-api-key: $public_key" \
  -H "x-bb-api-nonce: $nonce")

# Split response into body and status code
body=$(echo "$response" | /usr/bin/sed '$d')
status_code=$(echo "$response" | /usr/bin/tail -n1)

# Check status code
if [ "$status_code" != "200" ]; then
    echo "Error: API request failed with status code $status_code"
    echo "Response body:"
    echo "$body" | /usr/bin/jq '.'
    exit 1
fi

# Extract and display accounts with numbers
echo "\nAvailable accounts:"
declare -A accounts
i=1
while IFS= read -r id; do
    if [ ! -z "$id" ]; then
        accounts[$i]=$id
        balance=$(/usr/bin/printf "%s" "$body" | /usr/bin/jq -r ".accounts[] | select(.id == \"$id\") | .availableBtc")
        echo "$i) $id (Balance: $balance BTC)"
        ((i++))
    fi
done < <(/usr/bin/printf "%s" "$body" | /usr/bin/jq -r '.accounts[].id')

# If only one account, select it automatically
if [ $((i-1)) -eq 1 ]; then
    account_num=1
    selected_account=${accounts[$account_num]}
else
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
fi

# Output the final string with heading
echo "\nConnection configuration for your custom Lightning node:\n"
echo "type=barebitcoin;private-key=$secret_key;public-key=$public_key;account-id=$selected_account"
