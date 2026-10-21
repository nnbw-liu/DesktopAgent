using Xunit;

// 自检会创建 / 删除 %USERPROFILE% 与工作区里的固定哨兵文件，并启动真实进程；
// 顺序执行避免测试之间抢同一个文件。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
