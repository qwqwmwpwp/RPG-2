using System;
using System.Collections.Generic;

namespace RPG4.LoadoutTemplate
{
    public enum LoadoutConnectionState { Disconnected, Connecting, Connected }

    /// <summary>UI 创建的窗口会话。每次打开使用新实例，关闭后使迟到请求失效。</summary>
    public sealed class LoadoutWindowSession
    {
        public LoadoutWindowSession(string ownerKey)
        { OwnerKey = ownerKey ?? string.Empty; Id = Guid.NewGuid().ToString("N"); IsActive = true; }
        public string Id { get; }
        public string OwnerKey { get; }
        public bool IsActive { get; private set; }
        public void Close() => IsActive = false;
    }

    public readonly struct LoadoutRoleConnectRequest
    {
        public LoadoutRoleConnectRequest(LoadoutWindowSession session, object replySource, int requestId)
        { Session = session; ReplySource = replySource; RequestId = requestId; }
        public LoadoutWindowSession Session { get; }
        public object ReplySource { get; }
        public int RequestId { get; }
    }

    /// <summary>只包含显示投影和事件地址，UI 不需要取得控制器组件。</summary>
    public readonly struct LoadoutRoleBinding
    {
        public LoadoutRoleBinding(LoadoutSlotId slotId, object itemSource, object storageSource,
            ContainerSnapshot itemSnapshot, ContainerSnapshot storageSnapshot)
        {
            SlotId = slotId; ItemSource = itemSource; StorageSource = storageSource;
            ItemSnapshot = ContainerDisplaySnapshot.Capture(itemSnapshot);
            StorageSnapshot = ContainerDisplaySnapshot.Capture(storageSnapshot);
        }
        public LoadoutSlotId SlotId { get; }
        public object ItemSource { get; }
        public object StorageSource { get; }
        public ContainerDisplaySnapshot ItemSnapshot { get; }
        public ContainerDisplaySnapshot StorageSnapshot { get; }
    }

    public readonly struct LoadoutRoleConnectReply
    {
        public LoadoutRoleConnectReply(LoadoutRoleConnectRequest request, object providerSource,
            IEnumerable<LoadoutRoleBinding> bindings, string error = null)
        {
            Session = request.Session; RequestId = request.RequestId; ProviderSource = providerSource;
            Bindings = Array.AsReadOnly(bindings == null ? Array.Empty<LoadoutRoleBinding>()
                : new List<LoadoutRoleBinding>(bindings).ToArray());
            Error = error ?? string.Empty;
        }
        public LoadoutWindowSession Session { get; }
        public int RequestId { get; }
        public object ProviderSource { get; }
        public IReadOnlyList<LoadoutRoleBinding> Bindings { get; }
        public string Error { get; }
        public bool Success => string.IsNullOrEmpty(Error) && ProviderSource != null && Bindings.Count == 10;
    }

    public readonly struct LoadoutDragConnectRequest
    {
        public LoadoutDragConnectRequest(LoadoutWindowSession session, object replySource, int requestId,
            LoadoutInteractionInput input)
        { Session = session; ReplySource = replySource; RequestId = requestId; Input = input; }
        public LoadoutWindowSession Session { get; }
        public object ReplySource { get; }
        public int RequestId { get; }
        public LoadoutInteractionInput Input { get; }
    }

    public readonly struct LoadoutDragConnectReply
    {
        public LoadoutDragConnectReply(LoadoutDragConnectRequest request, long interactionId,
            bool success, string error = null, object providerSource = null)
        {
            Session = request.Session; RequestId = request.RequestId; InteractionId = interactionId;
            Success = success; Error = error ?? string.Empty; ProviderSource = providerSource;
        }
        public LoadoutWindowSession Session { get; }
        public int RequestId { get; }
        public long InteractionId { get; }
        public object ProviderSource { get; }
        public bool Success { get; }
        public string Error { get; }
    }
}
