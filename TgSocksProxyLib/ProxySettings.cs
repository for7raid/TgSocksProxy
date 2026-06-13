namespace TgSocksProxy;

/// <summary>Настройки одного upstream HTTPS-прокси.</summary>
public sealed record UpstreamSettings(
    string Host,
    int Port,
    string? Login,
    string? Password,
    bool IsPrimary = false);

/// <summary>Полные настройки Socks5-прокси.</summary>
public sealed record ProxySettings(
    int LocalPort = 10180,
    string? SocksLogin = null,
    string? SocksPassword = null,
    List<UpstreamSettings> Upstreams = null!)
{
    public List<UpstreamSettings> Upstreams { get; init; } = Upstreams ?? new();
}
