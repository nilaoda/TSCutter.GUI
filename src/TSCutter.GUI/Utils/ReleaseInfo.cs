using System;
using System.Reflection;

namespace TSCutter.GUI.Utils;

/// <summary>Release metadata supplied by the project file and read once at startup.</summary>
internal static class ReleaseInfo
{
    public static readonly string Version;
    public static readonly string Stage;
    public static readonly string Tag;
    public static readonly string WindowTitle;
    public static readonly string AboutVersion;

    static ReleaseInfo()
    {
        foreach (var attribute in typeof(ReleaseInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            switch (attribute.Key)
            {
                case "ReleaseVersion": Version = attribute.Value!; break;
                case "ReleaseStage": Stage = attribute.Value!; break;
                case "ReleaseTag": Tag = attribute.Value!; break;
            }
        }

        if (string.IsNullOrEmpty(Version) || string.IsNullOrEmpty(Stage) || string.IsNullOrEmpty(Tag))
            throw new InvalidOperationException("Release metadata is missing from the application assembly.");

        WindowTitle = $"TSCutter.GUI - {Stage} {Version}";
        AboutVersion = $"v{Version} ({Stage})";
    }
}
