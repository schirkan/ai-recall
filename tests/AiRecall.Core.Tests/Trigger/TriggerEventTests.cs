using System.Threading.Channels;
using AiRecall.Trigger;

namespace AiRecall.Core.Tests.Trigger;

public class TriggerEventTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var hwnd = new IntPtr(0x1234);
        var now = DateTimeOffset.Now;
        var ev = new TriggerEvent(hwnd, TriggerKind.Foreground, now);
        Assert.Equal(hwnd, ev.Hwnd);
        Assert.Equal(TriggerKind.Foreground, ev.Kind);
        Assert.Equal(now, ev.Timestamp);
    }

    [Fact]
    public void Record_EqualityWorks()
    {
        var hwnd = new IntPtr(0x1234);
        var now = DateTimeOffset.Now;
        var a = new TriggerEvent(hwnd, TriggerKind.Focus, now);
        var b = new TriggerEvent(hwnd, TriggerKind.Focus, now);
        var c = new TriggerEvent(hwnd, TriggerKind.Focus, now.AddMilliseconds(1));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void TriggerKind_HasAllExpectedValues()
    {
        // Reihenfolge und Existenz sind Teil der "Public API" — Spec 0005 §Trigger-Quellen.
        var values = Enum.GetValues<TriggerKind>();
        Assert.Contains(TriggerKind.Foreground, values);
        Assert.Contains(TriggerKind.Focus, values);
        Assert.Contains(TriggerKind.NameChange, values);
        Assert.Contains(TriggerKind.ValueChange, values);
        Assert.Contains(TriggerKind.Scroll, values);
        Assert.Contains(TriggerKind.MenuPopup, values);
        Assert.Contains(TriggerKind.Heartbeat, values);
    }

    [Fact]
    public void TriggerKind_DoesNotContainSelection()
    {
        // Selection wurde nach Martin-Review 2026-07-04 Punkt 2 entfernt.
        var names = Enum.GetNames<TriggerKind>();
        Assert.DoesNotContain("Selection", names);
    }

    [Fact]
    public void TriggerEvent_RoundtripThroughChannel()
    {
        var channel = Channel.CreateUnbounded<TriggerEvent>();
        var hwnd = new IntPtr(0xABCD);
        var ev = new TriggerEvent(hwnd, TriggerKind.NameChange, DateTimeOffset.Now);

        Assert.True(channel.Writer.TryWrite(ev));
        Assert.True(channel.Reader.TryRead(out var read));
        Assert.Equal(ev, read);
    }
}