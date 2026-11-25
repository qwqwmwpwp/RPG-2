using RPG4.PlayerIntent;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    public sealed partial class Player
    {
        private bool networkSimulation, networkAuthority, networkLocalInput;
        private PlayerAuthoritySnapshot confirmedAuthority;
        private UpperBodyDemand confirmedDemand;
        private bool hasConfirmedNetworkState;

        /// <summary>Network host owns scheduling and device ownership; offline hosts never call this.</summary>
        public void ConfigureNetworkSimulation(bool authoritative, bool localInput)
        {
            if (!networkSimulation || networkAuthority != authoritative || networkLocalInput != localInput)
            {
                Consumables?.Cancel();
                ResetOwnerPresentation();
                hasConfirmedNetworkState = false;
            }
            networkSimulation = true;
            networkAuthority = authoritative;
            networkLocalInput = localInput;
            if (attributes == null) attributes = GetComponent<RPG4.CharacterAttributes.CharacterAttributes>();
            attributes.ConfigureAuthority(authoritative, Combat);
            InputSource = PlayerInputSource.External;
            if (intents == null) return;
            context.World.ConfigureNetworkMotor(true);
            playerInput.SetNetworkLocalInput(localInput);
            ClearIntent();
        }

        private PlayerInputFrame reconcileInput;
        private bool reconcileInputAllowed;
        private ulong reconcileInputVersion;
        public void BeginOwnerReconcile()
        {
            PlayerInputSnapshot input = ReadState.Input.Snapshot;
            reconcileInput = input.Frame;
            reconcileInputAllowed = input.InputAllowed;
            reconcileInputVersion = input.Version;
            presenter?.BeforeOwnerReconcile();
        }
        public void EndOwnerReconcile(bool discontinuity = false)
        {
            intents.Blackboard.SetInput(reconcileInput, reconcileInputAllowed, reconcileInputVersion);
            intents.Publish(simulationTick);
            intents.CommitPublication();
            presenter?.AfterOwnerReconcile(discontinuity);
        }

        /// <summary>Server admission only; it does not begin actions or consume inventory.</summary>
        public bool CanAcceptNetworkAction(byte kind, ulong weaponId)
        {
            var weapon = Equipment?.CurrentWeapon;
            if (!Combat.WeaponActions || !networkAuthority || !IsAlive || !isActiveAndEnabled || IsHurt || weapon == null
                || weapon.ItemId.Value != weaponId || !Equipment.CanExecuteAuthority) return false;
            return kind == 2 ? !weapon.IsReloading : kind == 1 && !weapon.IsReloading && weapon.HasUsableRound;
        }
        public bool WasNetworkActionAccepted(byte kind)
        {
            var weapon = Equipment?.CurrentWeapon;
            if (!Combat.WeaponActions || !networkAuthority || !IsAlive || !isActiveAndEnabled || IsHurt || weapon == null) return false;
            PlayerInputSnapshot input = ReadState.Input.Snapshot;
            return kind == 2 ? weapon.IsReloading : kind == 1 && (weapon.ShotStartedThisFrame
                || weapon.Definition.FireMode == WeaponFireMode.Automatic && input.Frame.FireHeld
                    && !weapon.IsReloading && weapon.HasUsableRound);
        }
        public void SimulateNetworkTick(PlayerInputFrame input, long tick, double now, float deltaTime)
            => SimulateNetworkTick(input, tick, now, deltaTime, null);
        public void SimulateNetworkTick(PlayerInputFrame input, long tick, double now, float deltaTime,
            IPlayerNetworkMovementResolver resolver)
        {
            if (!networkSimulation || !networkAuthority || !isActiveAndEnabled) return;
            SimulateTick(tick, now, deltaTime, resolver, FilterCombatInput(input));
        }
        public PlayerInputFrame BeginLocalInputCycle()
        {
            PlayerInputFrame input = intents.BeginLocalCycle(networkAuthority);
            if ((!Combat.WeaponActions && (input.FirePressed || input.ReloadPressed))
                || (!Combat.Consumables && input.QuickUsePressed))
                intents.Inputs.ReportInteractionFeedback(NetworkCombatPausedMessage, false);
            return FilterCombatInput(input);
        }
        public void CompleteLocalInputCycle() => intents.CompleteFrame();
        public PlayerMotorState CaptureMotorState() => context.World.CaptureMotor();
        public PlayerLocomotionMode LastMotorMode => context.World.LastMotorMode;
        public void RestoreMotorState(PlayerMotorState state) => context.World.RestoreMotor(state);
        public void RestoreMotorState(PlayerMotorState state, PlayerLocomotionMode lastMode)
            => context.World.RestoreMotor(state, lastMode);
        /// <summary>Reads the live business board, including changes made before this tick is published.</summary>
        public PlayerAuthoritySnapshot CaptureNetworkAuthorityState()
        {
            PlayerBlackboard board = intents.Blackboard;
            var weapon = context.Weapon;
            return new PlayerAuthoritySnapshot
            {
                Tick = simulationTick, Time = simulationTime, HurtUntil = hurtUntil,
                Alive = IsAlive, Health = attributes != null ? attributes.CurrentHealth : 0f,
                MaxHealth = attributes != null ? attributes.MaxHealth : 0f,
                WeaponAvailable = board.CanUseWeapon, Reloading = Combat.WeaponActions && board.Reloading,
                ReloadPhase = Combat.WeaponActions ? board.ReloadPhase : 0f,
                WeaponId = weapon != null ? weapon.ItemId.Value : 0,
                ShotSequence = Combat.WeaponActions && weapon != null ? weapon.LastShot.Sequence : 0,
                ShotTime = Combat.WeaponActions && weapon != null ? weapon.LastShot.Time : 0d,
                ShotDuration = weapon != null ? weapon.Definition.ShotDuration : 0.01f,
                FadeDuration = weapon != null ? weapon.Definition.FadeOutDuration : 0f,
                ReloadDuration = weapon != null ? weapon.Definition.ReloadDuration : 1f,
                Action = Combat.WeaponActions ? board.WeaponAction : default,
                Consumable = Combat.Consumables && Consumables != null ? Consumables.State : default
            };
        }
        /// <summary>Collision queries use the current world. This replays movement only, never weapon/health/events.</summary>
        public void PredictNetworkMovement(PlayerInputFrame input, long tick, double now, float deltaTime, bool replay = false)
        {
            if (!networkSimulation || networkAuthority || deltaTime <= 0f) return;
            simulationTick = tick;
            simulationTime = now;
            intents.BeginPreparedFrame(input, replay);
            try
            {
                input = ReadState.Input.Snapshot.Frame;
                Vector2 view = input.ViewAngles ?? new Vector2(ReadState.View.ViewYaw + input.Look.x, ReadState.View.ViewPitch + input.Look.y);
                intents.RecordView(Mathf.DeltaAngle(0f, view.x), Mathf.Clamp(view.y, -89f, 89f));
                ResolvePredictedRules(input, now);
                LocomotionPlan executedPlan = ReadState.Locomotion.Plan;
                ExecutedMovementRules = new MovementRuleResult(executedPlan, ReadState.Weapon.WeaponDemand,
                    ReadState.Weapon.IsAiming);
                if (executedPlan.CanMove) context.World.ApplyPosture(executedPlan.Crouched);
                context.World.Move(executedPlan, executedPlan.Strafe, deltaTime);
                intents.SampleMotorPosture();
                intents.Publish(tick);
            }
            finally { intents.CompleteSimulationFrame(); }
        }
        private MovementRuleResult ResolvePredictedRules(PlayerInputFrame input, double now)
        {
            PlayerInputSnapshot currentInput = ReadState.Input.Snapshot;
            PlayerBlackboard board = intents.Blackboard;
            MovementRuleContext rules = PlayerMovementDemand.Configuration(stateData);
            rules.Active = isActiveAndEnabled;
            rules.Alive = hasConfirmedNetworkState ? confirmedAuthority.Alive : IsAlive;
            rules.CanUseWeapon = hasConfirmedNetworkState ? confirmedAuthority.WeaponAvailable : board.CanUseWeapon;
            rules.IsHurt = hasConfirmedNetworkState ? now < confirmedAuthority.HurtUntil : board.IsHurt;
            rules.PhysicallyCrouched = ReadState.Locomotion.PhysicallyCrouched;
            rules.CanStand = ReadState.Locomotion.CanStand;
            rules.ViewYaw = ReadState.View.ViewYaw;
            // Replays always derive business facts from authority, never from an earlier replay's demand.
            WeaponActionSnapshot action = Combat.WeaponActions
                ? (hasConfirmedNetworkState ? confirmedAuthority.Action : board.WeaponAction) : default;
            rules.UsingConsumable = Combat.Consumables && (hasConfirmedNetworkState ? confirmedAuthority.Consumable.Active : board.Consumable.Active);
            rules.Reloading = Combat.WeaponActions && (hasConfirmedNetworkState ? confirmedAuthority.Reloading : board.Reloading);
            double elapsed = hasConfirmedNetworkState ? System.Math.Max(0d, now - confirmedAuthority.ShotTime) : 0d;
            rules.ShotActive = action.ShotActive && (!hasConfirmedNetworkState || elapsed < confirmedAuthority.ShotDuration);
            rules.FadingOut = (action.ShotActive || action.FadingOut) && !rules.ShotActive
                && (!hasConfirmedNetworkState || elapsed < confirmedAuthority.ShotDuration + confirmedAuthority.FadeDuration);
            rules.WaitingAutomatic = Combat.WeaponActions && hasConfirmedNetworkState && confirmedDemand.AttackCamera
                && !action.ShotActive && !action.FadingOut && input.FireHeld && now - confirmedAuthority.Time <= 1.5d;
            MovementRuleResult result = PlayerMovementRules.Evaluate(rules, input);
            // Match authority's separate upper-body -> movement-plan -> motor stages. The newly
            // forecast weapon demand cannot feed this cycle's movement planner directly.
            LocomotionPlan nextPlan = PlayerMovementRules.ResolvePlan(ReadState.Locomotion.CanMove
                && currentInput.InputAllowed, rules.PhysicallyCrouched, rules.CanStand,
                input.CrouchHeld, input.Move, rules.ViewYaw, ReadState.Weapon.WeaponDemand,
                stateData.Crouch.MoveSpeed, stateData.Exploration.MoveInputThreshold, stateData.Aim.MoveInputThreshold);
            bool canAct = rules.Active && rules.Alive && rules.CanUseWeapon && !rules.IsHurt;
            board.Plan = nextPlan;
            board.IsAlive = rules.Alive;
            board.IsHurt = rules.IsHurt;
            board.CanMove = result.Plan.CanMove;
            board.CanLook = rules.Active && rules.Alive;
            board.CanUseWeapon = canAct;
            board.CanInteract = rules.Active && rules.Alive;
            board.WeaponDemand = result.Demand;
            board.Reloading = result.Demand.Reloading;
            board.WeaponAction = new WeaponActionSnapshot(false, false, rules.ShotActive && canAct,
                rules.FadingOut && canAct, action.IsAutomatic && canAct && (rules.ShotActive || rules.FadingOut),
                rules.ShotActive && hasConfirmedNetworkState && confirmedAuthority.ShotDuration > 0f
                    ? Mathf.Clamp01((float)(elapsed / confirmedAuthority.ShotDuration)) : action.ShotPhase);
            board.IsAiming = result.Aiming;
            return result;
        }
        /// <summary>Installs business facts and re-evaluates permission without advancing weapons or motor.</summary>
        public void ApplyNetworkAuthorityState(in PlayerAuthoritySnapshot state)
        {
            if (!networkSimulation || networkAuthority || intents == null
                || hasConfirmedNetworkState && state.Tick < confirmedAuthority.Tick) return;
            InstallAuthorityFacts(state);
            PlayerBlackboard board = intents.Blackboard;
            PlayerInputFrame held = ReadState.Input.Snapshot.Frame;
            var input = new PlayerInputFrame(held.Move, crouchHeld: held.CrouchHeld,
                aimHeld: held.AimHeld, fireHeld: held.FireHeld);
            MovementRuleResult rules = ResolvePredictedRules(input, System.Math.Max(simulationTime, state.Time));
            confirmedDemand = rules.Demand;
            localActions.ObserveConfirmed(confirmedAuthority, Time.unscaledTimeAsDouble);
            // Publish at the existing local simulation clock: this update has not simulated another step.
            board.Publish(simulationTick, simulationTime);
        }
        private void InstallAuthorityFacts(in PlayerAuthoritySnapshot state)
        {
            confirmedAuthority = state;
            hasConfirmedNetworkState = true;
            PlayerBlackboard board = intents.Blackboard;
            board.Consumable = state.Consumable;
            board.CurrentHealth = state.Health;
            board.MaxHealth = state.MaxHealth;
            board.HurtUntil = state.HurtUntil;
            board.ReloadPhase = state.ReloadPhase;
            board.WeaponId = state.WeaponId;
            board.ShotSequence = state.ShotSequence;
            board.ShotTime = state.ShotTime;
            board.ShotDuration = state.ShotDuration;
            board.FadeDuration = state.FadeDuration;
            board.ReloadDuration = state.ReloadDuration;
        }

        public void ApplyNetworkPresentation(PlayerNetworkState state)
        {
            if (!networkSimulation || networkAuthority) return;
            InstallAuthorityFacts(state.Authority);
            confirmedDemand = state.Demand;
            localActions.ObserveConfirmed(confirmedAuthority, Time.unscaledTimeAsDouble);
            simulationTick = state.Tick;
            simulationTime = state.Time;
            PlayerBlackboard board = intents.Blackboard;
            board.OwnerActive = isActiveAndEnabled;
            board.IsAlive = state.Alive;
            board.IsHurt = state.HurtUntil > state.Time;
            board.ViewYaw = state.ViewYaw;
            board.ViewPitch = state.ViewPitch;
            board.BodyYaw = state.Motor.Yaw;
            board.PlanarVelocity = state.Motor.Velocity;
            board.WorldPosition = state.Motor.Position;
            board.PhysicallyCrouched = state.Motor.Crouched;
            board.CanStand = state.CanStand;
            board.CanMove = state.Alive;
            board.CanLook = state.Alive;
            board.CanUseWeapon = state.WeaponAvailable;
            board.CanInteract = state.Alive;
            board.IsAiming = state.Aiming;
            board.Reloading = state.Reloading;
            board.Plan = state.Plan;
            board.WeaponDemand = state.Demand;
            board.WeaponAction = state.Action;
            board.Publish(state.Tick, state.Time);
        }

        public void RefreshNetworkPublication()
        {
            intents?.Publish(simulationTick, reuseUnchanged: true);
            intents?.CommitPublication();
        }
    }
}
