using System.Linq;
using System.Reflection;
using NUnit.Framework;
using RPG4.Backpack.Items;
using RPG4.CharacterAttributes;
using RPG4.Combat;
using RPG4.PlayerIntent;
using RPG4.PlayerMovement;
using UnityEditor;
using UnityEngine;

namespace RPG4.Networking.Tests
{
    public sealed class CharacterRuntimeRegressionTests
    {
        private GameObject root, ceiling;
        private Transform visual;
        private Player player;
        private Rigidbody body;
        private int tick;

        [SetUp]
        public void CreateActor()
        {
            tick = 0;
            root = new GameObject("Regression actor");
            root.SetActive(false);
            root.transform.position = new Vector3(1000, 50, 1000);
            visual = new GameObject("Visual").transform;
            visual.SetParent(root.transform, false);
            body = root.AddComponent<Rigidbody>();
            body.useGravity = false;
            var capsule = root.AddComponent<CapsuleCollider>();
            capsule.height = 2f; capsule.radius = 0.3f; capsule.center = Vector3.up;
            root.AddComponent<PlayerEquipment>();
            player = root.AddComponent<Player>();
            player.InputSource = PlayerInputSource.External;
            player.PresentationEnabled = false;
            using (var serialized = new SerializedObject(player))
            {
                serialized.FindProperty("stateData.content.presentationRoot").objectReferenceValue = visual;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            root.SetActive(true);
            if (player.ReadState == null)
            {
                // Edit Mode does not dispatch normal MonoBehaviour lifecycle messages.
                InvokeLifecycle(root.GetComponent<RPG4.Input.PlayerInput>(), "Awake");
                InvokeLifecycle(root.GetComponent<PlayerEquipment>(), "Awake");
                InvokeLifecycle(player, "Awake");
            }
            Physics.SyncTransforms();
        }

        private static void InvokeLifecycle(MonoBehaviour target, string method)
            => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

        [TearDown]
        public void DestroyActor()
        {
            if (ceiling != null) Object.DestroyImmediate(ceiling);
            if (root != null) Object.DestroyImmediate(root);
        }

        public enum MovementPath { Offline, ServerMotor, AcceptedMotor, OwnerPrediction }

        private sealed class AcceptPlan : IPlayerNetworkMovementResolver
        {
            public PlayerMovementCommit Resolve(in PlayerMovementEvaluation evaluation)
            {
                PlayerMotorState motor = evaluation.StartMotor;
                motor.Crouched = evaluation.Rules.Plan.Crouched;
                return new PlayerMovementCommit(false, motor);
            }
        }

        private void Advance(MovementPath path, bool crouch, IPlayerNetworkMovementResolver resolver)
        {
            var input = new PlayerInputFrame(Vector2.right, crouchHeld: crouch);
            tick++;
            if (path == MovementPath.OwnerPrediction)
                player.PredictNetworkMovement(input, tick, tick * 0.02d, 0.02f, replay: true);
            else if (path == MovementPath.Offline)
            {
                Assert.That(player.SubmitExternalIntent(input), Is.True);
                InvokeLifecycle(player, "FixedUpdate");
            }
            else player.SimulateNetworkTick(input, tick, tick * 0.02d, 0.02f, resolver);
        }

        [TestCase(MovementPath.Offline)]
        [TestCase(MovementPath.ServerMotor)]
        [TestCase(MovementPath.AcceptedMotor)]
        [TestCase(MovementPath.OwnerPrediction)]
        public void ReleasingCrouchUnderBeamStandsAfterMovingClear(MovementPath path)
        {
            if (path != MovementPath.Offline) player.ConfigureNetworkSimulation(path != MovementPath.OwnerPrediction, false);
            var resolver = path == MovementPath.AcceptedMotor ? new AcceptPlan() : null;
            for (int i = 0; i < 6; i++) Advance(path, true, resolver);
            Assert.That(player.PublishedFrame.PhysicallyCrouched, Is.True);

            ceiling = new GameObject("Low beam");
            ceiling.transform.position = body.position + Vector3.up * 1.65f;
            ceiling.AddComponent<BoxCollider>().size = new Vector3(4f, 0.3f, 4f);
            Physics.SyncTransforms();
            // Let the motor observe the beam before asking to stand.
            for (int i = 0; i < 3; i++) Advance(path, true, resolver);
            Assert.That(player.PublishedFrame.CanStand, Is.False);
            for (int i = 0; i < 4; i++) Advance(path, false, resolver);
            Assert.That(player.PublishedFrame.PhysicallyCrouched, Is.True);

            Vector3 clear = body.position + Vector3.right * 4f;
            body.position = clear;
            root.transform.position = clear;
            Physics.SyncTransforms();
            for (int i = 0; i < 5; i++) Advance(path, false, resolver);
            Assert.That(player.PublishedFrame.CanStand, Is.True);
            Assert.That(player.PublishedFrame.PhysicallyCrouched, Is.False);
            Assert.That(root.GetComponent<CapsuleCollider>().height, Is.EqualTo(2f));
        }

        private const string Items = "Assets/_Project/Code/Items/";
        private static RuntimeItemSnapshot Snapshot(ItemManifest manifest, ulong id)
        {
            Assert.That(manifest.TryCreateRuntimeItem(new ItemId(id), out var item, out var error), Is.True, error);
            Assert.That(RuntimeItemSnapshotCodec.TryCapture(item, out var snapshot, out error), Is.True, error);
            return snapshot;
        }

        private static RuntimeItem RestoreRifle(ulong attachmentId, bool chambered)
        {
            var rifle = AssetDatabase.LoadAssetAtPath<ItemManifest>(Items + "金属步枪_写实.asset");
            var muzzle = AssetDatabase.LoadAssetAtPath<ItemManifest>(Items + "基础枪口装置.asset");
            var ammo = AssetDatabase.LoadAssetAtPath<ItemManifest>(Items + "步枪标准弹.asset");
            var catalog = AssetDatabase.LoadAssetAtPath<ItemManifestCatalog>(Items + "物品清单目录.asset");
            RuntimeItemSnapshot original = Snapshot(rifle, 1);
            var weapon = new WeaponStateSnapshot(original.Weapon.Durability, chambered ? ammo.ItemType : default, 0);
            var snapshot = new RuntimeItemSnapshot(original.ItemId, original.ItemType, 1, original.Numbers.ToArray(),
                weapon, null, new[] { new InstalledItemSnapshot(ItemAssemblySlots.Muzzle, Snapshot(muzzle, attachmentId)) });
            Assert.That(rifle.TryRestoreRuntimeItem(snapshot, catalog, out var result, out var error), Is.True, error);
            return result;
        }

        [Test]
        public void EqualRevisionReplacementUpdatesAttachmentWhileAmmoOnlyUpdateRetainsModel()
        {
            var frames = new CharacterEquipmentFrames(visual);
            var first = RestoreRifle(11, false);
            var second = RestoreRifle(12, false);
            Assert.That(second.Revision, Is.EqualTo(first.Revision));
            frames.Request(first);
            var original = frames.BeginFrame(0, 0, false);
            Assert.That(original.View, Is.Not.Null);
            frames.Request(second);
            var changed = frames.BeginFrame(original.Identity, original.Version, true);
            Assert.That(changed, Is.Not.SameAs(original));
            var model = changed.View.GetComponent<ItemAssemblyModelView>();
            Assert.That(model.GetModel(new ItemId(11)), Is.Null);
            var attachment = model.GetModel(new ItemId(12));
            Assert.That(attachment, Is.Not.Null);
            uint version = changed.Version;
            frames.Request(RestoreRifle(12, true));
            var stable = frames.BeginFrame(changed.Identity, version, true);
            Assert.That(stable, Is.SameAs(changed));
            Assert.That(stable.Version, Is.EqualTo(version));
            Assert.That(model.GetModel(new ItemId(12)), Is.SameAs(attachment));
        }

        [Test]
        public void UnchangedServerFrameIsReusedUntilExternalHealthOrPositionChanges()
        {
            player.ConfigureNetworkSimulation(true, false);
            Advance(MovementPath.ServerMotor, false, null);
            var completed = player.PublishedFrame;
            player.RefreshNetworkPublication();
            Assert.That(player.PublishedFrame, Is.SameAs(completed));
            player.Attributes.SetMaxHealth(57f);
            player.RefreshNetworkPublication();
            var changed = player.PublishedFrame;
            Assert.That(changed, Is.Not.SameAs(completed));
            Assert.That(changed.MaxHealth, Is.EqualTo(57f));
            body.position += Vector3.right;
            player.RefreshNetworkPublication();
            Assert.That(player.PublishedFrame.WorldPosition.x, Is.EqualTo(body.position.x));
            Assert.That(player.PublishedFrame, Is.Not.SameAs(changed));
        }

        [Test]
        public void EarlyPoseRefreshLeavesOneFullRenderStepForCorrectionDecay()
        {
            player.PresentationEnabled = true;
            player.BeginOwnerReconcile();
            root.transform.position += Vector3.right * 0.5f;
            Physics.SyncTransforms();
            player.EndOwnerReconcile();
            float before = Mathf.Abs(visual.localPosition.x);
            Assert.That(before, Is.GreaterThan(0.4f));
            player.Presenter.RefreshPreparedFrame(true);
            Assert.That(Mathf.Abs(visual.localPosition.x), Is.EqualTo(before));
            player.Presenter.AdvanceFrame(0.06f);
            float after = Mathf.Abs(visual.localPosition.x);
            Assert.That(after, Is.EqualTo(before * 0.5f).Within(0.002f));
            player.Presenter.AdvanceFrame(0.06f);
            Assert.That(Mathf.Abs(visual.localPosition.x), Is.EqualTo(after));
        }

        [Test]
        public void ReplicatedEquipmentInvalidatesHudOncePerChange()
        {
            player.ConfigureNetworkSimulation(false, false);
            var equipment = player.Equipment;
            equipment.ApplyReplicatedEquipment(RestoreRifle(11, false));
            var empty = equipment.CaptureWeaponHud();
            Assert.That(equipment.CaptureWeaponHud(), Is.SameAs(empty));
            equipment.ApplyReplicatedEquipment(RestoreRifle(11, true));
            var chambered = equipment.CaptureWeaponHud();
            Assert.That(chambered.ChamberCount, Is.EqualTo(1));
            Assert.That(chambered, Is.Not.SameAs(empty));
            Assert.That(equipment.CaptureWeaponHud(), Is.SameAs(chambered));
        }

        [Test]
        public void CombatModesKeepCurrentWeaponConsumableAndDamagePermissions()
        {
            Assert.That(player.Combat.WeaponActions && player.Combat.Consumables, Is.True);
            Assert.That(player.Attributes.ApplyDamage(new DamageInfo(10f)), Is.False);
            player.ConfigureNetworkSimulation(true, false);
            Assert.That(player.Combat.WeaponActions || player.Combat.Consumables || player.Combat.Damage, Is.False);
            Assert.That(player.Attributes.ApplyDamage(new DamageInfo(10f)), Is.False);
        }
    }
}
