// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Transport;

public static class AirPlayRequestProfile
{
    // Receiver compatibility token, not the WinToPod product version. OS 27
    // rejects unrecognised product/version tokens before processing pairing.
    // Wire evidence and independent receiver reports: docs/PROTOCOL.md.
    public const string UserAgent = "AirPlay/999.0.0";
    public const string PairingContentType = "application/octet-stream";
    public const string Revision = "events-and-remembered-playback";
}
