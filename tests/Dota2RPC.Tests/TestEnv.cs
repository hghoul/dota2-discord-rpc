using System.Runtime.CompilerServices;
using Dota2RPC.Core;

namespace Dota2RPC.Tests;

internal static class TestEnv
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Все данные программы во время тестов — во временной папке, а не в профиле пользователя.
        var home = Path.Combine(Path.GetTempPath(), "dota2rpc-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("DOTA2RPC_HOME", home);
    }

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static AppSettings Settings(Action<AppSettings>? change = null)
    {
        var s = new AppSettings { GsiToken = "0123456789abcdef0123456789abcdef", ShowEmoji = false, AnimatedHeroIcons = false };
        change?.Invoke(s);
        return s;
    }
}
