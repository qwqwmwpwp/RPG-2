using RPG4.StateMachine;
using HierarchicalMachine = RPG4.StateMachine.StateMachine;
namespace RPG4.Frontend
{
    internal sealed class ClientSessionRoot : State
    {
        private readonly ClientSessionNode initial;
        internal ClientSessionRoot(HierarchicalMachine machine, ClientSessionController owner) : base(machine)
        {
            for (int i = 0; i < owner.Nodes.Length; i++) owner.Nodes[i] = new ClientSessionNode(machine, this, owner, (ClientSessionState)i);
            initial = owner.Nodes[(int)ClientSessionState.Bootstrapping];
        }
        protected override State GetInitialState() => initial;
    }
    /// <summary>Eight sibling states share policy through the controller and a credential-free context.</summary>
    internal sealed class ClientSessionNode : State
    {
        private readonly ClientSessionController owner;
        private readonly ClientSessionState state;
        internal ClientSessionNode(HierarchicalMachine machine, State parent, ClientSessionController owner, ClientSessionState state)
            : base(machine, parent) { this.owner = owner; this.state = state; }
        protected override void OnEnter() => owner.EnterState(state);
        protected override State GetTransition()
        {
            ClientSessionState next = owner.NextState(state);
            return next == state ? null : owner.Nodes[(int)next];
        }
        protected override void OnUpdate(float deltaTime) => owner.UpdateState(state);
    }
}
