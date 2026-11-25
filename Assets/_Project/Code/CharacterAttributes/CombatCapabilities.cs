namespace RPG4.CharacterAttributes
{
    /// <summary>当前模式开放的战斗能力。角色存活、装备就绪和网络写权限由各执行入口另行检查。</summary>
    public readonly struct CombatCapabilities
    {
        public static readonly CombatCapabilities Offline = new CombatCapabilities(weaponActions: true, damage: false, consumables: true);
        public static readonly CombatCapabilities Online = new CombatCapabilities(weaponActions: false, damage: false, consumables: false);
        public bool WeaponActions { get; }
        public bool Damage { get; }
        public bool Consumables { get; }

        private CombatCapabilities(bool weaponActions, bool damage, bool consumables)
        {
            WeaponActions = weaponActions;
            Damage = damage;
            Consumables = consumables;
        }
    }
}
