using Android.Content;
using Android.Content.PM;
using System.Globalization;

namespace TgSocksProxy;

/// <summary>
/// Определяет приложение Android, отправившее запрос через прокси,
/// по локальному порту через /proc/net/tcp → UID → package name.
/// </summary>
public static class AppResolver
{
    private static readonly string[] ProcNetPaths = { "/proc/net/tcp", "/proc/net/tcp6" };

    /// <summary>
    /// По локальному порту Socks5-клиента возвращает имя пакета приложения.
    /// null — не удалось определить.
    /// </summary>
    public static string? GetAppForLocalPort(int localPort, PackageManager pm)
    {
        int uid = GetUidForLocalPort(localPort);
        if (uid == -1) return null;

        string[]? packages = pm.GetPackagesForUid(uid);
        if (packages is null || packages.Length == 0)
            return uid.ToString();

        return packages[0];
    }

    /// <summary>
    /// Ищет UID по номеру локального порта в /proc/net/tcp и /proc/net/tcp6.
    /// </summary>
    private static int GetUidForLocalPort(int port)
    {
        string hexPort = port.ToString("X4");

        // Пробуем через Java FileReader (обходит некоторые SELinux-ограничения)
        foreach (string path in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                using var reader = new Java.IO.BufferedReader(new Java.IO.FileReader(path));
                string? first = reader.ReadLine(); // заголовок
                if (first is null) continue;

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    int uid = ParseLine(line, hexPort);
                    if (uid != -1) return uid;
                }
            }
            catch { }
        }

        // Fallback: Runtime.exec с sh -c (ловим stderr для диагностики)
        foreach (string path in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            try
            {
                var runtime = Java.Lang.Runtime.GetRuntime()!;
                var proc = runtime!.Exec(new[] { "sh", "-c", "cat " + path })!;

                string output;
                string error;
                using (var stdout = new System.IO.StreamReader(proc.InputStream!))
                    output = stdout.ReadToEnd();
                using (var stderr = new System.IO.StreamReader(proc.ErrorStream!))
                    error = stderr.ReadToEnd();
                proc.WaitFor();

                if (!string.IsNullOrEmpty(error))
                {
                    // Пишем ошибку в лог, чтобы пользователь видел причину
                    LogStore.Add($"[AppResolver] Shell error: {error.Trim()}");
                }

                if (!string.IsNullOrEmpty(output))
                {
                    int uid = ParseUidFromProcTable(output, hexPort);
                    if (uid != -1) return uid;
                }
            }
            catch { }
        }

        LogStore.Add("[AppResolver] Cannot access /proc/net/tcp — SELinux blocked on this device");
        return -1;
    }

    /// <summary>Парсит одну строку из /proc/net/tcp.</summary>
    private static int ParseLine(string line, string hexPort)
    {
        line = line.Trim();
        if (string.IsNullOrEmpty(line)) return -1;

        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 8) return -1;

        var addrParts = parts[1].Split(':');
        if (addrParts.Length != 2) return -1;

        if (!addrParts[1].Equals(hexPort, StringComparison.OrdinalIgnoreCase))
            return -1;

        if (parts[3].Trim() != "01") return -1; // TCP_ESTABLISHED

        if (int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid))
            return uid;

        return -1;
    }

    private static int ParseUidFromProcTable(string content, string hexPort)
    {
        if (string.IsNullOrEmpty(content)) return -1;

        using var reader = new System.IO.StringReader(content);
        reader.ReadLine(); // заголовок

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 8) continue;

            var addrParts = parts[1].Split(':');
            if (addrParts.Length != 2) continue;

            if (!addrParts[1].Equals(hexPort, StringComparison.OrdinalIgnoreCase))
                continue;

            if (parts[3].Trim() != "01") continue; // TCP_ESTABLISHED

            if (int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int uid))
                return uid;
        }

        return -1;
    }
}
