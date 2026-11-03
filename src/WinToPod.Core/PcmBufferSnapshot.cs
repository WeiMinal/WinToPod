// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.Core;

// One coherent read under the buffer lock; counters remain cumulative across Clear.
public readonly record struct PcmBufferSnapshot(long DroppedFrames, long MissingFrames, int BufferedFrames, int CapacityFrames);
