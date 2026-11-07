using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace RPG4.AI
{
    /// <summary>
    /// A small server-side target descriptor.  Player and NPC adapters can
    /// update IsAlive and Faction without coupling the AI to player classes.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ServerAiTarget : MonoBehaviour
    {
        [SerializeField] private int faction;
        [SerializeField] private Transform aimPoint;
        [SerializeField] private bool alive = true;

        public int Faction => faction;
        public Transform AimPoint => aimPoint;
        public bool IsAlive { get => alive; set => alive = value; }
        public Vector3 AimPosition => aimPoint != null ? aimPoint.position : transform.position + Vector3.up;
        public bool IsTargetable => isActiveAndEnabled && alive;
    }

    /// <summary>
    /// Independent server AI command output.  No player input, weapon item, or
    /// network connection type is required to consume this DTO.
    /// </summary>
    public readonly struct ServerAiIntent
    {
        public readonly long DecisionSequence;
        public readonly ServerAiTarget Target;
        public readonly ServerAiNavigationIntent Navigation;
        public readonly Vector3 LookDirection;
        public readonly bool HasTarget;
        public readonly bool HasLineOfSight;
        public readonly bool WantsMove;
        public readonly bool WantsAim;
        public readonly bool WantsFire;
        public readonly bool WantsReload;
        public readonly bool IsReloading;
        public readonly float TargetDistance;

        public ServerAiIntent(long decisionSequence, ServerAiTarget target,
            ServerAiNavigationIntent navigation, Vector3 lookDirection, bool hasLineOfSight,
            bool wantsMove, bool wantsAim, bool wantsFire, bool wantsReload, bool isReloading,
            float targetDistance)
        {
            DecisionSequence = decisionSequence;
            Target = target;
            Navigation = navigation;
            LookDirection = lookDirection;
            HasTarget = target != null;
            HasLineOfSight = hasLineOfSight;
            WantsMove = wantsMove;
            WantsAim = wantsAim;
            WantsFire = wantsFire;
            WantsReload = wantsReload;
            IsReloading = isReloading;
            TargetDistance = targetDistance;
        }

        public static ServerAiIntent Empty => new ServerAiIntent(0, null,
            ServerAiNavigationIntent.Empty, Vector3.forward, false, false, false,
            false, false, false, float.PositiveInfinity);
    }

    /// <summary>
    /// Minimal server-authoritative target, chase, visibility, and weapon-intent brain.
    /// It deliberately publishes intent rather than driving a player motor or weapon.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ServerAiBrain : MonoBehaviour
    {
        [Header("Identity and sensing")]
        [SerializeField] private int faction = -1;
        [SerializeField] private Transform eyePoint;
        [SerializeField] private Transform aimPoint;
        [SerializeField, Min(0.1f)] private float targetRadius = 40f;
        [SerializeField, Min(0.1f)] private float attackRange = 25f;
        [SerializeField, Min(0.1f)] private float preferredDistance = 12f;
        [SerializeField, Min(0f)] private float distanceTolerance = 1.5f;
        [SerializeField] private LayerMask lineOfSightMask = ~0;
        [SerializeField] private QueryTriggerInteraction lineOfSightTriggers = QueryTriggerInteraction.Ignore;

        [Header("Update staggering")]
        [SerializeField, Min(0.02f)] private float decisionInterval = 0.1f;
        [SerializeField, Min(0f)] private float phaseOffset;
        [SerializeField, Min(1)] private int phaseBuckets = 8;

        [Header("Independent weapon state")]
        [SerializeField] private bool weaponAvailable = true;
        [SerializeField, Min(0)] private int magazineCapacity = 30;
        [SerializeField, Min(0)] private int ammoInMagazine = 30;
        [SerializeField, Min(0)] private int reserveAmmo = 90;
        [SerializeField, Min(0.01f)] private float roundsPerSecond = 8f;
        [SerializeField, Min(0f)] private float reloadDuration = 1.6f;

        [Header("Navigation")]
        [SerializeField] private ServerAiNavigation navigation;

        private readonly List<ServerAiTarget> candidates = new List<ServerAiTarget>();
        private ServerAiTarget target;
        private ServerAiIntent currentIntent = ServerAiIntent.Empty;
        private double nextDecisionAt;
        private double reloadUntil;
        private double nextShotAt;
        private long decisionSequence;
        private bool reloadStartedThisDecision;

        public ServerAiTarget CurrentTarget => target;
        public ServerAiIntent CurrentIntent => currentIntent;
        public ServerAiNavigation Navigation => navigation;
        public int AmmoInMagazine => ammoInMagazine;
        public int ReserveAmmo => reserveAmmo;
        public bool IsReloading => reloadUntil > Time.realtimeSinceStartupAsDouble;
        public event Action<ServerAiIntent> IntentProduced;

        private void Awake()
        {
            if (navigation == null) navigation = GetComponent<ServerAiNavigation>();
            magazineCapacity = Mathf.Max(0, magazineCapacity);
            ammoInMagazine = Mathf.Clamp(ammoInMagazine, 0, magazineCapacity);
            reserveAmmo = Mathf.Max(0, reserveAmmo);
            roundsPerSecond = Mathf.Max(0.01f, roundsPerSecond);
            reloadDuration = Mathf.Max(0f, reloadDuration);
        }

        private void OnEnable()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            float interval = Mathf.Max(0.02f, decisionInterval);
            float stagger = Mathf.Repeat(phaseOffset, interval);
            if (phaseOffset <= 0f && phaseBuckets > 1)
            {
                int bucket = Mathf.Abs(GetInstanceID()) % Mathf.Max(1, phaseBuckets);
                stagger = interval * bucket / phaseBuckets;
            }
            nextDecisionAt = now + stagger;
        }

        private void OnDisable()
        {
            target = null;
            currentIntent = ServerAiIntent.Empty;
            if (navigation != null && NetworkServer.active) navigation.ClearDestination();
        }

        private void Update()
        {
            // This guard intentionally excludes offline and client-only worlds.
            if (!NetworkServer.active) return;

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < nextDecisionAt) return;
            Evaluate(now);

            float interval = Mathf.Max(0.02f, decisionInterval);
            nextDecisionAt = now + interval;
        }

        /// <summary>Updates the independent weapon counters for a server adapter.</summary>
        public void ConfigureWeapon(int magazine, int reserve, int capacity,
            float fireRate, float reloadSeconds, bool available = true)
        {
            magazineCapacity = Mathf.Max(0, capacity);
            ammoInMagazine = Mathf.Clamp(magazine, 0, magazineCapacity);
            reserveAmmo = Mathf.Max(0, reserve);
            roundsPerSecond = Mathf.Max(0.01f, fireRate);
            reloadDuration = Mathf.Max(0f, reloadSeconds);
            weaponAvailable = available;
        }

        public void SetWeaponAvailable(bool available) => weaponAvailable = available;

        /// <summary>For adapters that own health state, this clears a dead AI target.</summary>
        public void ClearTarget()
        {
            target = null;
            if (navigation != null && NetworkServer.active) navigation.ClearDestination();
        }

        private void Evaluate(double now)
        {
            reloadStartedThisDecision = false;
            UpdateReload(now);
            SelectTarget();

            Vector3 origin = eyePoint != null ? eyePoint.position
                : aimPoint != null ? aimPoint.position : transform.position + Vector3.up * 1.5f;
            bool hasTarget = target != null && target.IsTargetable;
            Vector3 targetPosition = hasTarget ? target.AimPosition : origin + transform.forward;
            Vector3 toTarget = targetPosition - origin;
            float distance = hasTarget ? toTarget.magnitude : float.PositiveInfinity;
            Vector3 lookDirection = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : transform.forward;
            bool visible = hasTarget && HasLineOfSight(origin, target, targetPosition, distance);

            bool wantsMove = false;
            if (hasTarget && navigation != null)
            {
                if (distance > preferredDistance + distanceTolerance)
                {
                    wantsMove = navigation.SetDestination(target.transform.position);
                }
                else if (distance < Mathf.Max(0f, preferredDistance - distanceTolerance))
                {
                    Vector3 retreat = transform.position - (target.transform.position - transform.position).normalized
                        * (preferredDistance - distance);
                    wantsMove = navigation.SetDestination(retreat);
                }
                else
                {
                    navigation.ClearDestination();
                }
            }
            else if (navigation != null)
            {
                navigation.ClearDestination();
            }

            bool wantsReload = reloadStartedThisDecision;
            bool reloading = reloadUntil > now;
            bool wantsAim = hasTarget && visible && distance <= attackRange;
            bool wantsFire = false;
            if (!reloading && weaponAvailable && ammoInMagazine <= 0 && reserveAmmo > 0)
            {
                StartReload(now);
                wantsReload = true;
                reloading = true;
            }
            else if (!reloading && weaponAvailable && wantsAim && ammoInMagazine > 0 && now >= nextShotAt)
            {
                ammoInMagazine--;
                nextShotAt = now + 1d / roundsPerSecond;
                wantsFire = true;
            }

            ServerAiNavigationIntent navigationIntent = navigation != null
                ? navigation.TickServer(0f) : ServerAiNavigationIntent.Empty;
            currentIntent = new ServerAiIntent(++decisionSequence, target, navigationIntent,
                lookDirection, visible, wantsMove, wantsAim, wantsFire, wantsReload,
                reloading, distance);
            IntentProduced?.Invoke(currentIntent);
        }

        private void UpdateReload(double now)
        {
            if (reloadUntil <= 0d || now < reloadUntil) return;
            reloadUntil = 0d;
            int needed = Mathf.Max(0, magazineCapacity - ammoInMagazine);
            int loaded = Mathf.Min(needed, reserveAmmo);
            ammoInMagazine += loaded;
            reserveAmmo -= loaded;
        }

        private void StartReload(double now)
        {
            if (reloadUntil > now || reserveAmmo <= 0 || magazineCapacity <= ammoInMagazine) return;
            reloadUntil = now + reloadDuration;
            reloadStartedThisDecision = true;
        }

        private void SelectTarget()
        {
            if (IsUsableTarget(target) && (target.transform.position - transform.position).sqrMagnitude
                <= targetRadius * targetRadius) return;

            target = null;
            candidates.Clear();
            ServerAiTarget[] found = FindObjectsOfType<ServerAiTarget>();
            float bestDistance = float.PositiveInfinity;
            foreach (ServerAiTarget candidate in found)
            {
                if (!IsUsableTarget(candidate)) continue;
                Vector3 offset = candidate.transform.position - transform.position;
                float squared = offset.sqrMagnitude;
                if (squared > targetRadius * targetRadius || squared >= bestDistance) continue;
                bestDistance = squared;
                target = candidate;
            }
        }

        private bool IsUsableTarget(ServerAiTarget candidate)
        {
            return candidate != null && candidate.gameObject != gameObject && candidate.IsTargetable
                && (faction < 0 || candidate.Faction != faction);
        }

        private bool HasLineOfSight(Vector3 origin, ServerAiTarget candidate,
            Vector3 targetPosition, float distance)
        {
            if (distance <= 0.0001f) return true;
            Vector3 direction = (targetPosition - origin) / distance;
            if (!Physics.Raycast(origin, direction, out RaycastHit hit, distance,
                lineOfSightMask, lineOfSightTriggers)) return true;
            ServerAiTarget hitTarget = hit.collider.GetComponentInParent<ServerAiTarget>();
            return hitTarget == candidate;
        }
    }
}
