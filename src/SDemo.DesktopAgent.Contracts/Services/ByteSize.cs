namespace SDemo.DesktopAgent.Services;

/// <summary>文件大小的短文本表示（1.2 KB / 3.4 MB）。Core 与 UI 共用，避免各自复制一份。</summary>
public static class ByteSize
{
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
