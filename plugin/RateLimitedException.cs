#nullable enable
using System;
using System.Net;
using System.Net.Http;

namespace BTCPayServer.Plugins.BareBitcoin;

internal sealed class RateLimitedException : HttpRequestException
{
    public TimeSpan? RetryAfter { get; }

    public RateLimitedException(string message, TimeSpan? retryAfter)
        : base(message, null, HttpStatusCode.TooManyRequests)
    {
        RetryAfter = retryAfter;
    }
}
