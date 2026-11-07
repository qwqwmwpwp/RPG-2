using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace RPG4.Raid.Domain
{
    /// <summary>The lifecycle of the one match currently owned by a server process.</summary>
    public enum MatchRunState : byte
    {
        Waiting,
        Countdown,
        Preparing,
        Running,
        Settling,
        Completed,
        Cancelled
    }

    public enum MatchRunEventKind : byte
    {
        Joined,
        CountdownStarted,
        RosterLocked,
        PreparingStarted,
        PlayerReady,
        Started,
        PlayerEnded,
        MatchEnded,
        Cancelled,
        Completed
    }

    public enum MatchCommandStatus : byte
    {
        Accepted,
        Rejected
    }

    /// <summary>A small, transport-independent result for a match command.</summary>
    public sealed class MatchCommandResult
    {
        public MatchCommandStatus Status { get; private set; }
        public MatchRunState State { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public bool Accepted { get { return Status == MatchCommandStatus.Accepted; } }
        public bool Success { get { return Accepted; } }

        private MatchCommandResult(MatchCommandStatus status, MatchRunState state, string code, string message)
        {
            Status = status;
            State = state;
            Code = code;
            Message = message;
        }

        public static MatchCommandResult Accept(MatchRunState state, string code, string message)
        {
            return new MatchCommandResult(MatchCommandStatus.Accepted, state, code, message);
        }

        public static MatchCommandResult Reject(MatchRunState state, string code, string message)
        {
            return new MatchCommandResult(MatchCommandStatus.Rejected, state, code, message);
        }
    }

    public sealed class MatchRunEvent
    {
        public MatchRun Source { get; private set; }
        public MatchRunEventKind Kind { get; private set; }
        public MatchRunState State { get; private set; }
        public string PlayerKey { get; private set; }
        public string Message { get; private set; }
        public double Time { get; private set; }

        internal MatchRunEvent(MatchRun source, MatchRunEventKind kind, MatchRunState state,
            string playerKey, string message, double time)
        {
            Source = source;
            Kind = kind;
            State = state;
            PlayerKey = playerKey;
            Message = message;
            Time = time;
        }
    }

    /// <summary>
    /// Pure C# state machine for one server-side match. Individual RaidRun objects remain
    /// owned by the raid application; this object only owns the shared match roster/lifecycle.
    /// </summary>
    public sealed class MatchRun
    {
        public const int DefaultMaximumPlayers = 20;
        public const int DefaultMinimumPlayers = 1;
        public const double DefaultCountdownSeconds = 30d;
        public const double DefaultPreparationSeconds = 90d;
        public const double DefaultMatchSeconds = 20d * 60d;

        private readonly int minimumPlayers;
        private readonly int maximumPlayers;
        private readonly double countdownSeconds;
        private readonly double preparationSeconds;
        private readonly double matchSeconds;
        private readonly List<string> joinedPlayers = new List<string>();
        private readonly List<string> roster = new List<string>();
        private readonly HashSet<string> readyPlayers = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> activePlayers = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> endedPlayers = new HashSet<string>(StringComparer.Ordinal);
        private double now;
        private double deadline;

        public MatchRunState State { get; private set; }
        public double Now { get { return now; } }
        public double Deadline { get { return deadline; } }
        public int MinimumPlayers { get { return minimumPlayers; } }
        public int MaximumPlayers { get { return maximumPlayers; } }
        public int JoinedPlayerCount { get { return joinedPlayers.Count; } }
        public int RosterPlayerCount { get { return roster.Count; } }
        public int ReadyPlayerCount { get { return readyPlayers.Count; } }
        public int ActivePlayerCount { get { return activePlayers.Count; } }
        public bool IsTerminal { get { return State == MatchRunState.Completed || State == MatchRunState.Cancelled; } }
        public ReadOnlyCollection<string> JoinedPlayers { get { return Snapshot(joinedPlayers); } }
        public ReadOnlyCollection<string> Roster { get { return Snapshot(roster); } }
        public ReadOnlyCollection<string> ReadyPlayers { get { return Snapshot(readyPlayers); } }
        public ReadOnlyCollection<string> ActivePlayers { get { return Snapshot(activePlayers); } }
        public ReadOnlyCollection<string> EndedPlayers { get { return Snapshot(endedPlayers); } }
        public event Action<MatchRunEvent> Changed;

        public MatchRun(int minimumPlayers = DefaultMinimumPlayers, int maximumPlayers = DefaultMaximumPlayers,
            double countdownSeconds = DefaultCountdownSeconds, double preparationSeconds = DefaultPreparationSeconds,
            double matchSeconds = DefaultMatchSeconds)
        {
            if (minimumPlayers < 1) throw new ArgumentOutOfRangeException(nameof(minimumPlayers));
            if (maximumPlayers < minimumPlayers || maximumPlayers > DefaultMaximumPlayers)
                throw new ArgumentOutOfRangeException(nameof(maximumPlayers));
            if (countdownSeconds <= 0d) throw new ArgumentOutOfRangeException(nameof(countdownSeconds));
            if (preparationSeconds <= 0d) throw new ArgumentOutOfRangeException(nameof(preparationSeconds));
            if (matchSeconds <= 0d) throw new ArgumentOutOfRangeException(nameof(matchSeconds));
            this.minimumPlayers = minimumPlayers;
            this.maximumPlayers = maximumPlayers;
            this.countdownSeconds = countdownSeconds;
            this.preparationSeconds = preparationSeconds;
            this.matchSeconds = matchSeconds;
            State = MatchRunState.Waiting;
        }

        public MatchCommandResult Join(string playerKey, double time)
        {
            SetNow(time);
            if (string.IsNullOrEmpty(playerKey)) return Reject("InvalidPlayer", "玩家标识不能为空。");
            // Countdown is the configured admission window. The roster freezes when
            // the countdown expires or the maximum player count is reached.
            if (State != MatchRunState.Waiting && State != MatchRunState.Countdown)
                return Reject("RosterLocked", "对局名单已锁定。");
            if (joinedPlayers.Contains(playerKey)) return Reject("AlreadyJoined", "玩家已在对局中。");
            if (joinedPlayers.Count >= maximumPlayers) return Reject("Full", "对局人数已满。");
            joinedPlayers.Add(playerKey);
            Emit(MatchRunEventKind.Joined, playerKey, "玩家已加入。");
            if (State == MatchRunState.Waiting && joinedPlayers.Count >= minimumPlayers)
            {
                State = MatchRunState.Countdown;
                deadline = now + countdownSeconds;
                Emit(MatchRunEventKind.CountdownStarted, null, "最低人数已满足，倒计时开始。");
            }
            if (joinedPlayers.Count == maximumPlayers) LockRosterCore();
            return Accept("Joined", "玩家已加入。");
        }

        public MatchCommandResult LockRoster(double time)
        {
            SetNow(time);
            if (State != MatchRunState.Waiting && State != MatchRunState.Countdown)
                return Reject("InvalidState", "当前阶段不能锁定名单。");
            if (joinedPlayers.Count < minimumPlayers) return Reject("NotEnoughPlayers", "尚未达到最低人数。");
            LockRosterCore();
            return Accept("RosterLocked", "对局名单已锁定。");
        }

        public MatchCommandResult ConfirmReady(string playerKey, double time)
        {
            SetNow(time);
            if (State != MatchRunState.Preparing) return Reject("InvalidState", "当前阶段不能确认准备。");
            if (!roster.Contains(playerKey)) return Reject("NotInRoster", "玩家不在锁定名单中。");
            if (readyPlayers.Add(playerKey)) Emit(MatchRunEventKind.PlayerReady, playerKey, "玩家已准备。");
            if (readyPlayers.Count == roster.Count) StartCore();
            return Accept("Ready", "准备状态已记录。");
        }

        public MatchCommandResult Start(double time)
        {
            SetNow(time);
            if (State != MatchRunState.Preparing) return Reject("InvalidState", "当前阶段不能开始对局。");
            if (readyPlayers.Count != roster.Count) return Reject("NotReady", "仍有玩家未准备。");
            StartCore();
            return Accept("Started", "对局已开始。");
        }

        public MatchCommandResult EndPlayer(string playerKey, double time)
        {
            SetNow(time);
            if (State != MatchRunState.Running) return Reject("InvalidState", "当前阶段不能结束个人对局。");
            if (!activePlayers.Remove(playerKey)) return Reject("NotActive", "玩家不在进行中的名单中。");
            endedPlayers.Add(playerKey);
            Emit(MatchRunEventKind.PlayerEnded, playerKey, "玩家对局已结束。");
            if (activePlayers.Count == 0) BeginSettling("所有玩家对局均已结束。");
            return Accept("PlayerEnded", "玩家对局已结束。");
        }

        public MatchCommandResult EndMatch(double time)
        {
            SetNow(time);
            if (State != MatchRunState.Running) return Reject("InvalidState", "当前阶段不能结束整局。");
            foreach (string player in activePlayers) endedPlayers.Add(player);
            activePlayers.Clear();
            BeginSettling("整局对局已结束。");
            return Accept("MatchEnded", "整局已进入结算。");
        }

        public MatchCommandResult Cancel(double time)
        {
            SetNow(time);
            if (State == MatchRunState.Completed || State == MatchRunState.Cancelled)
                return Reject("Terminal", "对局已经结束。");
            if (State == MatchRunState.Running || State == MatchRunState.Settling)
                return Reject("AlreadyStarted", "已开始的对局不能取消。");
            State = MatchRunState.Cancelled;
            Emit(MatchRunEventKind.Cancelled, null, "对局已取消。");
            return Accept("Cancelled", "对局已取消。");
        }

        public void Tick(double time)
        {
            SetNow(time);
            if (State == MatchRunState.Countdown && now >= deadline) BeginPreparingFromCountdown();
            if (State == MatchRunState.Preparing && now >= deadline) PrepareTimeout();
            if (State == MatchRunState.Running && now >= deadline)
            {
                foreach (string player in activePlayers) endedPlayers.Add(player);
                activePlayers.Clear();
                BeginSettling("对局时间已到。");
            }
            if (State == MatchRunState.Settling)
            {
                State = MatchRunState.Completed;
                Emit(MatchRunEventKind.Completed, null, "对局结算已完成。");
            }
        }

        private void LockRosterCore()
        {
            if (State != MatchRunState.Waiting && State != MatchRunState.Countdown) return;
            LockRosterSnapshot();
            State = MatchRunState.Preparing;
            deadline = now + preparationSeconds;
            Emit(MatchRunEventKind.PreparingStarted, null, "对局名单已锁定，等待玩家准备。");
        }

        private void LockRosterSnapshot()
        {
            if (roster.Count > 0) return;
            roster.AddRange(joinedPlayers);
            Emit(MatchRunEventKind.RosterLocked, null, "对局名单已锁定。");
        }

        private void BeginPreparingFromCountdown()
        {
            if (State != MatchRunState.Countdown) return;
            LockRosterSnapshot();
            State = MatchRunState.Preparing;
            deadline = now + preparationSeconds;
            Emit(MatchRunEventKind.PreparingStarted, null, "倒计时结束，进入准备阶段。");
        }

        private void PrepareTimeout()
        {
            if (readyPlayers.Count < minimumPlayers)
            {
                State = MatchRunState.Cancelled;
                Emit(MatchRunEventKind.Cancelled, null, "准备超时且准备人数不足。");
                return;
            }
            for (int index = roster.Count - 1; index >= 0; index--)
                if (!readyPlayers.Contains(roster[index])) roster.RemoveAt(index);
            StartCore();
        }

        private void StartCore()
        {
            State = MatchRunState.Running;
            activePlayers.Clear();
            foreach (string player in roster) activePlayers.Add(player);
            deadline = now + matchSeconds;
            Emit(MatchRunEventKind.Started, null, "对局已开始。");
        }

        private void BeginSettling(string message)
        {
            State = MatchRunState.Settling;
            deadline = now;
            Emit(MatchRunEventKind.MatchEnded, null, message);
        }

        private void SetNow(double time)
        {
            if (double.IsNaN(time) || double.IsInfinity(time)) throw new ArgumentOutOfRangeException(nameof(time));
            if (time < now) throw new ArgumentOutOfRangeException(nameof(time), "时间必须单调递增。");
            now = time;
        }

        private MatchCommandResult Accept(string code, string message)
        {
            return MatchCommandResult.Accept(State, code, message);
        }

        private MatchCommandResult Reject(string code, string message)
        {
            return MatchCommandResult.Reject(State, code, message);
        }

        private void Emit(MatchRunEventKind kind, string playerKey, string message)
        {
            Action<MatchRunEvent> handler = Changed;
            if (handler != null) handler(new MatchRunEvent(this, kind, State, playerKey, message, now));
        }

        private static ReadOnlyCollection<string> Snapshot(IEnumerable<string> values)
        {
            return new ReadOnlyCollection<string>(new List<string>(values));
        }
    }
}
