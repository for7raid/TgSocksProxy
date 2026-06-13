using Android.Content;
using Android.Views;
using System.Collections.Specialized;

namespace TgSocksProxy
{
    [Activity(Label = "@string/app_name", MainLauncher = true, Theme = "@style/AppTheme")]
    public class MainActivity : Activity
    {
        private Button? _btnToggle;
        private Button? _btnSettings;
        private TextView? _tvStatus;
        private TextView? _tvLog;
        private ScrollView? _scrollLog;
        private bool _autoScroll = true;

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_main);

            _btnToggle = FindViewById<Button>(Resource.Id.btnToggle)!;
            _btnSettings = FindViewById<Button>(Resource.Id.btnSettings)!;
            _tvStatus = FindViewById<TextView>(Resource.Id.tvStatus)!;
            _tvLog = FindViewById<TextView>(Resource.Id.tvLog)!;
            _scrollLog = FindViewById<ScrollView>(Resource.Id.scrollLog)!;

            UpdateUi();

            _btnToggle.Click += OnToggleClick;
            _btnSettings.Click += (_, _) => StartActivity(new Intent(this, typeof(SettingsActivity)));
            LogStore.CollectionChanged += OnLogChanged;
            SocksProxyService.StatusChanged += OnStatusChanged;

            AppendLogLines(LogStore.Lines);

            if (!SocksProxyService.IsRunning)
            {
                var intent = new Intent(this, typeof(SocksProxyService));
                StartForegroundService(intent);
            }
        }

        protected override void OnDestroy()
        {
            _btnToggle!.Click -= OnToggleClick;
            LogStore.CollectionChanged -= OnLogChanged;
            SocksProxyService.StatusChanged -= OnStatusChanged;
            base.OnDestroy();
        }

        private void OnToggleClick(object? sender, EventArgs e)
        {
            var intent = new Intent(this, typeof(SocksProxyService));
            if (SocksProxyService.IsRunning)
            {
                StopService(intent);
            }
            else
            {
                StartForegroundService(intent);
            }
        }

        private void OnStatusChanged(object? sender, EventArgs e)
        {
            RunOnUiThread(UpdateUi);
        }

        private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems?[0] is string line)
            {
                RunOnUiThread(() => AppendLogLine(line));
            }
        }

        private void UpdateUi()
        {
            bool running = SocksProxyService.IsRunning;
            _btnToggle!.Text = running ? GetString(Resource.String.stop) : GetString(Resource.String.start);
            _tvStatus!.Text = running
                ? GetString(Resource.String.status_running)
                : GetString(Resource.String.status_stopped);
        }

        private void AppendLogLine(string line)
        {
            _tvLog!.Append(line + "\n");
            if (_autoScroll)
            {
                _scrollLog!.Post(() => _scrollLog.FullScroll(FocusSearchDirection.Down));
            }
        }

        private void AppendLogLines(IReadOnlyList<string> lines)
        {
            foreach (var line in lines)
                _tvLog!.Append(line + "\n");
            _scrollLog!.Post(() => _scrollLog.FullScroll(FocusSearchDirection.Down));
        }
    }
}
