using System;

namespace Unnamed42.FriendlyNoclip;

// 离线 harness 用的替身：真实 Plugin 是 MelonMod，这里只需要 Log.Error/Msg 能被调用。
// 这样 StubAssembler / StubFactory 就**不用改一行**就能在活进程之外编译运行。
internal sealed class LogShim
{
    internal void Error(string message) => Console.WriteLine($"[shim:error] {message}");
    internal void Warning(string message) => Console.WriteLine($"[shim:warn] {message}");
    internal void Msg(string message) => Console.WriteLine($"[shim:msg] {message}");
}

internal static class Plugin
{
    internal static LogShim Log = new();
}
