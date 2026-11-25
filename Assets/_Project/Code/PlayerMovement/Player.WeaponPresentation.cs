using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using RPG4.PlayerIntent;
using UnityEngine;

namespace RPG4.PlayerMovement
{
    public sealed partial class Player
    {
        private readonly PlayerLocalActionAnticipation localActions = new PlayerLocalActionAnticipation();

        /// <summary>Render view uses the current character snapshot and confirmed/anticipated recoil only.</summary>
        public Vector2 GetLocalRenderViewAngles()
            => GetLocalRenderViewAngles(PublishedFrame);
        internal Vector2 GetLocalRenderViewAngles(PlayerPublishedFrame frame)
        {
            Vector2 view = frame != null ? new Vector2(frame.ViewYaw, frame.ViewPitch) : Vector2.zero;
            if (IsAlive && isActiveAndEnabled && playerInput != null && playerInput.CanPreviewLocalWeaponInput
                && (!networkSimulation || networkLocalInput))
            {
                if (Combat.WeaponActions && networkSimulation && !networkAuthority)
                    view += localActions.GetVisualRecoil(frame, Time.unscaledTimeAsDouble);
            }
            return new Vector2(Mathf.DeltaAngle(0f, view.x), Mathf.Clamp(view.y, -89f, 89f));
        }
        private bool CanAnticipateLocalActions => Combat.WeaponActions && networkSimulation && !networkAuthority && networkLocalInput
            && IsAlive && isActiveAndEnabled && !IsHurt && playerInput != null && playerInput.CanPreviewLocalWeaponInput
            && PublishedFrame != null && PublishedFrame.CanUseWeapon && !PublishedFrame.Consumable.Active;
        public void UpdateLocalActionInput(PlayerInputFrame input, double now)
        {
            if (!CanAnticipateLocalActions) { localActions.Cancel(); return; }
            localActions.UpdateInput(input);
        }
        public void AnticipateLocalActions(PlayerInputFrame input, ulong requestId, double now)
        {
            if (!CanAnticipateLocalActions || Equipment == null
                || !Equipment.TryGetPresentationWeapon(out ulong weaponId, out WeaponDefinitionFragment definition)) return;
            float interval = definition.EffectiveShotInterval;
            float recoilYaw = definition.RecoilYaw;
            float recoilPitch = definition.RecoilPitch;
            if (Equipment.TryGetPresentationRuntimeItem(out RuntimeItem item, out ItemManifestCatalog catalog)
                && item?.WeaponState != null && catalog != null)
            {
                BackpackTag ammoType = item.WeaponState.ChamberAmmo;
                if (ammoType.IsEmpty && item.WeaponState.Magazine?.MagazineState is MagazineItemState magazine
                    && magazine.RoundCount > 0)
                    ammoType = magazine.Rounds[0];
                if (ammoType.IsEmpty) TryGetReferenceAmmoType(catalog, definition.AmmoType, out ammoType);
                if (!ammoType.IsEmpty && WeaponParameterCalculator.TryCalculateAssembly(item, catalog, ammoType,
                    out WeaponCombatParameters parameters))
                {
                    interval = parameters.Interval;
                    recoilYaw *= parameters.Recoil;
                    recoilPitch *= parameters.Recoil;
                }
            }
            localActions.Begin(input, requestId, now, weaponId, definition, PublishedFrame.Reloading,
                Equipment.CanAnticipateShot, Equipment.PresentationShotSequence, interval, recoilYaw, recoilPitch);
        }

        private static bool TryGetReferenceAmmoType(ItemManifestCatalog catalog, BackpackTag weaponAmmoType,
            out BackpackTag ammoItemType)
        {
            foreach (ItemManifest manifest in catalog.Manifests)
            {
                AmmoDefinitionFragment ammo = manifest?.GetFragment<AmmoDefinitionFragment>();
                if (ammo != null && ammo.AmmoType.MatchesTagExact(weaponAmmoType))
                { ammoItemType = manifest.ItemType; return true; }
            }
            ammoItemType = default;
            return false;
        }
        public void ApplyLocalActionResult(ulong requestId, bool accepted, ulong weaponId, ulong shotSequence,
            double serverTime, uint ordinal = 0)
            => localActions.ApplyResult(requestId, accepted, weaponId, shotSequence, ordinal, Time.unscaledTimeAsDouble);
        public PlayerLocalWeaponPresentation GetLocalWeaponPresentation()
        {
            if (!CanAnticipateLocalActions || Equipment == null
                || !Equipment.TryGetPresentationWeapon(out ulong weaponId, out _))
            { localActions.Cancel(); return default; }
            return localActions.Get(PublishedFrame, stateData, Time.unscaledTimeAsDouble, weaponId,
                Equipment.CanAnticipateShot);
        }
        public bool TryDequeueAnticipatedShot(out PlayerAnticipatedShot shot)
        {
            if (!CanAnticipateLocalActions) { localActions.Cancel(); shot = default; return false; }
            return localActions.TryDequeue(Time.unscaledTimeAsDouble, out shot);
        }
        public void MarkAnticipatedShotPresented(PlayerAnticipatedShot shot) => localActions.MarkPresented(shot);
        public bool ShouldPresentConfirmedShot(ulong weaponId, ulong shotSequence)
            => localActions.ShouldPresentConfirmed(weaponId, shotSequence);
        public bool TryGetAnticipatedPresentationSequence(ulong weaponId, ulong serverShotSequence, out ulong cueSequence)
            => localActions.TryGetPresentationSequence(weaponId, serverShotSequence, out cueSequence);
        public bool ShouldDeferConfirmedShotPresentation(ulong weaponId, ulong serverShotSequence)
            => networkSimulation && !networkAuthority && networkLocalInput
                && localActions.ShouldDeferConfirmedShot(weaponId, serverShotSequence, Time.unscaledTimeAsDouble);

        public void ResetOwnerPresentation()
        {
            localActions.Reset();
            presenter?.ResetOwnerPresentation();
        }

        public void ApplyNetworkShot(ulong weaponId, ulong sequence, double time, bool automatic,
            bool damageApplied, bool obstructed)
        {
            if (!networkSimulation || networkAuthority) return;
            PlayerBlackboard board = intents.Blackboard;
            // Transport already applies the network-clock lifetime. Presentation ages this cue from
            // receipt on its own current clock, never by subtracting server time from predicted time.
            board.RecordEvent(PlayerEventKind.Shot, simulationTime, weaponId, sequence,
                success: damageApplied, obstructed: obstructed);
            board.Publish(simulationTick, simulationTime);
        }
    }
}
