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
        private bool stateChanged;
        private bool ownerActive, isAlive, isHurt, canMove, canLook, canUseWeapon, canInteract;
        private bool interactionVisible, hasInteractionTarget, physicallyCrouched, canStand;
        private bool isAiming, reloading, weaponCancellationRequested;
        private double hurtUntil, shotTime;
        private ConsumableActionSnapshot consumable;
        private Vector3 planarVelocity, worldPosition;
        private float bodyYaw, shotDuration, fadeDuration, reloadDuration, currentHealth, maxHealth, viewYaw, viewPitch, reloadPhase;
        private ulong weaponId, shotSequence;
        private string interactionPrompt;
        private PlayerInputSnapshot snapshot;
        private UpperBodyDemand weaponDemand;
        private LocomotionPlan plan;
        private WeaponActionSnapshot weaponAction;
        private MonoBehaviour interactionTarget;

        // Logic writes are normally coalesced by EndCycle. A later write invalidates same-tick reuse.
        private void SetWorking<T>(ref T field, T value)
        {
            field = value;
            stateChanged = true;
        }

        // External facts are sampled again before sending, even if the simulation already published.
        private void SetObserved<T>(ref T field, T value) where T : System.IEquatable<T>
        {
            if (field.Equals(value)) return;
            SetWorking(ref field, value);
        }

        internal bool OwnerActive { get => ownerActive; set => SetObserved(ref ownerActive, value); }
        internal double HurtUntil { get => hurtUntil; set => SetObserved(ref hurtUntil, value); }
        internal ConsumableActionSnapshot Consumable { get => consumable; set => SetObserved(ref consumable, value); }
        internal Vector3 PlanarVelocity { get => planarVelocity; set => SetObserved(ref planarVelocity, value); }
        internal Vector3 WorldPosition { get => worldPosition; set => SetObserved(ref worldPosition, value); }
        internal float BodyYaw { get => bodyYaw; set => SetObserved(ref bodyYaw, value); }
        internal ulong WeaponId { get => weaponId; set => SetObserved(ref weaponId, value); }
        internal ulong ShotSequence { get => shotSequence; set => SetObserved(ref shotSequence, value); }
        internal double ShotTime { get => shotTime; set => SetObserved(ref shotTime, value); }
        internal float ShotDuration { get => shotDuration; set => SetObserved(ref shotDuration, value); }
        internal float FadeDuration { get => fadeDuration; set => SetObserved(ref fadeDuration, value); }
        internal float ReloadDuration { get => reloadDuration; set => SetObserved(ref reloadDuration, value); }
        internal float CurrentHealth { get => currentHealth; set => SetObserved(ref currentHealth, value); }
        internal float MaxHealth { get => maxHealth; set => SetObserved(ref maxHealth, value); }
        internal bool InteractionVisible { get => interactionVisible; set => SetWorking(ref interactionVisible, value); }
        internal bool HasInteractionTarget { get => hasInteractionTarget; set => SetWorking(ref hasInteractionTarget, value); }
        internal string InteractionPrompt { get => interactionPrompt; set => SetWorking(ref interactionPrompt, value); }

        internal void RecordEvent(PlayerEventKind kind, double time, ulong weaponId = 0, ulong shotSequence = 0,
            float amount = 0f, string message = null, bool success = false,
            Vector3 direction = default, bool obstructed = false)
        {
            if (events.Count == EventCapacity) events.Dequeue();
            events.Enqueue(new PlayerPresentationEvent(++eventSequence, kind, time, weaponId, shotSequence,
                amount, message, success, direction, obstructed));
            eventsChanged = true;
        }

        internal void Publish(long tick, double time, bool reuseUnchanged = false)
        {
            if (reuseUnchanged && !publicationPending && !stateChanged && !eventsChanged
                && published != null && published.Tick == tick && published.Time == time) return;
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
            stateChanged = false;
            committedRead = (PlayerBlackboard)MemberwiseClone();
            committedRead.cycleRead = committedRead.committedRead = null;
        }

        IPlayerInputState IPlayerReadState.Input => Read;
        IPlayerLocomotionState IPlayerReadState.Locomotion => Read;
        IPlayerViewState IPlayerReadState.View => Read;
        IPlayerWeaponState IPlayerReadState.Weapon => Read;
        IPlayerPoseState IPlayerReadState.Pose => Read;
        IPlayerInteractionState IPlayerReadState.Interaction => Read;

        public bool IsAlive { get => isAlive; internal set => SetWorking(ref isAlive, value); }
        public bool IsHurt { get => isHurt; internal set => SetWorking(ref isHurt, value); }
        public PlayerInputSnapshot Snapshot { get => snapshot; private set => SetWorking(ref snapshot, value); }
        public bool InputAllowed => Snapshot.InputAllowed;
        public bool CanMove { get => canMove; internal set => SetWorking(ref canMove, value); }
        public bool CanLook { get => canLook; internal set => SetWorking(ref canLook, value); }
        public bool CanUseWeapon { get => canUseWeapon; internal set => SetWorking(ref canUseWeapon, value); }
        public bool CanInteract { get => canInteract; internal set => SetWorking(ref canInteract, value); }
        public PlayerInputFrame Frame => Snapshot.Frame;
        public ulong Version => Snapshot.Version;
        internal void SetInput(PlayerInputFrame frame, bool allowed, ulong inputVersion)
        { Snapshot = new PlayerInputSnapshot(frame, inputVersion, allowed); }
        public Vector2 Move => Frame.Move;
        public Vector2 Look => Frame.Look;
        public float Zoom => Frame.Zoom;
        public bool CrouchHeld => Frame.CrouchHeld;
        public bool AimHeld => Frame.AimHeld;
        public bool FirePressed => Frame.FirePressed;
        public bool FireHeld => Frame.FireHeld;
        public bool ReloadPressed => Frame.ReloadPressed;
        public bool InteractPressed => Frame.InteractPressed;
        public float ViewYaw { get => viewYaw; internal set => SetWorking(ref viewYaw, value); }
        public float ViewPitch { get => viewPitch; internal set => SetWorking(ref viewPitch, value); }
        public bool PhysicallyCrouched { get => physicallyCrouched; internal set => SetWorking(ref physicallyCrouched, value); }
        public bool CanStand { get => canStand; internal set => SetWorking(ref canStand, value); }
        public UpperBodyDemand WeaponDemand { get => weaponDemand; internal set => SetWorking(ref weaponDemand, value); }
        public LocomotionPlan Plan { get => plan; internal set => SetWorking(ref plan, value); }
        public WeaponActionSnapshot WeaponAction { get => weaponAction; internal set => SetWorking(ref weaponAction, value); }
        public float ReloadPhase { get => reloadPhase; internal set => SetWorking(ref reloadPhase, value); }
        public bool IsAiming { get => isAiming; internal set => SetWorking(ref isAiming, value); }
        public bool Reloading { get => reloading; internal set => SetWorking(ref reloading, value); }
        public bool WeaponCancellationRequested { get => weaponCancellationRequested; internal set => SetWorking(ref weaponCancellationRequested, value); }
        public MonoBehaviour InteractionTarget { get => interactionTarget; internal set => SetWorking(ref interactionTarget, value); }
    }

    public readonly struct ConsumableActionSnapshot : System.IEquatable<ConsumableActionSnapshot>
    {
        public readonly ulong ActionId, ItemId;
        public readonly double StartTime, EndTime;
        public readonly bool Active;
        public bool Equals(ConsumableActionSnapshot other) => ActionId == other.ActionId && ItemId == other.ItemId
            && StartTime == other.StartTime && EndTime == other.EndTime && Active == other.Active;

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
