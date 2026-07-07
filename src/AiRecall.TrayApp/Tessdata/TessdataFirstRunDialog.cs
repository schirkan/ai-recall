using System.Windows.Forms;
using AiRecall.Core.Tessdata;

namespace AiRecall.TrayApp.Tessdata;

/// <summary>
/// Modal-Dialog beim ersten Start der TrayApp, wenn tessdata für die
/// konfigurierten OCR-Sprachen fehlt (Spec 0012 v0.1).
///
/// **Status:** Skeleton-Stub v0.1 (Bug-Bash 2026-07-06).
/// Zeigt die fehlenden Sprachen und drei Buttons:
/// <list type="bullet">
///   <item><b>Download Now</b> — startet sofort den Download via <see cref="TessdataManager"/>.</item>
///   ///   <item><b>Later</b> — Dialog schließen, später per Balloon erinnern.</item>
///   ///   <item><b>Don't Ask Again</b> — User-Auswahl persistieren, Dialog nie wieder zeigen.</item>
/// </list>
///
/// Spec-Detail (vollständige UX, Progress-Bar, Error-Handling beim
/// Download-Fehler) folgt in eigenem Cluster.
/// </summary>
public sealed class TessdataFirstRunDialog : Form
{
    /// <summary>User-Auswahl aus dem Dialog.</summary>
    public enum DialogChoice
    {
        /// <summary>User klickte "Download Now" — Download wurde gestartet (oder Stub).</summary>
        DownloadNow,
        /// <summary>User klickte "Later" — Balloon-Erinnerung.</summary>
        Later,
        /// <summary>User klickte "Don't Ask Again" — Auswahl persistiert.</summary>
        NeverAskAgain,
    }

    private readonly IReadOnlyList<MissingLanguage> _missing;
    private readonly TessdataManager _manager;
    private readonly Action<bool> _onPersistNeverAskAgain;

    /// <summary>Resultat nach <see cref="Form.ShowDialog"/>.</summary>
    public DialogChoice Choice { get; private set; } = DialogChoice.Later;

    public TessdataFirstRunDialog(
        IReadOnlyList<MissingLanguage> missing,
        TessdataManager manager,
        Action<bool> onPersistNeverAskAgain)
    {
        ArgumentNullException.ThrowIfNull(missing);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(onPersistNeverAskAgain);

        _missing = missing;
        _manager = manager;
        _onPersistNeverAskAgain = onPersistNeverAskAgain;

        Text = "AiRecall — OCR tessdata fehlt";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 240);
        Font = new Font("Segoe UI", 9F);

        var infoLabel = new Label
        {
            Text = $"Tesseract-OCR ist konfiguriert, aber für {missing.Count} Sprache(n) " +
                   $"fehlen die tessdata-Dateien:\n\n  - {string.Join("\n  - ", missing.Select(m => m.FileName))}\n\n" +
                   "Sollen die Dateien jetzt automatisch heruntergeladen werden?",
            Location = new Point(16, 16),
            Size = new Size(448, 120),
            AutoSize = false,
        };

        var downloadButton = new Button
        {
            Text = "Download Now",
            Location = new Point(16, 160),
            Size = new Size(140, 32),
            DialogResult = DialogResult.OK,
        };
        downloadButton.Click += (_, _) =>
        {
            Choice = DialogChoice.DownloadNow;
            // TODO Spec 0012 v0.2: echten Download starten, Progress-Bar anzeigen.
        };

        var laterButton = new Button
        {
            Text = "Later",
            Location = new Point(170, 160),
            Size = new Size(100, 32),
            DialogResult = DialogResult.Cancel,
        };
        laterButton.Click += (_, _) =>
        {
            Choice = DialogChoice.Later;
        };

        var neverButton = new Button
        {
            Text = "Don't Ask Again",
            Location = new Point(284, 160),
            Size = new Size(180, 32),
        };
        neverButton.Click += (_, _) =>
        {
            Choice = DialogChoice.NeverAskAgain;
            _onPersistNeverAskAgain(true);
            DialogResult = DialogResult.Cancel;
            Close();
        };

        AcceptButton = downloadButton;
        CancelButton = laterButton;

        Controls.Add(infoLabel);
        Controls.Add(downloadButton);
        Controls.Add(laterButton);
        Controls.Add(neverButton);
    }
}