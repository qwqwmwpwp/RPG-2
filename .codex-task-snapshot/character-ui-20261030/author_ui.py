from pathlib import Path
import re
import json
import copy
import shutil
import yaml
import uuid

ROOT = Path(__file__).resolve().parents[2]
WORK = Path(__file__).resolve().parent


class Asset:
    def __init__(self, path, read_from=None):
        self.path = ROOT / path
        text = (read_from or self.path).read_text(encoding="utf-8-sig")
        parts = re.split(r"(?m)^--- !u!(\d+) &(-?\d+)( stripped)?\n", text)
        self.header = parts[0]
        self.docs = {}
        self.order = []
        for i in range(1, len(parts), 4):
            kind, fid, stripped, body = parts[i:i + 4]
            fid = int(fid)
            self.docs[fid] = dict(kind=int(kind), stripped=stripped or "", raw=body)
            self.order.append(fid)

    def data(self, fid):
        return next(iter(yaml.safe_load(self.docs[fid]["raw"]).values()))

    def field(self, fid, field, value):
        raw = self.docs[fid]["raw"]
        pattern = rf"(?m)^  {re.escape(field)}:.*(?:\n(?!  [A-Za-z_]|\Z).*)*"
        match = re.search(pattern, raw)
        if not match:
            raise ValueError(f"Missing {field} in {self.path}:{fid}")
        replacement = f"  {field}:" + ("" if str(value).startswith("\n") else " ") + str(value)
        self.docs[fid]["raw"] = raw[:match.start()] + replacement + raw[match.end():]

    def append_field(self, fid, field, value):
        self.docs[fid]["raw"] = self.docs[fid]["raw"].rstrip() + f"\n  {field}:" + ("" if str(value).startswith("\n") else " ") + str(value) + "\n"

    def clone(self, other, fid, new_id, mapping):
        doc = copy.deepcopy(other.docs[fid])
        doc["raw"] = re.sub(r"fileID: (-?\d+)", lambda m: f"fileID: {mapping.get(int(m[1]), int(m[1]))}", doc["raw"])
        self.docs[new_id] = doc
        self.order.append(new_id)

    def add(self, fid, kind, raw):
        if fid in self.docs:
            raise ValueError(f"Duplicate ID {fid}")
        self.docs[fid] = dict(kind=kind, stripped="", raw=raw.rstrip() + "\n")
        self.order.append(fid)

    def save(self):
        self.path.write_text(self.header + "".join(f"--- !u!{self.docs[fid]['kind']} &{fid}{self.docs[fid]['stripped']}\n{self.docs[fid]['raw']}" for fid in self.order), encoding="utf-8", newline="\n")

    def summary(self):
        names = {fid: self.data(fid).get("m_Name") for fid in self.order if self.docs[fid]["kind"] == 1}
        for fid in self.order:
            doc = self.docs[fid]
            data = self.data(fid)
            if doc["kind"] == 1:
                print(fid, data.get("m_Name"), data.get("m_Component"))
            elif doc["kind"] == 224:
                print(" rect", fid, names.get(data.get("m_GameObject", {}).get("fileID")), "parent", data.get("m_Father"), "pos", data.get("m_AnchoredPosition"), "size", data.get("m_SizeDelta"))
            elif doc["kind"] == 114 and data.get("m_EditorClassIdentifier"):
                print(" script", fid, data.get("m_EditorClassIdentifier"), {k: v for k, v in data.items() if not k.startswith("m_")})


def backup(paths):
    for relative in paths:
        source = ROOT / relative
        target = WORK / "before" / relative
        if source.exists() and not target.exists():
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)


def baseline(path):
    return Asset(path, WORK / "before" / path)


def ref(fid):
    return "{fileID: " + str(fid) + "}"


def v2(x, y):
    return f"{{x: {x}, y: {y}}}"


def quoted(value):
    return json.dumps(value, ensure_ascii=True)


def children(asset, parent, child):
    values = [item["fileID"] for item in asset.data(parent)["m_Children"]]
    if child not in values:
        values.append(child)
    asset.field(parent, "m_Children", "\n" + "\n".join("  - " + ref(item) for item in values))


def component(asset, go, fid):
    values = [item["component"]["fileID"] for item in asset.data(go)["m_Component"]]
    values.append(fid)
    asset.field(go, "m_Component", "\n" + "\n".join("  - component: " + ref(item) for item in values))


def rect(asset, fid, parent, x, y, width, height, anchor=(0, 1), pivot=(0, 1), stretch=False):
    fields = dict(m_Father=ref(parent), m_LocalPosition="{x: 0, y: 0, z: 0}",
                  m_LocalScale="{x: 1, y: 1, z: 1}", m_LocalRotation="{x: 0, y: 0, z: 0, w: 1}",
                  m_LocalEulerAnglesHint="{x: 0, y: 0, z: 0}", m_AnchorMin=v2(*anchor),
                  m_AnchorMax=v2(1, 1) if stretch else v2(*anchor), m_Pivot=v2(*pivot),
                  m_AnchoredPosition=v2(x, y), m_SizeDelta=v2(width, height))
    for key, value in fields.items():
        asset.field(fid, key, value)


HEALTH = "Assets/player/PlayerHealthHUD.prefab"
BACKPACK = "Assets/backpack/Prefabs/背包UI.prefab"
CHEST = "Assets/backpack/Prefabs/和箱子交互ui.prefab"
CROSSHAIR = "Assets/player/Interaction/CrosshairUI.prefab"
DETAILS = "Assets/backpack/Prefabs/ItemDetails.prefab"
INSPECTION = "Assets/backpack/Prefabs/WeaponInspectionPanel.prefab"


def empty(asset, template, go, name, parent, x=0, y=0, width=0, height=0, anchor=(0, 1), pivot=(0, 1), stretch=False):
    mapping = {950000120: go, 950000121: go + 1}
    asset.clone(template, 950000120, go, mapping)
    asset.clone(template, 950000121, go + 1, mapping)
    asset.field(go, "m_Name", quoted(name))
    asset.field(go, "m_Component", "\n  - component: " + ref(go + 1))
    asset.field(go + 1, "m_Children", "[]")
    rect(asset, go + 1, parent, x, y, width, height, anchor, pivot, stretch)
    children(asset, parent, go + 1)
    return go + 1


def image(asset, template, go, name, parent, x, y, width, height, color, anchor=(0, 1), pivot=(0, 1), stretch=False, raycast=False):
    mapping = {950000120 + n: go + n for n in range(4)}
    for n in range(4):
        asset.clone(template, 950000120 + n, go + n, mapping)
    asset.field(go, "m_Name", quoted(name))
    asset.field(go + 1, "m_Children", "[]")
    rect(asset, go + 1, parent, x, y, width, height, anchor, pivot, stretch)
    asset.field(go + 3, "m_Color", color)
    asset.field(go + 3, "m_RaycastTarget", int(raycast))
    children(asset, parent, go + 1)
    return go + 3


def label(asset, template, go, name, parent, x, y, width, height, text, size=20, align=257, anchor=(0, 1), pivot=(0, 1)):
    mapping = {950000110 + n: go + n for n in range(4)}
    for n in range(4):
        asset.clone(template, 950000110 + n, go + n, mapping)
    asset.field(go, "m_Name", quoted(name))
    asset.field(go + 1, "m_Children", "[]")
    rect(asset, go + 1, parent, x, y, width, height, anchor, pivot)
    for key, value in dict(m_text=quoted(text), m_fontSize=size, m_fontSizeBase=size,
                           m_enableAutoSizing=0, m_overflowMode=0, m_isRichText=1,
                           m_RaycastTarget=0).items():
        asset.field(go + 3, key, value)
    # TMP serializes horizontal and vertical alignment separately in this project.
    if "m_HorizontalAlignment" in asset.data(go + 3):
        asset.field(go + 3, "m_HorizontalAlignment", 2 if align == 514 else 1)
        asset.field(go + 3, "m_VerticalAlignment", 512 if align == 514 else 256)
    if "m_textAlignment" in asset.data(go + 3):
        asset.field(go + 3, "m_textAlignment", align)
    children(asset, parent, go + 1)
    return go + 3


def group(asset, go, fid, alpha=1, interactable=False):
    asset.add(fid, 225, f"""CanvasGroup:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_Enabled: 1
  m_Alpha: {alpha}
  m_Interactable: {int(interactable)}
  m_BlocksRaycasts: {int(interactable)}
  m_IgnoreParentGroups: 0
""")
    component(asset, go, fid)


def script_guid(path):
    meta = ROOT / (path + ".meta")
    if meta.exists():
        return re.search(r"(?m)^guid: (\w+)", meta.read_text(encoding="utf-8-sig"))[1]
    guid = uuid.uuid5(uuid.NAMESPACE_URL, "rpg2/character-ui/" + path).hex
    meta.write_text(f"""fileFormatVersion: 2
guid: {guid}
MonoImporter:
  externalObjects: {{}}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {{instanceID: 0}}
  userData:
  assetBundleName:
  assetBundleVariant:
""", encoding="utf-8")
    return guid


def script(asset, go, fid, path, class_name, fields):
    guid = script_guid(path)
    asset.add(fid, 114, f"""MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {guid}, type: 3}}
  m_Name:
  m_EditorClassIdentifier: {class_name}
""" + "".join(f"  {key}:" + ("" if str(value).startswith("\n") else " ") + str(value) + "\n" for key, value in fields.items()))
    component(asset, go, fid)


def button(asset, template, button_template, go, name, parent, x, y, width, height, text, size=20):
    image(asset, template, go, name, parent, x, y, width, height,
          "{r: 0.08, g: 0.17, b: 0.21, a: 1}", raycast=True)
    mapping = {7600000000000000040: go, 7600000000000000043: go + 3, 7600000000000000044: go + 4}
    asset.clone(button_template, 7600000000000000044, go + 4, mapping)
    asset.field(go + 4, "m_OnClick", "\n    m_PersistentCalls:\n      m_Calls: []")
    component(asset, go, go + 4)
    text_id = label(asset, template, go + 10, name + "Label", go + 1, 8, -2, width - 16, height - 4, text, size, 514)
    return go + 4, text_id


def text_scale(asset, go, fid, ids):
    script(asset, go, fid, "Assets/Scripts/UI/UiTextScale.cs", "RPG4.UI::RPG4.UI.UiTextScale",
           {"labels": "\n" + "\n".join("  - " + ref(item) for item in ids)})


def override(asset, instance, target, guid, field, value="", object_ref=None):
    raw = asset.docs[instance]["raw"]
    entries = list(re.finditer(r"(?ms)^    - target:.*?(?=^    - target:|^    m_Removed|^    m_Added|^  m_SourcePrefab:|\Z)", raw))
    values = asset.data(instance)["m_Modification"]["m_Modifications"]
    replacement = f"    - target: {{fileID: {target}, guid: {guid}, type: 3}}\n      propertyPath: {field}\n      value: {value}\n      objectReference: {object_ref or ref(0)}\n"
    index = next((i for i, entry in enumerate(values) if entry["target"]["fileID"] == target and entry["propertyPath"] == field), None)
    if index is not None:
        item = entries[index]
        raw = raw[:item.start()] + replacement + raw[item.end():]
    elif entries:
        pos = entries[-1].end()
        raw = raw[:pos] + replacement + raw[pos:]
    else:
        raw = raw.replace("    m_Modifications: []", "    m_Modifications:\n" + replacement.rstrip())
    asset.docs[instance]["raw"] = raw


def instance_for(asset, path):
    guid = re.search(r"(?m)^guid: (\w+)", (ROOT / (path + ".meta")).read_text())[1]
    for fid in asset.order:
        if asset.docs[fid]["kind"] == 1001 and asset.data(fid)["m_SourcePrefab"]["guid"] == guid:
            return fid, guid
    raise ValueError(path)


def stripped_component(asset, source_fid, instance, guid, local_fid, script_path):
    for fid in asset.order:
        data = asset.data(fid)
        if asset.docs[fid]["kind"] == 114 and data.get("m_CorrespondingSourceObject", {}).get("fileID") == source_fid and data.get("m_PrefabInstance", {}).get("fileID") == instance:
            return fid
    asset.add(local_fid, 114, f"""MonoBehaviour:
  m_CorrespondingSourceObject: {{fileID: {source_fid}, guid: {guid}, type: 3}}
  m_PrefabInstance: {{fileID: {instance}}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {script_guid(script_path)}, type: 3}}
  m_Name:
  m_EditorClassIdentifier:
""")
    asset.docs[local_fid]["stripped"] = " stripped"
    return local_fid


def rotate(asset, rect_id, degrees):
    import math
    angle = math.radians(degrees) / 2
    asset.field(rect_id, "m_LocalRotation", f"{{x: 0, y: 0, z: {math.sin(angle)}, w: {math.cos(angle)}}}")
    asset.field(rect_id, "m_LocalEulerAnglesHint", f"{{x: 0, y: 0, z: {degrees}}}")


def build_health(template, button_template):
    asset = baseline(HEALTH)
    root = 950000002
    asset.append_field(950000003, "showLowHealthWarning", 1)
    asset.append_field(950000003, "lowHealthThreshold", 0.25)
    asset.append_field(950000003, "lowHealthColor", "{r: 1, g: 0.25, b: 0.2, a: 1}")
    asset.field(950000101, "m_SizeDelta", v2(400, 132))
    rect(asset, 950000111, 950000101, 18, -10, 364, 42)
    rect(asset, 950000121, 950000101, 18, -66, 364, 14)
    rect(asset, 950000141, 950000101, 18, -88, 364, 34)
    for fid, size in [(950000113, 22), (950000143, 18)]:
        asset.field(fid, "m_fontSize", size)
        asset.field(fid, "m_fontSizeBase", size)
    posture = label(asset, template, 970000100, "PostureLabel", root, 32, 174, 300, 40, "蹲伏", 20, anchor=(0, 0), pivot=(0, 0))
    posture_feedback = label(asset, template, 970000120, "PostureFeedback", root, 32, 218, 560, 48, "", 20, anchor=(0, 0), pivot=(0, 0))
    direction = empty(asset, template, 970000200, "DamageDirection", root, width=48, height=32, anchor=(0.5, 0.5), pivot=(0.5, 0.5))
    group(asset, 970000200, 970000205, 0)
    cross = baseline(CROSSHAIR)
    asset.clone(cross, 22300000, 970000206, {100000: 970000200, 22300000: 970000206})
    asset.field(970000206, "m_OverrideSorting", 1)
    asset.field(970000206, "m_SortingOrder", 120)
    component(asset, 970000200, 970000206)
    for go, x, angle in [(970000220, -8, 45), (970000240, 8, -45)]:
        image(asset, template, go, "DamageChevron", direction, x, 0, 26, 5,
              "{r: 1, g: 0.3, b: 0.25, a: 1}", anchor=(0.5, 0.5), pivot=(0.5, 0.5))
        rotate(asset, go + 1, angle)

    death_go = 970001000
    image(asset, template, death_go, "DeathScreen", root, 0, 0, 0, 0,
          "{r: 0.012, g: 0.02, b: 0.03, a: 0.88}", anchor=(0, 0), pivot=(0.5, 0.5), stretch=True, raycast=True)
    group(asset, death_go, 970001005, 0)
    asset.clone(cross, 22300000, 970001007, {100000: death_go, 22300000: 970001007})
    asset.field(970001007, "m_OverrideSorting", 1)
    asset.field(970001007, "m_SortingOrder", 200)
    component(asset, death_go, 970001007)
    scene = Asset("Assets/Scenes/Main.unity")
    ray_id = next(fid for fid in scene.order if scene.docs[fid]["kind"] == 114 and "m_BlockingObjects" in scene.data(fid))
    old_go = scene.data(ray_id)["m_GameObject"]["fileID"]
    asset.clone(scene, ray_id, 970001008, {old_go: death_go, ray_id: 970001008})
    component(asset, death_go, 970001008)
    image(asset, template, 970001100, "DeathCard", death_go + 1, 0, 0, 680, 380,
          "{r: 0.035, g: 0.065, b: 0.085, a: 1}", anchor=(0.5, 0.5), pivot=(0.5, 0.5))
    death_title = label(asset, template, 970001120, "DeathTitle", 970001101, 32, -32, 616, 80, "角色已死亡", 38, 514)
    death_status = label(asset, template, 970001140, "DeathStatus", 970001101, 32, -130, 616, 88, "重新开始将重置当前场景与本局状态", 22, 514)
    restart, restart_label = button(asset, template, button_template, 970001200, "RestartButton", 970001101, 200, -270, 280, 64, "重新开始", 22)
    script(asset, death_go, 970001006, "Assets/Scripts/UI/PlayerDeathUI.cs", "RPG4.UI::RPG4.UI.PlayerDeathUI",
           dict(canvasGroup=ref(970001005), exclusiveGroup="Death", blocksOtherWindows=1,
                healthView=ref(950000003), restartButton=ref(restart), statusLabel=ref(death_status)))
    script(asset, 950000001, 970000003, "Assets/Scripts/UI/CharacterStatusHUD.cs", "RPG4.UI::RPG4.UI.CharacterStatusHUD",
           dict(postureLabel=ref(posture), postureFeedback=ref(posture_feedback), damageDirection=ref(direction),
                damageDirectionGroup=ref(970000205), deathScreen=ref(970001006), directionDuration=0.8))
    text_scale(asset, 950000001, 970000004, [950000113, 950000143, posture, posture_feedback, death_title, death_status, restart_label])
    asset.save()


def build_crosshair(template):
    asset = baseline(CROSSHAIR)
    root = 22400000
    marker = empty(asset, template, 980000100, "HitMarker", root, width=32, height=32, anchor=(0.5, 0.5), pivot=(0.5, 0.5))
    asset.field(980000100, "m_IsActive", 0)
    for go, angle in [(980000120, 45), (980000140, -45)]:
        image(asset, template, go, "HitStroke", marker, 0, 0, 22, 2,
              "{r: 1, g: 1, b: 1, a: 1}", anchor=(0.5, 0.5), pivot=(0.5, 0.5))
        rotate(asset, go + 1, angle)
    obstruction = label(asset, template, 980000200, "MuzzleObstruction", root, 0, 36, 440, 40,
                        "枪口受阻", 22, 514, anchor=(0.5, 0.5), pivot=(0.5, 0))
    asset.field(980000200, "m_IsActive", 0)
    asset.field(obstruction, "m_fontColor", "{r: 1, g: 0.78, b: 0.3, a: 1}")
    asset.field(obstruction, "m_fontColor32", "\n    serializedVersion: 2\n    rgba: " + str(255 + (199 << 8) + (77 << 16) + (255 << 24)))
    notification = label(asset, template, 980000240, "PickupNotification", root, -36, -80, 440, 100,
                         "", 22, anchor=(1, 0.5), pivot=(1, 1))
    for field, value in dict(notificationLabel=ref(notification), hitMarker=ref(980000100), obstructionLabel=ref(obstruction)).items():
        asset.append_field(11400000, field, value)
    text_scale(asset, 100000, 980000003, [11400004, 11400005, obstruction, notification])
    asset.save()


def build_details(template, button_template):
    asset = baseline(DETAILS)
    root_go = next(fid for fid in asset.order if asset.docs[fid]["kind"] == 1)
    view_id = 7500000000000000016
    for field, value in dict(docked=0, propertiesViewport=ref(895000001), propertiesScroll=ref(895000005)).items():
        asset.append_field(view_id, field, value)
    parent = 7500000000000000051
    old_children = [x["fileID"] for x in asset.data(parent)["m_Children"] if x["fileID"] != 7500000000000000131]
    asset.field(parent, "m_Children", "\n" + "\n".join("  - " + ref(item) for item in old_children))
    image(asset, template, 895000000, "PropertiesViewport", parent, 18, -190, 284, 162,
          "{r: 0, g: 0, b: 0, a: 0}", raycast=True)
    rect(asset, 7500000000000000131, 895000001, 0, 0, 284, 162)
    children(asset, 895000001, 7500000000000000131)
    asset.clone(button_template, 7600000000000000084, 895000004,
                {7600000000000000080: 895000000, 7600000000000000084: 895000004})
    component(asset, 895000000, 895000004)
    inspection = baseline(INSPECTION)
    asset.clone(inspection, 920000072, 895000005,
                {920000063: 895000000, 920000064: 895000001, 920000069: 7500000000000000131, 920000072: 895000005})
    asset.field(895000005, "m_Horizontal", 0)
    asset.field(895000005, "m_Vertical", 1)
    component(asset, 895000000, 895000005)
    asset.field(7500000000000000133, "m_overflowMode", 0)
    text_ids = [fid for fid in asset.order if asset.docs[fid]["kind"] == 114 and "m_fontAsset" in asset.data(fid)]
    text_scale(asset, root_go, 895000006, text_ids)
    asset.save()


def build_inspection():
    asset = baseline(INSPECTION)
    group(asset, 920000001, 920000106, 0)
    asset.append_field(920000105, "presentationGroup", ref(920000106))
    asset.save()


def build_window(path, template, button_template):
    asset = baseline(path)
    chest = path == CHEST
    prefix = 7700000000000000000 if chest else 7600000000000000000
    ui_id = prefix + 6
    ui_data = asset.data(ui_id)
    root_go = asset.data(ui_data["window"]["fileID"])["m_GameObject"]["fileID"]
    root = asset.data(root_go)["m_Component"][0]["component"]["fileID"]
    width = 1880 if chest else 1320
    right_x = 1396 if chest else 900
    right_width = width - right_x - 24
    asset.field(root, "m_SizeDelta", v2(width, 900))
    rect(asset, prefix + 11, root, 0, 0, width, 3)
    rect(asset, prefix + 21, root, 24, -14, width - 160, 48)
    rect(asset, prefix + 31, root, 24, -65, width - 48, 28)
    rect(asset, prefix + 41, root, width - 102, -20, 78, 40)
    asset.field(prefix + 23, "m_text", quoted("角色与容器" if chest else "角色与背包"))
    asset.field(prefix + 33, "m_text", quoted("查看角色状态、整理物品与检查装备"))
    asset.field(prefix + 23, "m_fontSize", 28)
    asset.field(prefix + 23, "m_fontSizeBase", 28)
    asset.field(prefix + 33, "m_fontSize", 16)
    asset.field(prefix + 33, "m_fontSizeBase", 16)
    if chest:
        for title, summary, viewport, x in [(7100000000000000401, prefix + 61, prefix + 71, 308),
                                             (7100000000000000501, prefix + 81, prefix + 91, 852)]:
            rect(asset, title, root, x, -100, 520, 32)
            rect(asset, summary, root, x, -142, 520, 50)
            rect(asset, viewport, root, x, -202, 520, 604)
        title_ids = [7100000000000000403, 7100000000000000503]
        divider, hint, status = prefix + 101, prefix + 111, prefix + 121
    else:
        rect(asset, prefix + 61, root, 308, -100, 568, 32)
        rect(asset, prefix + 71, root, 308, -142, 568, 50)
        rect(asset, prefix + 81, root, 308, -202, 568, 604)
        title_ids = [prefix + 63]
        divider, hint, status = prefix + 91, prefix + 101, prefix + 111
    rect(asset, divider, root, 24, -820, width - 48, 1)
    rect(asset, hint, root, 24, -830, width - 48, 36)
    rect(asset, status, root, 24, -868, width - 48, 30)
    for text_id in [ui_data["primarySummary"]["fileID"], ui_data["secondarySummary"]["fileID"], ui_data["actionHint"]["fileID"], ui_data["operationStatus"]["fileID"]]:
        if text_id:
            asset.field(text_id, "m_fontSize", 16)
            asset.field(text_id, "m_fontSizeBase", 16)
    for text_id in title_ids:
        asset.field(text_id, "m_fontSize", 20)
        asset.field(text_id, "m_fontSizeBase", 20)

    base = 8600000000000010000
    panel_go = base
    image(asset, template, panel_go, "CharacterOverview", root, 24, -100, 260, 706,
          "{r: 0.035, g: 0.08, b: 0.105, a: 1}", raycast=True)
    # The background must be behind the existing equipment prefab instance.
    root_children = [item["fileID"] for item in asset.data(root)["m_Children"]]
    root_children.remove(panel_go + 1)
    root_children.insert(0, panel_go + 1)
    asset.field(root, "m_Children", "\n" + "\n".join("  - " + ref(item) for item in root_children))
    title = label(asset, template, base + 20, "OverviewTitle", panel_go + 1, 16, -10, 228, 40, "角色概览", 22)
    health = label(asset, template, base + 40, "OverviewHealth", panel_go + 1, 16, -60, 228, 48, "生命状态未就绪", 20)
    state = label(asset, template, base + 60, "OverviewStatus", panel_go + 1, 16, -112, 228, 36, "等待角色就绪", 18)
    weapon = label(asset, template, base + 80, "EquippedWeaponName", panel_go + 1, 16, -320, 228, 72, "未装备武器", 16)
    stats = label(asset, template, base + 100, "EquippedWeaponStats", panel_go + 1, 16, -398, 228, 144, "", 16)
    scale_button, scale_label = button(asset, template, button_template, base + 200, "TextScaleButton", panel_go + 1, 14, -552, 232, 44, "文字 100%", 18)
    hit_button, hit_label = button(asset, template, button_template, base + 240, "HitFeedbackButton", panel_go + 1, 14, -600, 232, 44, "命中反馈：开", 18)
    pulse_button, pulse_label = button(asset, template, button_template, base + 280, "LowHealthPulseButton", panel_go + 1, 14, -648, 232, 44, "低血量脉冲：开", 18)
    details_button, details_label = button(asset, template, button_template, base + 320, "DetailsTab", root, right_x, -100, (right_width - 12) / 2, 36, "物品详情", 18)
    inspect_button, inspect_label = button(asset, template, button_template, base + 360, "InspectionTab", root, right_x + (right_width + 12) / 2, -100, (right_width - 12) / 2, 36, "武器装配", 18)
    selection = label(asset, template, base + 400, "SelectionState", root, right_x, -144, right_width, 50, "物品详情 · 悬停预览 / 点击选定", 18)
    comparison = label(asset, template, base + 440, "WeaponComparison", root, right_x, -614, right_width, 202, "", 17)

    # Keep the original weapon slot and its serialized inventory bindings.
    eq_instance, eq_guid = instance_for(asset, "Assets/backpack/Prefabs/EquipmentSlotView.prefab")
    eq_source = Asset("Assets/backpack/Prefabs/EquipmentSlotView.prefab")
    eq_rect = next(fid for fid in eq_source.order if eq_source.docs[fid]["kind"] == 224 and eq_source.data(fid).get("m_Father", {}).get("fileID") == 0)
    for key, value in {"m_AnchorMin.x": 0, "m_AnchorMax.x": 0, "m_AnchorMin.y": 1, "m_AnchorMax.y": 1,
                       "m_Pivot.x": 0, "m_Pivot.y": 1, "m_AnchoredPosition.x": 86, "m_AnchoredPosition.y": -252}.items():
        override(asset, eq_instance, eq_rect, eq_guid, key, value)
    override(asset, eq_instance, eq_rect, eq_guid, "m_SizeDelta.y", 156)
    override(asset, eq_instance, 8300000000000000020, eq_guid, "m_IsActive", 0)
    override(asset, eq_instance, 8300000000000000031, eq_guid, "m_AnchoredPosition.y", -36)

    detail_instance, detail_guid = instance_for(asset, DETAILS)
    details = Asset(DETAILS)
    original_details = baseline(DETAILS)
    for fid in details.order:
        if details.docs[fid]["kind"] != 224:
            continue
        data = details.data(fid)
        old_width = data.get("m_SizeDelta", {}).get("x", 0)
        if old_width >= 200:
            override(asset, detail_instance, fid, detail_guid, "m_SizeDelta.x", old_width + right_width - 320)
        if data.get("m_Father", {}).get("fileID") == 0:
            for key, value in {"m_AnchorMin.x": 0, "m_AnchorMax.x": 0, "m_AnchorMin.y": 1, "m_AnchorMax.y": 1,
                               "m_Pivot.x": 0, "m_Pivot.y": 1, "m_AnchoredPosition.x": right_x,
                               "m_AnchoredPosition.y": -202, "m_SizeDelta.y": 396}.items():
                override(asset, detail_instance, fid, detail_guid, key, value)
        elif data.get("m_SizeDelta", {}).get("y") == 380:
            override(asset, detail_instance, fid, detail_guid, "m_SizeDelta.y", 396)
    for fid in [895000001, 7500000000000000131]:
        override(asset, detail_instance, fid, detail_guid, "m_SizeDelta.y", 188)
    override(asset, detail_instance, 7500000000000000016, detail_guid, "docked", 1)

    inspection_instance, inspection_guid = instance_for(asset, INSPECTION)
    for key, value in {"m_AnchorMin.x": 0, "m_AnchorMax.x": 0, "m_AnchorMin.y": 1, "m_AnchorMax.y": 1,
                       "m_Pivot.x": 0, "m_Pivot.y": 1, "m_AnchoredPosition.x": right_x + (right_width - 340) / 2,
                       "m_AnchoredPosition.y": -180}.items():
        override(asset, inspection_instance, 920000002, inspection_guid, key, value)
    inspection_component = stripped_component(asset, 920000105, inspection_instance, inspection_guid,
                                               base + 900, "Assets/backpack/Scripts/UI/WeaponInspectionPanel.cs")
    script(asset, root_go, base + 901, "Assets/backpack/Scripts/UI/CharacterOverviewUI.cs",
           "RPG4.Backpack.UI::RPG4.Backpack.UI.CharacterOverviewUI",
           dict(ui=ref(ui_id), equipmentView=ref(8400000000000000002), healthLabel=ref(health),
                characterStatus=ref(state), weaponName=ref(weapon), weaponStats=ref(stats),
                selectionLabel=ref(selection), comparisonLabel=ref(comparison), inspection=ref(inspection_component),
                detailsButton=ref(details_button), inspectionButton=ref(inspect_button),
                activeTabColor="{r: 0.12, g: 0.34, b: 0.38, a: 1}", inactiveTabColor="{r: 0.08, g: 0.17, b: 0.21, a: 1}",
                textScaleButton=ref(scale_button), hitFeedbackButton=ref(hit_button), lowHealthPulseButton=ref(pulse_button),
                textScaleLabel=ref(scale_label), hitFeedbackLabel=ref(hit_label), lowHealthPulseLabel=ref(pulse_label)))
    scale_ids = [title, health, state, weapon, stats, scale_label, hit_label, pulse_label,
                 details_label, inspect_label, selection, comparison, prefix + 23, prefix + 33, prefix + 53,
                 ui_data["primarySummary"]["fileID"], ui_data["actionHint"]["fileID"], ui_data["operationStatus"]["fileID"]]
    if ui_data["secondarySummary"]["fileID"]:
        scale_ids.append(ui_data["secondarySummary"]["fileID"])
    scale_ids += title_ids
    text_scale(asset, root_go, base + 902, scale_ids)
    asset.save()


def build():
    template = baseline(HEALTH)
    buttons = baseline(BACKPACK)
    script_guid("Assets/Scripts/UI/PlayerUiPreferences.cs")
    build_health(template, buttons)
    build_crosshair(template)
    build_details(template, buttons)
    build_inspection()
    build_window(BACKPACK, template, buttons)
    build_window(CHEST, template, buttons)
    item = baseline("Assets/backpack/Prefabs/物品ui.prefab")
    item_guid = script_guid("Assets/backpack/Scripts/UI/BackpackItemUI.cs")
    item_view = next(fid for fid in item.order if item.docs[fid]["kind"] == 114 and item.data(fid).get("m_Script", {}).get("guid") == item_guid)
    item.append_field(item_view, "selectedColor", "{r: 0.85, g: 0.95, b: 1, a: 1}")
    item.save()
    print("Updated health, crosshair, details, inspection and both inventory prefabs; weapon ammo assets untouched.")


def audit():
    failures = []
    for path in [HEALTH, CROSSHAIR, DETAILS, INSPECTION, BACKPACK, CHEST, "Assets/backpack/Prefabs/物品ui.prefab"]:
        asset = Asset(path)
        for fid in asset.order:
            raw = asset.docs[fid]["raw"]
            data = asset.data(fid)
            for match in re.finditer(r"\{fileID: (-?\d+)(?:,\s*guid: ([a-f0-9]{32}))?", raw):
                target = int(match[1])
                if target and not match[2] and target not in asset.docs:
                    failures.append(f"{path}:{fid} unresolved local reference {target}")
            if asset.docs[fid]["kind"] == 224 and not asset.docs[fid]["stripped"]:
                parent = data["m_Father"]["fileID"]
                if parent and parent in asset.docs and not asset.docs[parent]["stripped"]:
                    if {"fileID": fid} not in asset.data(parent)["m_Children"]:
                        failures.append(f"{path}:{fid} absent from parent {parent}")
                for child in data["m_Children"]:
                    child_id = child["fileID"]
                    if child_id in asset.docs and not asset.docs[child_id]["stripped"] and asset.data(child_id)["m_Father"]["fileID"] != fid:
                        failures.append(f"{path}:{fid} inconsistent child {child_id}")
        print(f"{path}: {len(asset.docs)} serialized objects, references and hierarchy checked")
    for path in ["Assets/backpack/Scripts/UI/WeaponAmmoHUD.cs", "Assets/backpack/Prefabs/WeaponAmmoHUD.prefab"]:
        if (ROOT / path).read_bytes() != (WORK / "before" / path).read_bytes():
            failures.append(path + " unexpectedly changed")
    print("Weapon ammo script and prefab match task baseline byte for byte")
    graph = {}
    for area in ["Assets/Scripts", "Assets/backpack/Scripts", "Assets/Mirror"]:
        for path in (ROOT / area).rglob("*.asmdef"):
            data = json.loads(path.read_text(encoding="utf-8-sig"))
            graph[data["name"]] = data.get("references", [])
    visited = set()
    def visit(name, stack):
        if name in stack:
            failures.append("Assembly cycle: " + " -> ".join(stack + [name]))
            return
        if name in visited or name not in graph:
            return
        visited.add(name)
        for dependency in graph[name]:
            visit(dependency, stack + [name])
    for name in graph:
        visit(name, [])
    print(f"Assembly dependency cycle check: {len(graph)} local definitions")
    if failures:
        raise SystemExit("\n".join(failures))
    print("Static asset audit passed")


if __name__ == "__main__":
    import sys
    args = sys.argv[1:]
    if "--audit" in args:
        audit()
        sys.exit(0)
    if "--build" in args:
        build()
        sys.exit(0)
    if "--backup" in args:
        args.remove("--backup")
        backup(args)
        print(f"Backed up {len(args)} existing files")
        sys.exit(0)
    components = "--components" in args
    if components:
        args.remove("--components")
    for path in args:
        asset = Asset(path)
        if components:
            print(path)
            for fid in asset.order:
                data = asset.data(fid)
                if asset.docs[fid]["kind"] == 114 and "m_fontAsset" not in data and any(not key.startswith("m_") for key in data):
                    print(fid, json.dumps(data, ensure_ascii=True))
        else:
            asset.summary()
