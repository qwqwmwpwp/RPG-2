from pathlib import Path
import math
import re
import uuid

ROOT = Path(__file__).resolve().parents[2]
PREFAB = ROOT / 'Assets/player/player.prefab'
PACKAGE = ROOT / 'Library/PackageCache/com.unity.animation.rigging@1.2.1/Runtime/AnimationRig'

def guid(path):
    return re.search(r'^guid: (\w+)', Path(path).read_text(encoding='utf-8-sig'), re.M)[1]

def script_meta(name):
    path = ROOT / 'Assets/Scripts/PlayerMovement' / (name + '.cs.meta')
    if not path.exists():
        path.write_text('fileFormatVersion: 2\nguid: ' + uuid.uuid4().hex + '\nMonoImporter:\n'
                        '  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n'
                        '  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n'
                        '  assetBundleName: \n  assetBundleVariant: \n', encoding='utf-8')
    return guid(path)

body_guid = script_meta('PlayerBodyAimConstraint')
weapon_guid = script_meta('PlayerWeaponRig')
rig_guid = guid(PACKAGE / 'Rig.cs.meta')
builder_guid = guid(PACKAGE / 'RigBuilder.cs.meta')
parent_guid = guid(PACKAGE / 'Constraints/MultiParentConstraint.cs.meta')
hand_guid = guid(PACKAGE / 'Constraints/TwoBoneIKConstraint.cs.meta')
s = PREFAB.read_text(encoding='utf-8-sig')
assert '&720000000' not in s, 'Prefab is already configured; do not append twice.'
blocks = {m[2]: (m[1], m[3]) for m in re.finditer(
    r'^--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\Z)', s, re.M | re.S)}

def edit_block(fid, edit):
    global s
    pattern = r'(^--- !u!\d+ &' + str(fid) + r'[^\n]*\n)(.*?)(?=^--- !u!|\Z)'
    s, count = re.subn(pattern, lambda m: m[1] + edit(m[2]), s, flags=re.M | re.S)
    assert count == 1, fid

def child(fid, added):
    edit_block(fid, lambda b: b.replace('  m_Father:', f'  - {{fileID: {added}}}\n  m_Father:', 1))

def mono(fid, go, script, fields):
    return f'''--- !u!114 &{fid}
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {script}, type: 3}}
  m_Name:
  m_EditorClassIdentifier:
{fields}'''

def node(go, tf, name, parent, components=(), children=(), pos=(0, 0, 0)):
    comps = ''.join(f'  - component: {{fileID: {fid}}}\n' for fid in (tf,) + tuple(components))
    kids = '\n' + ''.join(f'  - {{fileID: {fid}}}\n' for fid in children) if children else ' []\n'
    return f'''--- !u!1 &{go}
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
{comps}  m_Layer: 0
  m_Name: {name}
  m_TagString: Untagged
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
--- !u!4 &{tf}
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  serializedVersion: 2
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: {pos[0]:.9g}, y: {pos[1]:.9g}, z: {pos[2]:.9g}}}
  m_LocalScale: {{x: 1, y: 1, z: 1}}
  m_ConstrainProportionsScale: 0
  m_Children:{kids}  m_Father: {{fileID: {parent}}}
  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}
'''

def vec(body, field, n):
    value = re.search(r'^  ' + field + r': \{(.*?)\}', body, re.M)[1]
    return tuple(float(v) for v in re.findall(r': ([^,}]+)', value))[:n]

def rotate(q, v):
    x, y, z, w = q
    t = (2*(y*v[2]-z*v[1]), 2*(z*v[0]-x*v[2]), 2*(x*v[1]-y*v[0]))
    return (v[0]+w*t[0]+y*t[2]-z*t[1], v[1]+w*t[1]+z*t[0]-x*t[2], v[2]+w*t[2]+x*t[1]-y*t[0])

def transform(fid, point=(0, 0, 0), inverse=False):
    b = blocks[str(fid)][1]
    parent = re.search(r'm_Father: \{fileID: (\d+)\}', b)[1]
    q, p, scale = vec(b, 'm_LocalRotation', 4), vec(b, 'm_LocalPosition', 3), vec(b, 'm_LocalScale', 3)
    if inverse:
        if parent != '0': point = transform(parent, point, True)
        v = rotate((-q[0], -q[1], -q[2], q[3]), tuple(point[i]-p[i] for i in range(3)))
        return tuple(v[i]/scale[i] for i in range(3))
    v = rotate(q, tuple(point[i]*scale[i] for i in range(3)))
    v = tuple(v[i]+p[i] for i in range(3))
    return transform(parent, v) if parent != '0' else v

upper, elbow, hand = (transform(fid) for fid in (6466711385289847554, 5937937697612863309, 3998494049880919930))
line = tuple(hand[i]-upper[i] for i in range(3))
t = sum((elbow[i]-upper[i])*line[i] for i in range(3))/sum(v*v for v in line)
bend = tuple(elbow[i]-upper[i]-t*line[i] for i in range(3))
length = math.sqrt(sum(v*v for v in bend))
direction = tuple(v/length for v in bend) if length > 0.005 else (-0.5, -0.3, -0.8)
hint_world = tuple(elbow[i]+0.3*direction[i] for i in range(3))
hint_local = transform(151728632950264630, hint_world, True)

edit_block(5963844012669126346, lambda b: b.replace('  m_Layer:', '  - component: {fileID: 720000000}\n  m_Layer:', 1))
child(2672218029476637041, 720000002)
child(151728632950264630, 720000016)
edit_block(5612448861134227116, lambda _: mono(5612448861134227116, 5963844012669126346, weapon_guid,
    '  weaponSocket: {fileID: 720000010}\n  supportHand: {fileID: 720000014}\n').split('\n', 1)[1])
edit_block(710000009, lambda b: b.replace('  pitchRange:', '  rigBuilder: {fileID: 720000000}\n'
    '  bodyAim: {fileID: 720000006}\n  weaponRig: {fileID: 5612448861134227116}\n  pitchRange:', 1)
    .replace('  finalIkLayer: 1', '  waistFraction: 0.8'))

def disable_legacy(b):
    marker = '    m_RemovedComponents: []'
    extra = '''    - target: {fileID: 6093372014242540394, guid: 07c341731397a82478e5ea0704d383d1, type: 3}
      propertyPath: m_Enabled
      value: 0
      objectReference: {fileID: 0}
    - target: {fileID: 6093372014242540394, guid: 07c341731397a82478e5ea0704d383d1, type: 3}
      propertyPath: m_Active
      value: 0
      objectReference: {fileID: 0}
'''
    assert marker in b
    return b.replace(marker, extra + marker)
edit_block(464032024294814340, disable_legacy)

s += mono(720000000, 5963844012669126346, builder_guid,
    '  m_RigLayers:\n  - m_Rig: {fileID: 720000005}\n    m_Active: 1\n'
    '  - m_Rig: {fileID: 720000009}\n    m_Active: 1\n'
    '  - m_Rig: {fileID: 720000013}\n    m_Active: 1\n  m_Effectors: []\n')
s += node(720000001, 720000002, 'PlayerRig', 2672218029476637041,
          children=(720000004, 720000008, 720000012))
for go, tf, name, rig, constraint in [(720000003,720000004,'BodyAimRig',720000005,720000006),
        (720000007,720000008,'WeaponRig',720000009,720000010),
        (720000011,720000012,'SupportHandRig',720000013,720000014)]:
    s += node(go, tf, name, 720000002, (rig, constraint))
    s += mono(rig, go, rig_guid, '  m_Weight: 1\n  m_Effectors: []\n')
s += mono(720000006, 720000003, body_guid, '''  m_Weight: 1
  m_Data:
    characterRoot: {fileID: 2672218029476637041}
    skeletonRoot: {fileID: 1007241051937588470}
    skeletonLocalRotation: {x: 0, y: 0, z: 0, w: 1}
    spine: {fileID: 8325756444387802274}
    chest: {fileID: 5705661809941099012}
    viewYaw: 0
    pitch: 0
    followWeight: 0
    pitchWeight: 0
    waistFraction: 0.8
''')
sources = [7425657703796439005, 3404361196904951583, 3903271632730636390]
weights = '      m_Length: 3\n' + ''.join(f'      m_Item{i}:\n        transform: {{fileID: {sources[i] if i < 3 else 0}}}\n'
    f'        weight: {1 if i == 0 else 0}\n' for i in range(8))
s += mono(720000010, 720000007, parent_guid, '''  m_Weight: 1
  m_Data:
    m_ConstrainedObject: {fileID: 6154778328145528361}
    m_SourceObjects:
''' + weights + '''    m_ConstrainedPositionAxes: {x: 1, y: 1, z: 1}
    m_ConstrainedRotationAxes: {x: 1, y: 1, z: 1}
    m_MaintainPositionOffset: 0
    m_MaintainRotationOffset: 0
''')
s += mono(720000014, 720000011, hand_guid, '''  m_Weight: 0
  m_Data:
    m_Root: {fileID: 6466711385289847554}
    m_Mid: {fileID: 5937937697612863309}
    m_Tip: {fileID: 3998494049880919930}
    m_Target: {fileID: 7850900430847778050}
    m_Hint: {fileID: 720000016}
    m_TargetPositionWeight: 1
    m_TargetRotationWeight: 1
    m_HintWeight: 1
    m_MaintainTargetPositionOffset: 0
    m_MaintainTargetRotationOffset: 0
''')
s += node(720000015, 720000016, 'LeftElbowHint', 151728632950264630, pos=hint_local)
PREFAB.write_text(s, encoding='utf-8', newline='\n')

scene = ROOT / 'Assets/Scenes/Main.unity'
text = scene.read_text(encoding='utf-8-sig')
text, n = re.subn(r'    - target: \{fileID: 710000009, guid: f2d4f027e36129f4e9c02cfb0488b20d, type: 3\}\n'
                 r'      propertyPath: finalIkLayer\n      value: 1\n      objectReference: \{fileID: 0\}\n', '', text)
assert n == 1, 'Expected one obsolete IK layer override.'
scene.write_text(text, encoding='utf-8', newline='\n')
controller = ROOT / 'Assets/player/player.controller'
text = controller.read_text(encoding='utf-8-sig')
assert text.count('    m_IKPass: 1') == 1
controller.write_text(text.replace('    m_IKPass: 1', '    m_IKPass: 0'), encoding='utf-8', newline='\n')
print('Configured three ordered Rig layers, weapon sockets and left-hand chain.')
print('Elbow hint local position:', tuple(round(v, 5) for v in hint_local))
