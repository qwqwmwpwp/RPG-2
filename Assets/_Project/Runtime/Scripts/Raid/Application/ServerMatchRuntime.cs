using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RPG4.Raid.Domain;

namespace RPG4.Raid.Application
{
    /// <summary>Connection-independent state for the one match owned by a server process.</summary>
    public sealed class ServerMatchPlayer
    {
        public string PlayerKey { get; private set; }
        public bool Connected { get; internal set; }
        public bool Alive { get; internal set; }
        public bool ControlEnded { get; internal set; }

        internal ServerMatchPlayer(string playerKey)
        {
            PlayerKey = playerKey;
            Connected = true;
            Alive = true;
        }
    }

    /// <summary>Unity-side adapters can find the active process-owned match without referencing server startup code.</summary>
    public static class ServerMatchRuntimeLocator
    {
        public static ServerMatchRuntime Current { get; private set; }

        public static void Set(ServerMatchRuntime runtime)
        {
            Current = runtime;
        }
    }

    /// <summary>
    /// Server facade for the single-process/single-match lifecycle. It intentionally has no
    /// Unity or Mirror dependency; a transport adapter supplies a stable player key and calls
    /// the connection, readiness and end-point methods from its main-thread callbacks.
    /// </summary>
    public sealed class ServerMatchRuntime : IDisposable
    {
        private readonly ServerMatchApplication application;
        private readonly Dictionary<string, ServerMatchPlayer> players =
            new Dictionary<string, ServerMatchPlayer>(StringComparer.Ordinal);
        private readonly HashSet<string> pendingReady = new HashSet<string>(StringComparer.Ordinal);
        private readonly int maxActiveAi;
        private readonly int aiDecisionIntervalMilliseconds;
        private bool preparationRequested;
        private bool disposed;

        public ServerMatchRuntime(int minimumPlayers = MatchRun.DefaultMinimumPlayers,
            int maximumPlayers = MatchRun.DefaultMaximumPlayers,
            double countdownSeconds = MatchRun.DefaultCountdownSeconds,
            double preparationSeconds = MatchRun.DefaultPreparationSeconds,
            double matchSeconds = MatchRun.DefaultMatchSeconds,
            int maxActiveAi = 30,
            int aiDecisionIntervalMilliseconds = 100)
        {
            if (maxActiveAi < 1 || maxActiveAi > 30) throw new ArgumentOutOfRangeException(nameof(maxActiveAi));
            if (aiDecisionIntervalMilliseconds < 10 || aiDecisionIntervalMilliseconds > 5000)
                throw new ArgumentOutOfRangeException(nameof(aiDecisionIntervalMilliseconds));
            application = new ServerMatchApplication(minimumPlayers, maximumPlayers, countdownSeconds,
                preparationSeconds, matchSeconds);
            this.maxActiveAi = maxActiveAi;
            this.aiDecisionIntervalMilliseconds = aiDecisionIntervalMilliseconds;
            application.Changed += OnApplicationChanged;
        }

        public MatchRun Current { get { return application.Current; } }
        public MatchRunState State { get { return application.State; } }
        public int PlayerCount { get { return players.Count; } }
        public int MaxActiveAi { get { return maxActiveAi; } }
        public int AiDecisionIntervalMilliseconds { get { return aiDecisionIntervalMilliseconds; } }
        public ReadOnlyCollection<string> Roster { get { return application.Roster; } }
        public ReadOnlyCollection<string> ActivePlayers { get { return application.ActivePlayers; } }
        public event Action<MatchRunEvent> Changed;

        /// <summary>Raised once when the roster enters the preparation phase; AI spawning can be bound here later.</summary>
        public event Action<ReadOnlyCollection<string>> PreparationRequested;
        public event Action<int, int> AiSpawnRequested;

        public MatchCommandResult Join(string playerKey, double time)
        {
            if (disposed) return MatchCommandResult.Reject(State, "Disposed", "对局运行时已停止。");
            if (string.IsNullOrEmpty(playerKey)) return application.Join(playerKey, time);
            ServerMatchPlayer existing;
            if (players.TryGetValue(playerKey, out existing))
            {
                if (!existing.Connected && existing.Alive && !existing.ControlEnded)
                    return Reconnect(playerKey, time);
                return MatchCommandResult.Reject(State, "AlreadyJoined", "玩家已在对局中。");
            }
            MatchCommandResult result = application.Join(playerKey, time);
            if (result.Accepted) players.Add(playerKey, new ServerMatchPlayer(playerKey));
            return result;
        }

        public MatchCommandResult JoinPlayer(string playerKey, double time) { return Join(playerKey, time); }

        public MatchCommandResult Reconnect(string playerKey, double time)
        {
            if (disposed) return MatchCommandResult.Reject(State, "Disposed", "对局运行时已停止。");
            ServerMatchPlayer player;
            if (!players.TryGetValue(playerKey, out player))
                return MatchCommandResult.Reject(State, "UnknownPlayer", "玩家不在本局名单中。");
            if (player.Connected) return MatchCommandResult.Reject(State, "AlreadyConnected", "玩家已经连接。");
            if (!player.Alive || player.ControlEnded || State == MatchRunState.Settling ||
                State == MatchRunState.Completed || State == MatchRunState.Cancelled)
                return MatchCommandResult.Reject(State, "ControlEnded", "角色已死亡、结束或本局已结算。");
            player.Connected = true;
            return MatchCommandResult.Accept(State, "Reconnected", "玩家已重新接入本局。");
        }

        /// <summary>Marks a transport disconnect without ending an alive player's match.</summary>
        public bool Disconnect(string playerKey, double time)
        {
            if (disposed) return false;
            ServerMatchPlayer player;
            if (!players.TryGetValue(playerKey, out player) || !player.Connected) return false;
            if (Current != null) Current.Tick(time);
            player.Connected = false;
            return true;
        }

        public MatchCommandResult ConfirmReady(string playerKey, double time)
        {
            if (!IsControllable(playerKey)) return MatchCommandResult.Reject(State, "ControlEnded", "玩家已结束对局控制权。");
            return application.ConfirmReady(playerKey, time);
        }

        public MatchCommandResult MarkReady(string playerKey, double time)
        {
            if (!IsControllable(playerKey)) return MatchCommandResult.Reject(State, "ControlEnded", "玩家已结束对局控制权。");
            if (State == MatchRunState.Preparing) return ConfirmReady(playerKey, time);
            pendingReady.Add(playerKey);
            return MatchCommandResult.Accept(State, "ReadyQueued", "玩家准备状态已记录。");
        }

        public MatchCommandResult Start(double time) { return application.Start(time); }

        public MatchCommandResult EndPlayer(string playerKey, double time)
        {
            ServerMatchPlayer player;
            if (!players.TryGetValue(playerKey, out player) || player.ControlEnded)
                return MatchCommandResult.Reject(State, "UnknownPlayer", "玩家不在进行中的名单中。");
            MatchCommandResult result = application.EndPlayer(playerKey, time);
            if (result.Accepted)
            {
                player.Alive = false;
                player.ControlEnded = true;
                player.Connected = false;
            }
            return result;
        }

        public MatchCommandResult MarkDead(string playerKey, double time) { return EndPlayer(playerKey, time); }
        public MatchCommandResult MarkExtracted(string playerKey, double time) { return EndPlayer(playerKey, time); }

        public MatchCommandResult EndMatch(double time) { return application.EndMatch(time); }

        public void Tick(double time)
        {
            if (disposed) return;
            application.Tick(time);
        }

        public bool TryGetPlayer(string playerKey, out ServerMatchPlayer player)
        {
            return players.TryGetValue(playerKey, out player);
        }

        private bool IsControllable(string playerKey)
        {
            ServerMatchPlayer player;
            return players.TryGetValue(playerKey, out player) && player.Alive && !player.ControlEnded;
        }

        private void OnApplicationChanged(MatchRunEvent value)
        {
            if (value.Kind == MatchRunEventKind.Completed || value.Kind == MatchRunEventKind.Cancelled)
            {
                preparationRequested = false;
                players.Clear();
                pendingReady.Clear();
            }
            if (value.Kind == MatchRunEventKind.PreparingStarted && !preparationRequested)
            {
                preparationRequested = true;
                foreach (string playerKey in new List<string>(pendingReady))
                    if (players.ContainsKey(playerKey)) application.ConfirmReady(playerKey, value.Time);
                pendingReady.Clear();
                Action<ReadOnlyCollection<string>> handler = PreparationRequested;
                if (handler != null) handler(value.Source.Roster);
                Action<int, int> aiHandler = AiSpawnRequested;
                if (aiHandler != null) aiHandler(maxActiveAi, aiDecisionIntervalMilliseconds);
            }
            Action<MatchRunEvent> changed = Changed;
            if (changed != null) changed(value);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            application.Changed -= OnApplicationChanged;
            players.Clear();
            pendingReady.Clear();
        }
    }
}
