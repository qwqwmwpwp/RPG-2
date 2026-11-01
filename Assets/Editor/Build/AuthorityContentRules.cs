using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG4.Editor.Build
{
    /// <summary>Auditable allowlists. New authority components, fragment types or reference kinds require a rule update.</summary>
    internal static class AuthorityContentRules
    {
        internal static readonly HashSet<string> Components = new HashSet<string>(StringComparer.Ordinal)
        {
            "Mirror.NetworkIdentity", "Mirror.NetworkStartPosition", "Mirror.DistanceInterestManagement",
            "Mirror.Transports.Encryption.EncryptionTransport", "kcp2k.KcpTransport",
            "RPG4.Networking.Transports.ConnectionAdmissionTransport", "RPG4.Networking.AccountAuthenticator",
            "RPG4.Networking.AuthoritativeNetworkManager", "RPG4.Networking.GameplaySceneBindings",
            "RPG4.Networking.OnlineSpawnConfiguration", "RPG4.Networking.OnlinePlayerIdentity",
            "RPG4.Networking.NetworkPlayerController", "RPG4.Networking.NetworkCharacterHealth",
            "RPG4.CharacterAttributes.CharacterAttributes", "RPG4.Combat.TrainingDummy", "RPG4.Combat.ContactDamage",
            "RPG4.PlayerMovement.Player", "RPG4.PlayerMovement.PlayerEquipment", "RPG4.Input.PlayerInput",
            "RPG4.Backpack.Runtime.BackpackInventory", "RPG4.Backpack.Runtime.EquipmentInventory",
            "RPG4.Backpack.Runtime.CharacterBackpack", "RPG4.Backpack.Runtime.ChestInventory",
            "RPG4.Backpack.Network.BackpackNetworkPlayer", "RPG4.Backpack.Network.BackpackNetworkInventory",
            "RPG4.Backpack.Network.BackpackNetworkEquipment"
        };
        internal static readonly HashSet<string> ManagedTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "GridFragment", "ImageFragment", "TextFragment", "LabeledNumberFragment", "StackableFragment",
            "ConsumableFragment", "EquipmentFragment", "ConsumeModifier", "EquipModifier", "WeaponDefinitionFragment",
            "AmmoDefinitionFragment", "MagazineDefinitionFragment", "AttachmentDefinitionFragment"
        };
        internal static readonly string[] SourceRoots = {
            "Assets/Scripts/CharacterAttributes", "Assets/Scripts/Combat", "Assets/Scripts/PlayerIntent",
            "Assets/Scripts/PlayerMovement", "Assets/Scripts/StateMachine", "Assets/Scripts/Networking",
            "Assets/backpack/Scripts/Core", "Assets/backpack/Scripts/Equipment", "Assets/backpack/Scripts/Inventory",
            "Assets/backpack/Scripts/Items", "Assets/backpack/Scripts/Runtime", "Assets/backpack/Scripts/Network", "Assets/backpack/Scripts/World",
            "Assets/Mirror/Core", "Assets/Mirror/Components/InterestManagement/Distance", "Assets/Mirror/Transports/KCP", "Assets/Mirror/Transports/Encryption",
            "Assets/Mirror/Editor/Weaver"
        };
        // These owning assembly definitions sit outside the selected source directories.
        internal static readonly string[] AdditionalAssemblyInputs = {
            "Assets/Scripts/Input/RPG4.Input.asmdef", "Assets/Mirror/Components/Mirror.Components.asmdef",
            "Assets/Mirror/Transports/Mirror.Transports.asmdef"
        };
        internal static readonly string[] SharedBinaries = {
            "Assets/Mirror/Transports/Encryption/Plugins/BouncyCastle/Mirror.BouncyCastle.Cryptography.dll",
            "Assets/Mirror/Plugins/Mono.Cecil/Mono.CecilX.dll", "Assets/Mirror/Plugins/Mono.Cecil/Mono.CecilX.Rocks.dll",
            "Assets/Mirror/Plugins/Mono.Cecil/Mono.CecilX.Pdb.dll", "Assets/Mirror/Plugins/Mono.Cecil/Mono.CecilX.Mdb.dll"
        };
        internal static readonly HashSet<string> ExcludedSourceNames = new HashSet<string>(StringComparer.Ordinal)
        { "EntryContentManifest.cs", "ClientPresentationRoot.cs", "PlayerCameraController.cs", "PlayerViewController.cs", "PlayerPresenter.cs", "PlayerAimPose.cs", "PlayerWeaponRig.cs", "PlayerBodyAimConstraint.cs", "PlayerShotPoseConstraint.cs",
            "WeaponView.cs", "WeaponAttachmentView.cs", "PlayerLocomotionPresentation.cs", "PlayerWeaponPresentation.cs", "PlayerPresentationTargets.cs" };

        internal static bool ExcludeSourcePath(string path) => path.Contains("/Rigging/")
            || path.Contains("/Editor/") && !path.StartsWith("Assets/Mirror/Editor/Weaver/", StringComparison.Ordinal);

        internal static bool IsPresentation(Component component)
        {
            Type type = component.GetType();
            string name = type.FullName ?? string.Empty;
            if (component is UnityEngine.Animations.ParentConstraint)
            {
                // The current rifle constraint drives only a visual hierarchy. Never silently omit
                // a future constraint driving colliders, spawn points or other authority components.
                foreach (Component child in component.GetComponentsInChildren<Component>(true))
                    if (child is Collider || child is Rigidbody || child != null && Components.Contains(child.GetType().FullName ?? string.Empty)) return false;
                return true;
            }
            if (component is Renderer || component is MeshFilter || component is Camera || component is AudioListener
                || component is AudioSource || component is Light || component is Animator || component is Animation
                || component is Canvas || component is CanvasRenderer || component is CanvasGroup || component is LODGroup
                || component is ParticleSystem || component is RectTransform) return true;
            if (name.StartsWith("UnityEngine.UI.", StringComparison.Ordinal) || name.StartsWith("UnityEngine.EventSystems.", StringComparison.Ordinal)
                || name.StartsWith("TMPro.", StringComparison.Ordinal) || name.StartsWith("Cinemachine.", StringComparison.Ordinal)
                || name.StartsWith("UnityEngine.Animations.Rigging.", StringComparison.Ordinal)) return true;
            switch (name)
            {
                case "RPG4.Networking.ClientPresentationRoot": case "RPG4.Frontend.GameplayUiBindingAdapter":
                case "RPG4.PlayerMovement.PlayerCameraController": case "RPG4.PlayerMovement.PlayerViewController":
                case "RPG4.PlayerMovement.PlayerAimPose": case "RPG4.PlayerMovement.PlayerWeaponRig":
                case "RPG4.PlayerMovement.PlayerBodyAimConstraint": case "RPG4.PlayerMovement.PlayerShotPoseConstraint":
                case "RPG4.Interaction.PlayerInteraction": // Local camera targeting; server access limits live in the network ingress.
                case "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData":
                case "UnityEngine.Rendering.Universal.UniversalAdditionalLightData":
                case "CombatGirlsCharacterPack.SDFFaceShadowController":
                case "MagicaCloth2.MagicaCloth": case "MagicaCloth2.MagicaPlaneCollider":
                case "MagicaCloth2.MagicaCapsuleCollider": case "MagicaCloth2.MagicaSphereCollider":
                case "RPG4.UI.UIManager": case "RPG4.UI.CharacterHealthView": case "RPG4.UI.CharacterStatusHUD":
                case "RPG4.UI.PlayerDeathUI": case "RPG4.UI.UiTextScale":
                case "RPG4.Backpack.UI.InventoryUICoordinator": case "RPG4.Backpack.UI.InventoryUIInputRouter":
                case "RPG4.Backpack.UI.WeaponAmmoHUD": return true;
                default: return false;
            }
        }
        internal static bool Exclude(string owner, string path)
        {
            if (path == "m_Script" || path == "m_Name" || path == "m_ObjectHideFlags" || path == "m_GameObject"
                || path == "m_CorrespondingSourceObject" || path == "m_PrefabInstance" || path == "m_PrefabAsset"
                || path == "m_EditorHideFlags" || path == "m_EditorClassIdentifier") return true;
            if (owner == "UnityEngine.Transform") return path != "m_LocalPosition" && path != "m_LocalRotation" && path != "m_LocalScale" && path != "m_Father";
            if (owner == "Mirror.NetworkIdentity" && (path == "sceneId" || path == "hasSpawned")) return true;
            if (owner == "RPG4.Networking.GameplaySceneBindings" && (path == "localCamera" || path == "localHealthViews" || path == "requiredClientBindings")) return true;
            if (owner == "RPG4.Networking.NetworkPlayerController" && path == "additionalClientPresentationBehaviours") return true;
            if (owner == "RPG4.Combat.TrainingDummy" && (path == "hitRenderers" || path == "flashDuration")) return true;
            if (owner == "RPG4.Backpack.Network.BackpackNetworkPlayer"
                && (path == "bodyRenderer" || path == "localColor" || path == "remoteColor")) return true;
            if (owner == "RPG4.PlayerMovement.PlayerEquipment" && path == "weaponRig") return true;
            if (owner == "RPG4.PlayerMovement.Player")
            {
                switch (path)
                {
                    case "presentationEnabled": case "stateData.content.animator": case "stateData.content.viewController":
                    case "stateData.content.aimPose": case "stateData.attack": case "stateData.exploration.animationTransitionDuration":
                    case "stateData.aim.aimAnimation": case "stateData.aim.animationTransitionDuration":
                    case "stateData.aim.animationParameterSmoothTime": case "stateData.aim.turnAnimationThreshold":
                    case "stateData.aim.turnAnimationMoveThreshold": case "stateData.aim.turnAnimationTransitionDuration":
                    case "stateData.hurt.transitionDuration": case "stateData.reload.reloadClip": case "stateData.reload.crouchReloadClip": return true;
                }
            }
            if (owner == "RPG4.Networking.AuthoritativeNetworkManager" && (path == "localCamera" || path == "offlinePlayer"
                || path == "offlineSceneObjects" || path == "networkAddress" || path == "showDebugMessages"
                || path == "timeInterpolationGui" || path == "collectTrafficStatistics"
                )) return true;
            if (owner == "Mirror.Transports.Encryption.EncryptionTransport" && (path.StartsWith("Client", StringComparison.Ordinal)
                || path.StartsWith("Server", StringComparison.Ordinal) || path == "OnClientValidateServerPubKey")) return true;
            if (owner == "kcp2k.KcpTransport" && (path == "port" || path.IndexOf("Gui", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("Statistics", StringComparison.OrdinalIgnoreCase) >= 0)) return true;
            if (owner == "RPG4.Backpack.Items.ItemManifest")
            {
                string leaf = path.Substring(path.LastIndexOf('.') + 1);
                if (leaf == "icon" || leaf == "iconDimensions" || leaf == "text" || leaf == "label" || leaf == "padding"
                    || leaf == "collapseLabel" || leaf == "collapseValue" || leaf == "minFractionalDigits" || leaf == "maxFractionalDigits"
                    || leaf == "equipPrefab" || leaf == "socketAttachPoint" || leaf == "model") return true;
            }
            return owner == "TagManager" && (path == "m_SortingLayers" || path == "m_RenderingLayers");
        }
    }
}
