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

    public override void OnCreate()
    {
        base.OnCreate();
        CreateNotificationChannel();
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
    }

    public override IBinder? OnBind(Intent? intent) => null;

    private void StartProxy()
    {
        if (_proxy is not null) return;

        var settings = ProxySettingsProvider.Get();
        var primary = settings.Upstreams.FirstOrDefault(u => u.IsPrimary) ?? settings.Upstreams.FirstOrDefault();
        if (primary is null)
        {
            LogStore.Add("[SocksProxyService] No upstream configured!");
            return;
        }

        string logsDir = System.IO.Path.Combine(CacheDir!.AbsolutePath, "logs");
        Directory.CreateDirectory(logsDir);

        var log = new LoggerConfiguration()
               .WriteTo.File(
                   path: System.IO.Path.Combine(logsDir, "log.txt"),
                   rollingInterval: RollingInterval.Day,
                   rollOnFileSizeLimit: true,
                   fileSizeLimitBytes: 1_048_576,
                   buffered: true,
                   flushToDiskInterval: TimeSpan.FromSeconds(3))
               .WriteTo.Sink(new LogStoreSink())
               .CreateLogger();

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSerilog(log);
            builder.AddDebug();
        });

        var logger = _loggerFactory!.CreateLogger<SocksProxy>();
        var packageManager = PackageManager!;

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

            Logger = logger,
        });

        _proxy.ClientConnected += e =>
        {
            UpdateNotify();
        };

        _proxy.ClientDisconnected += e =>
        {
            UpdateNotify();
        };

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
        var textBitmap = CreateTextBitmap($"{count}T", Color.Transparent.ToArgb(), Color.White.ToArgb(), 56);

        var icon = Icon.CreateWithBitmap(textBitmap);

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("Socks5 Proxy")
            .SetContentText($"Сервер {primaryUpstream?.Host}:{primaryUpstream?.Port}")
            .SetSubText($"{count} соединений")
            .SetSmallIcon(icon)
            .SetContentIntent(pendingIntent)
            .SetOngoing(true)
            .Build();
    }

    private static Android.Graphics.Bitmap CreateTextBitmap(
    string text, int bgColor, int textColor, int sizeDp)
    {
        var density = Android.App.Application.Context.Resources!.DisplayMetrics!.Density;
        int px = (int)(sizeDp * density);
        var bitmap = Android.Graphics.Bitmap.CreateBitmap(px, px, Android.Graphics.Bitmap.Config.Argb8888!)!;
        var canvas = new Android.Graphics.Canvas(bitmap);
        var paint = new Android.Graphics.Paint
        {
            Color = new Android.Graphics.Color(textColor),
            TextSize = px * 0.6f,
            AntiAlias = true,
            TextAlign = Android.Graphics.Paint.Align.Center
        };
        canvas.DrawColor(new Android.Graphics.Color(bgColor));
        canvas.DrawText(text, px / 2f, px * 0.75f, paint);
        return bitmap;
    }
}
