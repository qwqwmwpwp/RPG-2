using System.Collections.Generic;
using UnityEngine;

namespace RPG4.PlayerIntent
{
    public enum PlayerEventKind { Shot, Hurt, Died, Revived, WeaponCancelled, InteractionFeedback }

    /// <summary>黑板保留的有序表现事件；不包含场景对象或可变武器引用。</summary>
    public readonly struct PlayerPresentationEvent
    {
        public readonly ulong Sequence, WeaponId, ShotSequence;
        public readonly PlayerEventKind Kind;
        public readonly double Time;
        public readonly float Amount;
        public readonly string Message;
        public readonly bool Success;
        internal PlayerPresentationEvent(ulong sequence, PlayerEventKind kind, double time,
            ulong weaponId, ulong shotSequence, float amount, string message, bool success)
        {
            Sequence = sequence; Kind = kind; Time = time;
            WeaponId = weaponId; ShotSequence = shotSequence;
            Amount = amount; Message = message; Success = success;
        }
    }

    /// <summary>核心黑板发布的不可变读取版本；桥接层一次捕获，子表现不接触实时黑板。</summary>
    public sealed class PlayerPublishedFrame
    {
        public readonly ulong Version;
        public readonly long Tick;
        public readonly double Time, HurtUntil;
        public readonly bool Active, Alive, CanMove, CanLook, CanUseWeapon, PhysicallyCrouched;
        public readonly float ViewYaw, ViewPitch, Zoom, BodyYaw;
        public readonly Vector3 PlanarVelocity;
        public readonly LocomotionPlan Plan;
        public readonly UpperBodyDemand WeaponDemand;
        public readonly WeaponActionSnapshot WeaponAction;
        public readonly ulong WeaponId, ShotSequence;
        public readonly double ShotTime;
        public readonly float ShotDuration, FadeDuration, ReloadDuration, ReloadPhase;
        public readonly bool IsAiming, Reloading;
        public readonly float CurrentHealth, MaxHealth;
        public readonly bool InteractionVisible, HasInteractionTarget;
        public readonly string InteractionPrompt;
        public readonly ulong LastEventSequence;
        private readonly IReadOnlyList<PlayerPresentationEvent> events;
        public IReadOnlyList<PlayerPresentationEvent> Events => events;

        internal PlayerPublishedFrame(ulong version, long tick, double time, PlayerBlackboard source,
            IReadOnlyList<PlayerPresentationEvent> events, ulong lastEventSequence)
        {
            Version = version; Tick = tick; Time = time; HurtUntil = source.HurtUntil;
            Active = source.OwnerActive; Alive = source.IsAlive;
            CanMove = source.CanMove; CanLook = source.CanLook; CanUseWeapon = source.CanUseWeapon;
            PhysicallyCrouched = source.PhysicallyCrouched;
            ViewYaw = source.ViewYaw; ViewPitch = source.ViewPitch; Zoom = source.Zoom;
            BodyYaw = source.BodyYaw; PlanarVelocity = source.PlanarVelocity;
            Plan = source.Plan; WeaponDemand = source.WeaponDemand; WeaponAction = source.WeaponAction;
            WeaponId = source.WeaponId; ShotSequence = source.ShotSequence; ShotTime = source.ShotTime;
            ShotDuration = source.ShotDuration; FadeDuration = source.FadeDuration;
            ReloadDuration = source.ReloadDuration; ReloadPhase = source.ReloadPhase;
            IsAiming = source.IsAiming; Reloading = source.Reloading;
            CurrentHealth = source.CurrentHealth; MaxHealth = source.MaxHealth;
            InteractionVisible = source.CanInteract && source.InteractionVisible;
            HasInteractionTarget = source.HasInteractionTarget;
            InteractionPrompt = source.InteractionPrompt;
            this.events = events; LastEventSequence = lastEventSequence;
        }
    }
}
