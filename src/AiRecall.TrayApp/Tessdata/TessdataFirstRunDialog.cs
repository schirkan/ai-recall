using System.Diagnostics;
using AiRecall.Core.Tessdata;

namespace AiRecall.TrayApp.Tessdata;

/// <summary>
/// Modal-Dialog beim ersten Start der TrayApp, wenn tessdata für die
/// konfigurierten OCR-Sprachen fehlt (Spec 0012 v0.2).
///
/// Zeigt die fehlenden Sprachen mit Direkt-Download-Link pro Sprache und drei
/// Aktionen:
/// <list type="bullet">
///   <item><b>Download Now</b> — startet sofort den Download via <see cref="TessdataManager"/>,
///         zeigt ProgressBar + Status-Label. Bei Fehler erscheint ein Retry-Button.</item>
///   <item><b>Later</b> — Dialog schließen, später per Balloon erinnern.</item>
///   <item><b>Don't Ask Again</b> — User-Auswahl persistieren, Dialog nie wieder zeigen.</item>
/// </list>
///
/// **Manueller Download-Link**: Pro fehlender Sprache wird ein
/// <see cref="LinkLabel"/> mit der Direkt-URL
/// (<c>https://github.com/tesseract-ocr/tessdata_fast/raw/main/{lang}.traineddata</c>)
/// angezeigt. Klick öffnet die URL im Default-Browser.
/// </summary>
public sealed class TessdataFirstRunDialog : Form
{
    /// <summary>User-Auswahl aus dem Dialog.</summary>
    public enum DialogChoice
    {
        /// <summary>User klickte "Download Now" — Download wurde gestartet.</summary>
        DownloadNow,
        /// <summary>User klickte "Later" — Balloon-Erinnerung.</summary>
        Later,
        /// <summary>User klickte "Don't Ask Again" — Auswahl persistiert.</summary>
        NeverAskAgain,
    }

    private readonly IReadOnlyList<MissingLanguage> _missing;
    private readonly TessdataManager _manager;
    private readonly Action<bool> _onPersistNeverAskAgain;
    private readonly string _targetDir;
    private readonly Serilog.ILogger? _logger;
    private CancellationTokenSource? _cts;

    private Button _downloadButton = null!;
    private Button _laterButton = null!;
    private Button _neverButton = null!;
    private Button _retryButton = null!;
    private ProgressBar _progressBar = null!;
    private Label _statusLabel = null!;

    /// <summary>Resultat nach <see cref="Form.ShowDialog"/>.</summary>
    public DialogChoice Choice { get; private set; } = DialogChoice.Later;

    /// <summary>True, wenn der Dialog per Download-Flow geschlossen wurde (egal ob erfolgreich oder nicht).</summary>
    public bool DownloadAttempted { get; private set; }

    /// <summary>Ergebnis des letzten Downloads (null = kein Versuch oder läuft noch).</summary>
    public bool? DownloadSucceeded { get; private set; }

    public TessdataFirstRunDialog(
        IReadOnlyList<MissingLanguage> missing,
        TessdataManager manager,
        string targetDir,
        Action<bool> onPersistNeverAskAgain,
        Serilog.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(onPersistNeverAskAgain);
        ArgumentNullException.ThrowIfNull(targetDir);

        _missing = missing;
        _manager = manager;
        _onPersistNeverAskAgain = onPersistNeverAskAgain;
        _targetDir = targetDir;
        _logger = logger;

        Text = "AiRecall — OCR tessdata fehlt";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 360);
        Font = new Font("Segoe UI", 9F);

        BuildUi();
        AcceptButton = _downloadButton;
        CancelButton = _laterButton;
    }

    private void BuildUi()
    {
        var infoLabel = new Label
        {
            Text = $"Tesseract-OCR ist konfiguriert, aber für {_missing.Count} Sprache(n) " +
                   $"fehlen die tessdata-Dateien:",
            Location = new Point(16, 12),
            Size = new Size(528, 24),
            AutoSize = false,
        };

        var sourceLabel = new Label
        {
            Text = "Quelle: github.com/tesseract-ocr/tessdata_fast (Apache-2.0)",
            Location = new Point(16, 36),
            Size = new Size(528, 18),
            AutoSize = false,
            ForeColor = SystemColors.GrayText,
        };

        // Manueller-Download-Bereich: pro Sprache ein LinkLabel.
        var linksLabel = new Label
        {
            Text = "Manueller Download (öffnet im Browser):",
            Location = new Point(16, 60),
            Size = new Size(528, 18),
            AutoSize = false,
        };

        var linksY = 82;
        for (int i = 0; i < _missing.Count; i++)
        {
            var entry = _missing[i];
            var url = TessdataManager.DefaultBaseUrl + entry.FileName;
            var link = new LinkLabel
            {
                Text = entry.FileName,
                Location = new Point(24, linksY + i * 20),
                Size = new Size(280, 18),
                AutoSize = false,
            };
            link.LinkClicked += (_, _) => OpenUrl(url);
            Controls.Add(link);
        }

        // Status + Progress (initial nur Status sichtbar, Progress erscheint beim Download).
        var filesLabel = new Label
        {
            Text = "Dateien werden nach " + _targetDir + " heruntergeladen:",
            Location = new Point(16, linksY + _missing.Count * 20 + 8),
            Size = new Size(528, 18),
            AutoSize = false,
        };

        _statusLabel = new Label
        {
            Text = "Bereit. Klicke 'Download Now' oder nutze die Links oben.",
            Location = new Point(16, linksY + _missing.Count * 20 + 28),
            Size = new Size(528, 18),
            AutoSize = false,
            ForeColor = SystemColors.ControlText,
        };

        _progressBar = new ProgressBar
        {
            Location = new Point(16, linksY + _missing.Count * 20 + 50),
            Size = new Size(528, 22),
            Visible = false,
            Style = ProgressBarStyle.Continuous,
            Minimum = 0,
            Maximum = _missing.Count,
        };

        var buttonY = linksY + _missing.Count * 20 + 88;

        _downloadButton = new Button
        {
            Text = "Download Now",
            Location = new Point(16, buttonY),
            Size = new Size(140, 32),
            DialogResult = DialogResult.OK,
        };
        _downloadButton.Click += (_, _) => StartDownload();

        _retryButton = new Button
        {
            Text = "Retry",
            Location = new Point(160, buttonY),
            Size = new Size(80, 32),
            Visible = false,
        };
        _retryButton.Click += (_, _) => StartDownload();

        _laterButton = new Button
        {
            Text = "Later",
            Location = new Point(260, buttonY),
            Size = new Size(100, 32),
            DialogResult = DialogResult.Cancel,
        };
        _laterButton.Click += (_, _) =>
        {
            Choice = DialogChoice.Later;
            _cts?.Cancel();
        };

        _neverButton = new Button
        {
            Text = "Don't Ask Again",
            Location = new Point(364, buttonY),
            Size = new Size(180, 32),
        };
        _neverButton.Click += (_, _) =>
        {
            Choice = DialogChoice.NeverAskAgain;
            _onPersistNeverAskAgain(true);
            DialogResult = DialogResult.Cancel;
            _cts?.Cancel();
            Close();
        };

        Controls.Add(infoLabel);
        Controls.Add(sourceLabel);
        Controls.Add(linksLabel);
        Controls.Add(filesLabel);
        Controls.Add(_statusLabel);
        Controls.Add(_progressBar);
        Controls.Add(_downloadButton);
        Controls.Add(_retryButton);
        Controls.Add(_laterButton);
        Controls.Add(_neverButton);
    }

    private async void StartDownload()
    {
        DownloadAttempted = true;
        Choice = DialogChoice.DownloadNow;
        _downloadButton.Enabled = false;
        _laterButton.Enabled = false;
        _neverButton.Enabled = false;
        _retryButton.Visible = false;
        _progressBar.Visible = true;
        _progressBar.Value = 0;
        _statusLabel.ForeColor = SystemColors.ControlText;
        _statusLabel.Text = "Starte Download...";

        _cts = new CancellationTokenSource();
        var progress = new Progress<TessdataDownloadProgress>(OnProgress);

        try
        {
            await _manager.DownloadAsync(
                _missing.Select(m => m.Code),
                _targetDir,
                progress,
                _cts.Token).ConfigureAwait(true);

            DownloadSucceeded = true;
            _logger?.Information(
                "tessdata download completed: {Count} file(s) to {Dir}",
                _missing.Count, _targetDir);

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException)
        {
            _statusLabel.ForeColor = SystemColors.ControlText;
            _statusLabel.Text = "Download abgebrochen.";
            ResetButtonsAfterFailure();
        }
        catch (Exception ex)
        {
            DownloadSucceeded = false;
            _logger?.Warning(ex, "tessdata download failed");
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Fehler: " + Truncate(ex.Message, 80);
            ResetButtonsAfterFailure();
        }
    }

    private void OnProgress(TessdataDownloadProgress p)
    {
        // Progress<T>-Callback läuft auf dem UI-Thread (SynchronizationContext
        // wird durch Progress<T>.Post erfasst). Trotzdem defensiv Invoke,
        // falls Tests den Dialog ohne UI-Thread treiben.
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => OnProgress(p)));
            return;
        }
        _progressBar.Value = Math.Min(p.CompletedCount, _progressBar.Maximum);
        _statusLabel.ForeColor = SystemColors.ControlText;
        _statusLabel.Text = p.CurrentLanguage is null
            ? $"{p.CompletedCount}/{p.TotalCount} ({FormatBytes(p.TotalBytesReceived)})"
            : $"Lade {p.CurrentLanguage}.traineddata... ({p.CompletedCount}/{p.TotalCount})";
    }

    private void ResetButtonsAfterFailure()
    {
        _progressBar.Visible = false;
        _downloadButton.Enabled = true;
        _laterButton.Enabled = true;
        _neverButton.Enabled = true;
        _retryButton.Visible = true;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open URL '{url}': {ex.Message}");
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024.0:F2} MB";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnFormClosing(e);
    }
}