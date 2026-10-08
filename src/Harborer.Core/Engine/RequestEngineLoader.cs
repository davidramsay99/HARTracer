using System.Reflection;

namespace Harborer.Core.Engine;

/// <summary>
/// Loads Harborer.Net on demand. Nothing else in Harborer.Core or Harborer.App names a type from that
/// assembly, so the runtime never loads it until <see cref="Load"/> is called.
/// </summary>
public static class RequestEngineLoader
{
    public const string AssemblyName = "Harborer.Net";
    public const string EngineTypeName = "Harborer.Net.RequestEngine";

    private static IRequestEngine? s_engine;

    public static bool IsNetworkAssemblyLoaded =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.Ordinal));

    /// <summary>Loads the engine. Callers must check Offline Mode first; this method does not.</summary>
    public static IRequestEngine Load()
    {
        if (s_engine is not null)
        {
            return s_engine;
        }

        var assembly = Assembly.Load(new AssemblyName(AssemblyName));
        var type = assembly.GetType(EngineTypeName, throwOnError: true)!;
        s_engine = (IRequestEngine)Activator.CreateInstance(type)!;
        return s_engine;
    }
}
