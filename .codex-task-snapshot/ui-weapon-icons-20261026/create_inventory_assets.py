import json
import re
import uuid
from pathlib import Path

root = Path(r'E:\github\RPG-2')
task = root / '.codex-task-snapshot/ui-weapon-icons-20261026'
items = root / 'Assets/backpack/Data/Items/枪械'
prefabs = root / 'Assets/backpack/Prefabs/Weapons'
icons = root / 'Assets/UI素材/枪械'
items.mkdir(exist_ok=True)
prefabs.mkdir(exist_ok=True)
base_prefab = (root / 'Assets/CombatGirlsCharacterPack/RifleGirl/Prefab/Prefab_Parts/Weapon_Rifle.prefab').read_text(encoding='utf-8')

variants = [
    ('Style_Wood', 'StyleWood', '木质步枪_卡通', '木质枪托、握把与护木，卡通材质。'),
    ('Style_Meatl', 'StyleMetal', '金属步枪_卡通', '全金属黑色外观，卡通材质。'),
    ('Real_Wood', 'RealWood', '木质步枪_写实', '木质枪托、握把与护木，写实材质。'),
    ('Real_Metal', 'RealMetal', '金属步枪_写实', '全金属黑色外观，写实材质。'),
]

def guid(path):
    return re.search(r'^guid: (\w+)', path.read_text(encoding='utf-8'), re.M).group(1)

def write_new(path, text):
    if path.exists():
        raise FileExistsError(path)
    path.write_text(text, encoding='utf-8', newline='\n')

def meta(path, importer, extra=''):
    ident = uuid.uuid4().hex
    write_new(Path(str(path) + '.meta'), f'fileFormatVersion: 2\nguid: {ident}\n{importer}:\n  externalObjects: {{}}\n{extra}  userData: \n  assetBundleName: \n  assetBundleVariant: \n')
    return ident

for folder in (items, prefabs):
    write_new(Path(str(folder) + '.meta'), f'fileFormatVersion: 2\nguid: {uuid.uuid4().hex}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n')

records = []
for source_suffix, tag_suffix, display_name, appearance in variants:
    normalized = source_suffix.replace('Meatl', 'Metal')
    material = root / f'Assets/CombatGirlsCharacterPack/RifleGirl/Animations/Materials/Weapon/Rifle_Weapon_{source_suffix}.mat'
    material_guid = guid(Path(str(material) + '.meta'))
    icon = icons / f'Icon_Rifle_{normalized}.png'
    icon_guid = guid(Path(str(icon) + '.meta'))
    prefab = prefabs / f'Weapon_Rifle_{normalized}.prefab'
    prefab_text = base_prefab.replace('m_Name: Weapon_Rifle\n', f'm_Name: Weapon_Rifle_{normalized}\n').replace('guid: c03d6f01ed2b01e45a25498e1e7cdae3, type: 2', f'guid: {material_guid}, type: 2')
    write_new(prefab, prefab_text)
    prefab_guid = meta(prefab, 'PrefabImporter')
    item_type = f'GameItems.Equipment.Weapons.Rifle.{tag_suffix}'
    item = items / f'{display_name}.asset'
    item_text = f'''%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: 4c34e53e651b4930ac2714e72c40d43d, type: 3}}
  m_Name: {display_name}
  m_EditorClassIdentifier:
  itemType:
    name: {item_type}
  fragments:
  - rid: 1001
  - rid: 1002
  - rid: 1003
  - rid: 1004
  references:
    version: 2
    RefIds:
    - rid: 1001
      type: {{class: GridFragment, ns: RPG4.Backpack.Items, asm: RPG4.Backpack.Items}}
      data:
        fragmentTag:
          name: FragmentTags.Default.GridFragment
        width: 4
        height: 2
        padding: 0
    - rid: 1002
      type: {{class: ImageFragment, ns: RPG4.Backpack.Items, asm: RPG4.Backpack.Items}}
      data:
        fragmentTag:
          name: FragmentTags.Default.IconFragment
        icon: {{fileID: 21300000, guid: {icon_guid}, type: 3}}
        iconDimensions: {{x: 1024, y: 512}}
    - rid: 1003
      type: {{class: EquipmentFragment, ns: RPG4.Backpack.Items, asm: RPG4.Backpack.Items}}
      data:
        fragmentTag:
          name: FragmentTags.Equip.EquipmentFragment
        equipmentType:
          name: GameItems.Equipment.Weapons.Rifle
        equipPrefab: {{fileID: 6363982380300774935, guid: {prefab_guid}, type: 3}}
        socketAttachPoint: Hand_R_Socket
        modifiers: []
    - rid: 1004
      type: {{class: TextFragment, ns: RPG4.Backpack.Items, asm: RPG4.Backpack.Items}}
      data:
        fragmentTag:
          name: FragmentTags.Default.Description
        text: {appearance}占用 4×2 格，不可堆叠。
'''
    write_new(item, item_text)
    item_guid = meta(item, 'NativeFormatImporter', '  mainObjectFileID: 11400000\n')
    records.append(dict(name=display_name, item_type=item_type, item=str(item.relative_to(root)).replace('\\', '/'), item_guid=item_guid, icon=str(icon.relative_to(root)).replace('\\', '/'), icon_guid=icon_guid, prefab=str(prefab.relative_to(root)).replace('\\', '/'), prefab_guid=prefab_guid, material_guid=material_guid, grid=[4, 2], pixels=[1024, 512]))

def append_lines(path, lines):
    raw = path.read_bytes()
    newline = b'\r\n' if b'\r\n' in raw else b'\n'
    if not raw.endswith(newline):
        raw += newline
    path.write_bytes(raw + newline.join(s.encode('utf-8') for s in lines) + newline)

append_lines(root / 'Assets/backpack/Data/Items/物品清单目录.asset', [f'  - {{fileID: 11400000, guid: {r["item_guid"]}, type: 2}}' for r in records])
append_lines(root / 'Assets/backpack/Data/TagRegistry.asset', ['  - ' + r['item_type'] for r in records] + ['  - FragmentTags.Default.Description'])
(task / 'inventory-asset-records.json').write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps([dict(name=r['name'], grid=r['grid'], pixels=r['pixels']) for r in records], ensure_ascii=False))
