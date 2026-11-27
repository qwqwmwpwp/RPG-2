using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using RPG4.Backpack.Core;
using RPG4.Backpack.Items;
using UnityEngine;

namespace RPG4.LoadoutTemplate
{
    /// <summary>维护网络库存的授权物品目录和配置签名，并为角色与搜刮协议生成一致的编解码配置。</summary>
    internal sealed class NetworkLoadoutCatalog
    {
        // 配置签名独立于操作和状态协议版本。
        private const int ConfigurationSchema = 4, MaxCatalog = 256, MaxCells = LoadoutStateCodec.MaxCells;
        private readonly Dictionary<string, LoadoutItemDefinition> catalog =
            new Dictionary<string, LoadoutItemDefinition>(StringComparer.Ordinal);
        internal string Signature { get; private set; }

        /// <summary>校验网络角色及其槽位与物品定义，按协议顺序计算签名并生成编解码配置。</summary>
        internal bool TryBuild(NetworkRoleLoadout owner, RoleLoadoutController role, LoadoutItemDefinition[] itemCatalog,
            out LoadoutCodecConfiguration codecConfiguration, out string error)
        {
            codecConfiguration = null; error = null; catalog.Clear();
            if (role == null || role.GetComponentInParent<NetworkRoleLoadout>(true) != owner
                || role.Configuration == null || role.Configuration.Count != 10)
                return Fail("需要四容器及六装备槽配置。", out error);
            var text = new StringBuilder("loadout-schema-").Append(ConfigurationSchema);
            var sorted = new List<RoleSlotConfiguration>(role.Configuration);
            sorted.Sort((a, b) => (a == null ? -1 : (int)a.slotId).CompareTo(b == null ? -1 : (int)b.slotId));
            int mask = 0;
            foreach (RoleSlotConfiguration config in sorted)
            {
                if (config == null || (int)config.slotId < 0 || (int)config.slotId > 9 || config.rule == null
                    || (mask & (1 << (int)config.slotId)) != 0) return Fail("槽位配置无效或重复。", out error);
                mask |= 1 << (int)config.slotId;
                text.Append('|').Append((int)config.slotId).Append(':').Append(config.rule.ConfigurationSignature);
                if ((int)config.slotId < 4)
                {
                    var container = config.containerController;
                    if (container == null || container.GetComponentInParent<NetworkRoleLoadout>(true) != owner
                        || !container.isActiveAndEnabled || (int)container.ConfiguredKind != (int)config.slotId + 1)
                        return Fail("四个容器的类别与槽位不匹配。", out error);
                    if (container.ConfiguredCellCount > MaxCells
                        || !container.TryGetConfiguredShape(out List<Vector2Int> cells, out error) || cells.Count > MaxCells)
                        return Fail(error ?? "容器有效格超过协议上限。", out error);
                    text.Append(':').Append(container.ConfigurationSignature).Append(':').Append(container.Definition != null ? container.Definition.DefinitionId : "$container/" + (int)container.ConfiguredKind);
                    if (container.Definition != null && !AddDefinition(role.ManifestCatalog, container.Definition, out error)) return false;
                    foreach (ContainerInitialContent content in container.InitialContents ?? Array.Empty<ContainerInitialContent>())
                        if (content == null || content.definition == null || content.count < 1 || !AddDefinition(role.ManifestCatalog, content.definition, out error))
                            return Fail(error ?? "容器初始内容缺少有效物品定义或数量。", out error);
                }
            }
            foreach (RoleSlotConfiguration config in sorted)
                if (config.initialItem != null && !AddDefinition(role.ManifestCatalog, config.initialItem, out error)) return false;
            foreach (LoadoutItemDefinition definition in role.RuntimeDefinitions ?? Array.Empty<LoadoutItemDefinition>())
                if (definition == null || !AddDefinition(role.ManifestCatalog, definition, out error)) return Fail(error ?? "运行物品显示定义含空项。", out error);
            if (role.ManifestCatalog != null)
            {
                var manifests = new List<ItemManifest>(role.ManifestCatalog.Manifests);
                manifests.Sort((a, b) => string.CompareOrdinal(a != null ? a.ItemType.Name : null, b != null ? b.ItemType.Name : null));
                var types = new HashSet<string>(StringComparer.Ordinal);
                foreach (ItemManifest manifest in manifests)
                {
                    if (manifest == null || !types.Add(manifest.ItemType.Name)) return Fail("运行物品目录存在空项或重复类型。", out error);
                    try { manifest.ValidateConfiguration(); } catch (Exception exception) { return Fail(exception.Message, out error); }
                    text.Append('|').Append(LoadoutRuntimeItems.ManifestSignature(manifest));
                }
            }
            foreach (LoadoutItemDefinition definition in itemCatalog ?? Array.Empty<LoadoutItemDefinition>())
                if (definition == null || !AddDefinition(role.ManifestCatalog, definition, out error)) return Fail(error ?? "物品目录含空定义。", out error);
            var ids = new List<string>(catalog.Keys); ids.Sort(StringComparer.Ordinal);
            foreach (string id in ids) text.Append('|').Append(id).Append(':').Append(catalog[id].ConfigurationSignature);
            using (SHA256 hash = SHA256.Create()) Signature = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
            var rules = new LoadoutSlotRule[10];
            var containers = new LoadoutCodecContainer[4];
            foreach (RoleSlotConfiguration config in sorted)
            {
                int i = (int)config.slotId; rules[i] = config.rule;
                if (i >= 4) continue;
                var container = config.containerController;
                if (!container.TryGetConfiguredShape(out List<Vector2Int> shape, out error)) return false;
                containers[i] = new LoadoutCodecContainer(container.Definition, shape,
                    container.Definition == null ? container.CreateConfiguredBody("preview", null) : null);
            }
            codecConfiguration = new LoadoutCodecConfiguration(Signature, catalog, role.ManifestCatalog, rules, containers);
            return true;
        }

        /// <summary>按编号和运行物品类型校验定义唯一性及清单归属，再登记显示定义。</summary>
        private bool AddDefinition(ItemManifestCatalog manifestCatalog, LoadoutItemDefinition definition, out string error)
        {
            error = null;
            string id = definition.DefinitionId;
            if (!LoadoutStateCodec.ValidId(id) || id.StartsWith("$", StringComparison.Ordinal)) return Fail("物品定义编号无效。", out error);
            if (catalog.TryGetValue(id, out LoadoutItemDefinition existing))
                return existing == definition || Fail("物品目录含重复定义编号。", out error);
            if (catalog.Count >= MaxCatalog) return Fail("物品目录超过协议上限。", out error);
            if (definition.Manifest != null)
            {
                if (manifestCatalog == null || !manifestCatalog.TryGet(definition.Manifest.ItemType, out ItemManifest manifest)
                    || manifest != definition.Manifest) return Fail("运行定义的清单未登记在角色授权目录。", out error);
                try { manifest.ValidateConfiguration(); } catch (Exception exception) { return Fail(exception.Message, out error); }
                foreach (LoadoutItemDefinition existingDefinition in catalog.Values)
                    if (existingDefinition.Manifest != null && existingDefinition.Manifest.ItemType.MatchesTagExact(manifest.ItemType))
                        return Fail("一个运行物品类型只能登记一个显示定义。", out error);
            }
            catalog.Add(id, definition); return true;
        }

        /// <summary>按运行物品标签返回已授权的显示定义。</summary>
        internal bool TryGetRuntimeDefinition(BackpackTag type, out LoadoutItemDefinition definition)
        {
            foreach (LoadoutItemDefinition candidate in catalog.Values)
                if (candidate.Manifest != null && candidate.Manifest.ItemType.MatchesTagExact(type)) { definition = candidate; return true; }
            definition = null; return false;
        }

        /// <summary>返回本次配置构建失败的原因。</summary>
        private static bool Fail(string message, out string error) { error = message; return false; }
    }
}
