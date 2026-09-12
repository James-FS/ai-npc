using Xunit;

// 测试进程内共享大量静态状态（SessionStore/ChatLogService 的门面实现、ReadinessService 的
// 测试缝、SystemSettingsStore 的文件、AIBOT_* 环境变量）。xUnit 默认按测试类并行，会导致
// 一个类改动静态门面时另一个类读到被替换的实现/环境值，出现“单跑绿、全量偶发红”。
// 用例总量小（<1s），直接关闭并行以保证确定性。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
