using SDemo.DesktopAgent.Context;
using Xunit;

namespace SDemo.DesktopAgent.Core.Tests;

/// <summary>P7：Full 模式也必须带编码工作流约束，不能只靠通用助手提示词。</summary>
public sealed class FullPromptTests
{
    [Fact]
    public void Full提示词包含定位与最小改动约束()
    {
        Assert.Contains("search_files", SystemPrompts.Full);
        Assert.Contains("read_file", SystemPrompts.Full);
        Assert.Contains("replace_text", SystemPrompts.Full);
        Assert.Contains("不要把 write_file 当作补丁失败的默认兜底", SystemPrompts.Full);
        Assert.Contains("不要谎报成功", SystemPrompts.Full);
    }
}
