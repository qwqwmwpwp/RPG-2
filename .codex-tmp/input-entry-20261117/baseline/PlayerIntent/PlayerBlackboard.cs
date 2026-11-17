using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

[assembly: InternalsVisibleTo("RPG4.PlayerMovement")]

namespace RPG4.PlayerIntent
{
    /// <summary>每角色唯一工作黑板；分类读取固定在周期起点，Publish暂存并由周期末统一提交。</summary>
    internal sealed class PlayerBlackboard : IPlayerReadState, IPlayerInputState, IPlayerLocomotionState,
        IPlayerViewState, IPlayerWeaponState, IPlayerPoseState, IPlayerInteractionState
    {
        private const int EventCapacity = 128;
        private readonly Queue<PlayerPresentationEvent> events = new Queue<PlayerPresentationEvent>(EventCapacity);
        private IReadOnlyList<PlayerPresentationEvent> publishedEvents = System.Array.Empty<PlayerPresentationEvent>();
        private bool eventsChanged;
        private ulong version, eventSequence;
        private PlayerPublishedFrame published;
        private PlayerBlackboard committedRead, cycleRead;
        private bool publicationPending;
        private long pendingTick;
        private double pendingTime;
        internal PlayerPublishedFrame Published => Volatile.Read(ref published);
        internal bool CycleActive => cycleRead != null;
        // The category interfaces are stable bindings; their targets remain frozen until EndCycle.
        private PlayerBlackboard Read => cycleRead ?? committedRead ?? this;
        internal ConsumableActionSnapshot ReadConsumable => Read.Consumable;
        internal void BeginCycle()
        {
            if (cycleRead != null) return;
            CommitPending();
            cycleRead = (PlayerBlackboard)(committedRead ?? this).MemberwiseClone();
            // InputBuffer is a separate producer and has already committed this cycle's input.
            // Overlay it once at the boundary, rather than adding another input-buffer delay.
            cycleRead.SetInput(Frame, InputAllowed, Version);
        }
        internal void EndCycle()
        {
            cycleRead = null;
            CommitPending();
        }
        internal void CommitPending()
        {
            if (!publicationPending || cycleRead != null) return;
            publicationPending = false;
            Commit(pendingTick, pendingTime);
        }
        internal bool OwnerActive { get; set; }
        internal double HurtUntil { get; set; }
        internal ConsumableActionSnapshot Consumable { get; set; }
        internal Vector3 PlanarVelocity { get; set; }
        internal Vector3 WorldPosition { get; set; }
        internal float BodyYaw { get; set; }
        internal ulong WeaponId { get; set; }
        internal ulong ShotSequence { get; set; }
        internal double ShotTime { get; set; }
        internal float ShotDuration { get; set; }
        internal float FadeDuration { get; set; }
        internal float ReloadDuration { get; set; }
        internal float CurrentHealth { get; set; }
        internal float MaxHealth { get; set; }
        internal bool InteractionVisible { get; set; }
        internal bool HasInteractionTarget { get; set; }
        internal string InteractionPrompt { get; set; }

        internal void RecordEvent(PlayerEventKind kind, double time, ulong weaponId = 0, ulong shotSequence = 0,
            float amount = 0f, string message = null, bool success = false,
            Vector3 direction = default, bool obstructed = false)
        {
            if (events.Count == EventCapacity) events.Dequeue();
            events.Enqueue(new PlayerPresentationEvent(++eventSequence, kind, time, weaponId, shotSequence,
                amount, message, success, direction, obstructed));
            eventsChanged = true;
        }

        internal void Publish(long tick, double time)
        {
            pendingTick = tick; pendingTime = time;
            publicationPending = true;
        }
        private void Commit(long tick, double time)
        {
            if (eventsChanged)
            {
                publishedEvents = System.Array.AsReadOnly(events.ToArray());
                eventsChanged = false;
            }
            Volatile.Write(ref published, new PlayerPublishedFrame(++version, tick, time,
                this, publishedEvents, eventSequence));
            committedRead = (PlayerBlackboard)MemberwiseClone();
            committedRead.cycleRead = committedRead.committedRead = null;
        }

        IPlayerInputState IPlayerReadState.Input => Read;
        IPlayerLocomotionState IPlayerReadState.Locomotion => Read;
        IPlayerViewState IPlayerReadState.View => Read;
        IPlayerWeaponState IPlayerReadState.Weapon => Read;
        IPlayerPoseState IPlayerReadState.Pose => Read;
        IPlayerInteractionState IPlayerReadState.Interaction => Read;

        public bool IsAlive { get; internal set; }
        public bool IsHurt { get; internal set; }
        public bool InputAllowed { get; internal set; }
        public bool CanMove { get; internal set; }
        public bool CanLook { get; internal set; }
        public bool CanUseWeapon { get; internal set; }
        public bool CanInteract { get; internal set; }
        public PlayerInputFrame Frame { get; private set; }
        public ulong Version { get; private set; }
        internal void SetInput(PlayerInputFrame frame, bool allowed, ulong inputVersion)
        { Frame = frame; InputAllowed = allowed; Version = inputVersion; }
        public Vector2 Move => Frame.Move;
        public Vector2 Look => Frame.Look;
        public float Zoom => Frame.Zoom;
        public bool CrouchHeld => Frame.CrouchHeld;
        public bool AimHeld => Frame.AimHeld;
        public bool FirePressed => Frame.FirePressed;
        public bool FireHeld => Frame.FireHeld;
        public bool ReloadPressed => Frame.ReloadPressed;
        public bool InteractPressed => Frame.InteractPressed;
        public float ViewYaw { get; internal set; }
        public float ViewPitch { get; internal set; }
        public bool PhysicallyCrouched { get; internal set; }
        public bool CanStand { get; internal set; }
        public UpperBodyDemand WeaponDemand { get; internal set; }
        public LocomotionPlan Plan { get; internal set; }
        public WeaponActionSnapshot WeaponAction { get; internal set; }
        public float ReloadPhase { get; internal set; }
        public bool IsAiming { get; internal set; }
        public bool Reloading { get; internal set; }
        public bool WeaponCancellationRequested { get; internal set; }
        public MonoBehaviour InteractionTarget { get; internal set; }
    }

    public readonly struct ConsumableActionSnapshot
    {
        public readonly ulong ActionId, ItemId;
        public readonly double StartTime, EndTime;
        public readonly bool Active;
        public ConsumableActionSnapshot(ulong actionId, ulong itemId, double startTime, double endTime, bool active)
        { ActionId = actionId; ItemId = itemId; StartTime = startTime; EndTime = endTime; Active = active; }
    }

    public readonly struct UpperBodyDemand
    {
        public readonly bool Strafe, AimCamera, ShotPose, Reloading, AttackCamera;
        public readonly float MoveSpeed, AnimationSpeed;
        public UpperBodyDemand(bool strafe, bool aimCamera, bool shotPose, bool reloading,
            float moveSpeed, float animationSpeed, bool attackCamera = false)
        {
            Strafe = strafe; AimCamera = aimCamera; ShotPose = shotPose; Reloading = reloading;
            AttackCamera = attackCamera;
            MoveSpeed = moveSpeed; AnimationSpeed = animationSpeed;
        }
    }

    public readonly struct LocomotionPlan
    {
        public readonly bool CanMove, Crouched, Strafe;
        public readonly Vector2 Move;
        public readonly float ViewYaw, MoveSpeed, AnimationSpeed;
        public LocomotionPlan(bool canMove, bool crouched, bool strafe, Vector2 move,
            float viewYaw, float moveSpeed, float animationSpeed)
        {
            CanMove = canMove; Crouched = crouched; Strafe = strafe; Move = move;
            ViewYaw = viewYaw; MoveSpeed = moveSpeed; AnimationSpeed = animationSpeed;
        }
    }

    public readonly struct WeaponActionSnapshot
    {
        public readonly bool ShotStarted, FadeStarted, ShotActive, FadingOut, IsAutomatic;
        public readonly float ShotPhase;
        public WeaponActionSnapshot(bool shotStarted, bool fadeStarted, bool shotActive,
            bool fadingOut, bool isAutomatic, float shotPhase)
        {
            ShotStarted = shotStarted; FadeStarted = fadeStarted; ShotActive = shotActive;
            FadingOut = fadingOut; IsAutomatic = isAutomatic; ShotPhase = shotPhase;
        }
    }
}
