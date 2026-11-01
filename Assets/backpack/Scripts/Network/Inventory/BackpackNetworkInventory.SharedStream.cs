using System;
using System.Collections.Generic;
using System.IO;
using Mirror;
using RPG4.Backpack.Inventory;

namespace RPG4.Backpack.Network
{
    public sealed partial class BackpackNetworkInventory
    {
        private const int SharedStreamId = 1001;
        private const int SharedChunkBytes = 1024;
        private const int SharedHistoryLimit = 64;
        private const int SharedHistoryBytes = 2 * 1024 * 1024;
        private const int SharedPayloadLimit = 64 * 1024 * 1024;

        private sealed class SharedBatch
        {
            internal bool Baseline;
            internal ulong From, To;
            internal int Rows, Columns;
            internal byte[] Payload;
            internal uint Checksum;
        }

        private sealed class SharedReceive
        {
            internal uint Session, Transfer;
            internal SharedBatch Batch;
            internal int Offset;
        }

        private readonly LinkedList<SharedBatch> sharedHistory = new LinkedList<SharedBatch>();
        private int sharedHistorySize;
        private ulong sharedVersion, sharedClientVersion;
        private SharedBatch sharedBaselineCache;
        private SharedReceive sharedReceive;

        private void OnDisable()
        {
            RevokeAccessSubscriptions("库存会话已停用。");
            sharedReceive = null;
        }

        private void RevokeAccessSubscriptions(string reason)
        {
            foreach (BackpackNetworkInventory inventory in new List<BackpackNetworkInventory>(openedShared))
            {
                if (inventory == null) continue;
                foreach (KeyValuePair<NetworkConnectionToClient, Subscription> pair
                    in new List<KeyValuePair<NetworkConnectionToClient, Subscription>>(inventory.subscribers))
                    if (pair.Value.Ingress == this) inventory.RevokeSubscription(pair.Key, reason);
            }
            foreach (NetworkConnectionToClient connection in new List<NetworkConnectionToClient>(subscribers.Keys))
                RevokeSubscription(connection, reason);
            openedShared.Clear();
        }

        private static uint SharedChecksum(byte[] bytes)
        {
            uint hash = 2166136261;
            unchecked { foreach (byte value in bytes) hash = (hash ^ value) * 16777619; }
            return hash;
        }

        private void ClearSharedJournal()
        {
            sharedHistory.Clear();
            sharedHistorySize = 0;
            sharedBaselineCache = null;
            sharedVersion = 0;
            sharedServerStream.Entries.ClearChanges();
        }

        private void FlushSharedJournal()
        {
            InventorySyncList stream = sharedServerStream.Entries;
            if (stream.PendingChangeCount == 0) return;
            ulong from = sharedVersion++;
            sharedBaselineCache = null;
            if (subscribers.Count > 0)
            {
                using (NetworkWriterPooled writer = NetworkWriterPool.Get())
                {
                    stream.OnSerializeDelta(writer);
                    // 超大单批不进入历史；每个订阅者独立从最新快照恢复。
                    if (writer.Position <= SharedHistoryBytes)
                    {
                        byte[] payload = writer.ToArray();
                        sharedHistory.AddLast(new SharedBatch { From = from, To = sharedVersion,
                            Rows = Container.Rows, Columns = Container.Columns, Payload = payload,
                            Checksum = SharedChecksum(payload) });
                        sharedHistorySize += payload.Length;
                    }
                }
                while (sharedHistory.Count > SharedHistoryLimit || sharedHistorySize > SharedHistoryBytes)
                {
                    sharedHistorySize -= sharedHistory.First.Value.Payload.Length;
                    sharedHistory.RemoveFirst();
                }
            }
            else
            {
                sharedHistory.Clear();
                sharedHistorySize = 0;
            }
            stream.ClearChanges();
        }

        private SharedBatch CaptureSharedBaseline()
        {
            if (sharedBaselineCache != null) return sharedBaselineCache;
            using (NetworkWriterPooled writer = NetworkWriterPool.Get())
            {
                sharedServerStream.Entries.WriteSnapshot(writer);
                if (writer.Position > SharedPayloadLimit) throw new InvalidDataException("共享库存基线超过传输上限。");
                byte[] payload = writer.ToArray();
                return sharedBaselineCache = new SharedBatch { Baseline = true, To = sharedVersion,
                    Rows = Container.Rows, Columns = Container.Columns, Payload = payload,
                    Checksum = SharedChecksum(payload) };
            }
        }

        private bool IsCurrentSharedSubscription(NetworkConnectionToClient conn, Subscription subscription)
        {
            if (!subscribers.TryGetValue(conn, out Subscription current) || !ReferenceEquals(current, subscription)) return false;
            if (subscription.Error == null && CanAccessShared(subscription.Ingress, conn)) return true;
            // 调度器会丢弃失效动作；锁存撤销，避免权限短暂恢复后 Queued 永远悬空。
            subscription.Error = subscription.Error ?? "共享容器访问已失效。";
            return false;
        }

        private void PumpSharedSubscription(NetworkConnectionToClient conn, Subscription subscription)
        {
            if (subscription.Queued || subscription.Error != null || !IsCurrentSharedSubscription(conn, subscription)) return;
            if (subscription.Pending == null)
            {
                // 发送回调与 LateUpdate 之间可能有事务：先封口再捕获快照，版本覆盖恰好一致。
                FlushSharedJournal();
                if (subscription.HasBaseline && subscription.SentVersion == sharedVersion) return;
                SharedBatch next = null;
                if (subscription.HasBaseline)
                    foreach (SharedBatch batch in sharedHistory)
                        if (batch.From == subscription.SentVersion) { next = batch; break; }
                try { subscription.Pending = next ?? CaptureSharedBaseline(); }
                catch (InvalidDataException exception) { subscription.Error = exception.Message; return; }
                subscription.Offset = 0;
                if (++subscription.TransferSequence == 0) subscription.TransferSequence++;
            }

            SharedBatch pending = subscription.Pending;
            int offset = subscription.Offset;
            int count = Math.Min(SharedChunkBytes, pending.Payload.Length - offset);
            subscription.Queued = NetworkSendScheduler.QueueLatest(conn, netId, SharedStreamId,
                pending.Baseline ? 2 : 1, count + 160, 0.25, 0,
                () =>
                {
                    subscription.Queued = false;
                    if (!IsCurrentSharedSubscription(conn, subscription)) return;
                    var chunk = new byte[count];
                    Buffer.BlockCopy(pending.Payload, offset, chunk, 0, count);
                    subscription.Ingress.TargetSharedChunk(conn, netId, subscription.Session,
                        subscription.TransferSequence, pending.Baseline, pending.From, pending.To,
                        pending.Rows, pending.Columns, pending.Payload.Length, pending.Checksum, offset, chunk);
                    subscription.Offset += count;
                    if (subscription.Offset == pending.Payload.Length)
                    {
                        subscription.HasBaseline = true;
                        subscription.SentVersion = pending.To;
                        subscription.Pending = null;
                    }
                    // 在途基线不因历史溢出重启。完成后才补增量或下一完整基线。
                    PumpSharedSubscription(conn, subscription);
                }, () => IsCurrentSharedSubscription(conn, subscription));
        }

        [TargetRpc]
        private void TargetSharedChunk(NetworkConnectionToClient target, uint containerId, uint session,
            uint transfer, bool baseline, ulong from, ulong to, int gridRows, int gridColumns,
            int totalBytes, uint checksum, int offset, byte[] chunk)
        {
            if (!clientSessions.TryGetValue(containerId, out uint expected) || expected != session
                || !NetworkClient.spawned.TryGetValue(containerId, out NetworkIdentity identity)) return;
            BackpackNetworkInventory inventory = identity.GetComponent<BackpackNetworkInventory>();
            if (inventory == null) return;
            try
            {
                inventory.ReceiveSharedChunk(session, transfer, baseline, from, to, gridRows, gridColumns,
                    totalBytes, checksum, offset, chunk);
                if (inventory.sharedReceive != null) return;
                if (!inventory.mirrorReady) throw new InvalidDataException("共享库存镜像无法恢复。");
                if (baseline) inventory.SharedAccessChanged?.Invoke(true, null);
            }
            catch (Exception exception) when (exception is InvalidDataException || exception is ArgumentException
                || exception is InvalidOperationException || exception is IndexOutOfRangeException || exception is EndOfStreamException)
            {
                inventory.RegisterFailure("共享库存载荷被拒绝：" + exception.Message);
                RequestCloseShared(inventory);
            }
        }

        private void ReceiveSharedChunk(uint session, uint transfer, bool baseline, ulong from, ulong to,
            int gridRows, int gridColumns, int totalBytes, uint checksum, int offset, byte[] chunk)
        {
            if (totalBytes < sizeof(uint) || totalBytes > SharedPayloadLimit || chunk == null
                || chunk.Length == 0 || chunk.Length > SharedChunkBytes || offset < 0
                || offset > totalBytes - chunk.Length || transfer == 0 || gridRows <= 0 || gridColumns <= 0
                || (long)gridRows * gridColumns > InventoryContainer.MaxSlotCount)
                throw new InvalidDataException("共享库存分片边界非法。");
            if (offset == 0)
            {
                if (sharedReceive != null || !baseline && (!sharedClientSubscribed || sharedClientSession != session
                    || sharedClientVersion != from || to <= from))
                    throw new InvalidDataException("共享库存批次版本不连续。");
                sharedReceive = new SharedReceive { Session = session, Transfer = transfer,
                    Batch = new SharedBatch { Baseline = baseline, From = from, To = to, Rows = gridRows,
                        Columns = gridColumns, Checksum = checksum, Payload = new byte[totalBytes] } };
            }
            SharedReceive receive = sharedReceive;
            SharedBatch batch = receive?.Batch;
            if (receive == null || receive.Session != session || receive.Transfer != transfer || receive.Offset != offset
                || batch.Baseline != baseline || batch.From != from || batch.To != to || batch.Rows != gridRows
                || batch.Columns != gridColumns || batch.Checksum != checksum || batch.Payload.Length != totalBytes)
                throw new InvalidDataException("共享库存分片不属于当前批次。");
            Buffer.BlockCopy(chunk, 0, batch.Payload, offset, chunk.Length);
            receive.Offset += chunk.Length;
            if (receive.Offset != totalBytes) return;
            if (SharedChecksum(batch.Payload) != checksum) throw new InvalidDataException("共享库存分片校验失败。");
            using (NetworkReaderPooled reader = NetworkReaderPool.Get(batch.Payload))
            {
                if (baseline)
                {
                    sharedClientStream.Entries.OnDeserializeAll(reader);
                    sharedClientSubscribed = true;
                    sharedClientSession = session;
                    authoritativeRows = gridRows;
                    authoritativeColumns = gridColumns;
                    mirrorProjection.RequestFullRefresh(true);
                    sharedClientStream.Entries.Callback = OnSharedClientEntryChanged;
                }
                else sharedClientStream.Entries.OnDeserializeDelta(reader);
                if (reader.Remaining != 0) throw new InvalidDataException("共享库存批次含多余数据。");
            }
            sharedClientVersion = to;
            sharedReceive = null;
            mirrorDirty = true;
            RebuildMirror();
        }
    }
}
