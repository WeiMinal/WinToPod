// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Media;

// Cumulative, numeric-only snapshot. Arrival buckets are non-overlapping and
// count valid packets from the selected source, including rejected samples.
public readonly record struct PtpClockDiagnostics(
    long IntervalLe150Ms, long IntervalLe250Ms, long IntervalLe500Ms,
    long IntervalLe1000Ms, long IntervalLe2000Ms, long IntervalGt2000Ms,
    long MaxIntervalNanoseconds, long RejectedOffsetSamples, long MaxOffsetJumpNanoseconds,
    long SampleResets, long GapResets, long ReadinessLosses,
    long MaxSampleIntervalNanoseconds, long SampleAgeNanoseconds, long ObservationWindowNanoseconds)
{
    internal string DiagnosticFields => FormattableString.Invariant(
        $"到包间隔桶[≤150/250/500/1000/2000/>2000ms]={IntervalLe150Ms}/{IntervalLe250Ms}/{IntervalLe500Ms}/{IntervalLe1000Ms}/{IntervalLe2000Ms}/{IntervalGt2000Ms}；最大到包间隔ms={MaxIntervalNanoseconds / 1e6:F3}；突变拒绝={RejectedOffsetSamples}；最大绝对突变ms={MaxOffsetJumpNanoseconds / 1e6:F3}；样本清零={SampleResets}；间隔清零={GapResets}；就绪丢失={ReadinessLosses}；最大有效样本间隔ms={MaxSampleIntervalNanoseconds / 1e6:F3}；样本年龄ms={SampleAgeNanoseconds / 1e6:F3}；观测窗口ms={ObservationWindowNanoseconds / 1e6:F3}");
}
