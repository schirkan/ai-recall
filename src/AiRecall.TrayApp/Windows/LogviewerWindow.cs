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
    private readonly ToolStripCheckBox _autoScrollCheck;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly BindingList<LogEventEntry> _bindingList;
    private bool _suppressGridRefresh;

    public LogviewerWindow(LogviewerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        Text = "AiRecall — Live Logviewer";
        Width = 900;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
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
        _autoScrollCheck = new ToolStripCheckBox { Checked = true, Text = "Auto-Scroll" };
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
        ApplyFilter();      // populate + filter
        UpdateStatus();
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
        _grid.FirstDisplayedScrollingRowIndex = lastIdx;
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
}