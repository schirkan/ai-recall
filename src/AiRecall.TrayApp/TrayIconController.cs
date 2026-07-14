using AiRecall.Core.Configuration;
using AiRecall.Trigger;
using Serilog;

namespace AiRecall.TrayApp;

/// <summary>
/// Verwaltet das NotifyIcon + ContextMenuStrip im System-Tray und verdrahtet
/// die Aktionen (Start/Stop, Logviewer, Settings, Quit) mit dem
/// <see cref="TriggerSupervisor"/>. Status-Subscriptions halten Menu und
/// Tooltip synchron (Spec 0006 Schritt 4).
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startRecordingItem;
    private readonly ToolStripMenuItem _stopRecordingItem;
    // Spec 0014 Iter. 3: manuelle Audio-Steuerung ueber das Tray-Menu.
    // Privacy-First-Gate ueber AppConfig.Audio.Enabled (Spec 0013 v0.3).
    private readonly ToolStripMenuItem _startAudioItem;
    private readonly ToolStripMenuItem _stopAudioItem;
    private readonly ToolStripMenuItem _showLogsItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _quitItem;
    private readonly TriggerSupervisor _supervisor;
    private readonly Func<AppConfig> _configProvider;
    // Spec 0014 Iter. 3: Provider fuer die aktuelle IRecordingControl-Instanz.
    // Liefert null solange kein TriggerService laeuft (z. B. vor Start oder
    // nach Crashed). Lazy via Func, weil die Instanz zur Laufzeit wechselt
    // (Hot-Reload via ApplyConfig erzeugt neuen TriggerService + MeetingTrigger).
    private readonly Func<IRecordingControl?>? _recordingControlProvider;
    // Aktuell an RecordingStateChanged gebundene Control-Instanz. Wird bei
    // jedem RebindRecordingControl() aktualisiert, damit alte Events sauber
    // abgemeldet werden und keine Mehrfach-Subscriptions entstehen.
    private IRecordingControl? _boundRecordingControl;
    // Bug-Bash 2026-07-06 I-15: Capture-Counter aktualisiert sich waehrend
    // 'Running' nicht mehr von selbst, weil StateChanged nur bei
    // Zustandsuebergaengen feuert. StatusRefreshTimer pollt 1/s den Counter
    // und ruft ApplyState, solange der Supervisor laeuft.
    private readonly System.Windows.Forms.Timer _statusRefreshTimer;
    private readonly MenuImageCache _menuImages = new();
    // Welcher Tray-Icon-Key aktuell am _notifyIcon haengt. Idempotente
    // Updates: wir setzen das Icon nur bei State-Wechsel neu (sonst
    // flackert der Shell-Cache, und FromHandle kann zu HFON-Leak
    // fuehren).
    private string? _currentTrayIconKey;
    private bool _disposed;

    public event EventHandler? ExitRequested;
    public event EventHandler? ShowLogviewerRequested;
    public event EventHandler? ShowSettingsRequested;

    /// <summary>
    /// Zeigt eine Balloon-Tip am Tray-Icon an. Wird vom TrayAppContext fuer
    /// nicht-blockierende User-Hinweise verwendet (z. B. OCR-Init-Fehler).
    /// </summary>
    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Warning, int timeoutMs = 8000)
    {
        if (_disposed) return;
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(timeoutMs);
    }

    /// <summary>Enables the "Live Logviewer" menu item (call after LogviewerWindow is ready).</summary>
    public void EnableLogviewer() => _showLogsItem.Enabled = true;

    /// <summary>Enables the "Settings" menu item (call after SettingsDialog is ready).</summary>
    public void EnableSettings() => _settingsItem.Enabled = true;

    /// <summary>
    /// Konstruktor. <paramref name="recordingControlProvider"/> ist optional —
    /// ohne Provider (z. B. in alten Tests) sind die Audio-Items deaktiviert.
    /// Der Provider wird per <see cref="RebindRecordingControl"/> bei jedem
    /// <c>Supervisor.StateChanged</c> erneut aufgerufen, damit Hot-Reload
    /// (Service-Restart mit neuer <c>MeetingTrigger</c>-Instanz) automatisch
    /// die richtige Control bindet (Spec 0014 Iter. 3).
    /// </summary>
    public TrayIconController(
        TriggerSupervisor supervisor,
        Func<AppConfig> configProvider,
        Func<IRecordingControl?>? recordingControlProvider = null)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _recordingControlProvider = recordingControlProvider;

        _statusItem = new ToolStripMenuItem("Stopped")
        {
            Enabled = false,   // Status-Item ist nicht klickbar
            Image = _menuImages.GetOrAdd("status-stopped", () => _menuImages.GetOrAddEmbeddedIcon("status-stopped.ico").ToBitmap())
        };
        _startRecordingItem = new ToolStripMenuItem("Start Recording")
        {
            ShortcutKeys = Keys.Control | Keys.S,
            Image = _menuImages.GetOrAdd("start", () => _menuImages.GetOrAddEmbeddedIcon("start.ico").ToBitmap())
        };
        _stopRecordingItem = new ToolStripMenuItem("Stop Recording")
        {
            ShortcutKeys = Keys.Control | Keys.T,
            Enabled = false,
            Image = _menuImages.GetOrAdd("stop", () => _menuImages.GetOrAddEmbeddedIcon("stop.ico").ToBitmap())
        };
        _showLogsItem = new ToolStripMenuItem("Live Logviewer…")
        {
            ShortcutKeys = Keys.Control | Keys.L,
            Enabled = false,   // aktiv in Schritt 5 (Spec 0008) — via EnableLogviewer()
            Image = _menuImages.GetOrAdd("logs", () => _menuImages.GetOrAddEmbeddedIcon("logs.ico").ToBitmap())
        };
        _settingsItem = new ToolStripMenuItem("Settings…")
        {
            ShortcutKeys = Keys.Control | Keys.Oemcomma,
            Enabled = false,   // aktiv in Schritt 6 (Spec 0009) — via EnableSettings()
            Image = _menuImages.GetOrAdd("settings", () => _menuImages.GetOrAddEmbeddedIcon("settings.ico").ToBitmap())
        };
        _quitItem = new ToolStripMenuItem("Quit")
        {
            ShortcutKeys = Keys.Control | Keys.Q,
            // Bug-Bash 2026-07-06 I-UE: Quit-Icon wird jetzt aus "x"-Emoji
            // gerendert statt aus embedded quit.ico. EmojiIconFactory nutzt
            // den gleichen COLR/CPAL-Pfad wie bei anderen Menu-Icons — falls
            // der User spaeter weitere Menu-Icons auf Emoji umstellt, bleibt
            // der Code konsistent. Grosse=SmallIconSize skaliert sauber
            // mit HiDPI (16/24/32).
            Image = _menuImages.GetOrAdd(
                "quit-emoji",
                () => EmojiIconFactory.RenderBitmap("❌", SystemInformation.SmallIconSize.Height))
        };

        // Spec 0014 Iter. 3: manuelle Audio-Steuerung. Initial deaktiviert
        // (Enabled=false) — wird durch RebindRecordingControl() aktiviert,
        // sobald ein IRecordingControl-Provider eine Instanz liefert und
        // Audio.Enabled=true ist. Privacy-First-Gate ueber Visible=false
        // solange Audio.Enabled=false.
        _startAudioItem = new ToolStripMenuItem("🎙 Audio aufnehmen")
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.R,
            Enabled = false,
            Visible = _configProvider().Audio.Enabled,
            Image = _menuImages.GetOrAdd(
                "audio-record-emoji",
                () => EmojiIconFactory.RenderBitmap("🎙", SystemInformation.SmallIconSize.Height))
        };
        _stopAudioItem = new ToolStripMenuItem("🎙 Audio stoppen")
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.T,
            Enabled = false,
            Visible = _configProvider().Audio.Enabled,
            Image = _menuImages.GetOrAdd(
                "audio-stop-emoji",
                () => EmojiIconFactory.RenderBitmap("⏹", SystemInformation.SmallIconSize.Height))
        };

        _menu = new ContextMenuStrip();
        // Kein ImageScalingSize-Override: WinForms waehlt den Default-Slot
        // aus SystemInformation.SmallIconSize (16x16 @ 100% DPI, 24x24
        // @ 150%, 32x32 @ 200%). Das matched die RenderBitmap(size:16)-
        // Bitmaps bei Standard-DPI 1:1 und skaliert proportional bei HiDPI.
        // Fruehere Override-Versuche haben das Layout verschlechtert.
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            _startRecordingItem,
            _stopRecordingItem,
            new ToolStripSeparator(),
            // Spec 0014 Iter. 3: Audio-Items zwischen Capture- und Logviewer-
            // Block. Privacy-First-Gate ueber Visible (s.o.). Zusatzlicher
            // Separator trennt die Audio-Gruppe von den Capture-Items.
            _startAudioItem,
            _stopAudioItem,
            new ToolStripSeparator(),
            _showLogsItem,
            _settingsItem,
            new ToolStripSeparator(),
            _quitItem
        });

        // WinForms-Quirk: ToolStripMenuItem.Visible ist default false (anders
        // als die .NET-Doku suggeriert). Damit die Items nach AddRange
        // sichtbar sind (wenn Audio.Enabled=true), muessen wir Visible hier
        // nochmal explizit setzen — RebindRecordingControl macht das dann
        // spaeter beim jeden StateChanged wieder, falls Audio.Enabled
        // sich aendert.
        _startAudioItem.Visible = _configProvider().Audio.Enabled;
        _stopAudioItem.Visible = _configProvider().Audio.Enabled;

        // Tray-Icon = Capture-Indikator (Multi-Resolution .ico aus Embedded
        // Resource). Running -> 👁️ (Eye), sonst -> ⚫ (Black Circle). Die
        // .ico-Dateien werden vom EmojiIconGen-Tool generiert und ueber
        // AiRecall.TrayApp.csproj als EmbeddedResource eingebunden — kein
        // GDI+-Runtime-Rendering, daher zuverlaessig fuer NotifyIcon.
        _notifyIcon = new NotifyIcon
        {
            Icon = ResolveTrayIcon(),
            Text = "AiRecall",
            Visible = true,
            ContextMenuStrip = _menu
        };

        // Bug-Bash 2026-07-06 I-15: 1s-Tick zum Capture-Counter-Refresh.
        // Wird in OnSupervisorStateChanged gesteuert (Start/Stop) statt im
        // Timer selbst, damit der Refresh exakt an die Lifecycle-Phasen
        // gekoppelt ist.
        _statusRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = 1000
        };
        _statusRefreshTimer.Tick += (_, _) => RefreshStatus();
        _statusRefreshTimer.Start();

        // Supervisor -> UI
        _supervisor.StateChanged += OnSupervisorStateChanged;
        ApplyState(TrayIconState.FromSupervisor(
            _supervisor.State,
            captureCount: 0,
            crashCount: _supervisor.CrashCount));

        // UI -> Supervisor
        _startRecordingItem.Click += (_, _) =>
        {
            Log.Information("Menu: Start Recording clicked");
            try
            {
                _supervisor.Start(_configProvider());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Start failed from menu");
                ShowError("Failed to start recording", ex);
            }
        };
        _stopRecordingItem.Click += (_, _) =>
        {
            Log.Information("Menu: Stop Recording clicked");
            _supervisor.Stop();
        };
        // Spec 0014 Iter. 3: manuelle Audio-Click-Handler. Click ist nur
        // moeglich, wenn RebindRecordingControl() eine IRecordingControl
        // gebunden hat (Enabled wird dort auf Basis von IsRecording gesetzt)
        // UND Audio.Enabled=true (Visible-Gate). Provider-Lookup erfolgt
        // lazy im Click-Handler, damit nach Hot-Reload die aktuelle
        // MeetingTrigger-Instanz verwendet wird (nicht eine alte, ggf. schon
        // disposte Referenz aus _boundRecordingControl).
        _startAudioItem.Click += (_, _) =>
        {
            Log.Information("Menu: Start Audio (manual) clicked");
            var control = _recordingControlProvider?.Invoke();
            if (control is null)
            {
                Log.Warning("Menu: Start Audio clicked but no IRecordingControl available");
                ShowError("Audio-Aufnahme nicht verfügbar", new InvalidOperationException(
                    "Kein TriggerService aktiv. Bitte zuerst die Capture-Recording starten."));
                return;
            }
            try
            {
                // Fire-and-forget: StartManualAsync ist async, Click-Handler
                // ist void. Fehler (z. B. InvalidOperationException bei
                // Single-Active-Violation) werden via ShowError sichtbar.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var key = await control.StartManualAsync(CancellationToken.None).ConfigureAwait(false);
                        Log.Information("Menu: manual audio recording started (key={Key})", key);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Menu: Start Audio failed");
                        // MessageBox muss auf UI-Thread laufen — BeginInvoke,
                        // weil wir gerade auf Threadpool sind.
                        try
                        {
                            _notifyIcon.ContextMenuStrip?.BeginInvoke(new Action(() =>
                                ShowError("Audio-Aufnahme fehlgeschlagen", ex)));
                        }
                        catch (Exception invokeEx)
                        {
                            Log.Error(invokeEx, "Menu: BeginInvoke for error dialog failed");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Menu: Start Audio sync dispatch failed");
                ShowError("Audio-Aufnahme fehlgeschlagen", ex);
            }
        };
        _stopAudioItem.Click += (_, _) =>
        {
            Log.Information("Menu: Stop Audio (manual) clicked");
            var control = _recordingControlProvider?.Invoke();
            if (control is null)
            {
                Log.Warning("Menu: Stop Audio clicked but no IRecordingControl available");
                return;
            }
            try
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await control.StopAsync().ConfigureAwait(false);
                        Log.Information("Menu: manual audio recording stopped");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Menu: Stop Audio failed");
                        try
                        {
                            _notifyIcon.ContextMenuStrip?.BeginInvoke(new Action(() =>
                                ShowError("Audio-Stop fehlgeschlagen", ex)));
                        }
                        catch (Exception invokeEx)
                        {
                            Log.Error(invokeEx, "Menu: BeginInvoke for error dialog failed");
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Menu: Stop Audio sync dispatch failed");
                ShowError("Audio-Stop fehlgeschlagen", ex);
            }
        };
        _showLogsItem.Click += (_, _) =>
        {
            Log.Information("Menu: Show Logviewer requested");
            ShowLogviewerRequested?.Invoke(this, EventArgs.Empty);
        };
        _settingsItem.Click += (_, _) =>
        {
            Log.Information("Menu: Show Settings requested");
            ShowSettingsRequested?.Invoke(this, EventArgs.Empty);
        };
        _quitItem.Click += (_, _) =>
        {
            Log.Information("Menu: Quit clicked");
            ExitRequested?.Invoke(this, EventArgs.Empty);
        };
        _notifyIcon.DoubleClick += OnDoubleClick;

        Log.Information("TrayIconController wired to TriggerSupervisor (state={State})", _supervisor.State);

        // Spec 0014 Iter. 3: initial an die Recording-Control binden, falls
        // der Supervisor schon eine Instanz hat (z. B. nach Hot-Reload, wenn
        // der TrayApp neu startet waerend TriggerService laeuft — defensive).
        RebindRecordingControl();
    }

    private void OnSupervisorStateChanged(object? sender, TriggerStateChangedEventArgs e)
    {
        // Beim Stop die letzte Counter-Synchronisation noch einmal forcieren,
        // damit der Status nicht "Running — N captures" einfriert, nachdem
        // der Timer gleich pausiert.
        RefreshStatus();
        // Spec 0014 Iter. 3: nach jedem Supervisor-State-Wechsel kann eine
        // andere MeetingTrigger-Instanz aktiv sein (Running => neu erzeugt,
        // Stopped => disposed). RebindRecordingControl haengt die alte
        // Recording-State-Subscription sauber ab und an die neue an.
        RebindRecordingControl();
    }

    /// <summary>
    /// Bindet den Tray-Controller an die aktuelle <see cref="IRecordingControl"/>-
    /// Instanz. Idempotent — bei wiederholtem Aufruf ohne Wechsel wird die
    /// Subscription nicht doppelt registriert. Wird im Konstruktor, bei jedem
    /// <c>Supervisor.StateChanged</c> und aus Tests heraus aufgerufen
    /// (<see cref="RebindRecordingControlForTest"/>).
    /// </summary>
    private void RebindRecordingControl()
    {
        // Spec 0014 Iter. 3 Privacy-First-Gate: Visible folgt Audio.Enabled.
        // Wird hier ebenfalls aktualisiert, weil RefreshStatus (1 Hz-Timer)
        // nur alle 1s tickt und Hot-Reload von Audio.Enabled sofort wirken soll.
        var audioEnabled = _configProvider().Audio.Enabled;
        _startAudioItem.Visible = audioEnabled;
        _stopAudioItem.Visible = audioEnabled;

        // Alte Subscription abmelden, falls vorhanden. Setzt auch _bound-
        // RecordingControl auf null, damit die anschliessende Null-Pruefung
        // saubere Re-Bind-Logik ermoeglicht.
        var current = _recordingControlProvider?.Invoke();
        if (ReferenceEquals(current, _boundRecordingControl))
        {
            // Keine Aenderung — nur Enabled/Visible-State refresh.
            ApplyRecordingEnabledState();
            return;
        }

        if (_boundRecordingControl is not null)
        {
            _boundRecordingControl.RecordingStateChanged -= OnRecordingStateChanged;
            _boundRecordingControl = null;
        }

        if (current is null)
        {
            // Kein TriggerService aktiv — Audio-Items disabled, kein Event.
            _startAudioItem.Enabled = false;
            _stopAudioItem.Enabled = false;
            Log.Debug("TrayIconController: no IRecordingControl available, audio items disabled");
            return;
        }

        _boundRecordingControl = current;
        _boundRecordingControl.RecordingStateChanged += OnRecordingStateChanged;
        ApplyRecordingEnabledState();
        Log.Information("TrayIconController: bound to IRecordingControl (IsRecording={IsRecording})",
            _boundRecordingControl.IsRecording);
    }

    /// <summary>
    /// Aktualisiert Enabled-State der Audio-Items basierend auf der aktuell
    /// gebundenen Recording-Control. Wird nach jedem Rebind und nach jedem
    /// RecordingStateChanged-Event aufgerufen.
    /// </summary>
    private void ApplyRecordingEnabledState()
    {
        // Spec 0014 Iter. 3 Privacy-First-Gate: Audio.Enabled muss true sein,
        // damit Recording-State ueberhaupt Audio-Items enablen darf. Sonst
        // bleiben beide Items disabled - auch wenn IsRecording==true gerade
        // ein laufendes Meeting signalisiert (Spec 0016 First-Run-Dialog
        // und AES-Datenminimierung).
        if (_boundRecordingControl is null || !audioEnabledForItems())
        {
            _startAudioItem.Enabled = false;
            _stopAudioItem.Enabled = false;
            return;
        }
        if (_boundRecordingControl.IsRecording)
        {
            _startAudioItem.Enabled = false;   // Single-Active-Constraint
            _stopAudioItem.Enabled = true;
        }
        else
        {
            _startAudioItem.Enabled = true;
            _stopAudioItem.Enabled = false;
        }
    }

    private bool audioEnabledForItems() => _configProvider().Audio.Enabled;

    /// <summary>
    /// Reagiert auf RecordingStateChanged der gebundenen IRecordingControl und
    /// schaltet die Audio-Items in den korrekten Enabled-State (analog zu den
    /// Capture-Items bei Supervisor.StateChanged).
    /// </summary>
    private void OnRecordingStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        Log.Debug("TrayIconController: RecordingStateChanged received (IsRecording={IsRecording}, Source={Source})",
            e.IsRecording, e.Source);
        ApplyRecordingEnabledState();
        // Spec 0014 Iter. 2: Audio hat Prioritaet vor Capture-Icon.
        // Wenn Audio laeuft, zeigen wir das Mikrofon-Icon statt des
        // Eye-Icons — auch wenn Supervisor.State==Running.
        UpdateTrayIcon();
    }

    private void RefreshStatus()
    {
        if (_disposed) return;
        var state = TrayIconState.FromSupervisor(
            _supervisor.State,
            captureCount: (int)(_supervisor.Service?.CaptureCount ?? 0),
            crashCount: _supervisor.CrashCount);
        ApplyState(state);
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        Log.Information("Tray icon double-clicked (state={State})", _supervisor.State);
        if (_supervisor.State == TriggerState.Running)
        {
            _supervisor.Stop();
        }
        else if (_supervisor.State == TriggerState.Stopped || _supervisor.State == TriggerState.Crashed)
        {
            try
            {
                _supervisor.Start(_configProvider());
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Toggle-Start failed");
                ShowError("Failed to start recording", ex);
            }
        }
    }

    private static void ShowError(string title, Exception ex)
    {
        // Fire-and-forget; User sieht's, kann's wegklicken, läuft auf UI-Thread
        // (Click-Handler laufen auf UI-Thread)
        MessageBox.Show(
            $"{title}\n\n{ex.GetType().Name}: {ex.Message}",
            "AiRecall",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private void ApplyState(TrayIconState state)
    {
        if (_disposed) return;
        _statusItem.Text = state.StatusText;
        _statusItem.Image = _menuImages.GetOrAdd(
            $"status-{state.IconGlyph}",
            () => _menuImages.GetOrAddEmbeddedIcon(StateGlyphToResource(state.IconGlyph)).ToBitmap());
        _startRecordingItem.Enabled = state.StartEnabled;
        _stopRecordingItem.Enabled = state.StopEnabled;
        UpdateTrayIcon();
        _notifyIcon.Text = TruncateForTooltip(state.TooltipText, 63);
        Log.Debug("TrayIcon state applied: {StatusText} (start={Start}, stop={Stop})",
            state.StatusText, state.StartEnabled, state.StopEnabled);
    }

    /// <summary>NotifyIcon.Text is limited to 63 chars on Windows; truncate gracefully.</summary>
    private static string TruncateForTooltip(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..(maxLen - 1)] + "…";

    /// <summary>
    /// Test-Hook: loest ein Rebinding an die Recording-Control aus, ohne
    /// auf ein Supervisor.StateChanged-Event zu warten. Wird von
    /// TrayIconControllerAudioItemsTests verwendet, um State-Wechsel
    /// deterministisch zu simulieren.
    /// </summary>
    internal void RebindRecordingControlForTest() => RebindRecordingControl();

    /// <summary>
    /// Test-Hook: liefert das Start-Audio-MenuItem fuer Verifikationen
    /// (Visible, Enabled, Text). Read-only-Zugriff, weil Tests das Item
    /// nicht mutieren sollen — sie pruefen nur Observer-State.
    /// </summary>
    internal ToolStripMenuItem StartAudioItemForTest => _startAudioItem;

    /// <summary>
    /// Test-Hook: liefert das Stop-Audio-MenuItem fuer Verifikationen
    /// (Visible, Enabled, Text).
    /// </summary>
    internal ToolStripMenuItem StopAudioItemForTest => _stopAudioItem;

    /// <summary>
    /// Test-Hook: liefert die aktuell gebundene IRecordingControl-Instanz
    /// (oder null). Tests koennen damit verifizieren, dass Rebind die
    /// richtige Control-Instanz gebunden hat.
    /// </summary>
    internal IRecordingControl? BoundRecordingControlForTest => _boundRecordingControl;

    /// <summary>
    /// Liefert das Tray-<see cref="Icon"/> fuer den aktuellen Zustand aus
    /// dem Embedded-Resource-Cache. Prioritaet (Spec 0014 Iter. 2):
    ///   Audio laeuft           -> tray-audio-recording.ico (roter Kreis + M)
    ///   Capture laeuft         -> tray-recording.ico       (👁️ Eye)
    ///   sonst                  -> tray-idle.ico           (⚫ Black Circle)
    /// Bei Crashed bewusst Idle, weil keine Aufnahme laeuft.
    /// </summary>
    private Icon ResolveTrayIcon()
    {
        var key = ResolveTrayIconKey();
        return _menuImages.GetOrAddEmbeddedIcon(key);
    }

    /// <summary>
    /// Berechnet den Resource-Key fuer <see cref="ResolveTrayIcon"/>.
    /// Audio hat Vorrang vor Capture (Spec 0014 Iter. 2), weil Audio
    /// explizit manuell ausgeloest wurde und der User wissen soll, dass
    /// sein Mikrofon aktiv ist — Capture ist da eher Hintergrund.
    /// </summary>
    private string ResolveTrayIconKey() =>
        ResolveTrayIconKey(_supervisor.State, _boundRecordingControl?.IsRecording ?? false);

    /// <summary>
    /// Pure-Function-Variante von <see cref="ResolveTrayIconKey()"/>.
    /// <c>internal</c> fuer Tests (InternalsVisibleTo AiRecall.Core.Tests
    /// ist im csproj gesetzt), damit State-Kombinationen ohne
    /// NotifyIcon-Instanziierung geprueft werden koennen — keine WinForms-
    /// Handle-Leaks in Tests.
    /// </summary>
    internal static string ResolveTrayIconKey(TriggerState supervisorState, bool isAudioRecording)
    {
        if (isAudioRecording)
            return "tray-audio-recording.ico";
        return supervisorState == TriggerState.Running
            ? "tray-recording.ico"
            : "tray-idle.ico";
    }

    /// <summary>
    /// Aktualisiert das Tray-Icon idempotent. Wir setzen das Icon nur
    /// bei State-Wechsel neu, weil NotifyIcon sonst den HFON-Cache
    /// staendig invalidiert und das Shell-Paint stoert.
    /// Wird von OnSupervisorStateChanged UND OnRecordingStateChanged
    /// aufgerufen (Spec 0014 Iter. 2).
    /// </summary>
    private void UpdateTrayIcon()
    {
        var key = ResolveTrayIconKey();
        if (_currentTrayIconKey == key) return;
        _notifyIcon.Icon = ResolveTrayIcon();
        _currentTrayIconKey = key;
    }

    /// <summary>
    /// Mappt das <see cref="TrayIconState.IconGlyph"/> auf den
    /// Embedded-Resource-Namen der .ico-Datei. Die Status-Glyphen
    /// (🔴/🟡/🟢/⚠) sind fix in TrayIconState codiert — hier nur die
    /// Uebersetzung auf den Asset-Namen.
    /// </summary>
    private static string StateGlyphToResource(string glyph) => glyph switch
    {
        "🔴" => "status-stopped.ico",
        "🟡" => "status-starting.ico",
        "🟢" => "status-running.ico",
        "⚠" => "status-crashed.ico",
        _ => "status-stopped.ico"
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusRefreshTimer.Stop();
        _statusRefreshTimer.Dispose();
        _supervisor.StateChanged -= OnSupervisorStateChanged;
        // Spec 0014 Iter. 3: Recording-Control-Subscription abmelden, damit
        // keine Events auf disposed Controller feuern.
        if (_boundRecordingControl is not null)
        {
            _boundRecordingControl.RecordingStateChanged -= OnRecordingStateChanged;
            _boundRecordingControl = null;
        }
        _notifyIcon.DoubleClick -= OnDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        // Items vor Menu-Dispose von ihren Images trennen, sonst doppelte
        // Freigabe (Menu.Dispose laeuft ueber die Images).
        _statusItem.Image = null;
        _startRecordingItem.Image = null;
        _stopRecordingItem.Image = null;
        _startAudioItem.Image = null;
        _stopAudioItem.Image = null;
        _showLogsItem.Image = null;
        _settingsItem.Image = null;
        _quitItem.Image = null;
        _menu.Dispose();
        _menuImages.Dispose();
        Log.Information("TrayIconController disposed");
    }
}
