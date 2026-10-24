using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>模型档案解析（§3.2）：显式配置优先，其次名字推断，最后 provider 默认。</summary>
public sealed class ModelProfileResolverTests
{
    private static ModelProvider Provider(ModelProfile? defaultProfile = null) => new()
    {
        Id = "p",
        DisplayName = "测试",
        BaseUrl = "https://example.com/v1",
        DefaultProfile = defaultProfile
    };

    [Fact]
    public void 显式档位覆盖模型名推断()
    {
        var entry = new ModelEntry { Id = "deepseek-chat", Profile = ModelProfilePresets.FromPreset("32K") };
        var profile = ModelProfileResolver.ResolveConfigured(Provider(), "deepseek-chat", entry);

        // 名字推断会给 128K，但用户显式选了 32K。
        Assert.Equal(32_768, profile.ContextWindow);
        Assert.Equal("32K", profile.Preset);
    }

    [Fact]
    public void 显式关闭思考的档案解析后仍然保留_none()
    {
        // 「不思考」落的是 none 而不是 null：解析、夹取之后都不能被弄丢，否则请求又变成“端点默认”。
        var entry = new ModelEntry
        {
            Id = "qwen3.5-4b",
            Profile = ModelProfilePresets.FromPreset("32K")! with { ReasoningEffort = "none" }
        };

        var profile = ModelProfileResolver.ResolveConfigured(Provider(), "qwen3.5-4b", entry);

        Assert.Equal("none", profile.ReasoningEffort);
        Assert.Equal(32_768, profile.ContextWindow);
    }

    [Fact]
    public void 模型名里的档位优先于家族前缀()
    {
        var profile = ModelProfileResolver.ResolveConfigured(Provider(), "qwen2.5-32k-instruct");

        Assert.Equal(32_768, profile.ContextWindow);
    }

    [Fact]
    public void 家族前缀推断窗口()
    {
        Assert.Equal(131_072, ModelProfileResolver.ResolveConfigured(Provider(), "deepseek-v4.1-flash").ContextWindow);
        Assert.Equal(204_800, ModelProfileResolver.ResolveConfigured(Provider(), "claude-sonnet").ContextWindow);
        Assert.Equal(32_768, ModelProfileResolver.ResolveConfigured(Provider(), "llama-3-8b").ContextWindow);
    }

    [Fact]
    public void provider默认先于未知模型()
    {
        var provider = Provider(ModelProfilePresets.FromPreset("128K"));
        var profile = ModelProfileResolver.ResolveConfigured(provider, "totally-unknown-model");

        Assert.Equal(131_072, profile.ContextWindow);
    }

    [Fact]
    public void 未知模型按8K保守处理()
    {
        var profile = ModelProfileResolver.ResolveConfigured(Provider(), "totally-unknown-model");

        Assert.Equal(8192, profile.ContextWindow);
        Assert.Equal(2048, profile.MaxOutputTokens);
        Assert.True(profile.IsSmallModel);
    }

    [Fact]
    public void 归一化夹住非法窗口与输出()
    {
        var normalized = ModelProfileResolver.Normalize(new ModelProfile(0, 999_999));

        Assert.Equal(1024, normalized.ContextWindow);
        Assert.Equal(512, normalized.MaxOutputTokens);
    }

    [Fact]
    public void 学到窗口只覆盖未知模型_不覆盖显式配置()
    {
        var root = Path.Combine(Path.GetTempPath(), "sdemo-profile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var calibration = new TokenCalibrationStore(Path.Combine(root, "calibration.json"));
            calibration.LearnWindow("p", "unknown", 65_536);
            calibration.LearnWindow("p", "fixed", 65_536);

            var resolver = new ModelProfileResolver(calibration);
            var learned = resolver.Resolve(Provider(), "unknown");
            var explicit8K = resolver.Resolve(Provider(), "fixed", new ModelEntry
            {
                Id = "fixed",
                Profile = ModelProfilePresets.FromPreset("8K")
            });

            Assert.Equal(65_536, learned.ContextWindow);
            Assert.Equal(8192, explicit8K.ContextWindow);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
