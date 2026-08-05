using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NetTest.Core.Configuration;
using NetTest.Core.Enums;

namespace NetTest.Core.Probes;

/// <summary>
/// HTTPS 探针：GET + ResponseHeadersRead + 读至 EOF 丢弃正文；阶段计时覆盖
/// DNS/TCP/TLS/首字节/完整读取；Direct 模式 ConnectCallback 强制连接选定 IP，
/// SNI/Host 保持原域名（TechSpec 6.5）。
/// </summary>
public sealed class HttpsProbe : IProbe
{
    public ProbeType Type => ProbeType.Https;

    public async Task<ProbeMeasurement> ExecuteAsync(
        ProbeExecutionContext context,
        CancellationToken cancellationToken)
    {
        var config = (HttpsProbeConfiguration)context.Configuration.Configuration;
        var metrics = new HttpsMetricsV1();
        var total = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(config.TimeoutMs));
        CancellationToken token = timeoutCts.Token;

        bool timedOut = false;

        try
        {
            Uri currentUri = new(config.Url);
            IPAddress? connectTarget = null;

            if (config.Proxy.Mode == ProxyMode.Direct)
            {
                // DNS 阶段：探针内部解析（计时）；连接强制使用上下文选定的地址。
                var dnsWatch = Stopwatch.StartNew();
                connectTarget = context.ResolvedAddress ?? await ResolveFirstAsync(currentUri.Host, token);
                dnsWatch.Stop();
                metrics.DnsMs = dnsWatch.ElapsedMilliseconds;
            }

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = config.Proxy.Mode != ProxyMode.Direct,
                Proxy = BuildProxy(config.Proxy),
                ConnectTimeout = TimeSpan.FromMilliseconds(config.TimeoutMs),
                AutomaticDecompression = DecompressionMethods.None,
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = config.AllowInvalidCertificate
                        ? static (_, _, _, _) => true
                        : null!,
                },
                ConnectCallback = async (request, ct) =>
                {
                    if (config.Proxy.Mode == ProxyMode.Direct)
                    {
                        IPAddress target = connectTarget ?? await ResolveFirstAsync(request.DnsEndPoint.Host, ct);
                        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        var tcpWatch = Stopwatch.StartNew();
                        try
                        {
                            await socket.ConnectAsync(target, request.DnsEndPoint.Port, ct);
                            tcpWatch.Stop();
                            metrics.TcpConnectMs = tcpWatch.ElapsedMilliseconds;

                            var sslStream = new SslStream(new NetworkStream(socket, ownsSocket: true));
                            var tlsWatch = Stopwatch.StartNew();
                            try
                            {
                                await sslStream.AuthenticateAsClientAsync(
                                    new SslClientAuthenticationOptions
                                    {
                                        TargetHost = request.DnsEndPoint.Host,
                                        RemoteCertificateValidationCallback = config.AllowInvalidCertificate
                                            ? static (_, _, _, _) => true
                                            : null!,
                                    },
                                    ct);
                                tlsWatch.Stop();
                                metrics.TlsHandshakeMs = tlsWatch.ElapsedMilliseconds;
                                CaptureCertificate(sslStream, metrics, config.AllowInvalidCertificate);
                                return sslStream;
                            }
                            catch
                            {
                                sslStream.Dispose();
                                throw;
                            }
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }

                    // 代理模式：handler 原生建立代理连接，仅记录 TCP 完成时间。
                    var proxyTcpWatch = Stopwatch.StartNew();
                    var proxySocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await proxySocket.ConnectAsync(request.DnsEndPoint.Host, request.DnsEndPoint.Port, ct);
                        proxyTcpWatch.Stop();
                        metrics.TcpConnectMs = proxyTcpWatch.ElapsedMilliseconds;
                        return new NetworkStream(proxySocket, ownsSocket: true);
                    }
                    catch
                    {
                        proxySocket.Dispose();
                        throw;
                    }
                },
            };

            using var httpClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan, // 总超时由 CancelAfter 控制
            };

            Uri uri = currentUri;
            int redirectCount = 0;

            while (true)
            {
                long requestStart = Stopwatch.GetTimestamp();
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using HttpResponseMessage response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    token);
                metrics.TimeToFirstByteMs = (long)Stopwatch.GetElapsedTime(requestStart).TotalMilliseconds;

                metrics.StatusCode = (int)response.StatusCode;
                metrics.FinalUri = StripQuery(uri.ToString());

                if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest
                    && response.Headers.Location is not null)
                {
                    if (redirectCount >= config.MaxRedirects)
                    {
                        metrics.Redirects.Add(new RedirectStep { StatusCode = (int)response.StatusCode, Location = StripQuery(response.Headers.Location.ToString()) });
                        return new ProbeMeasurement(
                            true,
                            ProbeOutcome.HttpError,
                            metrics.TotalMs,
                            ProbeMetrics.SchemaVersion,
                            Finalize(metrics, total),
                            "TooManyRedirects",
                            $"超过最大重定向次数 {config.MaxRedirects}。");
                    }

                    metrics.Redirects.Add(new RedirectStep { StatusCode = (int)response.StatusCode, Location = StripQuery(response.Headers.Location.ToString()) });
                    uri = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(uri, response.Headers.Location);
                    redirectCount++;
                    continue;
                }

                await ReadBodyAsync(response, config.MaxResponseBytes, metrics, token);
                break;
            }

            if (metrics.ResponseLimitReached)
            {
                return new ProbeMeasurement(
                    true,
                    ProbeOutcome.ResponseLimitExceeded,
                    metrics.TotalMs,
                    ProbeMetrics.SchemaVersion,
                    Finalize(metrics, total),
                    "ResponseLimitExceeded",
                    $"读取超过上限 {config.MaxResponseBytes} 字节。");
            }

            if (metrics.StatusCode is >= 200 and < 400)
            {
                return new ProbeMeasurement(
                    true,
                    ProbeOutcome.Success,
                    metrics.TotalMs,
                    ProbeMetrics.SchemaVersion,
                    Finalize(metrics, total),
                    null,
                    null);
            }

            return new ProbeMeasurement(
                true,
                ProbeOutcome.HttpError,
                metrics.TotalMs,
                ProbeMetrics.SchemaVersion,
                Finalize(metrics, total),
                $"Http{(int)metrics.StatusCode}",
                $"HTTP 状态码 {(int)metrics.StatusCode}。");
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
            }
        }
        catch (HttpRequestException ex)
        {
            if (ex.InnerException is AuthenticationException authException)
            {
                return new ProbeMeasurement(
                    true,
                    ProbeOutcome.TlsError,
                    metrics.TotalMs,
                    ProbeMetrics.SchemaVersion,
                    Finalize(metrics, total),
                    "TlsError",
                    $"TLS 校验失败：{authException.Message}");
            }

            if (ex.InnerException is SocketException socketException)
            {
                return MapSocketException(socketException, metrics, total);
            }

            return new ProbeMeasurement(
                true,
                ProbeOutcome.InternalError,
                metrics.TotalMs,
                ProbeMetrics.SchemaVersion,
                Finalize(metrics, total),
                "HttpRequestFailed",
                ex.Message);
        }
        catch (SocketException ex)
        {
            return MapSocketException(ex, metrics, total);
        }

        if (timedOut)
        {
            return new ProbeMeasurement(
                true,
                ProbeOutcome.NetworkTimeout,
                metrics.TotalMs,
                ProbeMetrics.SchemaVersion,
                Finalize(metrics, total),
                "Timeout",
                $"总超时 {config.TimeoutMs} ms。");
        }

        // 调度取消：返回已完成阶段的部分指标。
        return new ProbeMeasurement(
            false,
            ProbeOutcome.None,
            metrics.TotalMs,
            ProbeMetrics.SchemaVersion,
            Finalize(metrics, total),
            null,
            null);
    }

    private static ProbeMeasurement MapSocketException(SocketException ex, HttpsMetricsV1 metrics, Stopwatch total)
    {
        ProbeOutcome outcome = ex.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => ProbeOutcome.ConnectionRefused,
            SocketError.NetworkUnreachable or SocketError.HostUnreachable => ProbeOutcome.NetworkUnreachable,
            _ => ProbeOutcome.InternalError,
        };

        return new ProbeMeasurement(
            true,
            outcome,
            metrics.TotalMs,
            ProbeMetrics.SchemaVersion,
            Finalize(metrics, total),
            ex.SocketErrorCode.ToString(),
            $"连接失败：{ex.Message}");
    }

    private static async Task ReadBodyAsync(
        HttpResponseMessage response,
        long maxResponseBytes,
        HttpsMetricsV1 metrics,
        CancellationToken token)
    {
        var downloadWatch = Stopwatch.StartNew();
        long bytesRead = 0;
        bool limitReached = false;
        byte[] buffer = new byte[81920];

        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token);
            if (read <= 0)
            {
                break;
            }

            bytesRead += read;
            if (bytesRead >= maxResponseBytes)
            {
                limitReached = true;
                break;
            }
        }

        downloadWatch.Stop();
        metrics.BytesRead = bytesRead;
        metrics.ResponseLimitReached = limitReached;
        metrics.DownloadMs = downloadWatch.ElapsedMilliseconds;
    }

    private static HttpsMetricsV1 Finalize(HttpsMetricsV1 metrics, Stopwatch total)
    {
        metrics.TotalMs = total.ElapsedMilliseconds;
        return metrics;
    }

    private static void CaptureCertificate(SslStream sslStream, HttpsMetricsV1 metrics, bool allowInvalidCertificate)
    {
        if (sslStream.RemoteCertificate is null)
        {
            return;
        }

        using var certificate = new X509Certificate2(sslStream.RemoteCertificate);
        metrics.CertificateExpiresAtUtc = certificate.NotAfter.ToUniversalTime();
        metrics.CertificateInvalid = allowInvalidCertificate;
    }

    /// <summary>
    /// 剥离 URI 的 query 和 fragment，防止敏感查询参数进入 metrics 并随导出泄露
    /// （SystemDesign 10 / TechSpec 8.3：URL 只保留 scheme、host、port 和 path）。
    /// 对相对 Location 同样适用。
    /// </summary>
    private static string StripQuery(string value)
    {
        int index = value.IndexOfAny(['?', '#']);
        return index >= 0 ? value[..index] : value;
    }

    private static IWebProxy? BuildProxy(ProxyConfiguration proxy)
    {
        return proxy.Mode switch
        {
            ProxyMode.System => HttpClient.DefaultProxy,
            ProxyMode.Custom when Uri.TryCreate(proxy.Url, UriKind.Absolute, out Uri? uri) =>
                new WebProxy(uri)
                {
                    Credentials = proxy.Username is not null
                        ? new NetworkCredential(proxy.Username, proxy.Password)
                        : null,
                },
            _ => null,
        };
    }

    private static async Task<IPAddress> ResolveFirstAsync(string host, CancellationToken token)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return literal;
        }

        IPAddress[] addresses = await System.Net.Dns.GetHostAddressesAsync(host, token);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return addresses[0];
    }
}
