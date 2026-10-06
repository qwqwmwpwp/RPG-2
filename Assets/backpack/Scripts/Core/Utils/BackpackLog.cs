using System;
using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>分级日志入口（静态门面）：开关来自 <see cref="Settings"/> 资产，每个级别都有 <c>Func&lt;string&gt;</c> 惰性重载供热点先判开关再拼字符串；方法体被 <c>#if UNITY_EDITOR || DEVELOPMENT_BUILD</c> 包住，发布构建零开销。</summary>
    public static class BackpackLog
    {
        private const string FormatPrefix = "Backpack"; // 拼在每条输出开头的前缀。

        private static LogSettings settings;

        /// <summary>当前日志配置：可显式注入（推荐由启动流程 / Bootstrap 赋值为 <c>Assets/backpack/Data/LogSettings.asset</c>），未注入或赋 <c>null</c> 时退化为运行时默认值（<c>Info</c> 级别 + 全模块放行）；输出通道映射为 Trace/Debug/Info → Log、Warn → LogWarning、Error/Fatal → LogError。</summary>
        public static LogSettings Settings
        {
            get
            {
                if (settings == null)
                {
                    settings = ResolveSettings();
                }

                return settings;
            }
            set => settings = value != null ? value : LogSettings.CreateRuntimeDefault();
        }

        /// <summary>清空已解析的配置，下次访问 <see cref="Settings"/> 时重新解析。</summary>
        public static void ResetSettings()
        {
            settings = null;
        }

        /// <summary>综合判定该模块 + 级别是否会被输出，供热点代码把参数构造挡在门外。</summary>
        public static bool IsEnabled(LogModule module, LogLevel level)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            LogSettings current = Settings;
            return current != null && current.IsEnabledFor(module, level);
#else
            return false;
#endif
        }

        // 六级 API（字符串版）

        /// <summary>Trace 级别日志。</summary>
        public static void Trace(LogModule module, string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Write(LogLevel.Trace, module, message);
#endif
        }

        /// <summary>Debug 级别日志。</summary>
        public static void Debug(LogModule module, string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Write(LogLevel.Debug, module, message);
#endif
        }

        /// <summary>Info 级别日志。</summary>
        public static void Info(LogModule module, string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Write(LogLevel.Info, module, message);
#endif
        }

        /// <summary>Warn 级别日志。</summary>
        public static void Warn(LogModule module, string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Write(LogLevel.Warn, module, message);
#endif
        }

        /// <summary>Error 级别日志。</summary>
        public static void Error(LogModule module, string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Write(LogLevel.Error, module, message);
#endif
        }

        /// <summary>Fatal 级别日志。</summary>
        public static void Fatal(LogModule module, string message)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Write(LogLevel.Fatal, module, message);
#endif
        }

        // 六级 API（惰性版：开关未通过时工厂不会被调用）

        /// <summary>Trace 级别日志（惰性求值）。</summary>
        public static void Trace(LogModule module, Func<string> messageFactory)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            WriteLazy(LogLevel.Trace, module, messageFactory);
#endif
        }

        /// <summary>Debug 级别日志（惰性求值）。</summary>
        public static void Debug(LogModule module, Func<string> messageFactory)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            WriteLazy(LogLevel.Debug, module, messageFactory);
#endif
        }

        /// <summary>Info 级别日志（惰性求值）。</summary>
        public static void Info(LogModule module, Func<string> messageFactory)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            WriteLazy(LogLevel.Info, module, messageFactory);
#endif
        }

        /// <summary>Warn 级别日志（惰性求值）。</summary>
        public static void Warn(LogModule module, Func<string> messageFactory)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            WriteLazy(LogLevel.Warn, module, messageFactory);
#endif
        }

        /// <summary>Error 级别日志（惰性求值）。</summary>
        public static void Error(LogModule module, Func<string> messageFactory)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            WriteLazy(LogLevel.Error, module, messageFactory);
#endif
        }

        /// <summary>Fatal 级别日志（惰性求值）。</summary>
        public static void Fatal(LogModule module, Func<string> messageFactory)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            WriteLazy(LogLevel.Fatal, module, messageFactory);
#endif
        }

        // 异常重载

        /// <summary>Error 级别日志（异常）。</summary>
        public static void Error(LogModule module, Exception exception)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (exception == null) return;
            Write(LogLevel.Error, module, exception.ToString());
#endif
        }

        /// <summary>Fatal 级别日志（异常）。</summary>
        public static void Fatal(LogModule module, Exception exception)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (exception == null) return;
            Write(LogLevel.Fatal, module, exception.ToString());
#endif
        }

        // 内部实现

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>惰性日志：开关未通过时不调用工厂，避免白拼字符串。</summary>
        private static void WriteLazy(LogLevel level, LogModule module, Func<string> messageFactory)
        {
            if (messageFactory == null) return;
            if (!IsEnabled(module, level)) return;

            Write(level, module, messageFactory());
        }

        /// <summary>统一输出：按级别映射到 Unity 的 Log / LogWarning / LogError，必要时追加显式堆栈。</summary>
        private static void Write(LogLevel level, LogModule module, string message)
        {
            LogSettings current = Settings;
            if (current == null || !current.IsEnabledFor(module, level)) return;

            string head = current.UseTimestamp
                ? $"[{FormatPrefix}][{DateTime.Now:HH:mm:ss.fff}][{level}][{module}] "
                : $"[{FormatPrefix}][{level}][{module}] ";

            string formatted = head + (message ?? string.Empty);

            // Unity 自身是否附带调用栈由 Player Settings 的 Stack Trace 决定；
            // 此处仅额外追加显式堆栈文本，便于把日志拷出编辑器后仍可定位。
            // 注意：UnityEngine.Debug 必须全限定，避免与 BackpackLog.Debug 同级歧义。
            switch (level)
            {
                case LogLevel.Warn:
                    UnityEngine.Debug.LogWarning(formatted);
                    break;
                case LogLevel.Error:
                case LogLevel.Fatal:
                    UnityEngine.Debug.LogError(formatted);
                    break;
                default:
                    UnityEngine.Debug.Log(formatted);
                    break;
            }

            if (current.IncludeStackTrace)
            {
                UnityEngine.Debug.Log(Environment.StackTrace);
            }
        }
#endif

        /// <summary>解析日志配置：先按 <see cref="LogSettings.ResourceName"/> 走 <c>Resources.Load</c>，缺失则退回开发期默认值。</summary>
        private static LogSettings ResolveSettings()
        {
            // 兜底①：任一 Resources/ 目录下存在同名资产即可自动生效（当前仅为预留钩子）。
            LogSettings fromResources = Resources.Load<LogSettings>(LogSettings.ResourceName);
            if (fromResources != null)
            {
                return fromResources;
            }

            // 兜底②：开发期默认值（Info 级别 + 全模块放行）。
            return LogSettings.CreateRuntimeDefault();
        }

        /// <summary>关闭 Domain Reload 时静态字段不会重置，此处显式复位，避免上一局游戏的配置残留。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStateOnSubsystemRegistration()
        {
            settings = null;
        }
    }
}
