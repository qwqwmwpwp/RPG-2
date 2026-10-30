from pathlib import Path
from copy import deepcopy
import re
import uuid
import yaml

ROOT = Path(__file__).resolve().parents[2]
SNAPSHOT = Path(__file__).resolve().parent
HEADER = '%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n'


def documents(path):
    source = (ROOT / path).read_text(encoding='utf-8-sig')
    result = {}
    for match in re.finditer(r'^--- !u!(\d+) &(-?\d+)(?: stripped)?\n(.*?)(?=^--- !u!|\Z)', source, re.M | re.S):
        result[int(match[2])] = (int(match[1]), yaml.safe_load(match[3]))
    return result


def ref(file_id=0, guid=None, kind=3):
    value = {'fileID': file_id}
    if guid is not None:
        value.update(guid=guid, type=kind)
    return value


def vec(x=0, y=0, z=None, w=None):
    value = {'x': x, 'y': y}
    if z is not None:
        value['z'] = z
    if w is not None:
        value['w'] = w
    return value


def color(r, g, b, a=1):
    return dict(r=r, g=g, b=b, a=a)


class Flow(dict):
    pass


def flow_representer(dumper, value):
    return dumper.represent_mapping('tag:yaml.org,2002:map', value, flow_style=True)


yaml.SafeDumper.add_representer(Flow, flow_representer)


def format_data(value):
    if isinstance(value, dict):
        cls = Flow if 'fileID' in value or set(value) <= {'x', 'y', 'z', 'w'} or set(value) == {'r', 'g', 'b', 'a'} else dict
        return cls((key, format_data(item)) for key, item in value.items())
    if isinstance(value, list):
        return [format_data(item) for item in value]
    return '' if value is None else value


def serialize(class_id, file_id, value):
    return f'--- !u!{class_id} &{file_id}\n' + yaml.safe_dump(format_data(value), allow_unicode=True, sort_keys=False, width=120)


def write_asset(path, docs):
    target = ROOT / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(HEADER + ''.join(serialize(*doc) for doc in docs), encoding='utf-8', newline='\n')


def metadata(path, kind='asset', main_id=0):
    target = ROOT / path
    meta = target.with_name(target.name + '.meta')
    if meta.exists():
        return re.search(r'^guid: (\w+)', meta.read_text(encoding='utf-8-sig'), re.M)[1]
    guid = uuid.uuid4().hex
    text = f'fileFormatVersion: 2\nguid: {guid}\n'
    if kind == 'folder':
        text += 'folderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n'
    elif kind == 'script':
        text += 'MonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n'
    elif kind == 'prefab':
        text += 'PrefabImporter:\n  externalObjects: {}\n'
    else:
        text += f'NativeFormatImporter:\n  externalObjects: {{}}\n  mainObjectFileID: {main_id}\n'
    text += '  userData: \n  assetBundleName: \n  assetBundleVariant: \n'
    meta.write_text(text, encoding='utf-8', newline='\n')
    return guid


def base():
    return dict(m_ObjectHideFlags=0, m_CorrespondingSourceObject=ref(), m_PrefabInstance=ref(), m_PrefabAsset=ref())


def mono(go, guid, **fields):
    return dict(base(), m_GameObject=ref(go), m_Enabled=1, m_EditorHideFlags=0,
                m_Script=ref(11500000, guid), m_Name='', m_EditorClassIdentifier='', **fields)


def gameobject(file_id, name, components, layer=0):
    return (1, file_id, {'GameObject': dict(base(), serializedVersion=6,
            m_Component=[{'component': ref(item)} for item in components], m_Layer=layer,
            m_Name=name, m_TagString='Untagged', m_Icon=ref(), m_NavMeshLayer=0,
            m_StaticEditorFlags=0, m_IsActive=1)})


def transform(file_id, go, parent=0, children=(), position=(0, 0, 0), scale=(1, 1, 1), rotation=(0, 0, 0, 1)):
    return (4, file_id, {'Transform': dict(base(), m_GameObject=ref(go), serializedVersion=2,
            m_LocalRotation=vec(*rotation), m_LocalPosition=vec(*position), m_LocalScale=vec(*scale),
            m_ConstrainProportionsScale=0, m_Children=[ref(item) for item in children],
            m_Father=ref(parent), m_LocalEulerAnglesHint=vec(0, 0, 0))})


def rect(file_id, go, parent=0, children=(), anchor=(0, 1), maximum=None, position=(0, 0), size=(0, 0), pivot=(0, 1), world_y=0, scale=1):
    value = transform(file_id, go, parent, children, (0, world_y, 0), (scale, scale, scale))[2]['Transform']
    value.pop('serializedVersion')
    value.update(m_AnchorMin=vec(*anchor), m_AnchorMax=vec(*(maximum or anchor)), m_AnchoredPosition=vec(*position),
                 m_SizeDelta=vec(*size), m_Pivot=vec(*pivot))
    return (224, file_id, {'RectTransform': value})


def renderer(file_id, go):
    return (222, file_id, {'CanvasRenderer': dict(base(), m_GameObject=ref(go), m_CullTransparentMesh=1)})


hud = documents('Assets/backpack/Prefabs/WeaponAmmoHUD.prefab')
scene = documents('Assets/Scenes/Main.unity')
player = documents('Assets/player/player.prefab')
image_template = hud[930000010][1]['MonoBehaviour']
text_template = hud[930000014][1]['MonoBehaviour']
canvas_template = scene[435649774][1]['Canvas']
mesh_renderer_template = next(value['MeshRenderer'] for _, value in scene.values() if 'MeshRenderer' in value)
box_template = next(value['BoxCollider'] for _, value in documents('Assets/player/Interaction/ItemInteractable.prefab').values() if 'BoxCollider' in value)
attribute_guid = metadata('Assets/Scripts/CharacterAttributes/CharacterAttributes.cs', 'script')
dummy_guid = metadata('Assets/Scripts/Combat/TrainingDummy.cs', 'script')
contact_guid = metadata('Assets/Scripts/Combat/ContactDamage.cs', 'script')
health_view_guid = metadata('Assets/Scripts/UI/CharacterHealthView.cs', 'script')


def image(file_id, go, tint):
    value = deepcopy(image_template)
    value.update(m_GameObject=ref(go), m_Color=tint, m_RaycastTarget=0, m_PreserveAspect=0)
    return (114, file_id, {'MonoBehaviour': value})


def text(file_id, go, label, font_size=18, centered=False, tint=None):
    value = deepcopy(text_template)
    tint = tint or color(0.91, 0.94, 0.96)
    value.update(m_GameObject=ref(go), m_text=label, m_fontSize=font_size, m_fontSizeBase=font_size,
                 m_fontColor=tint, m_Color=tint, m_HorizontalAlignment=2 if centered else 1,
                 m_enableWordWrapping=0, m_overflowMode=1, m_RaycastTarget=0)
    return (114, file_id, {'MonoBehaviour': value})


for folder in ['Assets/Combat', 'Assets/Combat/Materials', 'Assets/Combat/Prefabs']:
    (ROOT / folder).mkdir(exist_ok=True)
    metadata(folder, 'folder')

material_source = (ROOT / 'Assets/player/Materials/MovementObstacle.mat').read_text(encoding='utf-8-sig')
materials = {}
for name, tint in [('TrainingDummyBody', color(0.18, 0.28, 0.34)),
                   ('TrainingDummyAccent', color(0.95, 0.31, 0.06)),
                   ('TrainingDummyBase', color(0.07, 0.10, 0.13))]:
    path = f'Assets/Combat/Materials/{name}.mat'
    value = material_source.replace('m_Name: MovementObstacle', f'm_Name: {name}')
    rgba = '{' + ', '.join(f'{key}: {number}' for key, number in tint.items()) + '}'
    value = re.sub(r'(?m)^(    - _(?:BaseColor|Color):) .*$', lambda match: match[1] + ' ' + rgba, value)
    (ROOT / path).write_text(value, encoding='utf-8', newline='\n')
    materials[name] = metadata(path, main_id=2100000)


def mesh(go, tf, name, parent, position, scale, mesh_id, material, collider=None, rotation=(0, 0, 0, 1)):
    filter_id, renderer_id = go + 2, go + 3
    components = [tf, filter_id, renderer_id] + ([go + 4] if collider else [])
    result = [gameobject(go, name, components), transform(tf, go, parent, position=position, scale=scale, rotation=rotation)]
    result.append((33, filter_id, {'MeshFilter': dict(base(), m_GameObject=ref(go),
                   m_Mesh=ref(mesh_id, '0000000000000000e000000000000000', 0))}))
    value = deepcopy(mesh_renderer_template)
    value.update(m_GameObject=ref(go), m_Materials=[ref(2100000, materials[material], 2)])
    result.append((23, renderer_id, {'MeshRenderer': value}))
    if collider:
        value = deepcopy(box_template)
        value.update(m_GameObject=ref(go), m_IsTrigger=0, m_Size=vec(1, 1, 1), m_Center=vec(0, 0, 0))
        result.append((65, go + 4, {'BoxCollider': value}))
    return result


# Geometry and hurt trigger are authored assets; no runtime construction is needed.
D = 940000000
parts = [(D+20, 'Torso', (0, 1.18, 0), (0.8, 1.05, 0.4), 10202, 'TrainingDummyBody', True),
         (D+30, 'Head', (0, 1.96, 0), (0.48, 0.48, 0.48), 10207, 'TrainingDummyBody', True),
         (D+40, 'LeftArm', (-0.61, 1.25, 0), (0.25, 0.72, 0.3), 10202, 'TrainingDummyBody', True),
         (D+50, 'RightArm', (0.61, 1.25, 0), (0.25, 0.72, 0.3), 10202, 'TrainingDummyBody', True),
         (D+60, 'ChestTarget', (0, 1.28, -0.225), (0.56, 0.025, 0.56), 10206, 'TrainingDummyAccent', False),
         (D+70, 'Bullseye', (0, 1.28, -0.26), (0.23, 0.018, 0.23), 10206, 'TrainingDummyBase', False),
         (D+80, 'Stand', (0, 0.42, 0), (0.2, 0.32, 0.2), 10206, 'TrainingDummyBase', False)]
docs = [gameobject(D+1, 'EnemyTrainingDummy', [D+2, D+3, D+4]),
        transform(D+2, D+1, children=[D+11, D+91, D+101, D+121, D+201]),
        (114, D+3, {'MonoBehaviour': mono(D+1, attribute_guid, initialMaxHealth=200, invulnerabilityDuration=0)}),
        (114, D+4, {'MonoBehaviour': mono(D+1, dummy_guid, visualRoot=ref(D+11),
              hitRenderers=[ref(go+3) for go, *_ in parts[:6]], hitColliders=[ref(go+4) for go, *_ in parts[:4]],
              resetDelay=4, flashDuration=0.12)}),
        gameobject(D+10, 'VisualRoot', [D+11]),
        transform(D+11, D+10, D+2, children=[go+1 for go, *_ in parts])]
for go, name, position, scale, mesh_id, material, collider in parts:
    rotation = (0.7071068, 0, 0, 0.7071068) if name in ['ChestTarget', 'Bullseye'] else (0, 0, 0, 1)
    docs += mesh(go, go+1, name, D+11, position, scale, mesh_id, material, collider, rotation)
docs += mesh(D+90, D+91, 'Base', D+2, (0, 0.1, 0), (1.1, 0.1, 1.1), 10206, 'TrainingDummyBase')
docs += mesh(D+100, D+101, 'ContactWarningPad', D+2, (0, 0.012, 0), (2.6, 0.008, 2.6), 10206, 'TrainingDummyAccent')
docs += [gameobject(D+120, 'ContactDamage', [D+121, D+122, D+123]),
         transform(D+121, D+120, D+2, position=(0, 0.9, 0)),
         (135, D+122, {'SphereCollider': dict(base(), m_GameObject=ref(D+120), m_Material=ref(),
                    m_IsTrigger=1, m_Enabled=1, serializedVersion=3, m_Radius=1.3, m_Center=vec(0, 0, 0))}),
         (114, D+123, {'MonoBehaviour': mono(D+120, contact_guid, owner=ref(D+3), damage=10, repeatInterval=1,
                    targetLayers=dict(serializedVersion=2, m_Bits=4294967295))})]


def health_card(docs, start, parent, attributes, world=False, overlay=0):
    # Four visible elements plus a stretched fill inside the bar background.
    title, background, fill, status = start+10, start+20, start+30, start+40
    width = 260 if world else 330
    height = 90 if world else 98
    docs += [gameobject(start, 'HealthCard', [start+1, start+2, start+3], 5),
             rect(start+1, start, parent, children=[title+1, background+1, status+1],
                  anchor=(0.5, 0.5) if world else (0, 0), pivot=(0.5, 0.5) if world else (0, 0),
                  position=(0, 0) if world else (32, 32), size=(width, height)),
             renderer(start+2, start), image(start+3, start, color(0.035, 0.055, 0.075, 0.92)),
             gameobject(title, 'HealthLabel', [title+1, title+2, title+3], 5),
             rect(title+1, title, start+1, position=(14, -10), size=(width-28, 28)),
             renderer(title+2, title), text(title+3, title, '敌人练习靶   200 / 200' if world else '生命   100 / 100', 18),
             gameobject(background, 'HealthTrack', [background+1, background+2, background+3], 5),
             rect(background+1, background, start+1, children=[fill+1], position=(14, -44), size=(width-28, 12)),
             renderer(background+2, background), image(background+3, background, color(0.12, 0.16, 0.19)),
             gameobject(fill, 'HealthFill', [fill+1, fill+2, fill+3], 5),
             rect(fill+1, fill, background+1, anchor=(0, 0), maximum=(1, 1), pivot=(0, 0.5)),
             renderer(fill+2, fill), image(fill+3, fill, color(0.94, 0.32, 0.12) if world else color(0.22, 0.79, 0.58)),
             gameobject(status, 'Status', [status+1, status+2, status+3], 5),
             rect(status+1, status, start+1, position=(14, -64), size=(width-28, 20)),
             renderer(status+2, status), text(status+3, status, '射击练习 · 接近橙圈会受伤' if world else '受伤后短暂无敌', 13, tint=color(0.68, 0.77, 0.8))]
    return dict(attributes=attributes, healthFill=ref(fill+3), healthLabel=ref(title+3), statusLabel=ref(status+3),
                displayName='敌人练习靶' if world else '生命', idleStatus='射击练习 · 接近橙圈会受伤' if world else '受伤后短暂无敌',
                deadStatus='已击倒 · 等待恢复' if world else '已死亡', billboardRoot=ref(parent) if world else ref(),
                damageOverlay=ref(overlay), flashDuration=0.3)


canvas = deepcopy(canvas_template)
canvas.update(m_GameObject=ref(D+200), m_RenderMode=2, m_ReceivesEvents=0, m_SortingOrder=0)
docs += [gameobject(D+200, 'WorldHealth', [D+201, D+202, D+203], 5),
         rect(D+201, D+200, D+2, children=[D+301], anchor=(0.5, 0.5), pivot=(0.5, 0.5),
              size=(260, 90), position=(0, 2.8), world_y=2.8, scale=0.008),
         (223, D+202, {'Canvas': canvas})]
fields = health_card(docs, D+300, D+201, ref(D+3), world=True)
docs.append((114, D+203, {'MonoBehaviour': mono(D+200, health_view_guid, **fields)}))
dummy_path = 'Assets/Combat/Prefabs/EnemyTrainingDummy.prefab'
write_asset(dummy_path, docs)
dummy_prefab_guid = metadata(dummy_path, 'prefab')

H = 950000000
docs = [gameobject(H+1, 'PlayerHealthHUD', [H+2, H+3], 5),
        rect(H+2, H+1, children=[H+11, H+101], anchor=(0, 0), maximum=(1, 1), pivot=(0.5, 0.5)),
        gameobject(H+10, 'DamageOverlay', [H+11, H+12, H+13], 5),
        rect(H+11, H+10, H+2, anchor=(0, 0), maximum=(1, 1), pivot=(0.5, 0.5)),
        renderer(H+12, H+10), image(H+13, H+10, color(0.8, 0.035, 0.025, 0))]
fields = health_card(docs, H+100, H+2, ref(), overlay=H+13)
docs.append((114, H+3, {'MonoBehaviour': mono(H+1, health_view_guid, **fields)}))
hud_path = 'Assets/player/PlayerHealthHUD.prefab'
write_asset(hud_path, docs)
health_prefab_guid = metadata(hud_path, 'prefab')

# Keep every existing object's identifiers and serialization intact.
prefab_path = ROOT / 'Assets/player/player.prefab'
source = prefab_path.read_text(encoding='utf-8-sig')
source, count = re.subn(r'(  initialMaxHealth: 100\n)(?!  invulnerabilityDuration:)', r'\1  invulnerabilityDuration: 0.35\n', source)
assert count == 1
prefab_path.write_text(source, encoding='utf-8', newline='\n')

controller_path = ROOT / 'Assets/player/player.controller'
source = controller_path.read_text(encoding='utf-8-sig')
controller = documents('Assets/player/player.controller')
base_machine_id = -2160913602538721414
death_state_id, hurt_machine_id, empty_state_id, hit_state_id = 742000101, 742000110, 742000111, 742000112
assert not any(item in controller for item in [death_state_id, hurt_machine_id, empty_state_id, hit_state_id])
base_machine = deepcopy(controller[base_machine_id][1])
base_machine['AnimatorStateMachine']['m_ChildStates'].append(dict(serializedVersion=1, m_State=ref(death_state_id), m_Position=vec(1540, 100, 0)))
main_controller = deepcopy(controller[9100000][1])
layer = deepcopy(main_controller['AnimatorController']['m_AnimatorLayers'][1])
layer.update(m_Name='Damage Reaction', m_StateMachine=ref(hurt_machine_id))
main_controller['AnimatorController']['m_AnimatorLayers'].append(layer)


def replace_doc(source, file_id, class_id, data):
    pattern = rf'^--- !u!{class_id} &{file_id}\n.*?(?=^--- !u!|\Z)'
    source, count = re.subn(pattern, lambda _: serialize(class_id, file_id, data), source, flags=re.M | re.S)
    assert count == 1, file_id
    return source


source = replace_doc(source, base_machine_id, 1107, base_machine)
source = replace_doc(source, 9100000, 91, main_controller)
state_template = deepcopy(controller[-658891758991643330][1])


def animation_state(name, motion, speed=1):
    data = deepcopy(state_template)
    data['AnimatorState'].update(m_Name=name, m_Motion=motion, m_Speed=speed,
                                 m_SpeedParameterActive=0, m_SpeedParameter='')
    return data


machine = deepcopy(base_machine)
machine['AnimatorStateMachine'].update(m_Name='Damage Reaction',
    m_ChildStates=[dict(serializedVersion=1, m_State=ref(empty_state_id), m_Position=vec(260, 80, 0)),
                   dict(serializedVersion=1, m_State=ref(hit_state_id), m_Position=vec(260, 180, 0))],
    m_DefaultState=ref(empty_state_id))
source += serialize(1102, death_state_id, animation_state('Dead', ref(5447582539675603047, 'a0c38a748b749e646abf30c366cbace9')))
source += serialize(1107, hurt_machine_id, machine)
source += serialize(1102, empty_state_id, animation_state('Empty', ref()))
source += serialize(1102, hit_state_id, animation_state('Hit', ref(-931760275423846564, '8030672582f1a5b4a9712aa6aec547b8'), 3.5))
controller_path.write_text(source, encoding='utf-8', newline='\n')

scene_path = ROOT / 'Assets/Scenes/Main.unity'
source = scene_path.read_text(encoding='utf-8-sig')
(SNAPSHOT / 'Main.before-asset-authoring.unity').write_text(source, encoding='utf-8', newline='\n')
assert '960000001' not in source and '960000010' not in source
player_prefab_guid = metadata('Assets/player/player.prefab', 'prefab')
player_instance_id = next(file_id for file_id, (_, data) in scene.items() if 'PrefabInstance' in data
                          and data['PrefabInstance']['m_SourcePrefab'].get('guid') == player_prefab_guid)
player_health_scene_id = 960000020
player_health_source_id = 3132622325811843717


def prefab_instance(file_id, guid, parent=0, modifications=()):
    return (1001, file_id, {'PrefabInstance': dict(m_ObjectHideFlags=0, serializedVersion=2,
          m_Modification=dict(serializedVersion=3, m_TransformParent=ref(parent), m_Modifications=list(modifications),
          m_RemovedComponents=[], m_RemovedGameObjects=[], m_AddedGameObjects=[], m_AddedComponents=[]),
          m_SourcePrefab=ref(100100000, guid))})


def modification(guid, target, property_path, value='', reference=None):
    return dict(target=ref(target, guid), propertyPath=property_path, value=value, objectReference=reference or ref())


additions = [prefab_instance(960000001, dummy_prefab_guid, modifications=[
    modification(dummy_prefab_guid, D+2, 'm_LocalPosition.z', '8')]),
    prefab_instance(960000010, health_prefab_guid, 435649771, [
    modification(health_prefab_guid, H+3, 'attributes', reference=ref(player_health_scene_id))])]
source += ''.join(serialize(*doc) for doc in additions)
source += ('--- !u!224 &960000011 stripped\nRectTransform:\n'
           f'  m_CorrespondingSourceObject: {{fileID: {H+2}, guid: {health_prefab_guid}, type: 3}}\n'
           '  m_PrefabInstance: {fileID: 960000010}\n  m_PrefabAsset: {fileID: 0}\n')
source += ('--- !u!114 &960000020 stripped\nMonoBehaviour:\n'
           f'  m_CorrespondingSourceObject: {{fileID: {player_health_source_id}, guid: {player_prefab_guid}, type: 3}}\n'
           f'  m_PrefabInstance: {{fileID: {player_instance_id}}}\n  m_PrefabAsset: {{fileID: 0}}\n'
           f'  m_GameObject: {{fileID: 0}}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n'
           f'  m_Script: {{fileID: 11500000, guid: {attribute_guid}, type: 3}}\n  m_Name: \n  m_EditorClassIdentifier: \n')

# Register the added child and scene root using the scene's existing format.
canvas_pattern = r'(^--- !u!224 &435649771\n.*?  m_Children:\n)(.*?)(  m_Father:)'
source, count = re.subn(canvas_pattern, lambda m: m[1] + m[2] + '  - {fileID: 960000011}\n' + m[3], source, flags=re.M | re.S)
assert count == 1
roots_pattern = r'(^--- !u!1660057539 &\d+\nSceneRoots:\n  m_ObjectHideFlags: 0\n  m_Roots:\n)(.*?)(?=^--- !u!|\Z)'
source, count = re.subn(roots_pattern, lambda m: m[1] + m[2] + '  - {fileID: 960000001}\n', source, flags=re.M | re.S)
assert count == 1
scene_path.write_text(source, encoding='utf-8', newline='\n')
print('Authored EnemyTrainingDummy, PlayerHealthHUD, 3 materials, animation states and Main scene references.')
