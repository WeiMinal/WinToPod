// SPDX-License-Identifier: GPL-3.0-or-later
using WinToPod.AirPlay.Discovery;
namespace WinToPod.Desktop;
internal sealed class ReceiverSelection
{
    private PlaybackEntry[] _entries = [];
    public IReadOnlyList<PlaybackEntry> Entries => _entries;
    public PlaybackEntry? SelectedEntry { get; private set; }
    public int TargetCount => _entries.Length;
    public event EventHandler? ItemSelectionChanged;
    public void ReplaceTargets(IEnumerable<PlaybackEntry> entries, string? key)
    { _entries = entries.ToArray(); SelectedEntry = _entries.FirstOrDefault(x => x.Key == key); ItemSelectionChanged?.Invoke(this, EventArgs.Empty); }
    public bool SelectTarget(string key)
    { var row = _entries.FirstOrDefault(x => x.Key == key); if (row is null) return false; if (SelectedEntry?.Key == key) return true; SelectedEntry = row; ItemSelectionChanged?.Invoke(this, EventArgs.Empty); return true; }
}

internal sealed record DeviceViewState(IReadOnlyList<PlaybackEntry> Entries, string? SelectedKey, string? ActiveKey,
    bool Searching, bool Working, bool Connected, bool CanConnect, bool CanSingle, string ConnectText, string Status, bool Error, int Volume, bool ConnectionBlocked = false);

