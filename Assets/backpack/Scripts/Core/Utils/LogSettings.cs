using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>日志配置资产（ScriptableObject）：全局开关 + 最低级别 + 模块位掩码三件套，在 Inspector 中可视化编辑；资产规范位置 <c>Assets/backpack/Data/LogSettings.asset</c>，不由本类自动加载，需由 <c>BackpackLog.Settings</c> 显式注入，未注入时退化为开发期默认值。</summary>
    [CreateAssetMenu(fileName = "LogSettings", menuName = "RPG4/Backpack/Log Settings", order = 0)]
    public class LogSettings : ScriptableObject
    {
        public const string ResourceName = "LogSettings"; // 供 Resources.Load 兜底查找的资产名（资产需位于任一 Resources/ 目录下才生效）。

        [Header("总开关")]
        [Tooltip("关闭后所有模块、所有级别的日志一律不输出。")]
        [SerializeField]
        private bool isEnabled = true;

        [Tooltip("低于该级别的日志一律不输出。")]
        [SerializeField]
        private LogLevel minimumLevel = LogLevel.Info;

        [Header("模块过滤")]
        [Tooltip("位掩码：仅勾选的模块会输出日志。")]
        [SerializeField]
        private LogModule enabledModules = LogModule.All;

        [Header("输出格式")]
        [Tooltip("是否在消息前附加时间戳。")]
        [SerializeField]
        private bool useTimestamp = true;

        [Tooltip("是否把调用栈附加到日志（仅建议在排查问题时开启，有性能开销）。")]
        [SerializeField]
        private bool includeStackTrace;

        public bool IsEnabled => isEnabled; // 全局开关。

        public LogLevel MinimumLevel => minimumLevel; // 最低输出级别。

        public LogModule EnabledModules => enabledModules; // 启用的模块位掩码。

        public bool UseTimestamp => useTimestamp; // 是否附加时间戳。

        public bool IncludeStackTrace => includeStackTrace; // 是否附加调用栈。

        /// <summary>指定模块是否放行（不含全局开关与级别判定）。</summary>
        public bool IsModuleEnabled(LogModule module)
        {
            return (enabledModules & module) != 0;
        }

        /// <summary>指定级别是否达到最低级别要求。</summary>
        public bool IsLevelEnabled(LogLevel level)
        {
            return level >= minimumLevel;
        }

        /// <summary>综合判定：全局开关 + 级别 + 模块三者同时放行。</summary>
        public bool IsEnabledFor(LogModule module, LogLevel level)
        {
            return isEnabled && IsLevelEnabled(level) && IsModuleEnabled(module);
        }

        /// <summary>生成一个不落盘的运行时默认实例（<c>Info</c> 级别、全模块放行），用于未注入资产时的兜底。</summary>
        public static LogSettings CreateRuntimeDefault()
        {
            LogSettings settings = CreateInstance<LogSettings>();
            settings.name = "LogSettings (RuntimeDefault)";
            settings.hideFlags = HideFlags.HideAndDontSave;
            return settings;
        }
    }
}
