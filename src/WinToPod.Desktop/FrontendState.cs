// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Desktop;

// View state only. No hidden WinForms controls participate in audio decisions.
internal class ActionState(string text)
{
    public string Text { get; set; } = text;
    public bool Enabled { get; set; } = true;
    public bool Visible { get; set; } = true;
}
internal sealed class StatusState(string text) : ActionState(text)
{ public object? Tag { get; set; } }
internal sealed class VolumeState
{
    private int _value = 20;
    public int Value
    {
        get => _value;
        set { var next = Math.Clamp(value, 0, 100); if (next == _value) return; _value = next; ValueChanged?.Invoke(this, EventArgs.Empty); }
    }
    public event EventHandler? ValueChanged;
}
