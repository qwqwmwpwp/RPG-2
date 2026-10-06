namespace RPG4.Backpack.Core
{
    /// <summary>日志级别（六级）：枚举值顺序即严重度递增，可直接用整数比较做「最低级别」过滤；模块维度由 <see cref="LogModule"/> 位掩码承担。</summary>
    public enum LogLevel
    {
        Trace = 0, // 最细粒度追踪，仅定位疑难问题时临时开启。

        Debug = 1, // 开发期调试信息。

        Info = 2, // 常规信息（默认最低级别）。

        Warn = 3, // 警告：可继续运行但行为可疑。

        Error = 4, // 错误：本次操作失败。

        Fatal = 5, // 致命：系统已无法正常工作。
    }
}
