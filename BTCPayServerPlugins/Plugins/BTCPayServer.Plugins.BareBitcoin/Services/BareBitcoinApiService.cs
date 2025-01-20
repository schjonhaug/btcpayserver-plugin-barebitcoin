#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using BTCPayServer.Abstractions.Extensions;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

public class BareBitcoinApiService
{
    private readonly string _privateKey;
    private readonly string _publicKey;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    // Static nonce tracking with async-compatible lock
    private static readonly SemaphoreSlim _nonceLock = new SemaphoreSlim(1, 1);
    private static readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);
    private static long _lastNonce;

    public BareBitcoinApiService(string privateKey, string publicKey, HttpClient httpClient, ILogger logger, IHttpContextAccessor httpContextAccessor)
    {
        _privateKey = privateKey;
        _publicKey = publicKey;
        _httpClient = httpClient;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    private async Task<long> GetNextNonce()
    {
        await _nonceLock.WaitAsync();
        try
        {
            var currentTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _lastNonce = Math.Max(_lastNonce + 1, currentTimestamp);
            return _lastNonce;
        }
        finally
        {
            _nonceLock.Release();
        }
    }

    private string CreateHmac(string secret, string method, string path, long nonce, string? data = null)
    {
        try 
        {
            // Convert millisecond nonce to string
            var nonceStr = nonce.ToString();
            // Encode data: nonce and raw data
            var encodedData = data != null ? $"{nonceStr}{data}" : nonceStr;

            // SHA-256 hash of the encoded data
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var hashedData = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(encodedData));

            // Concatenate method, path, and hashed data
            var message = new List<byte>();
            message.AddRange(System.Text.Encoding.UTF8.GetBytes(method));
            message.AddRange(System.Text.Encoding.UTF8.GetBytes(path));
            message.AddRange(hashedData);

            // Decode secret from base64
            var decodedSecret = Convert.FromBase64String(secret);

            // Generate HMAC
            using var hmac = new System.Security.Cryptography.HMACSHA256(decodedSecret);
            var macsum = hmac.ComputeHash(message.ToArray());
            return Convert.ToBase64String(macsum);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating HMAC signature");
            throw;
        }
    }

    public async Task<string> MakeAuthenticatedRequest(string method, string path, string? data = null)
    {
        await _requestLock.WaitAsync();
        try 
        {
            var nonce = await GetNextNonce();

            _logger.LogInformation("Making {method} request to {path} with nonce {nonce}", method, path, nonce);

            var hmac = CreateHmac(_privateKey, method, path, nonce, data);
            
            var requestUrl = $"https://api.bb.no{path}";
            var request = new HttpRequestMessage(new HttpMethod(method), requestUrl);
            request.Headers.Add("x-bb-api-hmac", hmac);
            request.Headers.Add("x-bb-api-key", _publicKey);
            request.Headers.Add("x-bb-api-nonce", nonce.ToString());

            // Get store ID from current store context
            var storeData = _httpContextAccessor.HttpContext?.GetStoreData();
            var storeId = storeData?.Id ?? "unknown";
            request.Headers.Add("x-bb-trace", $"{storeId}+{Guid.NewGuid()}");

            foreach (var header in request.Headers)
            {
                _logger.LogInformation("Request header: {HeaderName}: {HeaderValue}", header.Key, header.Value);
            }

            if (data != null && method != "GET")
            {
                request.Content = new StringContent(data, System.Text.Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.SendAsync(request);
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Request failed with status {StatusCode}. Response body: {Body}", 
                    response.StatusCode, responseContent);
                response.EnsureSuccessStatusCode();
            }
            
            return responseContent;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP request failed");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error making authenticated request");
            throw;
        }
        finally
        {
            _requestLock.Release();
        }
    }
} 