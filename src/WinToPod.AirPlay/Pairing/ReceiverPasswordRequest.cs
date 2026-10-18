// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;

namespace WinToPod.AirPlay.Pairing;

// Identifies the physical receiver that requested a password. The answer must
// never be copied to an unprotected sibling or written to diagnostics/settings.
public sealed record ReceiverPasswordRequest(IPAddress RemoteAddress, bool ForPlayback = false);
