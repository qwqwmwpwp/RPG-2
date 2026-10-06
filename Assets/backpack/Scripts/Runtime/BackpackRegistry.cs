using System.Collections.Generic;
using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Runtime
{
    /// <summary>单机背包索引：登记场上全部 <see cref="BackpackInventory"/> 实例，提供「本地玩家背包」全局入口与「按物体取包」查询；组件在 OnEnable / OnDisable 自动登记与注销，重复的本地玩家包保留先注册者并记警告，静态状态按 EventBus 同款口径做 Domain Reload 复位。</summary>
    public static class BackpackRegistry
    {
        private static readonly List<BackpackInventory> registered = new List<BackpackInventory>(); // 已登记背包（仅启用中的组件）。

        /// <summary>本地玩家背包（登记为 isLocalPlayerInventory 的首个实例）；没有则为 null。</summary>
        public static BackpackInventory Primary { get; private set; }

        /// <summary>取本地玩家背包；没有则返回 false。</summary>
        public static bool TryGetPrimary(out BackpackInventory backpack)
        {
            backpack = Primary;
            return backpack != null;
        }

        /// <summary>按拥有者物体查找它自己的背包：先取同物体组件，再取子物体组件（含未激活），两处都没有则返回 false。</summary>
        public static bool TryGet(GameObject owner, out BackpackInventory backpack)
        {
            backpack = null;
            if (owner == null) return false;
            if (owner.TryGetComponent(out backpack)) return true;

            backpack = owner.GetComponentInChildren<BackpackInventory>(true);
            return backpack != null;
        }

        /// <summary>显式指定本地玩家背包（用于覆盖自动登记结果）；传 null 表示清除。已在同一个实例上时不重复记日志。</summary>
        public static void SetPrimary(BackpackInventory backpack)
        {
            if (ReferenceEquals(Primary, backpack)) return;

            if (Primary != null && backpack != null)
            {
                BackpackLog.Info(LogModule.Inventory, $"本地玩家背包被显式覆盖：{Primary.name} → {backpack.name}。");
            }

            Primary = backpack;
        }

        /// <summary>组件启用时登记；本地玩家包以「先注册者胜」定为 Primary，随后出现重复者只记警告。</summary>
        internal static void Register(BackpackInventory backpack)
        {
            if (backpack == null) return;
            if (registered.Contains(backpack)) return;

            registered.Add(backpack);
            if (!backpack.IsLocalPlayerInventory) return;

            if (Primary == null || ReferenceEquals(Primary, backpack))
            {
                Primary = backpack;
                return;
            }

            BackpackLog.Warn(
                LogModule.Inventory,
                $"已存在本地玩家背包（{Primary.name}），忽略 {backpack.name} 的 Primary 登记；如需替换请调用 BackpackRegistry.SetPrimary。");
        }

        /// <summary>组件禁用 / 销毁时注销；被注销者若为 Primary 则一并清空。</summary>
        internal static void Unregister(BackpackInventory backpack)
        {
            if (backpack == null) return;

            registered.Remove(backpack);
            if (ReferenceEquals(Primary, backpack)) Primary = null;
        }

        /// <summary>关闭 Domain Reload 时静态字段不会重置，此处显式复位，避免上一局的登记残留。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            registered.Clear();
            Primary = null;
        }
    }
}
