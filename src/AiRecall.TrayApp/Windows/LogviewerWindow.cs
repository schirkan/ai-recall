using System.ComponentModel;
using AiRecall.Trigger;
using Serilog;
using Serilog.Events;

namespace AiRecall.TrayApp.Windows;

/// <summary>
/// Live log viewer window (Spec 0008). Subscribes to a
/// <see cref="LogviewerSession"/> and shows a filtered DataGridView of
/// log events with level + search filters, pause, clear, and auto-scroll.
///
/// WinForms-only; pure-logic (filter, buffer) lives in
/// <see cref="LogFilter"/> + <see cref="LogviewerSession"/>.
/// </summary>
public sealed class LogviewerWindow : Form
{
    private readonly LogviewerSession _session;
    private readonly DataGridView _grid;
    private readonly ToolStripComboBox _levelFilterCombo;
    private readonly ToolStripTextBox _searchTextBox;
    private readonly ToolStripButton _pauseButton;
    private readonly ToolStripButton _clearButton;
    private readonly ToolStripButton _autoScrollCheck;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly BindingList<LogEventEntry> _bindingList;
    private bool _suppressGridRefresh;
    // Bug-Bash 2026-07-05 I-9: Wenn der User manuell weg vom Ende scrollt,
    // wird Auto-Scroll automatisch ausgeschaltet (sonst schiesst das naechste
    // EventAppended den User immer wieder nach unten). _programmaticScrolling
    // verhindert, dass ScrollToEnd() den Listener selber ausloest.
    private bool _programmaticScrolling;

    public LogviewerWindow(LogviewerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        Text = "AiRecall — Live Logviewer";
        Width = 900;
        Height = 500;
        // Bug-Bash 2026-07-06 I-UE: Konsistente Platzierung unten rechts
        // mit 20 px Padding (Settings-Dialog ebenfalls).
        StartPosition = FormStartPosition.Manual;
        WindowPlacement.PositionBottomRight(this, padding: 20);
        ShowInTaskbar = true;

        // Toolbar
        var toolbar = new ToolStrip();
        toolbar.Items.Add(new ToolStripLabel("Level:"));
        _levelFilterCombo = new ToolStripComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _levelFilterCombo.Items.AddRange(new object[]
        {
            "All", "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"
        });
        _levelFilterCombo.SelectedIndex = 0;
        _levelFilterCombo.SelectedIndexChanged += (_, _) => ApplyFilter();
        toolbar.Items.Add(_levelFilterCombo);

        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Search:"));
        _searchTextBox = new ToolStripTextBox { Width = 150 };
        _searchTextBox.TextChanged += (_, _) => ApplyFilter();
        toolbar.Items.Add(_searchTextBox);

        toolbar.Items.Add(new ToolStripSeparator());
        _pauseButton = new ToolStripButton("⏸ Pause");
        _pauseButton.CheckOnClick = true;
        _pauseButton.CheckedChanged += (_, _) =>
        {
            _session.IsPaused = _pauseButton.Checked;
            _pauseButton.Text = _session.IsPaused ? "▶ Resume" : "⏸ Pause";
            Log.Debug("Logviewer: pause toggled to {Paused}", _session.IsPaused);
        };
        toolbar.Items.Add(_pauseButton);

        _clearButton = new ToolStripButton("🗑 Clear");
        _clearButton.Click += (_, _) => _session.ClearBuffer();
        toolbar.Items.Add(_clearButton);

        toolbar.Items.Add(new ToolStripSeparator());
        // .NET 8 hat kein ToolStripCheckBox — ToolStripButton mit CheckOnClick ist
        // die kanonische Alternative (gleiches Checked-Bool, gleiches Verhalten).
        _autoScrollCheck = new ToolStripButton { CheckOnClick = true, Checked = true, Text = "Auto-Scroll" };
        toolbar.Items.Add(_autoScrollCheck);

        // DataGridView
        _bindingList = new BindingList<LogEventEntry>();
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            DataSource = _bindingList,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            VirtualMode = false,
            BackgroundColor = SystemColors.Window
        };

        // Spalten formatieren
        _grid.AutoGenerateColumns = false;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogEventEntry.Timestamp),
            HeaderText = "Time",
            FillWeight = 20,
            DefaultCellStyle = new DataGridViewCellStyle { Format = "HH:mm:ss.fff" }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogEventEntry.Level),
            HeaderText = "Level",
            FillWeight = 10
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogEventEntry.Logger),
            HeaderText = "Logger",
            FillWeight = 20
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = nameof(LogEventEntry.Message),
            HeaderText = "Message",
            FillWeight = 50
        });
        // Color-Coding pro Zeile
        _grid.CellFormatting += OnCellFormatting;
        _grid.Scroll += OnGridScroll;

        // Status-Bar
        _statusLabel = new ToolStripStatusLabel();
        var statusStrip = new StatusStrip();
        statusStrip.Items.Add(_statusLabel);

        // Layout
        Controls.Add(_grid);
        Controls.Add(toolbar);
        Controls.Add(statusStrip);
        // StatusStrip docked bottom by default; toolbar top; grid fill
        toolbar.Dock = DockStyle.Top;
        statusStrip.Dock = DockStyle.Bottom;

        // Initiale Population + Live-Subscribe
        _session.EventAppended += OnEventAppended;
        _session.Cleared += OnSessionCleared;
        ApplyFilter();      // populate + filter (no scroll — grid not laid out yet)
        UpdateStatus();

        // Bug-Bash 2026-07-06 I-13: Auto-Scroll erst nach Shown.
        // Im Konstruktor hat das DataGridView noch RowCount == 0, das Setzen
        // von FirstDisplayedScrollingRowIndex wuerfe ArgumentOutOfRangeException.
        // Nach Shown ist die initiale Layout-Pass durch.
        this.Shown += (_, _) =>
        {
            if (_autoScrollCheck.Checked) ScrollToEnd();
        };
    }

    private void OnEventAppended(object? sender, LogEventEntry e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnEventAppended(sender, e));
            return;
        }
        if (_session.IsPaused) return;

        if (_session.Filter.Matches(e))
        {
            _bindingList.Add(e);
            UpdateStatus();
            if (_autoScrollCheck.Checked) ScrollToEnd();
        }
    }

    private void OnSessionCleared(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnSessionCleared(sender, e));
            return;
        }
        _bindingList.Clear();
        UpdateStatus();
    }

    private void ApplyFilter()
    {
        if (_suppressGridRefresh) return;
        _suppressGridRefresh = true;
        try
        {
            // Map combo -> level
            _session.Filter.MinLevel = _levelFilterCombo.SelectedIndex switch
            {
                1 => LogEventLevel.Verbose,
                2 => LogEventLevel.Debug,
                3 => LogEventLevel.Information,
                4 => LogEventLevel.Warning,
                5 => LogEventLevel.Error,
                6 => LogEventLevel.Fatal,
                _ => null
            };
            _session.Filter.SearchText = string.IsNullOrEmpty(_searchTextBox.Text) ? null : _searchTextBox.Text;

            // Repopulate
            _bindingList.RaiseListChangedEvents = false;
            _bindingList.Clear();
            foreach (var entry in _session.Snapshot())
            {
                if (_session.Filter.Matches(entry)) _bindingList.Add(entry);
            }
            _bindingList.RaiseListChangedEvents = true;
            _bindingList.ResetBindings();
        }
        finally
        {
            _suppressGridRefresh = false;
        }
        UpdateStatus();
        if (_autoScrollCheck.Checked) ScrollToEnd();
    }

    private void UpdateStatus()
    {
        _statusLabel.Text = $"Events: {_bindingList.Count} / Buffer: {_session.BufferCount} | Filter: {_levelFilterCombo.SelectedItem} | {(_session.IsPaused ? "Paused" : "Live")}";
    }

    private void ScrollToEnd()
    {
        if (_bindingList.Count == 0) return;
        var lastIdx = _bindingList.Count - 1;
        // Bug-Bash 2026-07-06 I-13: Range-Check gegen Grid-RowCount (nicht
        // BindingList-Count). Vor dem ersten Layout-Pass (z. B. im Konstruktor
        // oder bevor das Form sichtbar wird) hat das Grid 0 Rows und ein
        // beliebiger FirstDisplayedScrollingRowIndex > 0 wirft
        // ArgumentOutOfRangeException.
        if (lastIdx < 0 || lastIdx >= _grid.RowCount) return;
        _programmaticScrolling = true;
        try
        {
            _grid.FirstDisplayedScrollingRowIndex = lastIdx;
        }
        catch (ArgumentOutOfRangeException)
        {
            // Defense in Depth: kann in Randfaellen auftreten, wenn das Grid
            // waehrend eines BeginInvoke-Batches relayoutet wird. Swallow —
            // naechstes EventAppended versucht es erneut.
        }
        finally
        {
            _programmaticScrolling = false;
        }
    }

    private void OnGridScroll(object? sender, ScrollEventArgs e)
    {
        // Programmatic Scroll (durch ScrollToEnd) soll den Auto-Scroll-Toggle
        // nicht anfassen — nur User-Initiiertes Scroll zaehlt.
        if (_programmaticScrolling) return;

        // User-Scroll: wenn die Top-Row nicht mehr die letzte sichtbare Zeile
        // ist, hat der User weg vom Ende navigiert. Auto-Scroll deaktivieren,
        // damit das naechste EventAppended den User nicht wieder nach unten
        // reisst. (Bug-Bash 2026-07-05 I-9)
        if (!_autoScrollCheck.Checked) return;
        if (_bindingList.Count == 0) return;
        var lastVisibleIdx = _grid.FirstDisplayedScrollingRowIndex + _grid.DisplayedRowCount(true) - 1;
        var lastRowIdx = _bindingList.Count - 1;
        if (lastVisibleIdx < lastRowIdx)
        {
            _autoScrollCheck.Checked = false;
        }
    }

    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _bindingList.Count) return;
        var entry = _bindingList[e.RowIndex];
        if (e.ColumnIndex == 1) // Level-Spalte
        {
            e.CellStyle!.ForeColor = entry.Level switch
            {
                LogEventLevel.Verbose => Color.Gray,
                LogEventLevel.Debug => Color.SteelBlue,
                LogEventLevel.Information => Color.Black,
                LogEventLevel.Warning => Color.DarkOrange,
                LogEventLevel.Error => Color.Firebrick,
                LogEventLevel.Fatal => Color.DarkRed,
                _ => Color.Black
            };
            e.CellStyle.Font = entry.Level >= LogEventLevel.Error
                ? new Font(_grid.Font, FontStyle.Bold)
                : _grid.Font;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _session.EventAppended -= OnEventAppended;
        _session.Cleared -= OnSessionCleared;
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Bug-Bash 2026-07-06 I-UE: Position nach OnShown ggf. nachjustieren
        // (synchron mit Settings-Dialog).
        WindowPlacement.OnShown(this);
    }
}