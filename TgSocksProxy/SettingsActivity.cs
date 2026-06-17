using Android.Content;
using Android.Views;

namespace TgSocksProxy;

[Activity(Label = "Settings")]
public class SettingsActivity : Activity
{
    private EditText? _etLocalPort;
    private EditText? _etSocksLogin;
    private EditText? _etSocksPassword;
    private LinearLayout? _layoutUpstreams;
    private readonly List<UpstreamRow> _upstreamRows = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_settings);

        _etLocalPort = FindViewById<EditText>(Resource.Id.etLocalPort)!;
        _etSocksLogin = FindViewById<EditText>(Resource.Id.etSocksLogin)!;
        _etSocksPassword = FindViewById<EditText>(Resource.Id.etSocksPassword)!;
        _layoutUpstreams = FindViewById<LinearLayout>(Resource.Id.layoutUpstreams)!;

        FindViewById<Button>(Resource.Id.btnAddUpstream)!.Click += (_, _) => AddUpstreamRow();
        FindViewById<Button>(Resource.Id.btnSave)!.Click += OnSave;

        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = ProxySettingsProvider.Get();
        _etLocalPort!.Text = settings.LocalPort.ToString();
        _etSocksLogin!.Text = settings.SocksLogin ?? "";
        _etSocksPassword!.Text = settings.SocksPassword ?? "";

        _layoutUpstreams!.RemoveAllViews();
        _upstreamRows.Clear();

        foreach (var u in settings.Upstreams)
            AddUpstreamRow(u);
    }

    private void AddUpstreamRow(UpstreamSettings? existing = null)
    {
        UpstreamRow? row = null;
        row = new UpstreamRow(this, existing, () =>
        {
            var idx = _upstreamRows.FindIndex(r => ReferenceEquals(r, row!));
            if (idx >= 0)
            {
                _layoutUpstreams!.RemoveView(_upstreamRows[idx].Root);
                _upstreamRows.RemoveAt(idx);
            }
        });


        _upstreamRows.Add(row);
        _layoutUpstreams!.AddView(row.Root);
    }

    private void OnSave(object? sender, EventArgs e)
    {
        if (!int.TryParse(_etLocalPort!.Text, out var port) || port < 1 || port > 65535)
        {
            Toast.MakeText(this, "Invalid local port", ToastLength.Short)?.Show();
            return;
        }

        var upstreams = _upstreamRows
            .Select(r => r.GetSettings())
            .Where(u => !string.IsNullOrEmpty(u.Host))
            .ToList();

        if (upstreams.Count == 0)
        {
            Toast.MakeText(this, "At least one upstream required", ToastLength.Short)?.Show();
            return;
        }

        if (!upstreams.Any(u => u.Enabled))
            upstreams[0] = upstreams[0] with { Enabled = true };

        var socksLogin = _etSocksLogin!.Text;
        var socksPassword = _etSocksPassword!.Text;

        var settings = new ProxySettings(
            LocalPort: port,
            SocksLogin: string.IsNullOrEmpty(socksLogin) ? null : socksLogin,
            SocksPassword: string.IsNullOrEmpty(socksPassword) ? null : socksPassword,
            Upstreams: upstreams);

        ProxySettingsProvider.Save(settings);

        StopService(new Intent(this, typeof(SocksProxyService)));
        StartForegroundService(new Intent(this, typeof(SocksProxyService)));

        Toast.MakeText(this, "Settings saved, proxy restarted", ToastLength.Short)?.Show();
        Finish();
    }

    private sealed class UpstreamRow
    {
        public View Root { get; }


        private readonly EditText _etHost;
        private readonly EditText _etPort;
        private readonly EditText _etLogin;
        private readonly EditText _etPassword;
        private readonly EditText _etSNIs;
        private readonly CheckBox _chEnabled;

        public void SetPrimary(bool value) => _chEnabled.Checked = value;

        public UpstreamRow(Context ctx, UpstreamSettings? existing, Action onRemove)
        {
            var density = ctx.Resources?.DisplayMetrics?.Density ?? 1f;
            int dp8 = (int)(8 * density);
            int dp4 = (int)(4 * density);

            var container = new LinearLayout(ctx)
            {
                LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
                {
                    TopMargin = dp8
                },
                Orientation = Orientation.Vertical
            };

            var headerRow = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };

            _chEnabled = new CheckBox(ctx)
            {
                Text = "Enabled",
                Checked = existing?.Enabled ?? false
            };
            headerRow.AddView(_chEnabled);

            var btnRemove = new Button(ctx) { Text = "Remove" };
            btnRemove.Click += (_, _) => onRemove();
            headerRow.AddView(btnRemove);
            container.AddView(headerRow);

            var row1 = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            _etHost = new EditText(ctx)
            {
                LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2f)
                { RightMargin = dp4 },
                Hint = "Host",
                Text = existing?.Host ?? ""
            };
            _etPort = new EditText(ctx)
            {
                LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
                { LeftMargin = dp4 },
                Hint = "Port",
                InputType = Android.Text.InputTypes.ClassNumber,
                Text = existing?.Port.ToString() ?? "443"
            };
            row1.AddView(_etHost);
            row1.AddView(_etPort);
            container.AddView(row1);

            var row2 = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            _etLogin = new EditText(ctx)
            {
                LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
                { RightMargin = dp4 },
                Hint = "Login",
                Text = existing?.Login ?? ""
            };
            _etPassword = new EditText(ctx)
            {
                LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f)
                { LeftMargin = dp4 },
                Hint = "Password",
                InputType = Android.Text.InputTypes.TextVariationPassword,
                Text = existing?.Password ?? ""
            };
            row2.AddView(_etLogin);
            row2.AddView(_etPassword);
            container.AddView(row2);

            var labelSNI = new Android.Widget.TextView(ctx)
            {
                Text = "SNI (one per line)",
                TextSize = 12f
            };
            container.AddView(labelSNI);

            _etSNIs = new EditText(ctx)
            {
                LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent),
                Hint = @"*\.example.com\n*.example.org",
                InputType = Android.Text.InputTypes.ClassText | Android.Text.InputTypes.TextFlagMultiLine,
                Gravity = Android.Views.GravityFlags.Top | Android.Views.GravityFlags.Start,
                Text = existing?.SNIs ?? ""
            };
            _etSNIs.SetMinLines(3);
            _etSNIs.SetMaxLines(6);
            container.AddView(_etSNIs);

            Root = container;
        }

        public UpstreamSettings GetSettings()
        {
            int.TryParse(_etPort.Text, out var port);
            return new UpstreamSettings(
                Host: _etHost.Text.Trim(),
                Port: port > 0 ? port : 443,
                Login: string.IsNullOrEmpty(_etLogin.Text) ? null : _etLogin.Text,
                Password: string.IsNullOrEmpty(_etPassword.Text) ? null : _etPassword.Text,
                SNIs: _etSNIs.Text.Trim(),
                Enabled: _chEnabled.Checked);
        }
    }
}
