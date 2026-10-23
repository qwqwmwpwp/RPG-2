using RPG4.Input;

namespace RPG4.PlayerMovement
{
    /// <summary>每个角色独立的运行时引用集合；输入和配置由各自的数据源维护。</summary>
    internal sealed class PlayerContext
    {
        internal PlayerInput Input { get; }
        internal PlayerContentData Content { get; }
        internal ExplorationState.Data Exploration { get; }

        internal PlayerContext(PlayerInput input, PlayerContentData content, ExplorationState.Data exploration)
        {
            Input = input;
            Content = content;
            Exploration = exploration;
        }
    }
}
