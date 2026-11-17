namespace RPG4.CharacterAttributes
{
    /// <summary>武器等命中来源使用的伤害入口；返回是否实际改变生命。</summary>
    public interface IDamageable
    {
        bool ApplyDamage(DamageInfo damage);
    }
}
