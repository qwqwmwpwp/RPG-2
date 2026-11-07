namespace RPG4.Backpack.Items
{
    /// <summary>物品数值随机源抽象，便于在服务端注入确定性实现或测试替身。</summary>
    public interface IItemRandomSource
    {
        /// <summary>返回 [minInclusive, maxInclusive] 区间内的随机浮点数。</summary>
        float Range(float minInclusive, float maxInclusive);
    }

    /// <summary>基于 Unity 随机数的物品随机源单例。</summary>
    public sealed class UnityItemRandomSource : IItemRandomSource
    {
        public static readonly UnityItemRandomSource Instance = new UnityItemRandomSource(); // 全局单例。

        /// <summary>私有构造，仅通过单例访问。</summary>
        private UnityItemRandomSource()
        {
        }

        /// <inheritdoc/>
        public float Range(float minInclusive, float maxInclusive)
        {
            return UnityEngine.Random.Range(minInclusive, maxInclusive);
        }
    }
}
