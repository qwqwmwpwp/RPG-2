using System;
using System.Collections.Generic;

namespace RPG4.StateMachine
{
    public sealed class StateMachine
    {
        public State Root { get; private set; }
        public State ActiveLeaf { get; private set; }

        public void Initialize(State root)
        {
            if (root == null) throw new ArgumentNullException(nameof(root));
            if (root.Machine != this || root.Parent != null)
                throw new ArgumentException("The root must belong to this machine and have no parent.", nameof(root));

            Stop();
            Root = root;
        }

        public void Tick(float deltaTime)
        {
            if (Root == null) throw new InvalidOperationException("Initialize the state machine before ticking it.");
            if (ActiveLeaf == null)
            {
                Enter(Root);
                EnterInitialStates();
            }

            for (State state = Root; state != null; state = state.ActiveChild)
            {
                State target = state.Transition();
                if (target == null || target == ActiveLeaf) continue;
                ValidateState(target);
                ChangeState(target);
                break;
            }

            for (State state = Root; state != null; state = state.ActiveChild)
                state.Update(deltaTime);
        }

        /// <summary>仅更新已由 Tick 激活的状态链，不启动状态或检查转换。</summary>
        public void FixedTick(float deltaTime)
        {
            if (ActiveLeaf == null) return;

            for (State state = Root; state != null; state = state.ActiveChild)
                state.FixedUpdate(deltaTime);
        }

        public void Stop()
        {
            while (ActiveLeaf != null) ExitLeaf();
        }

        private void ChangeState(State target)
        {
            State ancestor = CommonAncestor(ActiveLeaf, target);
            while (ActiveLeaf != ancestor) ExitLeaf();

            var path = new Stack<State>();
            for (State state = target; state != ancestor; state = state.Parent)
                path.Push(state);
            while (path.Count > 0) Enter(path.Pop());
            EnterInitialStates();
        }

        private void EnterInitialStates()
        {
            State child;
            while ((child = ActiveLeaf.InitialState()) != null)
            {
                if (child.Parent != ActiveLeaf)
                    throw new InvalidOperationException("An initial state must be a direct child of its parent.");
                ValidateState(child);
                Enter(child);
            }
        }

        private void Enter(State state)
        {
            if (state.Parent != null) state.Parent.ActiveChild = state;
            ActiveLeaf = state;
            state.Enter();
        }

        private void ExitLeaf()
        {
            State state = ActiveLeaf;
            state.Exit();
            state.ActiveChild = null;
            if (state.Parent != null) state.Parent.ActiveChild = null;
            ActiveLeaf = state.Parent;
        }

        private void ValidateState(State state)
        {
            State root = state;
            for (State current = state; current != null; current = current.Parent)
            {
                if (current.Machine != this)
                    throw new InvalidOperationException("A state must belong to this state machine.");
                root = current;
            }
            if (root != Root)
                throw new InvalidOperationException("A state must belong to the initialized root.");
        }

        private static State CommonAncestor(State first, State second)
        {
            int firstDepth = Depth(first);
            int secondDepth = Depth(second);
            while (firstDepth > secondDepth)
            {
                first = first.Parent;
                firstDepth--;
            }
            while (secondDepth > firstDepth)
            {
                second = second.Parent;
                secondDepth--;
            }
            while (first != second)
            {
                first = first.Parent;
                second = second.Parent;
            }
            return first;
        }

        private static int Depth(State state)
        {
            int depth = 0;
            for (; state != null; state = state.Parent) depth++;
            return depth;
        }
    }
}
