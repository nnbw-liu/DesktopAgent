using System.Reflection;

namespace SDemo.DesktopAgent.Plugins;

/// <summary>
/// 宿主版本，只用来和清单里的 <c>minHostVersion</c> 比大小。
/// 不做严格 semver：预发布后缀（<c>-beta</c> / <c>+sha</c>）直接忽略，缺省段按 0 补齐。
/// </summary>
public sealed record PluginHostVersion(int Major, int Minor, int Build = 0) : IComparable<PluginHostVersion>
{
    /// <summary>当前应用（Core 程序集）的版本。</summary>
    public static PluginHostVersion Current { get; } = FromAssembly(typeof(PluginHostVersion).Assembly);

    public int CompareTo(PluginHostVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Build.CompareTo(other.Build);
    }

    public override string ToString() => $"{Major}.{Minor}.{Build}";

    /// <summary>解析 "1.2.3" / "1.2" / "v2.0.0-beta.1"；解析不出来返回 false。</summary>
    public static bool TryParse(string? text, out PluginHostVersion version)
    {
        version = new PluginHostVersion(0, 0, 0);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        // 丢掉预发布 / 构建元数据后缀。
        var cut = value.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        var parts = value.Split('.', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length && i < 3; i++)
        {
            if (!int.TryParse(parts[i], out var number) || number < 0)
            {
                return false;
            }

            numbers[i] = number;
        }

        version = new PluginHostVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>读取程序集的展示版本（优先 informational version）。</summary>
    public static PluginHostVersion FromAssembly(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (TryParse(informational, out var parsed))
        {
            return parsed;
        }

        var version = assembly.GetName().Version;
        return version is null
            ? new PluginHostVersion(0, 0, 0)
            : new PluginHostVersion(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
