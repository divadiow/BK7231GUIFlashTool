using System.Text;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using BK7231Flasher;

namespace TuyaDumpJsonExtractor;

[Activity(
    Label = "Tuya Dump JSON",
    MainLauncher = true,
    Exported = true,
    Icon = "@drawable/ic_launcher",
    Theme = "@android:style/Theme.Material.Light.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataMimeType = "application/octet-stream")]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataMimeType = "application/x-binary")]
public sealed class MainActivity : Activity
{
    private const int OpenDumpRequest = 1101;
    private const int SaveJsonRequest = 1102;
    private const long MaximumDumpBytes = 64L * 1024L * 1024L;

    private Button _openButton = null!;
    private Button _copyButton = null!;
    private Button _saveButton = null!;
    private Button _shareButton = null!;
    private ProgressBar _progress = null!;
    private TextView _fileLabel = null!;
    private TextView _statusLabel = null!;
    private EditText _jsonOutput = null!;

    private string _currentJson = string.Empty;
    private string _currentFileName = string.Empty;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(BuildInterface());

        if (Intent?.Action == Intent.ActionView && Intent.Data != null)
            _ = OpenUriAsync(Intent.Data);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.Action == Intent.ActionView && intent.Data != null)
            _ = OpenUriAsync(intent.Data);
    }

    private View BuildInterface()
    {
        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        root.SetPadding(Dp(16), Dp(14), Dp(16), Dp(12));

        var title = new TextView(this)
        {
            Text = "Tuya Dump JSON Extractor",
            TextSize = 22f,
            Typeface = Typeface.DefaultBold
        };
        root.AddView(title, MatchWrap());

        var subtitle = new TextView(this)
        {
            Text = "Uses the current BK7231 Easy Flasher TuyaConfig.cs extraction path. Processing is entirely on-device; no network permission is requested.",
            TextSize = 14f
        };
        subtitle.SetPadding(0, Dp(4), 0, Dp(8));
        root.AddView(subtitle, MatchWrap());

        var warning = new TextView(this)
        {
            Text = "Flash dumps and extracted JSON can contain Wi-Fi credentials, device keys and identifiers.",
            TextSize = 13f
        };
        warning.SetTextColor(Color.Rgb(150, 55, 35));
        warning.SetPadding(0, 0, 0, Dp(10));
        root.AddView(warning, MatchWrap());

        var actions = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            Gravity = GravityFlags.CenterVertical
        };

        _openButton = CreateButton("Open dump");
        _copyButton = CreateButton("Copy JSON");
        _saveButton = CreateButton("Save JSON");
        _shareButton = CreateButton("Share");

        _openButton.Click += (_, _) => ShowOpenPicker();
        _copyButton.Click += (_, _) => CopyJson();
        _saveButton.Click += (_, _) => ShowSavePicker();
        _shareButton.Click += (_, _) => ShareJson();

        actions.AddView(_openButton, WeightedButton());
        actions.AddView(_copyButton, WeightedButton());
        actions.AddView(_saveButton, WeightedButton());
        actions.AddView(_shareButton, WeightedButton());
        root.AddView(actions, MatchWrap());

        var infoRow = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            Gravity = GravityFlags.CenterVertical
        };
        infoRow.SetPadding(0, Dp(8), 0, Dp(6));

        _progress = new ProgressBar(this)
        {
            Indeterminate = true,
            Visibility = ViewStates.Gone
        };
        infoRow.AddView(_progress, new LinearLayout.LayoutParams(Dp(28), Dp(28)));

        var labels = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical
        };
        labels.SetPadding(Dp(8), 0, 0, 0);

        _fileLabel = new TextView(this)
        {
            Text = "No dump selected",
            TextSize = 14f,
            Typeface = Typeface.DefaultBold
        };
        _statusLabel = new TextView(this)
        {
            Text = "Open a complete Tuya flash dump (.bin).",
            TextSize = 13f
        };
        labels.AddView(_fileLabel, MatchWrap());
        labels.AddView(_statusLabel, MatchWrap());
        infoRow.AddView(labels, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        root.AddView(infoRow, MatchWrap());

        _jsonOutput = new EditText(this)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            TextSize = 12f,
            Typeface = Typeface.Monospace,
            InputType = Android.Text.InputTypes.ClassText |
                        Android.Text.InputTypes.TextFlagMultiLine |
                        Android.Text.InputTypes.TextFlagNoSuggestions,
            HorizontallyScrolling = true,
            ShowSoftInputOnFocus = false,
            Text = "Extracted JSON will appear here."
        };
        _jsonOutput.SetTextIsSelectable(true);
        _jsonOutput.SetPadding(Dp(10), Dp(10), Dp(10), Dp(10));
        root.AddView(_jsonOutput, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            0,
            1f));

        SetResultActionsEnabled(false);
        return root;
    }

    private Button CreateButton(string text) => new(this)
    {
        Text = text,
        AllCaps = false,
        MinWidth = 0,
        MinimumWidth = 0
    };

    private static LinearLayout.LayoutParams MatchWrap() =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);

    private static LinearLayout.LayoutParams WeightedButton() =>
        new(0, ViewGroup.LayoutParams.WrapContent, 1f);

    private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    private void ShowOpenPicker()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraMimeTypes, new[]
        {
            "application/octet-stream",
            "application/x-binary",
            "application/macbinary",
            "*/*"
        });
        StartActivityForResult(intent, OpenDumpRequest);
    }

    private void ShowSavePicker()
    {
        if (string.IsNullOrEmpty(_currentJson))
            return;

        string title = Path.GetFileNameWithoutExtension(_currentFileName);
        if (string.IsNullOrWhiteSpace(title))
            title = "tuya-config";

        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("application/json");
        intent.PutExtra(Intent.ExtraTitle, title + ".json");
        StartActivityForResult(intent, SaveJsonRequest);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data == null)
            return;

        if (requestCode == OpenDumpRequest)
        {
            try
            {
                ContentResolver?.TakePersistableUriPermission(
                    data.Data,
                    data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission));
            }
            catch
            {
                // Some document providers do not offer persistable grants. The immediate grant is enough.
            }

            _ = OpenUriAsync(data.Data);
        }
        else if (requestCode == SaveJsonRequest)
        {
            _ = SaveJsonAsync(data.Data);
        }
    }

    private async Task OpenUriAsync(Android.Net.Uri uri)
    {
        SetBusy(true, "Reading dump…");
        _currentJson = string.Empty;
        SetResultActionsEnabled(false);

        try
        {
            _currentFileName = GetDisplayName(uri) ?? uri.LastPathSegment ?? "dump.bin";
            _fileLabel.Text = _currentFileName;

            byte[] data = await ReadUriWithLimitAsync(uri, MaximumDumpBytes).ConfigureAwait(false);
            TuyaExtractionResult result = await Task.Run(() => TuyaDumpExtractor.Extract(data)).ConfigureAwait(false);

            RunOnUiThread(() => PresentResult(data.LongLength, result));
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                _jsonOutput.Text = string.Empty;
                _statusLabel.Text = "Failed: " + ex.Message;
                SetBusy(false);
            });
        }
    }

    private async Task<byte[]> ReadUriWithLimitAsync(Android.Net.Uri uri, long limit)
    {
        using Stream stream = ContentResolver?.OpenInputStream(uri)
            ?? throw new IOException("Android could not open the selected document.");
        using var memory = new MemoryStream();
        byte[] buffer = new byte[128 * 1024];

        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0)
                break;

            if (memory.Length + read > limit)
                throw new IOException($"The selected file exceeds the {limit / 1024 / 1024} MB safety limit.");

            await memory.WriteAsync(buffer, 0, read).ConfigureAwait(false);
        }

        return memory.ToArray();
    }

    private void PresentResult(long fileBytes, TuyaExtractionResult result)
    {
        SetBusy(false);
        string size = fileBytes >= 1024 * 1024
            ? $"{fileBytes / 1024d / 1024d:0.00} MB"
            : $"{fileBytes / 1024d:0.0} KB";

        if (result.Success)
        {
            _currentJson = result.Json;
            _jsonOutput.Text = result.Json;
            _jsonOutput.SetSelection(0);
            _statusLabel.Text = $"Extracted {size}; config at {result.MagicPosition}; JSON SHA-256 {result.Sha256[..12]}…";
            SetResultActionsEnabled(true);
        }
        else
        {
            _currentJson = string.Empty;
            _jsonOutput.Text = string.Join("\r\n", result.LogLines);
            _statusLabel.Text = result.Message;
            SetResultActionsEnabled(false);
        }
    }

    private async Task SaveJsonAsync(Android.Net.Uri uri)
    {
        try
        {
            using Stream stream = ContentResolver?.OpenOutputStream(uri, "wt")
                ?? throw new IOException("Android could not create the destination document.");
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(_currentJson).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            RunOnUiThread(() => Toast.MakeText(this, "JSON saved", ToastLength.Short)?.Show());
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => Toast.MakeText(this, "Save failed: " + ex.Message, ToastLength.Long)?.Show());
        }
    }

    private void CopyJson()
    {
        if (string.IsNullOrEmpty(_currentJson))
            return;

        var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
        if (clipboard == null)
            return;

        clipboard.PrimaryClip = ClipData.NewPlainText("Tuya config JSON", _currentJson);
        Toast.MakeText(this, "JSON copied", ToastLength.Short)?.Show();
    }

    private void ShareJson()
    {
        if (string.IsNullOrEmpty(_currentJson))
            return;

        var intent = new Intent(Intent.ActionSend);
        intent.SetType("application/json");
        intent.PutExtra(Intent.ExtraText, _currentJson);
        intent.PutExtra(Intent.ExtraTitle, Path.GetFileNameWithoutExtension(_currentFileName) + ".json");
        StartActivity(Intent.CreateChooser(intent, "Share extracted JSON"));
    }

    private string? GetDisplayName(Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ContentResolver?.Query(
                uri,
                new[] { OpenableColumns.DisplayName },
                null,
                null,
                null);

            if (cursor != null && cursor.MoveToFirst())
            {
                int index = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                if (index >= 0)
                    return cursor.GetString(index);
            }
        }
        catch
        {
            // Fall back to the URI path.
        }

        return null;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _progress.Visibility = busy ? ViewStates.Visible : ViewStates.Gone;
        _openButton.Enabled = !busy;
        if (status != null)
            _statusLabel.Text = status;
    }

    private void SetResultActionsEnabled(bool enabled)
    {
        _copyButton.Enabled = enabled;
        _saveButton.Enabled = enabled;
        _shareButton.Enabled = enabled;
    }
}
