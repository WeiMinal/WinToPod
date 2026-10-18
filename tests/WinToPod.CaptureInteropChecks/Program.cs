// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text.Json;
using WinToPod.Desktop;

var checks = new List<string>();
void Check(string name, bool passed) { if (!passed) throw new Exception(name); checks.Add(name); }
Check("x64 AUDIOCLIENT_ACTIVATION_PARAMS matches the Windows ABI", Marshal.SizeOf<ProcessAudioCapture.ActivationParameters>() == 12);
Check("x64 PROPVARIANT has the 24-byte layout", Marshal.SizeOf<ProcessAudioCapture.BlobVariant>() == 24);
Check("blob length is at byte 8", (int)Marshal.OffsetOf<ProcessAudioCapture.BlobVariant>("Size") == 8);
Check("blob pointer is at byte 16", (int)Marshal.OffsetOf<ProcessAudioCapture.BlobVariant>("Data") == 16);
Check("completion callback advertises agility", typeof(ProcessAudioCapture.IAgileCallback).GUID == new Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"));
Check("activation result interface matches the SDK", typeof(ProcessAudioCapture.IActivationOperation).GUID == new Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"));
Check("classic COM callback types are publicly visible", new[] { typeof(ProcessAudioCapture.IActivationCompletion), typeof(ProcessAudioCapture.IActivationOperation), typeof(ProcessAudioCapture.IAgileCallback), typeof(ProcessAudioCapture.ActivationCompletion) }.All(t => t.IsVisible && t.GetCustomAttributes(typeof(ComVisibleAttribute), false).OfType<ComVisibleAttribute>().SingleOrDefault()?.Value == true));
Console.WriteLine(JsonSerializer.Serialize(new { passed = true, checks, production_interop_linked = true, actual_windows_activation_tested = false }));
