// Copyright (c) AiRecall Contributors.
// Licensed under the MIT License.
using System.Drawing;
using System.Windows.Forms;

namespace AiRecall.TrayApp.Windows;

/// <summary>
/// Einheitliche Fenster-Positionierung fuer TrayApp-Child-Fenster.
/// Bug-Bash 2026-07-06 I-UE: alle Dialoge (Settings, Logviewer) erscheinen
/// unten rechts am Bildschirmrand mit kleinem Padding, statt zentriert.
/// Vorteile:
///   - konsistente Position, egal wo der User gerade klickt
///   - Dialog verdeckt nicht das aktive Foreground-Fenster
///   - User findet das Fenster schnell wieder
/// </summary>
internal static class WindowPlacement
{
    /// <summary>
    /// Positioniert das Form unten rechts auf dem aktuellen Bildschirm
    /// (d. h. da, wo der Cursor gerade steht oder wo das Form den Focus hat).
    /// Wenn das Form zu gross fuer die Working-Area ist, wird es auf die
    /// Working-Area herunterskaliert (via <see cref="OnShown"/> nachtraeglich
    /// moeglich) und in den Eck-Bereich eingepasst.
    /// </summary>
    /// <param name="form">Ziel-Form. Muss <c>StartPosition=Manual</c> haben.</param>
    /// <param name="padding">Pixel Abstand zum Bildschirmrand (Default 20).</param>
    public static void PositionBottomRight(Form form, int padding = 20)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (padding < 0) padding = 0;

        var workingArea = Screen.PrimaryScreen?.WorkingArea
                          ?? new Rectangle(0, 0, Screen.PrimaryScreen?.Bounds.Width ?? 1920,
                                                Screen.PrimaryScreen?.Bounds.Height ?? 1080);

        // Form-Groesse an Working-Area anpassen, falls zu gross.
        // (OnShown kann das nachholen; hier vorab, damit Position plausibel ist.)
        int w = Math.Min(form.Width, workingArea.Width - 2 * padding);
        int h = Math.Min(form.Height, workingArea.Height - 2 * padding);

        int x = workingArea.Right - w - padding;
        int y = workingArea.Bottom - h - padding;

        // Sicherheitsnetz: nicht links/oberhalb des sichtbaren Bereichs.
        if (x < workingArea.Left + padding) x = workingArea.Left + padding;
        if (y < workingArea.Top + padding) y = workingArea.Top + padding;

        form.Location = new Point(x, y);
        // Speichere Padding als Tag-Wert, damit OnShown es nach WindowGrow
        // nochmal anwenden kann (Form.Size kann nach AutoSize/Resize groesser
        // sein als initial).
        form.Tag = padding;
    }

    /// <summary>
    /// Wird im <c>OnShown</c> der Form aufgerufen, um die Position nach
    /// der endgueltigen Size-Berechnung (AutoSize, Min/MaxSize) erneut
    /// zu setzen. Idempotent.
    /// </summary>
    public static void OnShown(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.Tag is int padding) PositionBottomRight(form, padding);
    }
}
