// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Media;

public sealed record VolumeConfirmationResult(bool Confirmed, double ActualDecibels, int Reads);

public static class VolumeConfirmation
{
    // SET happens once in the session. Only a valid but not-yet-matching GET
    // is read again. Network, ownership and invalid-data failures propagate.
    public static async Task<VolumeConfirmationResult> ReadAsync(double requested, bool mute,
        Func<CancellationToken, Task<double>> read, CancellationToken cancellation,
        Action<int, double, bool>? observed = null)
    {
        var actual = double.NaN;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (attempt > 1) await Task.Delay(100 * (attempt - 1), cancellation).ConfigureAwait(false);
            actual = await read(cancellation).ConfigureAwait(false);
            if (!double.IsFinite(actual) || actual is < -144.25 or > .25)
                throw new InvalidDataException("音箱返回了无效音量。");
            // Receivers may normalize mute to their floor or previous level;
            // the session also zeros PCM until a nonzero value is confirmed.
            var confirmed = mute || Math.Abs(actual - requested) <= .25;
            observed?.Invoke(attempt, actual, confirmed);
            if (confirmed) return new(true, actual, attempt);
        }
        return new(false, actual, 4);
    }
}
