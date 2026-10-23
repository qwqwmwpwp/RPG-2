using System;

namespace RPG4.StateMachine
{
    public abstract class State
    {
        public StateMachine Machine { get; }
        public State Parent { get; }
        public State ActiveChild { get; internal set; }

        protected State(StateMachine machine, State parent = null)
        {
            Machine = machine ?? throw new ArgumentNullException(nameof(machine));
            Parent = parent;
        }

        protected virtual State GetInitialState() => null;
        protected virtual State GetTransition() => null;
        protected virtual void OnEnter() { }
        protected virtual void OnExit() { }
        protected virtual void OnUpdate(float deltaTime) { }
        protected virtual void OnFixedUpdate(float deltaTime) { }

        internal State InitialState() => GetInitialState();
        internal State Transition() => GetTransition();
        internal void Enter() => OnEnter();
        internal void Exit() => OnExit();
        internal void Update(float deltaTime) => OnUpdate(deltaTime);
        internal void FixedUpdate(float deltaTime) => OnFixedUpdate(deltaTime);
    }
}
