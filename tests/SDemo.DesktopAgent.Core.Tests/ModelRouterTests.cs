using SDemo.DesktopAgent.Context;
using SDemo.DesktopAgent.Models;
using SDemo.DesktopAgent.Services;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>
/// 智能模型路由：分类、映射、窗口过滤、混合 / 总控、粘性、失败回落。
/// 不联网——路由模型由 FakeAdvisor 注入。
/// </summary>
public sealed class ModelRouterTests
{
    [Fact]
    public void 闲聊短句判为Chat()
    {
        var result = ModelTaskClassifier.Classify(Request("你好"), 0);

        Assert.Equal(ModelTaskKind.Chat, result.Task);
        Assert.True(result.Confidence >= 0.9);
    }

    [Fact]
    public void 代码修改判为Code()
    {
        var result = ModelTaskClassifier.Classify(Request("帮我改一下 settings.json 里的模型列表，并跑测试"), 0);

        Assert.Equal(ModelTaskKind.Code, result.Task);
    }

    [Fact]
    public void 跨文件重构判为Complex()
    {
        var result = ModelTaskClassifier.Classify(
            Request("帮我重构整个项目的模型路由，需要跨文件迁移并给出架构方案，同时排查性能问题"),
            0);

        Assert.Equal(ModelTaskKind.Complex, result.Task);
        Assert.True(result.Complexity >= 75);
    }

    [Fact]
    public void 简单问答的置信度故意偏低()
    {
        var result = ModelTaskClassifier.Classify(Request("解释一下什么是依赖注入"), 0);

        Assert.Equal(ModelTaskKind.Simple, result.Task);
        Assert.True(result.Confidence < 0.65);
    }

    [Fact]
    public async Task 手动固定模型时不调用路由模型()
    {
        var advisor = new FakeAdvisor();
        var router = new ModelRouter(new HeuristicTokenCounter(), advisor);

        var decision = await router.RouteAsync(Request(
            "帮我改代码",
            isAuto: false,
            settings: SettingsWithRoutes()));

        Assert.Equal(ModelRoutingSource.Manual, decision.Source);
        Assert.Equal("current", decision.ModelId);
        Assert.Equal(0, advisor.Calls);
    }

    [Fact]
    public async Task 规则模式按任务映射选择且不调用路由模型()
    {
        var advisor = new FakeAdvisor();
        var router = new ModelRouter(new HeuristicTokenCounter(), advisor);
        var request = Request(
            "帮我改一下 Program.cs 里的入口",
            settings: SettingsWithRoutes(),
            providers: Providers());

        var decision = await router.RouteAsync(request);

        Assert.Equal(ModelTaskKind.Code, decision.Task);
        Assert.Equal(ModelRoutingSource.Rules, decision.Source);
        Assert.Equal("code-model", decision.ModelId);
        Assert.Equal(0, advisor.Calls);
    }

    [Fact]
    public async Task 没有本类映射时使用兜底模型()
    {
        var settings = SettingsWithRoutes();
        settings.Routes.RemoveAll(route => route.Task == ModelTaskKind.Code);

        var router = new ModelRouter(new HeuristicTokenCounter());
        var decision = await router.RouteAsync(Request(
            "帮我改一下 Program.cs 里的入口",
            settings: settings,
            providers: Providers()));

        Assert.Equal("default-model", decision.ModelId);
        Assert.Contains("兜底", decision.Reason);
    }

    [Fact]
    public async Task 小窗口装不下时回落到能装下的兜底模型()
    {
        var settings = SettingsWithRoutes();
        // code-model 是 8K；把历史撑到 12K 以上，必须换窗口更大的模型。
        settings.DefaultProviderId = "p";
        settings.DefaultModelId = "large-model";

        var messages = new List<ChatMessage>
        {
            new(ChatMessageRole.User, new string('a', 24_000)),
            new(ChatMessageRole.User, "帮我改一下 Program.cs 里的入口")
        };

        var router = new ModelRouter(new HeuristicTokenCounter());
        var decision = await router.RouteAsync(new ModelRoutingRequest(
            "帮我改一下 Program.cs 里的入口",
            messages,
            Provider("p", "current"),
            "current",
            [Providers()],
            settings,
            IsAuto: true,
            SessionId: "s1"));

        Assert.Equal("large-model", decision.ModelId);
        Assert.Equal(ModelRoutingSource.Fallback, decision.Source);
        Assert.Contains("不可用", decision.Reason);
    }

    [Fact]
    public async Task 混合模式规则拿不准时才调用路由模型()
    {
        var advisor = new FakeAdvisor
        {
            Reply = new ModelRoutingAdvice(ModelTaskKind.Complex, Reason: "需要架构判断", Confidence: 0.9)
        };
        var router = new ModelRouter(new HeuristicTokenCounter(), advisor);
        var settings = SettingsWithRoutes();
        settings.Mode = ModelRoutingMode.Hybrid;

        var simple = await router.RouteAsync(Request("解释一下什么是依赖注入", settings: settings, providers: Providers()));
        Assert.Equal(ModelRoutingSource.Advisor, simple.Source);
        Assert.Equal(ModelTaskKind.Complex, simple.Task);
        Assert.Equal("complex-model", simple.ModelId);
        Assert.Equal(1, advisor.Calls);

        // 高置信度闲聊不再花一次路由调用。
        var chat = await router.RouteAsync(Request("你好", settings: settings, providers: Providers()));
        Assert.Equal(ModelRoutingSource.Rules, chat.Source);
        Assert.Equal(1, advisor.Calls);
    }

    [Fact]
    public async Task 总控模式接受路由模型点名的候选模型()
    {
        var advisor = new FakeAdvisor
        {
            Reply = new ModelRoutingAdvice(
                ModelTaskKind.Code,
                "p",
                "complex-model",
                "这个任务其实很难",
                0.8)
        };
        var router = new ModelRouter(new HeuristicTokenCounter(), advisor);
        var settings = SettingsWithRoutes();
        settings.Mode = ModelRoutingMode.Model;

        var decision = await router.RouteAsync(Request(
            "帮我改一下 Program.cs 里的入口",
            settings: settings,
            providers: Providers()));

        Assert.Equal(ModelRoutingSource.Advisor, decision.Source);
        Assert.Equal("complex-model", decision.ModelId);
        Assert.Contains("很难", decision.Reason);
    }

    [Fact]
    public async Task 总控模式只给任务类型时仍按用户映射选模型()
    {
        var advisor = new FakeAdvisor
        {
            Reply = new ModelRoutingAdvice(ModelTaskKind.Complex, Reason: "复杂", Confidence: 0.9)
        };
        var router = new ModelRouter(new HeuristicTokenCounter(), advisor);
        var settings = SettingsWithRoutes();
        settings.Mode = ModelRoutingMode.Model;

        var decision = await router.RouteAsync(Request(
            "帮我改一下 Program.cs 里的入口",
            settings: settings,
            providers: Providers()));

        Assert.Equal("complex-model", decision.ModelId);
    }

    [Fact]
    public async Task 输入预览只跑规则且不写粘性()
    {
        var advisor = new FakeAdvisor
        {
            Reply = new ModelRoutingAdvice(ModelTaskKind.Complex, Reason: "复杂", Confidence: 0.9)
        };
        var router = new ModelRouter(new HeuristicTokenCounter(), advisor);
        var settings = SettingsWithRoutes();
        settings.Mode = ModelRoutingMode.Hybrid;

        var preview = await router.RouteAsync(new ModelRoutingRequest(
            "解释一下什么是依赖注入",
            [new ChatMessage(ChatMessageRole.User, "解释一下什么是依赖注入")],
            Provider("p", "current"),
            "current",
            [Providers()],
            settings,
            IsAuto: true,
            SessionId: "s-preview",
            AllowAdvisor: false));

        Assert.Equal(ModelRoutingSource.Rules, preview.Source);
        Assert.Equal(0, advisor.Calls);
        Assert.StartsWith("预览", preview.Reason);

        // 真正发送时才调用路由模型，预览没有把规则结果粘住。
        var sent = await router.RouteAsync(new ModelRoutingRequest(
            "解释一下什么是依赖注入",
            [new ChatMessage(ChatMessageRole.User, "解释一下什么是依赖注入")],
            Provider("p", "current"),
            "current",
            [Providers()],
            settings,
            IsAuto: true,
            SessionId: "s-preview"));

        Assert.Equal(ModelRoutingSource.Advisor, sent.Source);
        Assert.Equal(1, advisor.Calls);
    }

    [Fact]
    public async Task 路由模型抛异常时回落规则且不抛给调用方()
    {
        var advisor = new FakeAdvisor { Failure = new HttpRequestException("boom") };
        var router = new ModelRouter(new HeuristicTokenCounter(), advisor);
        var settings = SettingsWithRoutes();
        settings.Mode = ModelRoutingMode.Hybrid;

        var decision = await router.RouteAsync(Request(
            "解释一下什么是依赖注入",
            settings: settings,
            providers: Providers()));

        Assert.Equal(ModelRoutingSource.Fallback, decision.Source);
        Assert.Contains("回落规则", decision.Reason);
    }

    [Fact]
    public async Task 同一会话同任务类型复用上一轮模型()
    {
        var advisor = new FakeAdvisor
        {
            Reply = new ModelRoutingAdvice(ModelTaskKind.Code, "p", "code-model", "改代码", 0.9)
        };
        var router = new ModelRouter(new HeuristicTokenCounter(), advisor);
        var settings = SettingsWithRoutes();
        settings.Mode = ModelRoutingMode.Model;

        var first = await router.RouteAsync(Request(
            "帮我改一下 Program.cs 里的入口",
            settings: settings,
            providers: Providers(),
            sessionId: "s1"));

        var second = await router.RouteAsync(Request(
            "再帮我改一下另一个文件",
            settings: settings,
            providers: Providers(),
            sessionId: "s1"));

        Assert.Equal(ModelRoutingSource.Advisor, first.Source);
        Assert.Equal(ModelRoutingSource.Sticky, second.Source);
        Assert.Equal("code-model", second.ModelId);
        Assert.Equal(1, advisor.Calls);

        // 清掉会话粘性后重新询问。
        router.ClearSession("s1");
        await router.RouteAsync(Request(
            "再帮我改一下另一个文件",
            settings: settings,
            providers: Providers(),
            sessionId: "s1"));
        Assert.Equal(2, advisor.Calls);
    }

    [Fact]
    public void 解析路由模型JSON并过滤未知模型()
    {
        var candidates = Candidates();

        var advice = LlmModelRoutingAdvisor.Parse(
            """{"task":"complex","provider":"p","model":"complex-model","reason":"跨文件","confidence":0.8}""",
            candidates);

        Assert.NotNull(advice);
        Assert.Equal(ModelTaskKind.Complex, advice!.Task);
        Assert.Equal("complex-model", advice.ModelId);
        Assert.Equal(0.8, advice.Confidence);

        var unknown = LlmModelRoutingAdvisor.Parse(
            """{"task":"code","model":"not-in-catalog"}""",
            candidates);

        Assert.NotNull(unknown);
        Assert.Null(unknown!.ModelId);
        Assert.Equal(ModelTaskKind.Code, unknown.Task);
    }

    [Fact]
    public void 非法JSON或未知任务返回空()
    {
        Assert.Null(LlmModelRoutingAdvisor.Parse("not json", Candidates()));
        Assert.Null(LlmModelRoutingAdvisor.Parse("""{"task":"unknown"}""", Candidates()));
    }

    [Fact]
    public void 路由提示包含候选与成本偏好()
    {
        var prompt = LlmModelRoutingAdvisor.BuildUserPrompt(new ModelRoutingAdviceRequest(
            Provider("p", "router"),
            "router",
            "帮我重构",
            "用户：之前聊过上下文",
            ModelTaskKind.Complex,
            80,
            Candidates(),
            ModelCostPreference.Quality));

        Assert.Contains("complex-model", prompt);
        Assert.Contains("质量", prompt);
        Assert.Contains("JSON Schema", prompt);
        Assert.Contains("之前聊过上下文", prompt);
    }

    [Fact]
    public void 作用域可以显式关闭或打开自动路由()
    {
        var global = new AppSettings
        {
            LastProviderId = "p",
            LastModelId = "current",
            ModelRouting = new ModelRoutingSettings { Mode = ModelRoutingMode.Rules }
        };

        var inherited = EffectiveSettings.Resolve(global, null, null);
        Assert.True(inherited.UseModelRouting);

        var session = new ChatSession
        {
            Id = "c1",
            Title = "聊天室",
            ScopedSettings = new ScopedSettings { AutoModelRouting = false }
        };

        var fixedModel = EffectiveSettings.Resolve(global, null, session);
        Assert.False(fixedModel.UseModelRouting);

        // 旧会话已经显式选过模型（没有 AutoModelRouting 标记）：按固定处理，不因全局开关悄悄换模型。
        var legacySession = new ChatSession
        {
            Id = "legacy",
            Title = "旧会话",
            ScopedSettings = new ScopedSettings { ProviderId = "p", ModelId = "current" }
        };

        Assert.False(EffectiveSettings.Resolve(global, null, legacySession).UseModelRouting);

        // 全局 Off 是总开关：即使聊天室记着「自动」也不路由。
        global.ModelRouting.Mode = ModelRoutingMode.Off;
        var autoSession = new ChatSession
        {
            Id = "c2",
            Title = "聊天室",
            ScopedSettings = new ScopedSettings { AutoModelRouting = true }
        };

        Assert.False(EffectiveSettings.Resolve(global, null, autoSession).UseModelRouting);
    }

    private static ModelRoutingRequest Request(
        string input,
        bool isAuto = true,
        ModelRoutingSettings? settings = null,
        ModelProvider? providers = null,
        string sessionId = "") =>
        new(
            input,
            [new ChatMessage(ChatMessageRole.User, input)],
            Provider("p", "current"),
            "current",
            [providers ?? Provider("p", "current")],
            settings ?? SettingsWithRoutes(),
            isAuto,
            sessionId);

    private static ModelRoutingSettings SettingsWithRoutes() => new()
    {
        Mode = ModelRoutingMode.Rules,
        Routes =
        [
            new ModelRouteBinding
            {
                Task = ModelTaskKind.Chat,
                ProviderId = "p",
                ModelId = "small-model"
            },
            new ModelRouteBinding
            {
                Task = ModelTaskKind.Simple,
                ProviderId = "p",
                ModelId = "small-model"
            },
            new ModelRouteBinding
            {
                Task = ModelTaskKind.Code,
                ProviderId = "p",
                ModelId = "code-model"
            },
            new ModelRouteBinding
            {
                Task = ModelTaskKind.Complex,
                ProviderId = "p",
                ModelId = "complex-model"
            }
        ],
        DefaultProviderId = "p",
        DefaultModelId = "default-model"
    };

    private static ModelProvider Providers() => new()
    {
        Id = "p",
        DisplayName = "测试",
        Models =
        [
            new ModelEntry
            {
                Id = "current",
                Profile = new ModelProfile(32_768, 4096)
            },
            new ModelEntry
            {
                Id = "small-model",
                Profile = new ModelProfile(32_768, 4096)
            },
            new ModelEntry
            {
                Id = "code-model",
                Profile = new ModelProfile(8192, 2048)
            },
            new ModelEntry
            {
                Id = "complex-model",
                Profile = new ModelProfile(131_072, 8192)
            },
            new ModelEntry
            {
                Id = "large-model",
                Profile = new ModelProfile(200_000, 8192)
            },
            new ModelEntry
            {
                Id = "default-model",
                Profile = new ModelProfile(131_072, 8192)
            }
        ]
    };

    private static ModelProvider Provider(string id, string modelId) => new()
    {
        Id = id,
        DisplayName = id,
        Models = [new ModelEntry { Id = modelId, Profile = new ModelProfile(32_768, 4096) }]
    };

    private static IReadOnlyList<ModelRoutingCandidate> Candidates() =>
    [
        new("p", "测试", "current", 32_768, true, null, null, []),
        new("p", "测试", "code-model", 8192, true, 1m, 2m, [ModelTaskKind.Code]),
        new("p", "测试", "complex-model", 131_072, true, 10m, 30m, [ModelTaskKind.Complex])
    ];

    private sealed class FakeAdvisor : IModelRoutingAdvisor
    {
        public int Calls { get; private set; }

        public ModelRoutingAdvice? Reply { get; init; }

        public Exception? Failure { get; init; }

        public Task<ModelRoutingAdvice?> AdviseAsync(
            ModelRoutingAdviceRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Failure is null
                ? Task.FromResult(Reply)
                : Task.FromException<ModelRoutingAdvice?>(Failure);
        }
    }
}
