using System;
using System.Collections.Generic;
using Mirror;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public sealed partial class NetworkRoleLoadout
    {
        private NetworkLootSource serverLoot, clientLoot;
        private LootSource serverLootData;
        private uint serverLootAnchorId, clientLootAnchorId;
        private bool clientGroundLoot;
        private Vector3 clientLootCenter;
        private float clientLootDistance;
        private int clientLootScene;
        private bool CanAccessServerLoot() => serverLootData?.GroundSession != null
            ? serverLootData.GroundSession.CanAccess(transform) : serverLoot != null && serverLoot.CanAccess(transform);
        private string serverLootToken, clientLootToken, clientLootSourceId;
        private int clientLootRevision = -1, lootOpenSequence, serverLootOpenId;
        private bool receivingLoot, closingLoot;

        public bool TryPrepareCorpseLoot(out LootSource source, out string error)
        {
            source = null; error = null;
            if (!NetworkServer.active || !serverInitialized || role == null) return Fail("服务器角色库存未就绪。", out error);
            if (!role.TryPrepareCorpseLoot(out LootSource prepared, out error)) return false;
            if (!TryEncodeLoot(prepared, out _, out error)) return false;
            source = prepared; return true;
        }
        public bool CommitCorpseLoot(LootSource source, out string error)
        {
            error = null;
            if (!NetworkServer.active || !serverInitialized || serverRevision == long.MaxValue)
                return Fail("服务器角色库存未就绪或版本达到上限。", out error);
            return role.CommitCorpseLoot(source, out error);
        }

        internal bool TryPrepareCorpseChanges(Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates,
            LootSource source, out string error)
        {
            if (!NetworkServer.active || !serverInitialized || serverRevision == long.MaxValue)
                return Fail("服务器角色库存未就绪或版本达到上限。", out error);
            return TryCapture(out _, out error, candidates, serverRevision + 1)
                && TryEncodeLoot(source, out _, out error);
        }

        public bool OpenLoot(NetworkLootSource source)
        {
            if (!localStarted || !isLocalPlayer || !isOwned || !IsReady || !localInputAllowed || source == null
                || !source.IsAvailable) return false;
            if (Presentation == null) return false;
            if (!Presentation.CanOpenLoot(out string bindingError))
            {
                Debug.LogWarning(bindingError, this);
                return false;
            }
            CloseLoot();
            clientLoot = source; clientLootAnchorId = source.netId; clientGroundLoot = source.GetComponent<NetworkGroundLootItem>() != null; clientLootCenter = source.transform.position; clientLootDistance = source.InteractionDistance; clientLootScene = source.gameObject.scene.handle;
            CmdOpenLoot(source.netId, clientSession, clientEpoch, lootOpenSequence);
            return true;
        }
        /// <summary>释放本次搜刮地址并关闭展示，通知服务器结束对应会话。</summary>
        public void CloseLoot()
        {
            if (closingLoot) return;
            closingLoot = true;
            try
            {
                int closedOpenId = lootOpenSequence;
                string closedSession = clientSession;
                string closedEpoch = clientEpoch;
                lootOpenSequence++;
                bool hadSession = clientLoot != null || !string.IsNullOrEmpty(clientLootToken);
                clientLoot = null; clientLootToken = null; clientLootSourceId = null; clientLootRevision = -1; clientGroundLoot = false;
                projection.CloseLoot();
                Presentation?.CloseLoot();
                if (hadSession && localStarted && isLocalPlayer && isOwned)
                    CmdCloseLoot(closedSession, closedEpoch, closedOpenId);
            }
            finally { closingLoot = false; }
        }
        [Command] private void CmdOpenLoot(uint sourceId, string session, string epoch, int openId, NetworkConnectionToClient sender = null)
        {
            if (!Owns(sender) || !serverInitialized || !operationsAllowed || session != serverClientSession
                || epoch != serverEpoch || openId <= serverLootOpenId) return;
            CloseServerLoot(); serverLootOpenId = openId;
            if (!NetworkServer.spawned.TryGetValue(sourceId, out NetworkIdentity identity)) { SendLootClosed(sender, "战利品源已消失。"); return; }
            NetworkLootSource source = identity.GetComponent<NetworkLootSource>();
            if (source == null || !source.TryOpenServer(transform, out LootSource data, out string error))
            { SendLootClosed(sender, "战利品源不可用、距离过远或无法装箱。"); return; }
            if (!TryEncodeLoot(data, out _, out error))
            { data.GroundSession?.Dispose(); SendLootClosed(sender, error); return; }
            serverLoot = source; serverLootData = data; serverLootAnchorId = sourceId; serverLootToken = Guid.NewGuid().ToString("N");
            data.Changed += OnServerLootChanged;
            OnServerLootChanged();
        }
        [Command]
        private void CmdCloseLoot(string session, string epoch, int openId, NetworkConnectionToClient sender = null)
        {
            if (!Owns(sender) || session != serverClientSession || epoch != serverEpoch || openId != serverLootOpenId) return;
            CloseServerLoot();
        }
        private void CloseServerLoot()
        {
            if (serverLootData != null)
            { serverLootData.Changed -= OnServerLootChanged; serverLootData.GroundSession?.Dispose(); }
            serverLootData = null; serverLoot = null; serverLootToken = null; serverLootAnchorId = 0;
        }
        private void OnServerLootChanged()
        {
            if (serverLootData == null || !CanAccessServerLoot() || !operationsAllowed || connectionToClient == null)
            { CloseServerLoot(); if (connectionToClient != null) SendLootClosed(connectionToClient, "战利品会话已关闭。"); return; }
            if (!TryEncodeLoot(serverLootData, out string json, out string error))
            { CloseServerLoot(); SendLootClosed(connectionToClient, error); return; }
            TargetReceiveLoot(connectionToClient, serverLootAnchorId, serverLootToken, clientSession: serverClientSession, epoch: serverEpoch, openId: serverLootOpenId, json: json);
        }
        private void SendLootClosed(NetworkConnectionToClient target, string reason) => TargetLootClosed(target, serverClientSession, serverEpoch, serverLootOpenId, BoundResultMessage(reason));
        [TargetRpc] private void TargetLootClosed(NetworkConnectionToClient target, string session, string epoch, int openId, string reason)
        {
            if (!localStarted || session != clientSession || epoch != clientEpoch || openId != lootOpenSequence) return;
            CloseLoot(); if (!string.IsNullOrEmpty(reason)) Debug.LogWarning(reason, this);
        }
        private void UpdateLoot()
        {
            if (isServer && serverLootData != null && (!operationsAllowed || !CanAccessServerLoot()))
            { CloseServerLoot(); if (connectionToClient != null) SendLootClosed(connectionToClient, "离开战利品交互距离。"); }
            if (!localStarted || !isLocalPlayer || clientLoot == null && !clientGroundLoot) return;
            if ((!clientGroundLoot && (clientLoot == null || !clientLoot.IsAvailable)) || !clientOperationsAllowed || !localInputAllowed
                || (clientGroundLoot ? gameObject.scene.handle != clientLootScene || (transform.position - clientLootCenter).sqrMagnitude > clientLootDistance * clientLootDistance
                    : (transform.position - clientLoot.transform.position).sqrMagnitude > clientLoot.InteractionDistance * clientLoot.InteractionDistance)
                || !receivingLoot && !string.IsNullOrEmpty(clientLootToken) && (Presentation == null || !Presentation.IsLootOpen)) CloseLoot();
        }

        /// <summary>校验当前搜刮窗口、令牌和两个投影端点，再提交搜刮请求。</summary>
        private void SubmitLootOperation(LoadoutOperationRequest request, bool endpointsReady)
        {
            var session = request.Session as LoadoutWindowSession;
            if (!localStarted || !isLocalPlayer || !projectionReady || !clientOperationsAllowed || !localInputAllowed || clientLoot == null && !clientGroundLoot
                || string.IsNullOrEmpty(clientLootToken) || session == null || !session.IsActive || session.OwnerKey != ownerKey
                || Presentation == null || !Presentation.OwnsSession(session) || !Presentation.IsLootOpen || !endpointsReady || pendingRequest != 0 || pendingRuntimeRequest != 0 || nextRequest == int.MaxValue || awaitingSync)
            {
                EventBus.Global.Publish(LoadoutChannels.OperationCompleted, request.Session, new LoadoutOperationResult(request, LocalOutcome(LoadoutResultCode.SessionExpired, "战利品会话或玩家目标无效。", LoadoutOperationChannel.Loot))); return;
            }
            SendClientOperation(request, LoadoutOperationChannel.Loot);
        }

        [Command] private void CmdLootOperate(string token, string json, NetworkConnectionToClient sender = null)
        { ExecuteInventoryRequest(json, token, true, sender); }

        private bool TryResolveLootEndpoint(string id, out ILoadoutEndpoint target)
        {
            if (serverLootData.TryGetEndpoint(id, out target)) return true;
            return serverEndpoints.TryGetValue(id, out target);
        }

        private bool TryEncodeLoot(LootSource source, out string json, out string error,
            Dictionary<ILoadoutEndpoint, List<ItemDisplayData>> candidates = null)
        {
            using var encodingScope = LoadoutDiagnostics.Begin(LoadoutDiagnosticStage.Encode);
            if (!lootCodec.TryEncodeLoot(source, out json, out error, candidates)) return false;
            if (LoadoutDiagnostics.Enabled)
                LoadoutDiagnostics.RecordSnapshot(CurrentDiagnosticKey(), "loot", System.Text.Encoding.UTF8.GetByteCount(json));
            return true;
        }
        private bool TryDecodeLoot(string json, out DecodedLootState result, out string error) =>
            lootCodec.TryDecodeLoot(json, out result, out error);

        /// <summary>校验搜刮会话与版本，写完整批投影并在展示绑定成功后通知端点。</summary>
        [TargetRpc] private void TargetReceiveLoot(NetworkConnectionToClient target, uint sourceId, string token, string clientSession, string epoch, int openId, string json)
        {
            if (!localStarted || !isLocalPlayer || this.clientSession != clientSession || epoch != clientEpoch || openId != lootOpenSequence || sourceId != clientLootAnchorId || !ValidToken(token)) return;
            if (!TryDecodeLoot(json, out DecodedLootState decoded, out string error)) { Debug.LogWarning(error, this); CloseLoot(); return; }
            int revision = decoded.Revision;
            if (clientLootToken == token && revision < clientLootRevision) return;
            clientGroundLoot = decoded.Ground; clientLootSourceId = decoded.Id;
            clientLootToken = token; clientLootRevision = revision;
            receivingLoot = true;
            try
            {
                List<LootSectionSnapshot> projected = projection.ApplyLoot(decoded);
                if (Presentation == null) { CloseLoot(); return; }
                if (!Presentation.ShowLoot(projected, out string bindingError))
                {
                    Debug.LogWarning(bindingError, this);
                    CloseLoot();
                    return;
                }
                projection.NotifyLootChanged();
            }
            finally { receivingLoot = false; }
        }
    }
}
