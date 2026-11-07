#!/usr/bin/env node
"use strict";

const readline = require("readline");
const https = require("https");

// Use Node’s built‐in promises API for readline.
function askQuestion(query) {
  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
  });
  return new Promise((resolve, reject) =>
    {
      let answered = false;
      rl.once("close", () => { if (!answered) reject(new Error("Input closed before an answer was supplied")); });
      rl.once("SIGINT", () => rl.close());
      rl.question(query, (ans) => {
        answered = true;
        resolve(ans.trim());
        rl.close();
      });
    }
  );
}

function getAccountName(account) {
  const name = account.name || account.displayName || account.label;
  return typeof name === "string" && name.trim() ? name.trim() : null;
}

function requestAccounts(options, request = https.request, timeoutMs = 15000) {
  return new Promise((resolve, reject) => {
    let timer;
    const req = request(options, (res) => {
      let data = "";
      res.setEncoding("utf8");
      res.on("data", chunk => {
        data += chunk;
        if (data.length > 1024 * 1024) req.destroy(new Error("Account response is too large"));
      });
      res.once("error", reject);
      res.once("aborted", () => reject(new Error("Account response was interrupted")));
      res.once("end", () => resolve({ statusCode: res.statusCode, body: data }));
    });
    timer = setTimeout(() => req.destroy(new Error("Account request timed out")), timeoutMs);
    req.once("error", reject);
    req.once("close", () => {
      clearTimeout(timer);
      reject(new Error("Account request closed before a complete response"));
    });
    req.end();
  });
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

  // Set up API request options
  const options = {
    hostname: "api.bb.no",
    port: 443,
    path: path,
    method: method,
    headers: {
      "x-bb-api-key": publicKey,
    },
  };

  // Make HTTPS GET request
  const responseData = await requestAccounts(options);

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
    const name = getAccountName(acct);
    const balance = Number(acct.availableBtc) === 0 ? "0" : Number(acct.availableBtc).toFixed(8);
    console.log(
      `${index + 1}) ${name ? `${name} - ` : ""}${acct.id} (Balance: ${balance} BTC)`
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
    const accountNum = /^\d+$/.test(answer) ? Number(answer) : NaN;
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

  if (typeof selectedAccountId !== "string" || !selectedAccountId.trim() || selectedAccountId.includes(";"))
    throw new Error("The provider returned an invalid account ID");

  // Output final configuration
  console.log("\nConnection configuration for your custom Lightning node:\n");
  console.log(
    `type=barebitcoin;private-key=${secretKey};public-key=${publicKey};account-id=${selectedAccountId}`
  );
}

module.exports = { requestAccounts, askQuestion };
if (require.main === module) main().catch((err) => {
  console.error("An unexpected error occurred:", err);
  process.exit(1);
});
