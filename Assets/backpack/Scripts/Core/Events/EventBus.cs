using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using UnityEngine;

namespace RPG4.Backpack.Core
{
    /// <summary>进程内事件总线：按 source 定向投递（同一频道键下不同实例互不串台）、同步派发（回调在同一调用栈内完成，订阅方不得假设下一帧才收到）、监听者弱引用自动解绑（也可用 <see cref="EventSubscription"/> 显式退订）；单个回调抛异常会被隔离并记 Error，避免吞掉整条广播。</summary>
    public sealed class EventBus
    {
        // 通配来源哨兵

        /// <summary>通配来源哨兵：仅用于让日志里的来源显示为可读文本。</summary>
        private sealed class AnySourceSentinel
        {
            /// <inheritdoc/>
            public override string ToString()
            {
                return "EventBus.AnySource";
            }
        }

        public static readonly object AnySource = new AnySourceSentinel(); // 通配来源：Subscribe 时用它作 source，可接收该频道任意来源的消息。

        // 全局实例

        private static EventBus global;

        /// <summary>全局总线：业务层统一使用它；需要互不干扰的隔离场景（如 Editor 自检）请 <c>new EventBus()</c>。仅限主线程调用，非主线程记 <c>Warn</c> 后忽略。</summary>
        public static EventBus Global
        {
            get
            {
                if (global == null)
                {
                    global = new EventBus();
                }

                return global;
            }
        }

        /// <summary>丢弃全局实例（下一个 <see cref="Global"/> 访问会新建）。取消订阅随旧实例一并作废。</summary>
        public static void ResetGlobal()
        {
            global = null;
        }

        /// <summary>关闭 Domain Reload 时静态字段不会重置，此处显式复位，避免上一局游戏的订阅残留。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetGlobalOnSubsystemRegistration()
        {
            global = null;
        }

        // 内部存储

        /// <summary>订阅表键：频道键 + 来源对象，来源按引用相等比较。</summary>
        private readonly struct ChannelKey : IEquatable<ChannelKey>
        {
            public readonly string Channel;
            public readonly object Source;

            /// <summary>按频道键与来源创建键。</summary>
            public ChannelKey(string channel, object source)
            {
                Channel = channel;
                Source = source;
            }

            /// <inheritdoc/>
            public bool Equals(ChannelKey other)
            {
                return ReferenceEquals(Source, other.Source)
                    && string.Equals(Channel, other.Channel, StringComparison.Ordinal);
            }

            /// <inheritdoc/>
            public override bool Equals(object obj)
            {
                return obj is ChannelKey other && Equals(other);
            }

            /// <inheritdoc/>
            public override int GetHashCode()
            {
                unchecked
                {
                    int channelHash = StringComparer.Ordinal.GetHashCode(Channel ?? string.Empty);
                    return (channelHash * 397) ^ RuntimeHelpers.GetHashCode(Source);
                }
            }
        }

        /// <summary>单条订阅记录。</summary>
        private sealed class Entry
        {
            public int Handle;
            public WeakReference<object> Listener; // null = 常驻强订阅（订阅时 listener 传了 null）
            public Type[] Signature;               // 载荷逐位类型，派发时严格比对
            public Delegate Callback;
            public bool Removed; // 已标记失效，等待压实阶段从列表移除
        }

        /// <summary>句柄到订阅归属（键 + 条目）的映射值。</summary>
        private readonly struct EntryOwner
        {
            public readonly ChannelKey Key;
            public readonly Entry Entry;

            /// <summary>记录订阅所属的键与条目。</summary>
            public EntryOwner(ChannelKey key, Entry entry)
            {
                Key = key;
                Entry = entry;
            }
        }

        private readonly Dictionary<ChannelKey, List<Entry>> subscriptions = new Dictionary<ChannelKey, List<Entry>>(); // 频道键 + 来源 → 订阅列表
        private readonly Dictionary<int, EntryOwner> handles = new Dictionary<int, EntryOwner>(); // 句柄 → 归属，供退订与存活查询
        private int nextHandle = 1; // 句柄自增源，从 1 起（0 保留给 default(EventSubscription)）
        private int dispatchDepth; // 派发嵌套深度，归零后才允许压实集合
        private bool hasRemovedEntries; // 是否存在待压实的已移除条目
        private static int mainThreadId = -1; // 首次调用时锁定的主线程 id

        // 订阅（listener 由总线持弱引用）

        /// <summary>订阅无参消息。</summary>
        public EventSubscription Subscribe(string channel, object source, object listener, Action callback)
        {
            return Add(channel, source, listener, Array.Empty<Type>(), callback);
        }

        /// <summary>订阅 1 参消息（载荷类型必须与频道注释中的签名一致）。</summary>
        public EventSubscription Subscribe<T1>(string channel, object source, object listener, Action<T1> callback)
        {
            return Add(channel, source, listener, new[] { typeof(T1) }, callback);
        }

        /// <summary>订阅 2 参消息。</summary>
        public EventSubscription Subscribe<T1, T2>(string channel, object source, object listener, Action<T1, T2> callback)
        {
            return Add(channel, source, listener, new[] { typeof(T1), typeof(T2) }, callback);
        }

        /// <summary>订阅 3 参消息。</summary>
        public EventSubscription Subscribe<T1, T2, T3>(string channel, object source, object listener, Action<T1, T2, T3> callback)
        {
            return Add(channel, source, listener, new[] { typeof(T1), typeof(T2), typeof(T3) }, callback);
        }

        // 发布（同步派发）

        /// <summary>发布无参消息：source 传发送方组件实例，传 <c>null</c> 时仅通配订阅者收到；非通配来源会额外向 <see cref="AnySource"/> 派发一轮。</summary>
        public void Publish(string channel, object source)
        {
            if (!CanDispatch(channel, nameof(Publish))) return;

            dispatchDepth++;
            try
            {
                PublishZero(channel, source);
                if (!ReferenceEquals(source, AnySource)) PublishZero(channel, AnySource);
            }
            finally
            {
                dispatchDepth--;
            }

            CompactAfterDispatch();
        }

        /// <summary>发布 1 参消息。</summary>
        public void Publish<T1>(string channel, object source, T1 arg1)
        {
            if (!CanDispatch(channel, nameof(Publish))) return;

            dispatchDepth++;
            try
            {
                PublishOne(channel, source, arg1);
                if (!ReferenceEquals(source, AnySource)) PublishOne(channel, AnySource, arg1);
            }
            finally
            {
                dispatchDepth--;
            }

            CompactAfterDispatch();
        }

        /// <summary>发布 2 参消息。</summary>
        public void Publish<T1, T2>(string channel, object source, T1 arg1, T2 arg2)
        {
            if (!CanDispatch(channel, nameof(Publish))) return;

            dispatchDepth++;
            try
            {
                PublishTwo(channel, source, arg1, arg2);
                if (!ReferenceEquals(source, AnySource)) PublishTwo(channel, AnySource, arg1, arg2);
            }
            finally
            {
                dispatchDepth--;
            }

            CompactAfterDispatch();
        }

        /// <summary>发布 3 参消息。</summary>
        public void Publish<T1, T2, T3>(string channel, object source, T1 arg1, T2 arg2, T3 arg3)
        {
            if (!CanDispatch(channel, nameof(Publish))) return;

            dispatchDepth++;
            try
            {
                PublishThree(channel, source, arg1, arg2, arg3);
                if (!ReferenceEquals(source, AnySource)) PublishThree(channel, AnySource, arg1, arg2, arg3);
            }
            finally
            {
                dispatchDepth--;
            }

            CompactAfterDispatch();
        }

        // 退订

        /// <summary>按句柄退订（<see cref="EventSubscription.Dispose"/> 走这里）。已失效返回 <c>false</c>。</summary>
        public bool Unsubscribe(int handle)
        {
            if (!handles.TryGetValue(handle, out EntryOwner owner)) return false;

            LogDebug(() => $"[EventBus] 退订 #{handle} | 频道={owner.Key.Channel}");
            MarkRemoved(owner.Entry);
            CompactAfterDispatch();
            return true;
        }

        /// <summary>退订某监听者在某频道下全部来源的订阅。</summary>
        public int Unsubscribe(string channel, object listener)
        {
            if (string.IsNullOrEmpty(channel) || listener == null) return 0;

            int count = 0;
            foreach (KeyValuePair<ChannelKey, List<Entry>> pair in subscriptions)
            {
                if (!string.Equals(pair.Key.Channel, channel, StringComparison.Ordinal)) continue;

                count += MarkMatching(pair.Value, listener);
            }

            CompactAfterDispatch();
            return count;
        }

        /// <summary>退订某监听者在「指定频道 + 指定来源」下的订阅。</summary>
        public int Unsubscribe(string channel, object listener, object source)
        {
            if (string.IsNullOrEmpty(channel) || listener == null) return 0;

            List<Entry> list = Find(channel, source);
            int count = list != null ? MarkMatching(list, listener) : 0;

            CompactAfterDispatch();
            return count;
        }

        /// <summary>退订某监听者的全部订阅（跨频道、跨来源）。</summary>
        public int UnsubscribeAll(object listener)
        {
            if (listener == null) return 0;

            int count = 0;
            foreach (KeyValuePair<ChannelKey, List<Entry>> pair in subscriptions)
            {
                count += MarkMatching(pair.Value, listener);
            }

            CompactAfterDispatch();
            return count;
        }

        /// <summary>清空全部订阅（<see cref="ResetGlobal"/> 之外的重启路径用）。</summary>
        public void Clear()
        {
            foreach (KeyValuePair<ChannelKey, List<Entry>> pair in subscriptions)
            {
                List<Entry> list = pair.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    MarkRemoved(list[i]);
                }
            }

            CompactAfterDispatch();
        }

        // 查询与回收

        /// <summary>句柄是否仍指向有效订阅（供 <see cref="EventSubscription.IsAlive"/> 使用）。</summary>
        public bool IsAliveHandle(int handle)
        {
            return handles.TryGetValue(handle, out EntryOwner owner) && !owner.Entry.Removed;
        }

        /// <summary>某监听者在某频道下是否还有存活订阅。</summary>
        public bool IsAlive(string channel, object listener)
        {
            if (string.IsNullOrEmpty(channel) || listener == null) return false;

            foreach (KeyValuePair<ChannelKey, List<Entry>> pair in subscriptions)
            {
                if (!string.Equals(pair.Key.Channel, channel, StringComparison.Ordinal)) continue;
                if (ContainsListener(pair.Value, listener)) return true;
            }

            return false;
        }

        /// <summary>某监听者在「指定频道 + 指定来源」下是否还有存活订阅。</summary>
        public bool IsAlive(string channel, object listener, object source)
        {
            if (string.IsNullOrEmpty(channel) || listener == null) return false;

            List<Entry> list = Find(channel, source);
            return list != null && ContainsListener(list, listener);
        }

        /// <summary>主动回收死订阅，返回本次回收条数。派发路径本身也会惰性剔除，本方法供自检 / 编辑器排查使用。</summary>
        public int PruneDeadListeners()
        {
            int count = 0;
            foreach (KeyValuePair<ChannelKey, List<Entry>> pair in subscriptions)
            {
                List<Entry> list = pair.Value;
                for (int i = 0; i < list.Count; i++)
                {
                    Entry entry = list[i];
                    if (entry.Removed || IsListenerAlive(entry)) continue;

                    MarkRemoved(entry);
                    count++;
                }
            }

            CompactAfterDispatch();
            return count;
        }

        /// <summary>某频道下存活订阅条数（含通配来源条目）。</summary>
        public int SubscriptionCount(string channel)
        {
            if (string.IsNullOrEmpty(channel)) return 0;

            int count = 0;
            foreach (KeyValuePair<ChannelKey, List<Entry>> pair in subscriptions)
            {
                if (!string.Equals(pair.Key.Channel, channel, StringComparison.Ordinal)) continue;
                count += CountAlive(pair.Value);
            }

            return count;
        }

        /// <summary>某频道 + 指定来源下的存活订阅条数；source 传 <c>null</c> 表示「无来源」订阅。</summary>
        public int SubscriptionCount(string channel, object source)
        {
            List<Entry> list = string.IsNullOrEmpty(channel) ? null : Find(channel, source);
            return list != null ? CountAlive(list) : 0;
        }

        // 内部：订阅登记

        /// <summary>登记订阅并返回句柄；listener 传 <c>null</c> 即为常驻强订阅，宿主销毁后不会自动解绑。</summary>
        private EventSubscription Add(string channel, object source, object listener, Type[] signature, Delegate callback)
        {
            if (!ValidateChannel(channel, nameof(Subscribe)) || !ValidateMainThread(nameof(Subscribe))) return default;
            if (callback == null)
            {
                LogWarn(() => $"[EventBus] Subscribe 回调为 null，已忽略 | 频道={channel}");
                return default;
            }

            if (listener == null)
            {
                LogWarn(() => $"[EventBus] Subscribe 未传 listener：该订阅为常驻强订阅，宿主销毁后不会自动解绑 | 频道={channel}");
            }

            Entry entry = new Entry
            {
                Handle = nextHandle++,
                Listener = listener != null ? new WeakReference<object>(listener) : null,
                Signature = signature,
                Callback = callback,
            };

            ChannelKey key = new ChannelKey(channel, source);
            if (!subscriptions.TryGetValue(key, out List<Entry> list))
            {
                list = new List<Entry>();
                subscriptions.Add(key, list);
            }

            list.Add(entry);
            handles.Add(entry.Handle, new EntryOwner(key, entry));

            LogDebug(() => $"[EventBus] 订阅 #{entry.Handle} | 频道={channel} | 来源={(source == null ? "(null)" : source.ToString())} | 参数个数={signature.Length}");
            return new EventSubscription(this, entry.Handle);
        }

        // 内部：派发（含弱引用惰性剔除 + 签名校验 + 异常隔离）

        /// <summary>向指定来源派发无参消息；按快照计数遍历，派发期间新增的订阅不参与本轮。</summary>
        private void PublishZero(string channel, object source)
        {
            List<Entry> list = Find(channel, source);
            if (list == null) return;

            int count = list.Count; // 快照计数：派发期间新增的订阅不参与本轮
            for (int i = 0; i < count; i++)
            {
                Entry entry = list[i];
                if (entry.Removed) continue;
                if (!IsListenerAlive(entry))
                {
                    MarkRemoved(entry);
                    continue;
                }

                if (entry.Signature.Length != 0)
                {
                    WarnSignatureMismatch(channel, entry, 0, "无参");
                    continue;
                }

                if (entry.Callback is Action action)
                {
                    InvokeSafely(channel, entry, action);
                }
            }
        }

        /// <summary>向指定来源派发 1 参消息；按快照计数遍历，派发期间新增的订阅不参与本轮。</summary>
        private void PublishOne<T1>(string channel, object source, T1 arg1)
        {
            List<Entry> list = Find(channel, source);
            if (list == null) return;

            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                Entry entry = list[i];
                if (entry.Removed) continue;
                if (!IsListenerAlive(entry))
                {
                    MarkRemoved(entry);
                    continue;
                }

                if (entry.Signature.Length != 1 || entry.Signature[0] != typeof(T1))
                {
                    WarnSignatureMismatch(channel, entry, 1, typeof(T1).Name);
                    continue;
                }

                if (entry.Callback is Action<T1> action)
                {
                    InvokeSafely(channel, entry, action, arg1);
                }
            }
        }

        /// <summary>向指定来源派发 2 参消息；按快照计数遍历，派发期间新增的订阅不参与本轮。</summary>
        private void PublishTwo<T1, T2>(string channel, object source, T1 arg1, T2 arg2)
        {
            List<Entry> list = Find(channel, source);
            if (list == null) return;

            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                Entry entry = list[i];
                if (entry.Removed) continue;
                if (!IsListenerAlive(entry))
                {
                    MarkRemoved(entry);
                    continue;
                }

                if (entry.Signature.Length != 2 || entry.Signature[0] != typeof(T1) || entry.Signature[1] != typeof(T2))
                {
                    WarnSignatureMismatch(channel, entry, 2, $"{typeof(T1).Name}, {typeof(T2).Name}");
                    continue;
                }

                if (entry.Callback is Action<T1, T2> action)
                {
                    InvokeSafely(channel, entry, action, arg1, arg2);
                }
            }
        }

        /// <summary>向指定来源派发 3 参消息；按快照计数遍历，派发期间新增的订阅不参与本轮。</summary>
        private void PublishThree<T1, T2, T3>(string channel, object source, T1 arg1, T2 arg2, T3 arg3)
        {
            List<Entry> list = Find(channel, source);
            if (list == null) return;

            int count = list.Count;
            for (int i = 0; i < count; i++)
            {
                Entry entry = list[i];
                if (entry.Removed) continue;
                if (!IsListenerAlive(entry))
                {
                    MarkRemoved(entry);
                    continue;
                }

                if (entry.Signature.Length != 3
                    || entry.Signature[0] != typeof(T1)
                    || entry.Signature[1] != typeof(T2)
                    || entry.Signature[2] != typeof(T3))
                {
                    WarnSignatureMismatch(channel, entry, 3, $"{typeof(T1).Name}, {typeof(T2).Name}, {typeof(T3).Name}");
                    continue;
                }

                if (entry.Callback is Action<T1, T2, T3> action)
                {
                    InvokeSafely(channel, entry, action, arg1, arg2, arg3);
                }
            }
        }

        // 内部：通用工具

        /// <summary>按频道 + 来源取订阅列表；不存在返回 <c>null</c>。</summary>
        private List<Entry> Find(string channel, object source)
        {
            subscriptions.TryGetValue(new ChannelKey(channel, source), out List<Entry> list);
            return list;
        }

        /// <summary>标记条目失效并从句柄表摘除；列表清理由压实阶段完成。</summary>
        private void MarkRemoved(Entry entry)
        {
            if (entry.Removed) return;

            entry.Removed = true;
            hasRemovedEntries = true;
            handles.Remove(entry.Handle);
        }

        /// <summary>标记该列表中属于指定监听者的全部条目，返回标记条数。</summary>
        private int MarkMatching(List<Entry> list, object listener)
        {
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Entry entry = list[i];
                if (entry.Removed || !MatchListener(entry, listener)) continue;

                MarkRemoved(entry);
                count++;
            }

            return count;
        }

        /// <summary>列表压实：只在派发栈归零后执行，避免派发中修改集合。</summary>
        private void CompactAfterDispatch()
        {
            if (dispatchDepth > 0 || !hasRemovedEntries) return;

            hasRemovedEntries = false;
            List<ChannelKey> emptyKeys = null;

            foreach (KeyValuePair<ChannelKey, List<Entry>> pair in subscriptions)
            {
                List<Entry> list = pair.Value;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].Removed)
                    {
                        list.RemoveAt(i);
                    }
                }

                if (list.Count == 0)
                {
                    if (emptyKeys == null) emptyKeys = new List<ChannelKey>();
                    emptyKeys.Add(pair.Key);
                }
            }

            if (emptyKeys == null) return;

            for (int i = 0; i < emptyKeys.Count; i++)
            {
                subscriptions.Remove(emptyKeys[i]);
            }
        }

        /// <summary>监听者是否仍存活：常驻强订阅恒为真；弱引用已失效或被 Destroy 的 Unity 对象为假。</summary>
        private static bool IsListenerAlive(Entry entry)
        {
            if (entry.Listener == null) return true; // 常驻强订阅
            if (!entry.Listener.TryGetTarget(out object target)) return false;

            // Unity 对象被 Destroy 后托管包装仍存在，必须用 Unity 的 == 重载判定。
            return !(target is UnityEngine.Object unityObject) || unityObject != null;
        }

        /// <summary>条目是否属于该监听者（引用相等）；常驻强订阅不参与匹配。</summary>
        private static bool MatchListener(Entry entry, object listener)
        {
            if (entry.Listener == null) return false;

            return entry.Listener.TryGetTarget(out object target) && ReferenceEquals(target, listener);
        }

        /// <summary>列表中是否还有该监听者的未移除条目。</summary>
        private static bool ContainsListener(List<Entry> list, object listener)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Entry entry = list[i];
                if (!entry.Removed && MatchListener(entry, listener)) return true;
            }

            return false;
        }

        /// <summary>统计未移除且监听者仍存活的条目数。</summary>
        private static int CountAlive(List<Entry> list)
        {
            int count = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Entry entry = list[i];
                if (!entry.Removed && IsListenerAlive(entry)) count++;
            }

            return count;
        }

        /// <summary>派发前守卫：频道键非空且处于主线程。</summary>
        private static bool CanDispatch(string channel, string api)
        {
            return ValidateChannel(channel, api) && ValidateMainThread(api);
        }

        /// <summary>频道键非空即通过；为空时记 <c>Warn</c> 并拒绝该次调用。</summary>
        private static bool ValidateChannel(string channel, string api)
        {
            if (!string.IsNullOrEmpty(channel)) return true;

            LogWarn(() => $"[EventBus] {api} 收到空频道键，已忽略：键必须取自 InvChannels / CmdChannels / UiChannels");
            return false;
        }

        /// <summary>首次调用时锁定主线程 id；非主线程记 <c>Warn</c> 并拒绝该次调用。</summary>
        private static bool ValidateMainThread(string api)
        {
            if (mainThreadId < 0) mainThreadId = Thread.CurrentThread.ManagedThreadId;
            if (Thread.CurrentThread.ManagedThreadId == mainThreadId) return true;

            LogWarn(() => $"[EventBus] {api} 在非主线程调用，已忽略（源 GMP_VERIFY_GAME_THREAD）");
            return false;
        }

        // 内部：回调执行与日志（BackpackLog 惰性重载，避免热点拼字符串）

        /// <summary>执行无参回调并隔离异常，单个订阅抛错不影响其余派发。</summary>
        private static void InvokeSafely(string channel, Entry entry, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                ReportCallbackException(channel, entry, exception);
            }
        }

        /// <summary>执行 1 参回调并隔离异常，单个订阅抛错不影响其余派发。</summary>
        private static void InvokeSafely<T1>(string channel, Entry entry, Action<T1> action, T1 arg1)
        {
            try
            {
                action(arg1);
            }
            catch (Exception exception)
            {
                ReportCallbackException(channel, entry, exception);
            }
        }

        /// <summary>执行 2 参回调并隔离异常，单个订阅抛错不影响其余派发。</summary>
        private static void InvokeSafely<T1, T2>(string channel, Entry entry, Action<T1, T2> action, T1 arg1, T2 arg2)
        {
            try
            {
                action(arg1, arg2);
            }
            catch (Exception exception)
            {
                ReportCallbackException(channel, entry, exception);
            }
        }

        /// <summary>执行 3 参回调并隔离异常，单个订阅抛错不影响其余派发。</summary>
        private static void InvokeSafely<T1, T2, T3>(string channel, Entry entry, Action<T1, T2, T3> action, T1 arg1, T2 arg2, T3 arg3)
        {
            try
            {
                action(arg1, arg2, arg3);
            }
            catch (Exception exception)
            {
                ReportCallbackException(channel, entry, exception);
            }
        }

        /// <summary>记录被隔离的回调异常：先记宿主信息，再记完整异常。</summary>
        private static void ReportCallbackException(string channel, Entry entry, Exception exception)
        {
            LogWarnError(channel, entry);
            BackpackLog.Error(LogModule.Inventory, exception);
        }

        /// <summary>记录回调异常所属频道与回调宿主类型。</summary>
        private static void LogWarnError(string channel, Entry entry)
        {
            BackpackLog.Error(LogModule.Inventory,
                () => $"[EventBus] 回调抛出异常，已隔离并继续派发 | 频道={channel} | 回调宿主={DescribeCallback(entry)}");
        }

        /// <summary>取回调声明类型名，缺失时返回 (unknown)。</summary>
        private static string DescribeCallback(Entry entry)
        {
            Type declaring = entry.Callback?.Method?.DeclaringType;
            return declaring != null ? declaring.Name : "(unknown)";
        }

        /// <summary>载荷签名不匹配的告警：分别打印发布的参数个数与订阅签名。</summary>
        private static void WarnSignatureMismatch(string channel, Entry entry, int publishArity, string publishTypes)
        {
            LogWarn(() => $"[EventBus] 载荷签名不匹配，已跳过 | 频道={channel} | 发布 {publishArity} 参 ({publishTypes}) | 订阅 {entry.Signature.Length} 参 ({DescribeSignature(entry.Signature)})");
        }

        /// <summary>把载荷类型数组描述为可读文本；空数组为「无参」。</summary>
        private static string DescribeSignature(Type[] signature)
        {
            if (signature == null || signature.Length == 0) return "无参";

            string result = signature[0].Name;
            for (int i = 1; i < signature.Length; i++)
            {
                result += ", " + signature[i].Name;
            }

            return result;
        }

        /// <summary>以 <c>Warn</c> 级别写入事件总线日志。</summary>
        private static void LogWarn(Func<string> message)
        {
            BackpackLog.Warn(LogModule.Inventory, message);
        }

        /// <summary>先判 Debug 开关再写日志，避免热点白拼字符串。</summary>
        private static void LogDebug(Func<string> message)
        {
            if (!BackpackLog.IsEnabled(LogModule.Inventory, LogLevel.Debug)) return;

            BackpackLog.Debug(LogModule.Inventory, message);
        }
    }
}
