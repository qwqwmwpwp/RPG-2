using System;
using Mirror;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>场景中的战利品访问点，独立于原角色生命周期。网络数据只发给已验证的打开者。</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity))]
    public sealed class NetworkLootSource : NetworkBehaviour
    {
        [SerializeField, Min(.1f)] private float interactionDistance = 3f;
        [SyncVar] private bool available;
        [SyncVar] private bool empty;
        public bool IsAvailable => available;
        public bool IsEmpty => empty;
        public float InteractionDistance => float.IsNaN(interactionDistance) || float.IsInfinity(interactionDistance) ? 3f : Mathf.Clamp(interactionDistance, .1f, 20f);
        internal LootSource Source { get; private set; }
        private NetworkGroundLootItem ground;
        internal bool InitializeGroundServer(NetworkGroundLootItem item, out string error)
        {
            error = null;
            if (!NetworkServer.active || item == null || Source != null || ground != null)
            { error = "地面访问点已绑定或服务器未就绪。"; return false; }
            ground = item; return true;
        }
        internal void RefreshGround() { available = ground != null && ground.IsPresent; empty = !available; }
        internal bool TryOpenServer(Transform actor, out LootSource source, out string error)
        {
            source = null; error = null;
            if (!CanAccess(actor)) { error = "战利品来源不可访问。"; return false; }
            if (ground == null) { source = Source; return true; }
            if (!GroundLootSession.TryCreate(this, ground, out GroundLootSession session, out error)) return false;
            source = session.Source; return true;
        }
        internal event Action Changed;
        public bool InitializeServer(LootSource source, out string error)
        {
            error = null;
            if (!NetworkServer.active || source == null || Source != null || ground != null)
            { error = "战利品实体需要服务器及未绑定的数据源。"; return false; }
            Source = source; Source.Changed += OnSourceChanged; OnSourceChanged(); return true;
        }
        internal bool CanAccess(Transform actor) => this != null && isServer && available && (ground != null ? ground.IsPresent : Source != null && Source.IsActive)
            && actor != null && actor.gameObject.scene == gameObject.scene
            && (actor.position - transform.position).sqrMagnitude <= InteractionDistance * InteractionDistance;
        private void OnSourceChanged()
        {
            available = Source != null && Source.IsActive;
            empty = Source == null || Source.IsEmpty;
            Notify();
        }
        public override void OnStopServer() { available = false; Notify(); }
        private void Notify()
        {
            if (Changed == null) return;
            foreach (Action subscriber in Changed.GetInvocationList())
                try { subscriber(); } catch (Exception exception) { Debug.LogException(exception, this); }
        }
        private void OnDestroy()
        {
            if (Source != null) Source.Changed -= OnSourceChanged;
            Notify(); Source = null;
        }
    }
}
