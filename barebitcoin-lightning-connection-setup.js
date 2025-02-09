#!/usr/bin/env node
"use strict";

const crypto = require("crypto");
const readline = require("readline");
const https = require("https");

// Use Node’s built‐in promises API for readline.
function askQuestion(query) {
  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
  });
  return new Promise((resolve) =>
    rl.question(query, (ans) => {
      rl.close();
      resolve(ans.trim());
    })
  );
}

async function main() {
  // Prompt for public and secret keys
  const publicKey = await askQuestion("Please enter your public key: ");
  const secretKey = await askQuestion("Please enter your secret key: ");

  // Validate input
  if (!publicKey || !secretKey) {
    console.error("Error: Both public key and secret key are required");
    process.exit(1);
  }

  // Set up request components
  const method = "GET";
  const path = "/v1/user/bitcoin-accounts";
  const nonce = Math.floor(Date.now() / 1000).toString();

  // Create nonce hash (binary output)
  const nonceHash = crypto
    .createHash("sha256")
    .update(nonce, "utf8")
    .digest();

  // Concatenate method, path, and nonceHash
  const messageBuffer = Buffer.concat([
    Buffer.from(method + path, "utf8"),
    nonceHash,
  ]);

  // Decode secret key (base64-decoded)
  let secretDecoded;
  try {
    secretDecoded = Buffer.from(secretKey, "base64");
  } catch (err) {
    console.error("Error: Failed to decode secret key from base64.");
    process.exit(1);
  }

  // Create HMAC (binary output, then base64-encode it)
  const hmacBuffer = crypto
    .createHmac("sha256", secretDecoded)
    .update(messageBuffer)
    .digest();
  const hmac = hmacBuffer.toString("base64");

  // Set up API request options
  const options = {
    hostname: "api.bb.no",
    port: 443,
    path: path,
    method: method,
    headers: {
      "x-bb-api-hmac": hmac,
      "x-bb-api-key": publicKey,
      "x-bb-api-nonce": nonce,
    },
  };

  // Make HTTPS GET request
  const responseData = await new Promise((resolve, reject) => {
    const req = https.request(options, (res) => {
      let data = "";
      res.setEncoding("utf8");
      res.on("data", (chunk) => {
        data += chunk;
      });
      res.on("end", () => {
        resolve({ statusCode: res.statusCode, body: data });
      });
    });
    req.on("error", (e) => reject(e));
    req.end();
  });

  // Check status code and parse body
  if (responseData.statusCode !== 200) {
    console.error(
      `Error: API request failed with status code ${responseData.statusCode}`
    );
    try {
      const parsed = JSON.parse(responseData.body);
      console.error("Response body:");
      console.error(JSON.stringify(parsed, null, 2));
    } catch (err) {
      console.error("Response body:");
      console.error(responseData.body);
    }
    process.exit(1);
  }

  let data;
  try {
    data = JSON.parse(responseData.body);
  } catch (err) {
    console.error("Error: Unable to parse JSON response.");
    process.exit(1);
  }

  // Extract and display accounts with their available balance
  if (!Array.isArray(data.accounts) || data.accounts.length === 0) {
    console.error("Error: No accounts found in the response.");
    process.exit(1);
  }

  console.log("\nAvailable accounts:");
  // Build an array of account IDs for selection
  const accounts = data.accounts;
  accounts.forEach((acct, index) => {
    console.log(
      `${index + 1}) ${acct.id} (Balance: ${acct.availableBtc} BTC)`
    );
  });

  let selectedAccountId;
  if (accounts.length === 1) {
    selectedAccountId = accounts[0].id;
  } else {
    // Prompt for account selection
    const answer = await askQuestion(
      `\nSelect account number (1-${accounts.length}): `
    );
    const accountNum = parseInt(answer, 10);
    if (
      isNaN(accountNum) ||
      accountNum < 1 ||
      accountNum > accounts.length
    ) {
      console.error("Invalid selection");
      process.exit(1);
    }
    selectedAccountId = accounts[accountNum - 1].id;
  }

  // Output final configuration
  console.log("\nConnection configuration for your custom Lightning node:\n");
  console.log(
    `type=barebitcoin;private-key=${secretKey};public-key=${publicKey};account-id=${selectedAccountId}`
  );
}

main().catch((err) => {
  console.error("An unexpected error occurred:", err);
  process.exit(1);
});