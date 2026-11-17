using System;
using RPG4.Backpack.Core;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    public enum LoadoutEquipmentSlotId
    {
        Helmet = (int)LoadoutSlotId.Helmet,
        BodyArmor = (int)LoadoutSlotId.BodyArmor,
        SecondaryWeapon = (int)LoadoutSlotId.SecondaryWeapon,
        MeleeWeapon = (int)LoadoutSlotId.MeleeWeapon,
        PrimaryWeapon1 = (int)LoadoutSlotId.PrimaryWeapon1,
        PrimaryWeapon2 = (int)LoadoutSlotId.PrimaryWeapon2
    }

    [Serializable]
    public sealed class LoadoutSlotBinding
    {
        public LoadoutEquipmentSlotId slotId = LoadoutEquipmentSlotId.Helmet;
        public LoadoutSlotUI view;
    }

    /// <summary>维护 UI 与事件端点的关系，按窗口打开请求角色连接。</summary>
    [DisallowMultipleComponent]
    public sealed class LoadoutTemplateUI : MonoBehaviour
    {
        [SerializeField] private string ownerKey = "local-player";
        [Tooltip("四个容器面板，每个面板包含容器本体槽和内部格子。")]
        [SerializeField] private ContainerUI[] containers = new ContainerUI[4];
        [Tooltip("六个装备槽，依次配置头盔、胸甲、副武器、近战武器和两个主武器槽。")]
        [SerializeField] private LoadoutSlotBinding[] slots =
        {
            new LoadoutSlotBinding { slotId = LoadoutEquipmentSlotId.Helmet },
            new LoadoutSlotBinding { slotId = LoadoutEquipmentSlotId.BodyArmor },
            new LoadoutSlotBinding { slotId = LoadoutEquipmentSlotId.SecondaryWeapon },
            new LoadoutSlotBinding { slotId = LoadoutEquipmentSlotId.MeleeWeapon },
            new LoadoutSlotBinding { slotId = LoadoutEquipmentSlotId.PrimaryWeapon1 },
            new LoadoutSlotBinding { slotId = LoadoutEquipmentSlotId.PrimaryWeapon2 }
        };
        [SerializeField, Min(.1f)] private float connectionTimeout = 8f;
        private readonly object windowSource = new object();
        private EventSubscription windowSubscription, replySubscription, availableSubscription, lostSubscription;
        private LoadoutWindowSession session;
        private object providerSource;
        private LoadoutRoleConnectReply candidateReply;
        private bool windowOpen, publishingRequest;
        private int requestSequence, pendingRequest, replyCount;
        private float deadline;

        public object WindowSource => windowSource;
        public LoadoutWindowSession Session => session;
        public LoadoutConnectionState ConnectionState { get; private set; }
        public string ConnectionError { get; private set; } = string.Empty;

        internal void SetNetworkOwner(string value)
        {
            Disconnect();
            ownerKey = value;
        }

        private void OnEnable()
        {
            EventBus bus = EventBus.Global;
            windowSubscription = bus.Subscribe<bool>(LoadoutChannels.WindowChanged, windowSource, this, OnWindowChanged);
            replySubscription = bus.Subscribe<LoadoutRoleConnectReply>(LoadoutChannels.RoleConnected, windowSource, this, OnRoleReply);
            availableSubscription = bus.Subscribe<string>(LoadoutChannels.RoleAvailable, EventBus.AnySource, this, OnRoleAvailable);
            bus.Publish(LoadoutChannels.WindowStateRequested, windowSource);
        }

        private void Start() => EventBus.Global.Publish(LoadoutChannels.WindowStateRequested, windowSource);

        private void Update()
        {
            if (ConnectionState == LoadoutConnectionState.Connecting && Time.unscaledTime >= deadline)
                FailConnection("角色连接请求等待超时，请重新打开背包。");
        }

        private void OnDisable()
        {
            windowOpen = false;
            Disconnect();
            windowSubscription.Dispose();
            replySubscription.Dispose();
            availableSubscription.Dispose();
        }

        private void OnWindowChanged(bool open)
        {
            if (windowOpen == open && (open ? session != null : session == null)) return;
            windowOpen = open;
            if (open) RequestConnection();
            else Disconnect();
        }

        private void OnRoleAvailable(string key)
        {
            if (windowOpen && ConnectionState != LoadoutConnectionState.Connected
                && string.Equals(ownerKey, key, StringComparison.Ordinal)) RequestConnection();
        }

        private void RequestConnection()
        {
            Disconnect();
            if (!ValidateConfiguration(out string error))
            {
                FailConnection(error);
                return;
            }
            session = new LoadoutWindowSession(ownerKey);
            ConnectionState = LoadoutConnectionState.Connecting;
            ConnectionError = string.Empty;
            pendingRequest = ++requestSequence;
            replyCount = 0;
            candidateReply = default;
            float timeout = float.IsNaN(connectionTimeout) || float.IsInfinity(connectionTimeout)
                ? 8f : Mathf.Max(.1f, connectionTimeout);
            deadline = Time.unscaledTime + timeout;
            // 总线同步派发，先建立等待状态，再收集本次发现请求的全部响应。
            publishingRequest = true;
            try
            {
                EventBus.Global.Publish(LoadoutChannels.RoleConnectRequested, windowSource,
                    new LoadoutRoleConnectRequest(session, windowSource, pendingRequest));
            }
            finally
            {
                publishingRequest = false;
            }
            if (replyCount > 0) CompleteConnection();
        }

        private void OnRoleReply(LoadoutRoleConnectReply reply)
        {
            if (!windowOpen || session == null || !session.IsActive
                || !ReferenceEquals(reply.Session, session) || reply.RequestId != pendingRequest) return;
            if (ConnectionState == LoadoutConnectionState.Connected)
            {
                if (!ReferenceEquals(providerSource, reply.ProviderSource))
                    FailConnection("同一角色标识存在多个角色连接提供方。");
                return;
            }
            if (ConnectionState != LoadoutConnectionState.Connecting) return;
            candidateReply = reply;
            replyCount++;
            if (!publishingRequest) CompleteConnection();
        }

        private void CompleteConnection()
        {
            if (replyCount != 1)
            {
                FailConnection("同一角色标识存在多个角色连接提供方。");
                return;
            }
            if (!candidateReply.Success)
            {
                FailConnection(candidateReply.Error);
                return;
            }
            var bindings = candidateReply.Bindings;
            int identities = 0;
            foreach (LoadoutRoleBinding binding in bindings)
            {
                int id = (int)binding.SlotId;
                if (id < 0 || id > 9 || (identities & (1 << id)) != 0 || binding.ItemSource == null
                    || binding.ItemSnapshot == null
                    || !ReferenceEquals(binding.ItemSource, binding.ItemSnapshot.EventSource)
                    || (id < 4 && (binding.StorageSource == null || binding.StorageSnapshot == null
                        || !ReferenceEquals(binding.StorageSource, binding.StorageSnapshot.EventSource))))
                {
                    FailConnection("角色连接结果缺少有效的唯一槽位或事件地址。");
                    return;
                }
                identities |= 1 << id;
            }
            providerSource = candidateReply.ProviderSource;
            lostSubscription = EventBus.Global.Subscribe(LoadoutChannels.RoleDisconnected, providerSource, this, OnRoleLost);
            ConnectionState = LoadoutConnectionState.Connected;
            foreach (LoadoutRoleBinding binding in bindings)
            {
                foreach (ContainerUI container in containers ?? Array.Empty<ContainerUI>())
                    if (container != null && (int)container.ContainerType - 1 == (int)binding.SlotId)
                        container.BindConnection(session, binding.StorageSource, binding.ItemSource, binding.StorageSnapshot);
                foreach (LoadoutSlotBinding slot in slots ?? Array.Empty<LoadoutSlotBinding>())
                    if (slot?.view != null && (int)slot.slotId == (int)binding.SlotId)
                        slot.view.Bind(binding.ItemSource, session);
            }
            candidateReply = default;
        }

        private void OnRoleLost()
        {
            if (windowOpen) RequestConnection();
            else Disconnect();
        }

        private void FailConnection(string error)
        {
            Disconnect();
            ConnectionError = string.IsNullOrEmpty(error) ? "角色连接失败。" : error;
            Debug.LogWarning(ConnectionError, this);
        }

        private void Disconnect()
        {
            LoadoutWindowSession previous = session;
            session = null;
            providerSource = null;
            pendingRequest = 0;
            replyCount = 0;
            candidateReply = default;
            ConnectionState = LoadoutConnectionState.Disconnected;
            lostSubscription.Dispose();
            if (previous != null)
            {
                previous.Close();
                EventBus.Global.Publish(LoadoutChannels.SessionClosed, previous);
            }
            foreach (ContainerUI container in containers ?? Array.Empty<ContainerUI>())
                if (container != null) container.Disconnect();
            foreach (LoadoutSlotBinding slot in slots ?? Array.Empty<LoadoutSlotBinding>())
                if (slot?.view != null) slot.view.Disconnect();
        }

        internal bool ValidateNetworkConfiguration(out string error)
        {
            if (!ValidateConfiguration(out error)) return false;
            if (containers == null || containers.Length != 4 || slots == null || slots.Length != 6)
            { error = "联机背包必须完整接线四容器和六装备槽。"; return false; }
            foreach (ContainerUI container in containers)
                if (container == null || !container.ValidateNetworkConfiguration(out error))
                { error = error ?? "联机背包存在未接线的容器面板。"; return false; }
            foreach (LoadoutSlotBinding slot in slots)
                if (slot?.view == null)
                { error = "联机背包存在未接线的装备槽。"; return false; }
            return true;
        }

        public bool ValidateConfiguration(out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(ownerKey))
            { error = "请配置角色标识，用于发现角色控制器和交换器。"; return false; }
            if ((containers?.Length ?? 0) > 4 || (slots?.Length ?? 0) > 6)
            { error = "UI 管理器配置四个容器面板和六个装备槽。"; return false; }
            int kinds = 0;
            foreach (ContainerUI container in containers ?? Array.Empty<ContainerUI>())
            {
                if (container == null) continue;
                int kind = (int)container.ContainerType;
                if (kind < 1 || kind > 4 || (kinds & (1 << kind)) != 0)
                { error = "四个容器面板必须使用互不重复的有效类别。"; return false; }
                kinds |= 1 << kind;
            }
            int ids = 0;
            var views = new System.Collections.Generic.HashSet<LoadoutSlotUI>();
            foreach (LoadoutSlotBinding binding in slots ?? Array.Empty<LoadoutSlotBinding>())
            {
                if (binding == null) continue;
                int id = (int)binding.slotId;
                if (id < 4 || id > 9 || (ids & (1 << id)) != 0
                    || (binding.view != null && !views.Add(binding.view)))
                { error = "六个装备槽的类别和视图引用必须各自唯一。"; return false; }
                ids |= 1 << id;
            }
            return true;
        }
    }
}
