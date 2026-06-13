using System.Text.Json.Serialization;

namespace TgSocksProxy;

[JsonSerializable(typeof(ProxySettings))]
[JsonSerializable(typeof(UpstreamSettings))]
[JsonSourceGenerationOptions(WriteIndented = false)]
public partial class ProxySettingsContext : JsonSerializerContext
{
}
