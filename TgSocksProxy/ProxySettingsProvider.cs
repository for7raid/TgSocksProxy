using Android.Content;
using System.Text.Json;

namespace TgSocksProxy;

/// <summary>
/// Сохраняет и загружает ProxySettings через Android SharedPreferences.
/// </summary>
public static class ProxySettingsProvider
{
    private const string PrefsName = "socks_proxy_prefs";
    private const string SettingsKey = "proxy_settings";

    private static ISharedPreferences Prefs =>
        Android.App.Application.Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    /// <summary>Загрузить настройки. Если нет — вернуть дефолтные.</summary>
    public static ProxySettings Get()
    {
        var json = Prefs.GetString(SettingsKey, null);
        if (string.IsNullOrEmpty(json))
            return DefaultSettings();

        try
        {
            return JsonSerializer.Deserialize(json, ProxySettingsContext.Default.ProxySettings) ?? DefaultSettings();
        }
        catch
        {
            return DefaultSettings();
        }
    }

    /// <summary>Сохранить настройки.</summary>
    public static void Save(ProxySettings settings)
    {
        var json = JsonSerializer.Serialize(settings, ProxySettingsContext.Default.ProxySettings);
        Prefs.Edit()!
            .PutString(SettingsKey, json)
            .Apply();
    }

    private static ProxySettings DefaultSettings() => new(
        LocalPort: 10180,
        Upstreams: new());
}
