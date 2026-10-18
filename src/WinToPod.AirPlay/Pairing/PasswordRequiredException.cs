// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinToPod.AirPlay.Pairing;

public sealed class PasswordRequiredException(bool rejected = false) : IOException(rejected
    ? "输入的AirPlay密码未通过验证，也可能受到音箱访问权限限制。"
    : "音箱要求输入AirPlay密码。")
{ public bool Rejected { get; } = rejected; }
