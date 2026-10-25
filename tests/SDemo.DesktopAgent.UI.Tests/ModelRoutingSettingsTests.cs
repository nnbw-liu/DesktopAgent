using Bunit;
using Microsoft.Extensions.DependencyInjection;
using SDemo.DesktopAgent.Components.Settings;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.UI.Tests;

/// <summary>设置页的智能路由分区：能渲染、能切换模式并落盘。</summary>
public sealed class ModelRoutingSettingsTests : TestContext
{
    private readonly InMemorySettings _settings = new();

    public ModelRoutingSettingsTests()
    {
        ChatRoomFixture.SeedProvider(_settings.Current);
        Services.AddSingleton<ISettingsService>(_settings);
    }

    [Fact]
    public void 渲染智能路由分区并切换工作方式()
    {
        var cut = RenderComponent<ModelSettings>();

        Assert.Contains("智能模型路由", cut.Markup);
        Assert.Contains("规则（本地判断，零额外请求）", cut.Markup);
        // 路由设置与提供商列表共用滚动容器，底部「添加提供商」保持固定。
        Assert.NotNull(cut.Find(".models-scroll"));
        Assert.NotNull(cut.Find(".provider-add-bar"));

        // 两块都可以收起：路由默认收起，提供商默认展开。
        var folds = cut.FindAll("details.settings-collapsible");
        Assert.Equal(2, folds.Count);
        Assert.False(folds.ElementAt(0).HasAttribute("open"));
        Assert.True(folds.ElementAt(1).HasAttribute("open"));

        // 第一个下拉是工作方式；切到混合后映射区出现。
        cut.FindAll("select").ElementAt(0).Change("Hybrid");

        Assert.Equal(ModelRoutingMode.Hybrid, _settings.Current.ModelRouting.Mode);
        Assert.NotEmpty(cut.FindAll(".settings-subsection"));
        Assert.True(_settings.SaveCount > 0);
    }

    [Fact]
    public void 关闭时只保留工作方式下拉()
    {
        var cut = RenderComponent<ModelSettings>();

        Assert.Empty(cut.FindAll(".settings-subsection"));
        Assert.Contains("智能模型路由", cut.Markup);
    }
}
