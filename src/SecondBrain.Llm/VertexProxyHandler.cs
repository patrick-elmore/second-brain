using System.Net;

namespace SecondBrain.Llm;

/// <summary>
/// Forces every outbound request's authority (scheme/host/port) to a target proxy URL,
/// preserving the SDK-built path and query.
///
/// Why this exists: the Vertex SDK's BeforeSend hook rewrites the request URI from
/// only <c>Scheme + Host</c> (see <c>AnthropicVertexClientWithRawResponse.BeforeSend</c>),
/// dropping any custom port. Setting <c>ClientOptions.BaseUrl</c> with a non-default
/// port (e.g. <c>http://localhost:9996</c>) silently produces requests to port 80/443.
/// This handler intercepts after the SDK is done composing and re-applies the full
/// authority on its way to the wire.
/// </summary>
internal sealed class VertexProxyHandler : DelegatingHandler
{
    private readonly Uri _proxyAuthority;
    private readonly bool _sendBypassHeader;

    public VertexProxyHandler(string proxyUrl, HttpMessageHandler innerHandler, bool sendBypassHeader = false)
        : base(innerHandler)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            throw new ArgumentException("proxyUrl is required", nameof(proxyUrl));
        _proxyAuthority = new Uri(proxyUrl);
        _sendBypassHeader = sendBypassHeader;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } original)
        {
            request.RequestUri = new UriBuilder(original)
            {
                Scheme = _proxyAuthority.Scheme,
                Host = _proxyAuthority.Host,
                Port = _proxyAuthority.Port,
            }.Uri;
        }

        if (_sendBypassHeader)
            request.Headers.TryAddWithoutValidation("X-CC-Proxy-Bypass", "1");

        return base.SendAsync(request, cancellationToken);
    }
}
