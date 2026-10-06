using System;

namespace RPG4.Backpack.Core
{
    /// <summary><see cref="EventBus.Subscribe"/> 的返回值，用于显式退订；不持有 listener 强引用（否则弱引用自动解绑会变成常驻），<c>default</c> 即未订阅且 <see cref="Dispose"/> 幂等。</summary>
    public readonly struct EventSubscription : IDisposable
    {
        private readonly EventBus hub;
        private readonly int handle;

        /// <summary>由 <see cref="EventBus"/> 内部构造，业务代码不直接调用。</summary>
        public EventSubscription(EventBus hub, int handle)
        {
            this.hub = hub;
            this.handle = handle;
        }

        public bool IsAlive => hub != null && handle > 0 && hub.IsAliveHandle(handle); // 句柄是否指向一条仍生效的订阅。

        /// <summary>退订。总线已销毁或重复调用时安全返回（幂等）。</summary>
        public void Dispose()
        {
            if (hub == null || handle <= 0) return;

            hub.Unsubscribe(handle);
        }

        /// <summary>调试文本：句柄号；未订阅时输出 (none)。</summary>
        public override string ToString()
        {
            return handle > 0 ? $"(handle {handle})" : "(none)";
        }
    }
}
