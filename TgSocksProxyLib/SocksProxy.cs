using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.ComponentModel.Design;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TgSocksProxy;

/// <summary>
/// Полнофункциональный Socks5 прокси-сервер, работающий через upstream HTTPS-прокси.
/// Реализует RFC 1928 (Socks5) с поддержкой аутентификации (No Auth / Username/Password).
/// Все исходящие соединения туннелируются через указанный HTTPS-прокси методом CONNECT.
/// </summary>
public sealed class SocksProxy : IDisposable, IAsyncDisposable
{
    public SocksProxyOptions Options { get; init; }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly ConcurrentDictionary<string, DnsCacheEntry> _dnsCache = new();
    private volatile bool _disposed;
    private volatile int _connectionCount;
    private ulong _uploadBytes; //отправлено в интернет от клиента
    private ulong _downloadBytes; //получено из интернета и передано клиенту

    public int ConnectionCount => _connectionCount;
    public ulong UploadBytes => _uploadBytes;
    public ulong DownloadBytes => _downloadBytes;

    /// <summary>Подключение установлено.</summary>
    public event Action<SocksProxy, SocksClientEventArgs>? ClientConnected;

    /// <summary>Подключение закрыто.</summary>
    public event Action<SocksProxy, SocksClientEventArgs>? ClientDisconnected;

    public SocksProxy(SocksProxyOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Options.Validate();

        _listener = new TcpListener(options.LocalEndPoint);
        _cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Запускает Socks5-сервер. Не блокирует вызывающий поток.
    /// </summary>
    public void Start()
    {
        ThrowIfDisposed();
        _listener.Start(Options.Backlog);
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Останавливает сервер и освобождает ресурсы.
    /// </summary>
    public void Stop()
    {
        if (_disposed) return;
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _cts.Dispose();
        _disposed = true;
        await Task.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SocksProxy));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptSocketAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Options.Logger?.LogError(ex, "Accept loop error");
        }
    }

    // ── Основной цикл обработки клиента ──────────────────────────────

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        using var _ = client;
        using var clientStream = new NetworkStream(client, ownsSocket: false);
        try
        {
            // Шаг 1: Socks5 Handshake
            var authResult = await PerformHandshakeAsync(client, ct).ConfigureAwait(false);
            if (!authResult)
            {
                Options.Logger?.LogWarning("Handshake failed");
                return;
            }

            // Шаг 2: Socks5 Request (получаем целевой адрес)
            var target = await ReadSocks5RequestAsync(client, ct).ConfigureAwait(false);
            if (target is null)
            {
                Options.Logger?.LogWarning("Invalid request received");
                return;
            }

            Options.Logger?.LogInformation("Connect request to {Host}:{Port}", target.Host, target.Port);

            var clientEp = (IPEndPoint?)client.RemoteEndPoint;

            // Шаг 3: Подключаемся к upstream HTTPS-прокси
            using var upstreamStream = await ConnectViaHttpsProxyAsync(target.Host, target.Port, ct).ConfigureAwait(false);

            // Успех – сообщаем клиенту
            await SendSocks5ReplyAsync(client, 0, IPAddress.Any, 0, ct).ConfigureAwait(false);

            Interlocked.Increment(ref _connectionCount);
            var count = _connectionCount;
            Options.Logger?.LogInformation("Connected {count} clients", count);

            ClientConnected?.Invoke(this, new SocksClientEventArgs(target.Host, target.Port, clientEp?.Port ?? 0, count));

            // Шаг 4: Двунаправленная пересылка данных
            await RelayAsync(clientStream, upstreamStream, ct).ConfigureAwait(false);

            Interlocked.Decrement(ref _connectionCount);
            count = _connectionCount;
            Options.Logger?.LogInformation("Disconnected. Now {count} clients", count);

            ClientDisconnected?.Invoke(this, new SocksClientEventArgs(target.Host, target.Port, clientEp?.Port ?? 0, count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Options.Logger?.LogError(ex, "Client handler error");
        }
    }

    // ── Socks5 Handshake (RFC 1928, раздел 3) ────────────────────────

    /// <returns>true если handshake успешен</returns>
    private async Task<bool> PerformHandshakeAsync(Socket client, CancellationToken ct)
    {
        var buffer = new byte[258]; // версия (1) + nmethods (1) + до 256 методов

        // Читаем: VER | NMETHODS | METHODS[1..255]
        int read = await ReceiveExactAsync(client, buffer.AsMemory(0, 2), ct).ConfigureAwait(false);
        if (read != 2) return false;

        byte ver = buffer[0];
        byte nmethods = buffer[1];
        if (ver != 5 || nmethods == 0) return false;

        read = await ReceiveExactAsync(client, buffer.AsMemory(2, nmethods), ct).ConfigureAwait(false);
        if (read != nmethods) return false;

        // Клиент отправляет список методов. Выбираем:
        bool supportsNoAuth = false;
        bool supportsUserPass = false;
        for (int i = 0; i < nmethods; i++)
        {
            if (buffer[2 + i] == 0x00) supportsNoAuth = true;
            if (buffer[2 + i] == 0x02) supportsUserPass = true;
        }

        if (Options.Credentials is not null && supportsUserPass)
        {
            // Требуем Username/Password
            await client.SendAsync(new byte[] { 5, 2 }, SocketFlags.None, ct).ConfigureAwait(false);
            return await PerformUserPassAuthAsync(client, ct).ConfigureAwait(false);
        }
        else if (Options.Credentials is not null && !supportsUserPass)
        {
            // Требуется аутентификация, но клиент не поддерживает – отказ
            await client.SendAsync(new byte[] { 5, 0xFF }, SocketFlags.None, ct).ConfigureAwait(false);
            return false;
        }
        else if (supportsNoAuth)
        {
            await client.SendAsync(new byte[] { 5, 0 }, SocketFlags.None, ct).ConfigureAwait(false);
            return true;
        }
        else
        {
            await client.SendAsync(new byte[] { 5, 0xFF }, SocketFlags.None, ct).ConfigureAwait(false);
            return false;
        }
    }

    private async Task<bool> PerformUserPassAuthAsync(Socket client, CancellationToken ct)
    {
        // Формат субпереговоров (RFC 1929):
        // VER (1) | ULEN (1) | UNAME (1..255) | PLEN (1) | PASSWD (1..255)

        var buffer = new byte[513];
        int read = await ReceiveExactAsync(client, buffer.AsMemory(0, 2), ct).ConfigureAwait(false);
        if (read != 2) return false;

        byte authVer = buffer[0]; // должно быть 1
        byte ulen = buffer[1];
        if (authVer != 1 || ulen == 0 || ulen > 255) return false;

        read = await ReceiveExactAsync(client, buffer.AsMemory(2, ulen), ct).ConfigureAwait(false);
        if (read != ulen) return false;
        string username = Encoding.UTF8.GetString(buffer, 2, ulen);

        read = await ReceiveExactAsync(client, buffer.AsMemory(2 + ulen, 1), ct).ConfigureAwait(false);
        if (read != 1) return false;
        byte plen = buffer[2 + ulen];
        if (plen > 255) return false;

        read = await ReceiveExactAsync(client, buffer.AsMemory(2 + ulen + 1, plen), ct).ConfigureAwait(false);
        if (read != plen) return false;
        string password = Encoding.UTF8.GetString(buffer, 2 + (int)ulen + 1, plen);

        bool ok = Options.Credentials?.Username == username
               && Options.Credentials?.Password == password;

        // Ответ: VER | STATUS (0=OK, иначе ошибка)
        await client.SendAsync(new byte[] { 1, (byte)(ok ? 0 : 1) }, SocketFlags.None, ct).ConfigureAwait(false);
        return ok;
    }

    // ── Socks5 Request (RFC 1928, раздел 4) ──────────────────────────

    private async Task<Socks5Target?> ReadSocks5RequestAsync(Socket client, CancellationToken ct)
    {
        var buffer = new byte[262]; // VER(1) CMD(1) RSV(1) ATYP(1) + max addr(255+2)

        int read = await ReceiveExactAsync(client, buffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        if (read != 4) return null;

        byte ver = buffer[0];
        byte cmd = buffer[1];
        byte atyp = buffer[3];

        // Поддерживаем только CONNECT (cmd = 1)
        if (ver != 5 || cmd != 1)
        {
            await SendSocks5ReplyAsync(client, 0x07, IPAddress.Any, 0, ct).ConfigureAwait(false); // Command not supported
            return null;
        }

        (string host, int bytesRead) = atyp switch
        {
            1 => await ReadIPv4AddressAsync(client, buffer, ct).ConfigureAwait(false),   // IPv4
            3 => await ReadDomainAddressAsync(client, buffer, ct).ConfigureAwait(false),  // Domain name
            4 => await ReadIPv6AddressAsync(client, buffer, ct).ConfigureAwait(false),   // IPv6
            _ => (string.Empty, 0)
        };

        if (string.IsNullOrEmpty(host))
        {
            await SendSocks5ReplyAsync(client, 0x08, IPAddress.Any, 0, ct).ConfigureAwait(false); // Address type not supported
            return null;
        }

        // Читаем порт (2 байта)
        var portBuf = new byte[2];
        read = await ReceiveExactAsync(client, portBuf, ct).ConfigureAwait(false);
        if (read != 2) return null;
        ushort port = (ushort)((portBuf[0] << 8) | portBuf[1]);

        return new Socks5Target(host, port);
    }

    private static async Task<(string Host, int BytesRead)> ReadIPv4AddressAsync(Socket client, byte[] buffer, CancellationToken ct)
    {
        int read = await ReceiveExactAsync(client, buffer.AsMemory(0, 4), ct).ConfigureAwait(false);
        if (read != 4) return (string.Empty, 0);
        var addr = $"{buffer[0]}.{buffer[1]}.{buffer[2]}.{buffer[3]}";
        return (addr, 4);
    }

    private static async Task<(string Host, int BytesRead)> ReadDomainAddressAsync(Socket client, byte[] buffer, CancellationToken ct)
    {
        var lenBuf = new byte[1];
        int read = await ReceiveExactAsync(client, lenBuf, ct).ConfigureAwait(false);
        if (read != 1) return (string.Empty, 0);

        int length = lenBuf[0];
        if (length == 0) return (string.Empty, 0);

        read = await ReceiveExactAsync(client, buffer.AsMemory(0, length), ct).ConfigureAwait(false);
        if (read != length) return (string.Empty, 0);
        var host = Encoding.ASCII.GetString(buffer, 0, length);
        return (host, length + 1);
    }

    private static async Task<(string Host, int BytesRead)> ReadIPv6AddressAsync(Socket client, byte[] buffer, CancellationToken ct)
    {
        int read = await ReceiveExactAsync(client, buffer.AsMemory(0, 16), ct).ConfigureAwait(false);
        if (read != 16) return (string.Empty, 0);

        var addr = new IPAddress(buffer.AsSpan(0, 16)).ToString();
        return (addr, 16);
    }

    private static async Task SendSocks5ReplyAsync(Socket client, byte reply, IPAddress bindAddr, ushort bindPort, CancellationToken ct)
    {
        var bindBytes = bindAddr.MapToIPv4().GetAddressBytes();
        var replyPacket = new byte[10];
        replyPacket[0] = 5;       // VER
        replyPacket[1] = reply;   // REP
        replyPacket[2] = 0;       // RSV
        replyPacket[3] = 1;       // ATYP = IPv4
        Array.Copy(bindBytes, 0, replyPacket, 4, 4);
        replyPacket[8] = (byte)(bindPort >> 8);
        replyPacket[9] = (byte)(bindPort & 0xFF);

        await client.SendAsync(replyPacket, SocketFlags.None, ct).ConfigureAwait(false);
    }

    // ── HTTPS CONNECT к upstream прокси ──────────────────────────────

    /// <summary>
    /// Резолвит DNS с retry, IPv4-only и DoH-фолбеком.
    /// </summary>
    private async Task<IPAddress[]> ResolveUpstreamHostAsync(string host, CancellationToken ct)
    {
        // Проверяем кэш
        if (_dnsCache.TryGetValue(host, out var cached) && cached.Expiry > DateTime.UtcNow)
        {
            Options.Logger?.LogDebug("DNS cache hit for '{Host}'", host);
            return cached.Addresses;
        }


        Func<IPAddress[], IPAddress[]> cacheAndReturn = (addresses) =>
        {
            _dnsCache[host] = new DnsCacheEntry(addresses, DateTime.UtcNow.Add(Options.DnsCacheTtl));
            return addresses.OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetworkV6 ? 1 : 0).ToArray();
        };

        // Попытка 1–3: системный DNS (IPv4 only), с retry и backoff
        int[] delays = { 0, 500, 1000 };
        for (int i = 0; i < 3; i++)
        {
            if (i > 0) await Task.Delay(delays[i], ct).ConfigureAwait(false);
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, ct)
                    .ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    Options.Logger?.LogDebug("System DNS resolved '{Host}' (attempt {Attempt})", host, i + 1);

                    return cacheAndReturn(addresses);
                }
            }
            catch (SocketException ex)
            {
                Options.Logger?.LogWarning(ex, "System DNS attempt {Attempt} failed for '{Host}'", i + 1, host);
            }
        }

        // Фолбек: DNS-over-HTTPS через Cloudflare по IP (1.1.1.1), без DNS-зависимости
        Options.Logger?.LogInformation("Falling back to DNS-over-HTTPS for '{Host}' (via 1.1.1.1:443)", host);
        try
        {
            var addresses = await ResolveViaDohOverIpAsync(host, ct).ConfigureAwait(false);
            if (addresses.Length > 0)
            {
                Options.Logger?.LogInformation("DoH resolved '{Host}' to {Addresses}", host,
                    string.Join(", ", addresses.Select(a => a.ToString())));
                return cacheAndReturn(addresses);

            }
        }
        catch (Exception ex)
        {
            Options.Logger?.LogError(ex, "DNS-over-HTTPS fallback failed for '{Host}'", host);
        }

        throw new InvalidOperationException(
            $"All DNS resolution methods failed for '{host}'. Check device network.");
    }

    /// <summary>
    /// DNS-over-HTTPS через Cloudflare напрямую по IP (1.1.1.1:443), не требует DNS.
    /// </summary>
    private static async Task<IPAddress[]> ResolveViaDohOverIpAsync(string host, CancellationToken ct)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.1.1.1"), 443), ct).ConfigureAwait(false);

        using var networkStream = new NetworkStream(socket, ownsSocket: true);
        using var ssl = new SslStream(networkStream, leaveInnerStreamOpen: false,
            (_, _, _, _) => true);
        await ssl.AuthenticateAsClientAsync("cloudflare-dns.com").ConfigureAwait(false);

        string request = $"GET /dns-query?name={Uri.EscapeDataString(host)}&type=A HTTP/1.1\r\n" +
                         "Host: cloudflare-dns.com\r\n" +
                         "Accept: application/dns-json\r\n" +
                         "Connection: close\r\n\r\n";

        var requestBytes = Encoding.ASCII.GetBytes(request);
        await ssl.WriteAsync(requestBytes, ct).ConfigureAwait(false);
        await ssl.FlushAsync(ct).ConfigureAwait(false);

        // Читаем ответ, пропускаем HTTP-заголовки
        using var reader = new StreamReader(ssl, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);

        // Пропускаем строки заголовков до пустой строки
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (line.Length == 0) break; // конец заголовков
        }

        // Читаем JSON-тело
        string? body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body)) return Array.Empty<IPAddress>();

        using var json = JsonDocument.Parse(body);
        var answers = json.RootElement.GetProperty("Answer");
        var result = new List<IPAddress>();
        foreach (var answer in answers.EnumerateArray())
        {
            if (answer.GetProperty("type").GetInt32() == 1)
            {
                string? ip = answer.GetProperty("data").GetString();
                if (ip is not null && IPAddress.TryParse(ip, out var addr))
                    result.Add(addr);
            }
        }

        return result.ToArray();
    }

    /// <summary>
    /// Устанавливает TCP-соединение через upstream HTTPS-прокси методом CONNECT.
    /// </summary>
    private async Task<Stream> ConnectViaHttpsProxyAsync(string targetHost, ushort targetPort, CancellationToken ct)
    {
        var upstreamHost = Options.UpstreamProxyEndPoint.Host;
        var upstreamPort = Options.UpstreamProxyEndPoint.Port;

        IPAddress[] addresses = await ResolveUpstreamHostAsync(upstreamHost, ct).ConfigureAwait(false);

        Options.Logger?.LogInformation("Resolved upstream proxy '{Host}' to {Addresses}",
            upstreamHost, string.Join(", ", addresses.Select(a => a.ToString())));

        var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);

        foreach (var addr in addresses)
        {
            try
            {
                await upstream.ConnectAsync(new IPEndPoint(addr, upstreamPort), ct).ConfigureAwait(false);
                Options.Logger?.LogInformation("Connected proxy to {Address}", addr);

                break;
            }
            catch (SocketException ex) when (addr != addresses.Last())
            {
                Options.Logger?.LogWarning(ex, "Failed to connect to {Address}, trying next", addr);
            }
        }

        if (!upstream.Connected)
        {
            upstream.Dispose();
            throw new InvalidOperationException($"Failed to connect to upstream proxy '{upstreamHost}:{upstreamPort}'");
        }

        // Оборачиваем в TLS, если включено
        Stream upstreamStream = new NetworkStream(upstream, ownsSocket: true);
        if (Options.UseTls)
        {
            var sslStream = new SslStream(upstreamStream, leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);
            await sslStream.AuthenticateAsClientAsync(upstreamHost).ConfigureAwait(false);
            upstreamStream = sslStream;
        }

        // Формируем запрос CONNECT (IPv6 адреса в квадратных скобках)
        string formattedTarget = IPAddress.TryParse(targetHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{targetHost}]:{targetPort}"
            : $"{targetHost}:{targetPort}";
        string connectRequest = $"CONNECT {formattedTarget} HTTP/1.1\r\n" +
                                $"Host: {formattedTarget}\r\n";

        if (Options.UpstreamCredentials is not null)
        {
            string credential = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{Options.UpstreamCredentials.Username}:{Options.UpstreamCredentials.Password}"));
            connectRequest += $"Proxy-Authorization: Basic {credential}\r\n";
        }

        connectRequest += "\r\n";

        var requestBytes = Encoding.ASCII.GetBytes(connectRequest);
        await upstreamStream.WriteAsync(requestBytes, ct).ConfigureAwait(false);
        await upstreamStream.FlushAsync(ct).ConfigureAwait(false);

        // Читаем ответ
        var responseBuffer = new byte[4096];
        int totalRead = 0;

        while (totalRead < responseBuffer.Length)
        {
            int read = await upstreamStream.ReadAsync(
                responseBuffer.AsMemory(totalRead), ct).ConfigureAwait(false);
            if (read == 0) break;

            totalRead += read;
            string response = Encoding.ASCII.GetString(responseBuffer, 0, totalRead);

            int headerEnd = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0)
            {
                response = response[..headerEnd];
                if (response.StartsWith("HTTP/1.1 200") || response.StartsWith("HTTP/1.0 200"))
                {
                    return upstreamStream;
                }

                Options.Logger?.LogWarning("Upstream proxy rejected CONNECT: {StatusLine}, {connectRequest}", response.Split('\r', '\n')[0], connectRequest);
                upstreamStream.Dispose();
                throw new InvalidOperationException($"Upstream proxy CONNECT failed: {response.Split('\r', '\n')[0]}, requets: {connectRequest}");
            }
        }

        upstreamStream.Dispose();
        throw new InvalidOperationException("Upstream proxy did not return a complete HTTP response to CONNECT");
    }

    // ── Двунаправленная пересылка данных ─────────────────────────────

    private async Task RelayAsync(Stream client, Stream upstream, CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var t1 = CopyAsync(client, upstream, Direction.Upload, linkedCts);
        var t2 = CopyAsync(upstream, client, Direction.Download, linkedCts);

        var completed = await Task.WhenAny(t1, t2).ConfigureAwait(false);

        linkedCts.Cancel();

        try { await completed.ConfigureAwait(false); } catch { }
        try { await (completed == t1 ? t2 : t1).ConfigureAwait(false); } catch { }
    }

    private async Task CopyAsync(Stream from, Stream to, Direction direction, CancellationTokenSource linkedCts)
    {
        var buffer = new byte[8192];
        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                int read = await from.ReadAsync(buffer, linkedCts.Token).ConfigureAwait(false);
                if (read == 0) break;

                if (direction == Direction.Upload)
                {
                    Interlocked.Add(ref _uploadBytes, (ulong)read);
                }
                else
                {
                    Interlocked.Add(ref _downloadBytes, (ulong)read);

                }

                await to.WriteAsync(buffer.AsMemory(0, read), linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            linkedCts.Cancel();
        }
    }

    // ── Вспомогательные методы ───────────────────────────────────────

    private static async Task<int> ReceiveExactAsync(Socket socket, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await socket.ReceiveAsync(buffer[total..], SocketFlags.None, ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total == buffer.Length) break;
        }
        return total;
    }
}

// ── Вспомогательные типы ────────────────────────────────────────────

/// <summary>Конфигурация SocksProxy.</summary>
public sealed class SocksProxyOptions
{
    /// <summary>Локальный endpoint, на котором будет слушать Socks5-сервер.</summary>
    public IPEndPoint LocalEndPoint { get; set; } = new(IPAddress.Loopback, 1080);

    /// <summary>Endpoint вышестоящего HTTPS-прокси (домен/хост + порт).</summary>
    public DnsEndPoint UpstreamProxyEndPoint { get; set; } = new("localhost", 8080);

    /// <summary>Максимальная очередь ожидающих подключений.</summary>
    public int Backlog { get; set; } = 100;

    /// <summary>
    /// Учётные данные для входящих Socks5-клиентов (null = аутентификация не требуется).
    /// </summary>
    public ProxyCredentials? Credentials { get; set; }

    /// <summary>
    /// Учётные данные для аутентификации на upstream HTTPS-прокси (null = не требуется).
    /// </summary>
    public ProxyCredentials? UpstreamCredentials { get; set; }

    /// <summary>Логгер Microsoft.Extensions.Logging (null = без логирования).</summary>
    public ILogger? Logger { get; set; }

    /// <summary>
    /// Использовать TLS к upstream прокси (для HTTPS-прокси на 443 порту).
    /// </summary>
    public bool UseTls { get; set; }

    /// <summary>Время жизни DNS-кэша. По умолчанию 5 минут.</summary>
    public TimeSpan DnsCacheTtl { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(LocalEndPoint);
        ArgumentNullException.ThrowIfNull(UpstreamProxyEndPoint);
        if (Backlog < 1) throw new ArgumentException("Backlog must be at least 1");
    }
}

/// <summary>Учётные данные для аутентификации.</summary>
public sealed record ProxyCredentials(string Username, string Password);

/// <summary>Адрес цели, запрошенной Socks5-клиентом.</summary>
internal sealed record Socks5Target(string Host, ushort Port);

/// <summary>Аргументы событий подключения/отключения.</summary>
public sealed record SocksClientEventArgs(string Host, int Port, int ClientPort, int ConnectionCount);

/// <summary>Запись в DNS-кэше.</summary>
internal readonly record struct DnsCacheEntry(IPAddress[] Addresses, DateTime Expiry);

internal enum Direction
{
    Upload,
    Download
}
