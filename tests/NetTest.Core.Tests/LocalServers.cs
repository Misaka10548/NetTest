using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NetTest.Core.Tests;

/// <summary>最小 UDP DNS 服务器：响应 A/AAAA 记录或 NXDOMAIN，统计查询次数（验证无客户端缓存）。</summary>
internal sealed class LocalDnsServer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>域名（小写）-> A 记录。</summary>
    public Dictionary<string, IPAddress> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>返回 NXDOMAIN 的域名集合。</summary>
    public HashSet<string> NxDomains { get; } = new(StringComparer.OrdinalIgnoreCase);

    private int _queryCount;

    public int QueryCount => _queryCount;

    public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    public LocalDnsServer()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _loop = Task.Run(LoopAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        try
        {
            _loop?.Wait(1000);
        }
        catch (AggregateException)
        {
        }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                UdpReceiveResult result = await _udp.ReceiveAsync(_cts.Token);
                Interlocked.Increment(ref _queryCount);
                byte[] response = BuildResponse(result.Buffer);
                await _udp.SendAsync(response, result.RemoteEndPoint, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                // 忽略瞬态错误
            }
        }
    }

    private byte[] BuildResponse(byte[] query)
    {
        if (query.Length < 12)
        {
            return query;
        }

        // 解析 QNAME
        int offset = 12;
        var nameBuilder = new System.Text.StringBuilder();
        while (offset < query.Length && query[offset] != 0)
        {
            int labelLength = query[offset];
            offset++;
            if (offset + labelLength > query.Length)
            {
                return query;
            }

            if (nameBuilder.Length > 0)
            {
                nameBuilder.Append('.');
            }

            nameBuilder.Append(System.Text.Encoding.ASCII.GetString(query, offset, labelLength));
            offset += labelLength;
        }

        int questionEnd = offset + 1 + 4; // 0 结尾 + QTYPE + QCLASS
        string name = nameBuilder.ToString().ToLowerInvariant();

        ushort id = (ushort)((query[0] << 8) | query[1]);
        bool nxdomain = NxDomains.Contains(name);
        bool found = Records.TryGetValue(name, out IPAddress? address);

        byte[] answer;
        if (found)
        {
            // flags: 0x8180 (QR + RD + RA)，ANCOUNT=1
            answer = new byte[questionEnd + 16];
            // question 原样复制（先复制，再覆盖 header 字段）
            Array.Copy(query, 0, answer, 0, questionEnd);
            answer[2] = 0x81;
            answer[3] = 0x80;
            answer[6] = 0x00;
            answer[7] = 0x01;
            answer[8] = 0x00;
            answer[9] = 0x00;
            answer[10] = 0x00;
            answer[11] = 0x00;
            // answer: pointer 0xC00C
            answer[questionEnd] = 0xC0;
            answer[questionEnd + 1] = 0x0C;
            // type A
            answer[questionEnd + 2] = 0x00;
            answer[questionEnd + 3] = 0x01;
            // class IN
            answer[questionEnd + 4] = 0x00;
            answer[questionEnd + 5] = 0x01;
            // ttl 60
            answer[questionEnd + 6] = 0x00;
            answer[questionEnd + 7] = 0x00;
            answer[questionEnd + 8] = 0x00;
            answer[questionEnd + 9] = 0x3C;
            // rdlength 4
            answer[questionEnd + 10] = 0x00;
            answer[questionEnd + 11] = 0x04;
            // rdata
            byte[] ip = address!.GetAddressBytes();
            Array.Copy(ip, 0, answer, questionEnd + 12, 4);
        }
        else
        {
            // NXDOMAIN 或 NOERROR 无答案
            answer = new byte[questionEnd];
            Array.Copy(query, 0, answer, 0, questionEnd);
            answer[2] = 0x81;
            answer[3] = nxdomain ? (byte)0x83 : (byte)0x80;
            answer[8] = 0x00;
            answer[9] = 0x00;
            answer[10] = 0x00;
            answer[11] = 0x00;
        }

        answer[0] = (byte)(id >> 8);
        answer[1] = (byte)(id & 0xFF);
        return answer;
    }
}

/// <summary>Kestrel HTTPS 服务器（自签证书）用于探针测试。</summary>
internal sealed class LocalHttpsServer : IAsyncDisposable
{
    private readonly IHost _host;
    public string BaseUrl { get; }

    public LocalHttpsServer(Func<HttpContext, Task> handler, bool requireTls = true)
    {
        X509Certificate2 certificate = CreateSelfSignedCertificate();
        int port = GetFreePort();

        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, port, listen =>
                    {
                        listen.UseHttps(certificate);
                    });
                });
                web.Configure(app =>
                {
                    app.Run(new RequestDelegate(handler));
                });
            })
            .Build();

        BaseUrl = $"https://127.0.0.1:{port}/";
        _ = _host.StartAsync();
        // 等待服务器就绪
        Thread.Sleep(300);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(TimeSpan.FromSeconds(2));
        _host.Dispose();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));

        byte[] pfx = certificate.Export(X509ContentType.Pfx, "nettest-test");
        return X509CertificateLoader.LoadPkcs12(pfx, "nettest-test");
    }
}
