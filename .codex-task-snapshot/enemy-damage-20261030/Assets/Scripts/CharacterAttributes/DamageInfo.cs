namespace RPG4.CharacterAttributes
{
    /// <summary>一次伤害请求；接收方只接受有限且大于零的伤害量。</summary>
    public readonly struct DamageInfo
    {
        public float Amount { get; }

        public DamageInfo(float amount)
        {
            Amount = amount;
        }
    }
}
