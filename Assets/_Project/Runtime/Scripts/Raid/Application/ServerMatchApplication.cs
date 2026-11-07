using System;
using System.Collections.ObjectModel;
using RPG4.Raid.Domain;

namespace RPG4.Raid.Application
{
    /// <summary>Single-process coordinator for the shared match lifecycle.</summary>
    public sealed class ServerMatchApplication
    {
        private readonly int minimumPlayers;
        private readonly int maximumPlayers;
        private readonly double countdownSeconds;
        private readonly double preparationSeconds;
        private readonly double matchSeconds;
        private MatchRun current;

        public MatchRun Current { get { return current; } }
        public MatchRunState State { get { return current == null ? MatchRunState.Waiting : current.State; } }
        public event Action<MatchRunEvent> Changed;

        public ServerMatchApplication(int minimumPlayers = MatchRun.DefaultMinimumPlayers,
            int maximumPlayers = MatchRun.DefaultMaximumPlayers,
            double countdownSeconds = MatchRun.DefaultCountdownSeconds,
            double preparationSeconds = MatchRun.DefaultPreparationSeconds,
            double matchSeconds = MatchRun.DefaultMatchSeconds)
        {
            // Let MatchRun perform the complete range validation while keeping this class configuration-only.
            new MatchRun(minimumPlayers, maximumPlayers, countdownSeconds, preparationSeconds, matchSeconds);
            this.minimumPlayers = minimumPlayers;
            this.maximumPlayers = maximumPlayers;
            this.countdownSeconds = countdownSeconds;
            this.preparationSeconds = preparationSeconds;
            this.matchSeconds = matchSeconds;
        }

        public MatchCommandResult Join(string playerKey, double time)
        {
            if (current == null || current.IsTerminal) CreateRun();
            return current.Join(playerKey, time);
        }

        public MatchCommandResult JoinPlayer(string playerKey, double time) { return Join(playerKey, time); }
        public MatchCommandResult LockRoster(double time) { return RequireRun().LockRoster(time); }
        public MatchCommandResult ConfirmReady(string playerKey, double time) { return RequireRun().ConfirmReady(playerKey, time); }
        public MatchCommandResult Start(double time) { return RequireRun().Start(time); }
        public MatchCommandResult StartMatch(double time) { return Start(time); }
        public MatchCommandResult EndPlayer(string playerKey, double time) { return RequireRun().EndPlayer(playerKey, time); }
        public MatchCommandResult EndMatch(double time) { return RequireRun().EndMatch(time); }
        public MatchCommandResult Cancel(double time) { return RequireRun().Cancel(time); }
        public MatchCommandResult CancelMatch(double time) { return Cancel(time); }
        public void Tick(double time) { if (current != null) current.Tick(time); }

        public ReadOnlyCollection<string> Roster { get { return current == null ? Empty() : current.Roster; } }
        public ReadOnlyCollection<string> ActivePlayers { get { return current == null ? Empty() : current.ActivePlayers; } }

        private void CreateRun()
        {
            current = new MatchRun(minimumPlayers, maximumPlayers, countdownSeconds, preparationSeconds, matchSeconds);
            current.Changed += ForwardChange;
        }

        private MatchRun RequireRun()
        {
            if (current == null) return CreateAndReturn();
            return current;
        }

        private MatchRun CreateAndReturn()
        {
            CreateRun();
            return current;
        }

        private void ForwardChange(MatchRunEvent value)
        {
            Action<MatchRunEvent> handler = Changed;
            if (handler != null) handler(value);
        }

        private static ReadOnlyCollection<string> Empty()
        {
            return new ReadOnlyCollection<string>(new string[0]);
        }
    }
}
