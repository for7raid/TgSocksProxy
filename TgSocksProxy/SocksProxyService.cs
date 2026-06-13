using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Net;

namespace TgSocksProxy;

[Service(ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class SocksProxyService : Service
{
    private const string ChannelId = "socks_proxy_channel";
    private const int NotificationId = 1;

    private static bool _isRunning;
    private SocksProxy? _proxy;
    private ILoggerFactory? _loggerFactory;
    private Handler? _mainHandler;
    private readonly ILogger<SocksProxyService> _logger;

    public static bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? StatusChanged;

    public SocksProxyService()
    {
        _logger = LoggerFactoryService.LoggerFactoryInstance.CreateLogger<SocksProxyService>();
    }
    public override void OnCreate()
    {
        base.OnCreate();
        _mainHandler = new Handler(Looper.MainLooper!);
        CreateNotificationChannel();
        _logger.LogInformation("SocksProxyService created");
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification();
        StartForeground(NotificationId, notification);

        StartProxy();

        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        IsRunning = false;
        _proxy?.Stop();
        _proxy?.Dispose();
        _loggerFactory?.Dispose();
        base.OnDestroy();
        _logger.LogInformation("SocksProxyService destroyed");

    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void StartProxy()
    {
        if (_proxy is not null) return;

        var settings = ProxySettingsProvider.Get();
        var primary = settings.Upstreams.FirstOrDefault(u => u.IsPrimary) ?? settings.Upstreams.FirstOrDefault();
        if (primary is null)
        {
            _logger.LogError("No upstream configured!");
            return;
        }



        var socksCredentials = settings.SocksLogin is not null
            ? new ProxyCredentials(settings.SocksLogin, settings.SocksPassword ?? "")
            : null;

        var upstreamCredentials = primary.Login is not null
            ? new ProxyCredentials(primary.Login, primary.Password ?? "")
            : null;

        _proxy = new SocksProxy(new SocksProxyOptions
        {
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, settings.LocalPort),
            Credentials = socksCredentials,

            UpstreamProxyEndPoint = new DnsEndPoint(primary.Host, primary.Port),
            UpstreamCredentials = upstreamCredentials,
            UseTls = true,

            Logger = LoggerFactoryService.LoggerFactoryInstance.CreateLogger<SocksProxy>(),
        });

        // События из фонового потока — переключаем на главный поток через Handler
        _proxy.ClientConnected += (s, e) => { try { _mainHandler?.Post(UpdateNotify); } catch { } };
        _proxy.ClientDisconnected += (s, e) => { try { _mainHandler?.Post(UpdateNotify); } catch { } };

        _proxy.Start();
        IsRunning = true;
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(
                ChannelId,
                "Socks5 Proxy",
                NotificationImportance.Low);
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(channel);
        }
    }

    private void UpdateNotify()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Notify(NotificationId, BuildNotification());
        }
    }

    private Notification BuildNotification()
    {
        var pendingIntent = PendingIntent.GetActivity(
            this, 0, new Intent(this, typeof(MainActivity)),
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var count = _proxy?.ConnectionCount ?? 0;
        var primaryUpstream = _proxy?.Options?.UpstreamProxyEndPoint;
        var textBitmap = CreateTextBitmap($"{count}↕", Color.Transparent.ToArgb(), Color.White.ToArgb(), 60);

        var icon = Icon.CreateWithBitmap(textBitmap);

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle($"Сервер {primaryUpstream?.Host}:{primaryUpstream?.Port}")
            .SetContentText($"↑{FormatSize(_proxy?.UploadBytes ?? 0)}\t\t↓{FormatSize(_proxy?.DownloadBytes ?? 0)}")
            .SetSubText($"{count} соединений")
            .SetSmallIcon(icon)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .Build();
    }
    private static string FormatSize(ulong bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1048576 ? $"{bytes / 1024.0:F1} KB" :
        $"{bytes / 1048576.0:F1} MB";
    private static Bitmap CreateTextBitmap(string text, int bgColor, int textColor, int sizeDp)
    {
        var density = Android.App.Application.Context.Resources!.DisplayMetrics!.Density;
        int px = (int)(sizeDp * density);
        var bitmap = Bitmap.CreateBitmap(px, px, Bitmap.Config.Argb8888!)!;
        var canvas = new Canvas(bitmap);
        var paint = new Paint
        {
            Color = new Android.Graphics.Color(textColor),
            TextSize = px * 0.6f,
            AntiAlias = true,
            TextAlign = Paint.Align.Center
        };
        canvas.DrawColor(new Android.Graphics.Color(bgColor));
        canvas.DrawText(text, px / 2f, px * 0.75f, paint);
        return bitmap;
    }
}
