using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SDemo.DesktopAgent.Services;

namespace SDemo.DesktopAgent.Context;

/// <summary>
/// 一个 provider/model 的校准记录。Factor 是「实际 input token ÷ 估算」的 EMA。
/// WindowOverride 来自 400 自愈，不写回用户的显式配置（§3.2 / §3.8）。
/// </summary>
public sealed class CalibrationEntry
{
    public double Factor { get; set; } = 1.0;

    public int Samples { get; set; }

    /// <summary>连续「偏差 &gt; 25%」的次数；达到 3 次自动停用校准。</summary>
    public int MismatchStreak { get; set; }

    /// <summary>停用后只用 1.0 因子，等用户点「重置校准」再恢复。</summary>
    public bool Disabled { get; set; }

    public int? WindowOverride { get; set; }

    public DateTime? UpdatedAt { get; set; }
}

/// <summary>一次校准记录的结果，便于调用方写日志 / 报告。</summary>
public readonly record struct CalibrationResult(double RawRatio, double Factor, bool Disabled, bool Skipped);

/// <summary>
/// token 校准存储（§3.3）：文件 <c>%APPDATA%\SDemo\DesktopAgent\context-calibration.json</c>。
/// 键是 <c>{providerId}/{modelId}</c>，与模型配置分开，避免污染 settings.json。
/// </summary>
public sealed class TokenCalibrationStore
{
    /// <summary>EMA 的新样本权重。</summary>
    private const double Alpha = 0.3;

    /// <summary>因子上下限：超出说明估算或服务端 usage 有问题，继续放大只会更糟。</summary>
    private const double MinFactor = 0.7;
    private const double MaxFactor = 1.6;

    /// <summary>偏差超过这个比例记为一次「异常」。</summary>
    private const double MismatchThreshold = 0.25;

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, CalibrationEntry> _entries;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TokenCalibrationStore(IAppPaths paths)
        : this(Path.Combine(paths.DataRoot, "context-calibration.json"))
    {
    }

    public TokenCalibrationStore(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
    }

    public string FilePath => _filePath;

    /// <summary>当前因子；停用或没有样本时返回 1.0。</summary>
    public double GetFactor(string providerId, string modelId)
    {
        lock (_gate)
        {
            var entry = Find(providerId, modelId);
            return entry is null || entry.Disabled || entry.Samples == 0
                ? 1.0
                : Math.Clamp(entry.Factor, MinFactor, MaxFactor);
        }
    }

    /// <summary>400 学到的窗口；没有返回 null。</summary>
    public int? GetWindowOverride(string providerId, string modelId)
    {
        lock (_gate)
        {
            return Find(providerId, modelId)?.WindowOverride;
        }
    }

    /// <summary>记录一次成功的请求：用到的是「本次实际输入 token / 本累计估算」。</summary>
    public CalibrationResult RecordUsage(
        string providerId,
        string modelId,
        int estimatedInputTokens,
        int actualInputTokens)
    {
        if (string.IsNullOrWhiteSpace(providerId)
            || string.IsNullOrWhiteSpace(modelId)
            || estimatedInputTokens <= 0
            || actualInputTokens <= 0)
        {
            // 服务端不返回 usage：跳过校准，报告里标 estimated=true。
            return new CalibrationResult(1.0, 1.0, false, Skipped: true);
        }

        lock (_gate)
        {
            var entry = GetOrCreate(providerId, modelId);

            if (entry.Disabled)
            {
                return new CalibrationResult(1.0, 1.0, Disabled: true, Skipped: false);
            }

            var rawRatio = (double)actualInputTokens / Math.Max(estimatedInputTokens, 1);
            var clamped = Math.Clamp(rawRatio, MinFactor, MaxFactor);

            entry.Factor = Math.Clamp(
                entry.Factor * (1 - Alpha) + clamped * Alpha,
                MinFactor,
                MaxFactor);
            entry.Samples++;
            entry.UpdatedAt = DateTime.Now;

            if (Math.Abs(rawRatio - 1) > MismatchThreshold)
            {
                entry.MismatchStreak++;

                Debug.WriteLine(
                    $"上下文估算偏差 {(rawRatio - 1) * 100:F0}%（{providerId}/{modelId}，"
                    + $"估算 {estimatedInputTokens}，实际 {actualInputTokens}，连续 {entry.MismatchStreak} 次）");

                // 连续 3 次异常：停用校准，避免把误差放大到裁剪策略里。
                if (entry.MismatchStreak >= 3)
                {
                    entry.Disabled = true;
                    Debug.WriteLine($"已停用 {providerId}/{modelId} 的上下文校准，请在设置里重置校准。");
                }
            }
            else
            {
                entry.MismatchStreak = 0;
            }

            Save();
            return new CalibrationResult(rawRatio, GetFactorLocked(entry), entry.Disabled, Skipped: false);
        }
    }

    /// <summary>400 自愈：记下服务端告诉我们的真实窗口。</summary>
    public void LearnWindow(string providerId, string modelId, int window)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId) || window <= 0)
        {
            return;
        }

        lock (_gate)
        {
            var entry = GetOrCreate(providerId, modelId);
            entry.WindowOverride = window;
            entry.UpdatedAt = DateTime.Now;
            Save();
        }
    }

    /// <summary>设置页「重置校准」：清空所有因子与学到的窗口。</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
        }
    }

    /// <summary>只清一个模型（测试 / 排查用）。</summary>
    public void Reset(string providerId, string modelId)
    {
        lock (_gate)
        {
            _entries.Remove(Key(providerId, modelId));
            Save();
        }
    }

    public IReadOnlyDictionary<string, CalibrationEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToDictionary(
                pair => pair.Key,
                pair => new CalibrationEntry
                {
                    Factor = pair.Value.Factor,
                    Samples = pair.Value.Samples,
                    MismatchStreak = pair.Value.MismatchStreak,
                    Disabled = pair.Value.Disabled,
                    WindowOverride = pair.Value.WindowOverride,
                    UpdatedAt = pair.Value.UpdatedAt
                },
                StringComparer.Ordinal);
        }
    }

    private static string Key(string providerId, string modelId) => $"{providerId}/{modelId}";

    private CalibrationEntry GetOrCreate(string providerId, string modelId)
    {
        var key = Key(providerId, modelId);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new CalibrationEntry();
            _entries[key] = entry;
        }

        return entry;
    }

    private CalibrationEntry? Find(string providerId, string modelId) =>
        _entries.TryGetValue(Key(providerId, modelId), out var entry) ? entry : null;

    private static double GetFactorLocked(CalibrationEntry entry) =>
        entry.Disabled ? 1.0 : Math.Clamp(entry.Factor, MinFactor, MaxFactor);

    private static Dictionary<string, CalibrationEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, CalibrationEntry>(StringComparer.Ordinal);
            }

            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<Dictionary<string, CalibrationEntry>>(json, JsonOptions);

            return entries is null
                ? new Dictionary<string, CalibrationEntry>(StringComparer.Ordinal)
                : new Dictionary<string, CalibrationEntry>(entries, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 文件损坏时从空开始，不影响启动。
            Debug.WriteLine($"读取上下文校准文件失败（{path}）：{ex.Message}");
            return new Dictionary<string, CalibrationEntry>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = _filePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temporary, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去不影响本次请求：校准只是优化项。
            Debug.WriteLine($"写入上下文校准文件失败（{_filePath}）：{ex.Message}");
        }
    }
}
