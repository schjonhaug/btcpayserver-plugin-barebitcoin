#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.BareBitcoin.Services;

/// <summary>
/// Service responsible for making authenticated requests to the BareBitcoin API.
/// Supports both HMAC-based authentication and simple API key authentication.
/// </summary>
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

    /// <summary>
    /// Initializes a new instance of the BareBitcoinApiService.
    /// </summary>
    /// <param name="privateKey">The private key used for HMAC authentication</param>
    /// <param name="publicKey">The public key (API key) used for all requests</param>
    /// <param name="httpClient">The HTTP client to use for requests</param>
    /// <param name="logger">Logger for request and error tracking</param>
    /// <param name="httpContextAccessor">Accessor for store context information</param>
    public BareBitcoinApiService(string privateKey, string publicKey, HttpClient httpClient, ILogger logger, IHttpContextAccessor httpContextAccessor)
    {
        _privateKey = privateKey;
        _publicKey = publicKey;
        _httpClient = httpClient;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Gets the next nonce value for HMAC authentication in a thread-safe manner.
    /// The nonce is guaranteed to be monotonically increasing and greater than the current timestamp.
    /// </summary>
    /// <returns>The next nonce value to use</returns>
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

    /// <summary>
    /// Creates an HMAC signature for request authentication.
    /// The signature is created by combining the method, path, nonce, and optional data.
    /// </summary>
    /// <param name="secret">The private key to use for signing</param>
    /// <param name="method">The HTTP method of the request</param>
    /// <param name="path">The API endpoint path</param>
    /// <param name="nonce">The current nonce value</param>
    /// <param name="data">Optional request body data to include in the signature</param>
    /// <returns>Base64 encoded HMAC signature</returns>
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

    /// <summary>
    /// Makes an authenticated request to the BareBitcoin API.
    /// Supports both HMAC authentication and simple API key authentication.
    /// </summary>
    /// <param name="method">The HTTP method to use</param>
    /// <param name="path">The API endpoint path</param>
    /// <param name="data">Optional request body data</param>
    /// <param name="useSimpleAuth">If true, uses only API key authentication. If false, uses full HMAC authentication</param>
    /// <returns>The response content as a string</returns>
    /// <remarks>
    /// When using HMAC authentication (useSimpleAuth = false), this method:
    /// - Generates a nonce
    /// - Creates an HMAC signature
    /// - Adds required authentication headers (x-bb-api-hmac, x-bb-api-nonce)
    /// 
    /// When using simple authentication (useSimpleAuth = true), this method:
    /// - Only uses the API key header
    /// 
    /// In both cases, the API key (x-bb-api-key) and trace headers are included.
    /// </remarks>
    public async Task<string> MakeAuthenticatedRequest(string method, string path, string? data = null, bool useSimpleAuth = false)
    {
        if (useSimpleAuth)
        {
            _logger.LogInformation("Making simple authenticated {method} request to {path} using API key only", method, path);
            
            return await MakeRequest(method, path, data);
        }
        
        await _requestLock.WaitAsync();
        try 
        {
            var nonce = await GetNextNonce();
            var hmac = CreateHmac(_privateKey, method, path, nonce, data);
            
            var additionalHeaders = new Dictionary<string, string>
            {
                ["x-bb-api-hmac"] = hmac,
                ["x-bb-api-nonce"] = nonce.ToString()
            };

            _logger.LogInformation("Making {method} request to {path} with nonce {nonce}", method, path, nonce);
            return await MakeRequest(method, path, data, additionalHeaders);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// Makes an HTTP request to the BareBitcoin API with the specified parameters and headers.
    /// This is the core request method that handles the actual HTTP communication.
    /// </summary>
    /// <param name="method">The HTTP method to use</param>
    /// <param name="path">The API endpoint path</param>
    /// <param name="data">Optional request body data</param>
    /// <param name="additionalHeaders">Optional additional headers to include in the request</param>
    /// <returns>The response content as a string</returns>
    /// <remarks>
    /// This method:
    /// - Creates the HTTP request with the specified method and URL
    /// - Adds the API key header
    /// - Adds any additional headers provided
    /// - Adds trace information
    /// - Handles request body data for non-GET requests
    /// - Executes the request and processes the response
    /// - Handles error logging and status code checking
    /// </remarks>
    private async Task<string> MakeRequest(string method, string path, string? data = null, Dictionary<string, string>? additionalHeaders = null)
    {
        try 
        {
            var requestUrl = $"https://api.bb.no{path}";
            var request = new HttpRequestMessage(new HttpMethod(method), requestUrl);
            
            // Add API key header
            request.Headers.Add("x-bb-api-key", _publicKey);

            // Add any additional headers
            if (additionalHeaders != null)
            {
                foreach (var header in additionalHeaders)
                {
                    request.Headers.Add(header.Key, header.Value);
                }
            }

            // Get store ID from current store context for tracing
            var storeData = _httpContextAccessor.HttpContext?.GetStoreData();
            var storeId = storeData?.Id ?? "unknown";
            request.Headers.Add("x-bb-trace", $"{storeId}+{Guid.NewGuid()}");

            foreach (var header in request.Headers)
            {
                _logger.LogDebug("Request header: {HeaderName}: {HeaderValue}", header.Key, header.Value);
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
    }
} 