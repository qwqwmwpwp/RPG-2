using UnityEngine;
using RPG4.Backpack.Core;

namespace RPG4.Backpack.Runtime
{
    /// <summary>背包索引：本机角色组件负责登记默认背包，基础背包仅供按物体查询；重复登记保留先注册者，静态状态在进入运行模式时复位。</summary>
    public static class BackpackRegistry
    {
        /// <summary>首个登记的本机角色背包；没有则为 null。</summary>
        public static BackpackInventory Primary { get; private set; }

        /// <summary>取本地玩家背包；没有则返回 false。</summary>
        public static bool TryGetPrimary(out BackpackInventory backpack)
        {
            backpack = Primary;
            return backpack != null && backpack.isActiveAndEnabled;
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

        /// <summary>角色启用时登记本机玩家背包；远端角色与 NPC 不登记，重复者只记警告。</summary>
        internal static void Register(CharacterBackpack character)
        {
            if (character == null || !character.isActiveAndEnabled || !character.IsLocalPlayer) return;

            BackpackInventory backpack = character.Inventory;
            if (backpack == null) return;

            if (Primary == null || ReferenceEquals(Primary, backpack))
            {
                Primary = backpack;
                return;
            }

            BackpackLog.Warn(
                LogModule.Inventory,
                $"已存在本地玩家背包（{Primary.name}），忽略 {backpack.name} 的 Primary 登记；如需替换请调用 BackpackRegistry.SetPrimary。");
        }

        /// <summary>角色禁用或失去本机归属时，清除由该角色登记的默认背包。</summary>
        internal static void Unregister(CharacterBackpack character)
        {
            if (character == null) return;

            if (ReferenceEquals(Primary, character.Inventory)) Primary = null;
        }

        /// <summary>关闭 Domain Reload 时静态字段不会重置，此处显式复位，避免上一局的登记残留。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Primary = null;
        }
    }
}
