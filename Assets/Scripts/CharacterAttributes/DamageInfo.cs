using UnityEngine;

namespace RPG4.CharacterAttributes
{
    public enum DamageKind : byte { Direct, Periodic }

    /// <summary>一次伤害请求；接收方只接受有限且大于零的伤害量。</summary>
    public readonly struct DamageInfo
    {
        public float Amount { get; }
        public DamageKind Kind { get; }
        public GameObject Source { get; }
        public Vector3 Point { get; }
        /// <summary>伤害由来源指向接收者的世界空间方向。</summary>
        public Vector3 Direction { get; }

        public DamageInfo(float amount) : this(amount, null, Vector3.zero, Vector3.zero) { }

        public DamageInfo(float amount, GameObject source, Vector3 point, Vector3 direction, DamageKind kind = DamageKind.Direct)
        {
            Amount = amount;
            Kind = kind;
            Source = source;
            Point = point;
            Direction = direction;
        }
    }
}
