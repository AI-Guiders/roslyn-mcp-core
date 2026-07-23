using Microsoft.Build.Locator;

namespace RoslynMcp.ServiceLayer;

/// <summary>Process-once MSBuildLocator.RegisterDefaults — safe under parallel first calls.</summary>
public static class MsBuildLocatorOnce
{
    static readonly object Gate = new();
    static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
            return;
        lock (Gate)
        {
            if (_registered)
                return;
            try
            {
                MSBuildLocator.RegisterDefaults();
            }
            catch (InvalidOperationException)
            {
                // Already registered in this process.
            }

            _registered = true;
        }
    }
}
