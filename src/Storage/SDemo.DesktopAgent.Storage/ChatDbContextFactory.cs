using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SDemo.DesktopAgent.Data;

/// <summary>
/// 设计时工厂：只给 <c>dotnet ef migrations add</c> 用，产品运行期不会创建它。
/// SQLite 的连接串不影响迁移脚本的内容（不产生 provider 特有的 DDL），所以用内存占位路径。
/// </summary>
public sealed class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new ChatDbContext(options);
    }
}
