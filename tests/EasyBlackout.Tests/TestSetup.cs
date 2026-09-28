using System.Runtime.CompilerServices;
using EasyBlackout.Core.Logging;

namespace EasyBlackout.Tests;

internal static class TestSetup
{
    /// <summary>Keep test runs out of the real application log.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Log.Initialize(Path.Combine(Path.GetTempPath(), "EasyBlackoutTests", "logs"));
}
