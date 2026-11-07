using System;
using System.Collections.Generic;
using System.Text;
using Mirror;
using UnityEngine;

namespace RPG4.Backpack.Network
{
    public partial class BackpackNetworkManager
    {
        [Header("同步观察")]
        [Tooltip("会话启动时按消息类型累计 Mirror 字节；不含传输头，Host 包含本地连接。默认不订阅，运行中可通过 CollectTrafficStatistics 属性切换。")]
        [SerializeField] private bool collectTrafficStatistics;

        private readonly Dictionary<Type, MessageTrafficStatistics> trafficStatistics = new Dictionary<Type, MessageTrafficStatistics>();
        private bool trafficCaptureActive, statisticsServerRunning, statisticsClientRunning;

        public IReadOnlyDictionary<Type, MessageTrafficStatistics> TrafficStatistics => trafficStatistics;
        public bool CollectTrafficStatistics
        {
            get => collectTrafficStatistics;
            set
            {
                collectTrafficStatistics = value;
                if (!value) StopTrafficCapture();
                else if (statisticsServerRunning || statisticsClientRunning) StartTrafficCapture();
            }
        }

        public void ResetTrafficStatistics() => trafficStatistics.Clear();

        public string DescribeTrafficStatistics()
        {
            var text = new StringBuilder("Mirror 消息统计（不含传输头，Host 含本地连接）");
            foreach (KeyValuePair<Type, MessageTrafficStatistics> pair in trafficStatistics)
                text.Append('\n').Append(pair.Key.Name).Append(": send=").Append(pair.Value.SentMessages)
                    .Append('/').Append(pair.Value.SentBytes).Append("B receive=").Append(pair.Value.ReceivedMessages)
                    .Append('/').Append(pair.Value.ReceivedBytes).Append('B');
            return text.ToString();
        }

        public override void OnDestroy()
        {
            StopTrafficCapture();
            base.OnDestroy();
        }

        private void StartTrafficCapture()
        {
            if (!collectTrafficStatistics || trafficCaptureActive) return;
            ResetTrafficStatistics();
            NetworkDiagnostics.OutMessageEvent += OnMessageSent;
            NetworkDiagnostics.InMessageEvent += OnMessageReceived;
            trafficCaptureActive = true;
        }

        private void StopTrafficCaptureIfIdle()
        {
            if (!statisticsServerRunning && !statisticsClientRunning) StopTrafficCapture();
        }

        private void StopTrafficCapture()
        {
            if (!trafficCaptureActive) return;
            NetworkDiagnostics.OutMessageEvent -= OnMessageSent;
            NetworkDiagnostics.InMessageEvent -= OnMessageReceived;
            trafficCaptureActive = false;
        }

        private void OnMessageSent(NetworkDiagnostics.MessageInfo info) => RecordTraffic(info, true);
        private void OnMessageReceived(NetworkDiagnostics.MessageInfo info) => RecordTraffic(info, false);

        private void RecordTraffic(NetworkDiagnostics.MessageInfo info, bool outgoing)
        {
            if (info.message == null || info.count <= 0) return;
            Type type = info.message.GetType();
            if (!trafficStatistics.TryGetValue(type, out MessageTrafficStatistics count))
            {
                count = new MessageTrafficStatistics();
                trafficStatistics.Add(type, count);
            }
            long bytes = (long)info.bytes * info.count;
            if (outgoing) { count.SentMessages += info.count; count.SentBytes += bytes; }
            else { count.ReceivedMessages += info.count; count.ReceivedBytes += bytes; }
        }
    }

    /// <summary>消息层计数：发送字节乘以实际接收连接数，不代表网卡层吞吐。</summary>
    public sealed class MessageTrafficStatistics
    {
        public long SentMessages { get; internal set; }
        public long SentBytes { get; internal set; }
        public long ReceivedMessages { get; internal set; }
        public long ReceivedBytes { get; internal set; }
    }
}
