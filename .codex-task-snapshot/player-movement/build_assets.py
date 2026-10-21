from pathlib import Path
import json
import re
import uuid

assets = Path(__file__).resolve().parents[2] / 'Assets'
scratch = Path(__file__).parent
cm = assets.parent / 'Library/PackageCache/com.unity.cinemachine@2.10.7'
guids = {}

def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8', newline='\n')

def meta(path, kind='DefaultImporter', folder=False):
    p = Path(str(path) + '.meta')
    if p.exists():
        guid = re.search(r'^guid: (\w+)', p.read_text(), re.M).group(1)
    else:
        guid = uuid.uuid4().hex
        extra = 'folderAsset: yes\n' if folder else ''
        if kind == 'MonoImporter':
            body = '  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n'
        else:
            body = '  externalObjects: {}\n'
        if kind == 'NativeFormatImporter':
            body += '  mainObjectFileID: 2100000\n'
        write(p, f'fileFormatVersion: 2\nguid: {guid}\n{extra}{kind}:\n{body}  userData: \n  assetBundleName: \n  assetBundleVariant: \n')
    guids[path.relative_to(assets).as_posix()] = guid
    return guid

def replace_once(s, old, new):
    assert s.count(old) == 1, (old[:100], s.count(old))
    return s.replace(old, new, 1)

def fields(s, values):
    for name, value in values.items():
        s, count = re.subn(r'^  '+re.escape(name)+r':[^\n]*$', '  '+name+': '+str(value), s, flags=re.M)
        assert count == 1, (name, count)
    return s

for rel in ['Scripts/PlayerMovement', 'player/Materials']:
    p = assets / rel
    p.mkdir(parents=True, exist_ok=True)
    meta(p, folder=True)
for rel in ['Scripts/PlayerMovement/PlayerMovement.cs', 'Scripts/PlayerMovement/PlayerCameraController.cs']:
    meta(assets / rel, 'MonoImporter')
meta(assets / 'Scripts/PlayerMovement/RPG4.PlayerMovement.asmdef', 'AssemblyDefinitionImporter')

# Extend only the player map. Preserve all existing action and binding identifiers.
path = assets / 'input/InputActions.inputactions'
original = path.read_text(encoding='utf-8-sig')
write(scratch / 'InputActions.inputactions.before', original)
data = json.loads(original)
player = next(m for m in data['maps'] if m['name'] == 'player')
for name, control, binding, processors in [
    ('look', 'Vector2', '<Mouse>/delta', ''),
    ('zoom', 'Axis', '<Mouse>/scroll/y', 'scale(factor=0.008333333)')]:
    assert not any(a['name'] == name for a in player['actions'])
    player['actions'].append(dict(name=name, type='Value', id=str(uuid.uuid4()),
        expectedControlType=control, processors='', interactions='', initialStateCheck=False))
    player['bindings'].append(dict(name='', id=str(uuid.uuid4()), path=binding,
        interactions='', processors=processors, groups='', action=name, isComposite=False, isPartOfComposite=False))
asset_json = json.dumps(data, indent=4, ensure_ascii=False)
write(path, asset_json + '\n')

# Synchronize the source-generated wrapper without launching Unity or running its importer.
path = assets / 'Scripts/Input/InputActions.cs'
s = path.read_text(encoding='utf-8-sig')
write(scratch / 'InputActions.cs.before', s)
s, count = re.subn(r'(?<=asset = InputActionAsset.FromJson\(@").*?(?="\);\n)',
    lambda _: asset_json.replace('"', '""'), s, count=1, flags=re.S)
assert count == 1
for template in [
    '            m_player_move = m_player.FindAction("move", throwIfNotFound: true);',
    '        private readonly InputAction m_player_move;',
    '            /// <summary>\n            /// Provides access to the underlying input action "player/move".\n            /// </summary>\n            public InputAction @move => m_Wrapper.m_player_move;',
    '                @move.started += instance.OnMove;\n                @move.performed += instance.OnMove;\n                @move.canceled += instance.OnMove;',
    '                @move.started -= instance.OnMove;\n                @move.performed -= instance.OnMove;\n                @move.canceled -= instance.OnMove;',
]:
    s = replace_once(s, template, template + '\n' + '\n'.join(
        template.replace('move', name).replace('OnMove', 'On'+name.title()) for name in ['look','zoom']))
interface = re.search(r'            /// <summary>\n            /// Method invoked when associated input action "move".*?            void OnMove\(InputAction.CallbackContext context\);', s, re.S).group(0)
s = replace_once(s, interface, interface + '\n' + '\n'.join(
    interface.replace('"move"', '"'+name+'"').replace('OnMove', 'On'+name.title()) for name in ['look','zoom']))
write(path, s)

# Convert the existing FBX variant while retaining its GUID and surviving component IDs.
path = assets / 'player/player.prefab'
s = path.read_text(encoding='utf-8-sig')
write(scratch / 'player.prefab.before', s)
s = replace_once(s, 'propertyPath: m_ApplyRootMotion\n      value: 1', 'propertyPath: m_ApplyRootMotion\n      value: 0')
s = replace_once(s, 'propertyPath: m_UpdateMode\n      value: 1', 'propertyPath: m_UpdateMode\n      value: 0')
source = '639b6ce73b347e5429ac0526c9c29094'
root_source = '919132149155446097'
transform_source = '-8679921383154817045'
target_go, target_transform, root_transform, animator, movement = 710000001,710000002,710000003,710000004,710000005
s = replace_once(s, '    m_RemovedComponents: []', f'''    - target: {{fileID: {root_source}, guid: {source}, type: 3}}
      propertyPath: m_TagString
      value: Player
      objectReference: {{fileID: 0}}
    m_RemovedComponents: []''')
s = replace_once(s, '    m_AddedGameObjects: []', f'''    m_AddedGameObjects:
    - targetCorrespondingSourceObject: {{fileID: {transform_source}, guid: {source}, type: 3}}
      insertIndex: -1
      addedObject: {{fileID: {target_transform}}}''')
s = re.sub(r'    - targetCorrespondingSourceObject: \{fileID: 919132149155446097, guid: '+source+r',\n        type: 3\}\n      insertIndex: -1\n      addedObject: \{fileID: 8169181711437512733\}\n', '', s, count=1)
s = replace_once(s, '  m_SourcePrefab:', f'''    - targetCorrespondingSourceObject: {{fileID: {root_source}, guid: {source}, type: 3}}
      insertIndex: -1
      addedObject: {{fileID: {movement}}}
  m_SourcePrefab:''')
s = re.sub(r'^--- !u!54 &8169181711437512733\n.*?(?=^--- !u!)', '', s, count=1, flags=re.M|re.S)
cc = '''--- !u!143 &9113369961250505187
CharacterController:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 8736259960116370302}
  m_Material: {fileID: 0}
  m_IncludeLayers:
    serializedVersion: 2
    m_Bits: 0
  m_ExcludeLayers:
    serializedVersion: 2
    m_Bits: 0
  m_LayerOverridePriority: 0
  m_IsTrigger: 0
  m_ProvidesContacts: 0
  m_Enabled: 1
  serializedVersion: 3
  m_Height: 2
  m_Radius: 0.3
  m_SlopeLimit: 45
  m_StepOffset: 0.3
  m_SkinWidth: 0.03
  m_MinMoveDistance: 0
  m_Center: {x: 0, y: 1, z: 0}
'''
s, count = re.subn(r'^--- !u!136 &9113369961250505187\n.*?(?=^--- !u!)', lambda _: cc, s, count=1, flags=re.M|re.S)
assert count == 1

def gameobject(go, transform, name, components=(), tag='Untagged', hide=0):
    return f'''--- !u!1 &{go}
GameObject:
  m_ObjectHideFlags: {hide}
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  serializedVersion: 6
  m_Component:
  - component: {{fileID: {transform}}}
''' + ''.join(f'  - component: {{fileID: {c}}}\n' for c in components) + f'''  m_Layer: 0
  m_Name: {name}
  m_TagString: {tag}
  m_Icon: {{fileID: 0}}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
'''

def transform(t, go, parent=0, children=(), position=(0,0,0), scale=(1,1,1)):
    return f'''--- !u!4 &{t}
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {go}}}
  serializedVersion: 2
  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}
  m_LocalPosition: {{x: {position[0]}, y: {position[1]}, z: {position[2]}}}
  m_LocalScale: {{x: {scale[0]}, y: {scale[1]}, z: {scale[2]}}}
  m_ConstrainProportionsScale: 0
''' + ('  m_Children:\n' + ''.join(f'  - {{fileID: {c}}}\n' for c in children) if children else '  m_Children: []\n') + f'''  m_Father: {{fileID: {parent}}}
  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}
'''

def mono(i, go, script, content=''):
    return f'''--- !u!114 &{i}
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
''' + content

s += f'''--- !u!4 &{root_transform} stripped
Transform:
  m_CorrespondingSourceObject: {{fileID: {transform_source}, guid: {source}, type: 3}}
  m_PrefabInstance: {{fileID: 8501678942199572015}}
  m_PrefabAsset: {{fileID: 0}}
--- !u!95 &{animator} stripped
Animator:
  m_CorrespondingSourceObject: {{fileID: 5866666021909216657, guid: {source}, type: 3}}
  m_PrefabInstance: {{fileID: 8501678942199572015}}
  m_PrefabAsset: {{fileID: 0}}
'''
s += gameobject(target_go, target_transform, 'CameraTarget')
s += transform(target_transform, target_go, root_transform, position=(0,1.5,0))
s += mono(movement, 8736259960116370302, guids['Scripts/PlayerMovement/PlayerMovement.cs'], f'''  cameraTransform: {{fileID: 0}}
  animator: {{fileID: {animator}}}
  moveSpeed: 4
  turnSmoothTime: 0.12
  gravity: -25
''')
write(path, s)

# Reuse the package's serialized FreeLook rig layout, stripping all scene dependencies.
s = (scratch/'camera-source.yaml').read_text()
s = s.replace('m_PrefabParentObject:', 'm_CorrespondingSourceObject:').replace('m_PrefabInternal: {fileID: 0}', 'm_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}')
s = s.replace('m_StaticEditorFlags:', 'm_StaticEditorFlags:')
s = s.replace('m_InvertAxis:', 'm_InvertInput:').replace('m_HeadingDefinition:', 'm_Definition:').replace('m_HeadingBias:', 'm_Bias:')
s = s.replace('  m_BindingMode: 5', '  m_BindingMode: 4').replace('    m_Bias: 180', '    m_Bias: 0')
blocks = re.split(r'(?=^--- !u!)', s, flags=re.M)
for i,b in enumerate(blocks):
    if 'guid: f4044717213e31446939f7bd49c896ea' in b:
        blocks[i] = fields(b, dict(m_HorizontalDamping=0, m_VerticalDamping=0, m_ScreenX=0.5, m_ScreenY=0.5,
            m_DeadZoneWidth=0, m_DeadZoneHeight=0, m_SoftZoneWidth=0.8, m_SoftZoneHeight=0.8, m_BiasX=0, m_BiasY=0))
    elif 'guid: 9384ab8608cdc3d479fe89cd51eed48f' in b:
        blocks[i] = fields(b, dict(m_XDamping=0.2, m_YDamping=0.2, m_ZDamping=0.2))
    elif 'guid: ac0b09e7857660247b1477e93731de29' in b:
        # No noise profile is used; the package still expects this pipeline component.
        blocks[i] = b
    elif 'guid: 319d2fe34a804e245819465c9505ea59' in b:
        b = fields(b, dict(m_LookAt='{fileID: 0}', m_Follow='{fileID: 0}', m_SplineCurvature=0.2))
        b = b.replace('FieldOfView: 50','FieldOfView: 60').replace('FarClipPlane: 5000','FarClipPlane: 1000')
        start=b.index('  m_YAxis:');end=b.index('  m_Heading:')
        b = b[:start]+'''  m_YAxis:
    Value: 0.5
    m_SpeedMode: 1
    m_MaxSpeed: 0.0025
    m_AccelTime: 0
    m_DecelTime: 0
    m_InputAxisName: 
    m_InputAxisValue: 0
    m_InvertInput: 1
    m_MinValue: 0
    m_MaxValue: 1
    m_Wrap: 0
  m_YAxisRecentering:
    m_enabled: 0
    m_WaitTime: 1
    m_RecenteringTime: 2
  m_XAxis:
    Value: 0
    m_SpeedMode: 1
    m_MaxSpeed: 0.15
    m_AccelTime: 0
    m_DecelTime: 0
    m_InputAxisName: 
    m_InputAxisValue: 0
    m_InvertInput: 0
    m_MinValue: -180
    m_MaxValue: 180
    m_Wrap: 1
''' + b[end:]
        b = re.sub(r'  m_Orbits:\n(?:  - m_Height:.*\n    m_Radius:.*\n){3}', '''  m_Orbits:
  - m_Height: 3.3
    m_Radius: 2.3
  - m_Height: 1
    m_Radius: 4
  - m_Height: -1.1
    m_Radius: 3.6
''', b)
        blocks[i] = b
    elif b.startswith('--- !u!1 &1956070571\n'):
        blocks[i] = gameobject(1956070571,1956070573,'Player FreeLook',(1956070572,1956070574,1956070575))
    elif b.startswith('--- !u!4 &1956070573\n'):
        b = fields(b,dict(m_Father='{fileID: 1001}',m_LocalPosition='{x: 0, y: 2.5, z: -4}'))
        blocks[i] = b
s=''.join(blocks)
s += mono(1956070574,1956070571,guids['Scripts/PlayerMovement/PlayerCameraController.cs'],'''  freeLook: {fileID: 1956070572}
  minimumDistance: 2
  maximumDistance: 6
  zoomSpeed: 0.5
''')
s += mono(1956070575,1956070571,'e501d18bb52cf8c40b1853ca4904654f', '''  m_CollideAgainst:
    serializedVersion: 2
    m_Bits: 1
  m_IgnoreTag: Player
  m_TransparentLayers:
    serializedVersion: 2
    m_Bits: 0
  m_MinimumDistanceFromTarget: 0.1
  m_AvoidObstacles: 1
  m_DistanceLimit: 0
  m_MinimumOcclusionTime: 0
  m_CameraRadius: 0.2
  m_Strategy: 0
  m_MaximumEffort: 4
  m_SmoothingTime: 0.05
  m_Damping: 0.35
  m_DampingWhenOccluded: 0
  m_OptimalTargetDistance: 0
''')

# The real camera uses the project's existing URP configuration, with Brain added.
sample=(assets/'Scenes/SampleScene.unity').read_text(encoding='utf-8-sig')
scene_blocks=re.split(r'(?=^--- !u!)',sample,flags=re.M)
for b in scene_blocks:
    if re.search(r'^  m_GameObject: \{fileID: 330585543\}',b,re.M) or b.startswith('--- !u!1 &330585543\n'):
        if b.startswith('--- !u!1 &'):
            b=gameobject(330585543,330585546,'Main Camera',(330585545,330585544,330585547,330585548),'MainCamera')
        elif b.startswith('--- !u!4'):
            b=transform(330585546,330585543,1001,position=(0,2.5,-4))
        elif b.startswith('--- !u!20'):
            b=b.replace('near clip plane: 0.3','near clip plane: 0.1')
        s+=b
s += mono(330585548,330585543,'72ece51f2901e7445ab60da3685d6b5f', '''  m_ShowDebugText: 0
  m_ShowCameraFrustum: 1
  m_IgnoreTimeScale: 0
  m_WorldUpOverride: {fileID: 0}
  m_UpdateMethod: 2
  m_BlendUpdateMethod: 1
  m_DefaultBlend:
    m_Style: 1
    m_Time: 0.3
  m_CustomBlends: {fileID: 0}
  m_CameraCutEvent:
    m_PersistentCalls:
      m_Calls: []
  m_CameraActivatedEvent:
    m_PersistentCalls:
      m_Calls: []
''')
s += gameobject(1000,1001,'PlayerCamera')+transform(1001,1000,children=(330585546,1956070573))
path=assets/'player/PlayerCamera.prefab'
write(path,s);camera_guid=meta(path,'PrefabImporter')

# A separate ready-to-open scene keeps the existing backpack scenes untouched.
scene='%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n'
for b in scene_blocks:
    if any(b.startswith('--- !u!'+str(k)+' &') for k in [29,104,157,196]):
        scene+=b
    elif '{fileID: 410087039}' in b or b.startswith('--- !u!1 &410087039\n'):
        # Directional light and its URP light data in SampleScene.
        scene+=b

def prefab_instance(i,guid,mods=''):
    return f'''--- !u!1001 &{i}
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {{fileID: 0}}
    m_Modifications:
{mods}    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {{fileID: 100100000, guid: {guid}, type: 3}}
'''

def mod(fileid,guid,property,value='',reference=0):
    return f'''    - target: {{fileID: {fileid}, guid: {guid}, type: 3}}
      propertyPath: {property}
      value: {value}
      objectReference: {{fileID: {reference}}}
'''

def stripped(kind,i,sourceid,guid,instance):
    name={4:'Transform',114:'MonoBehaviour'}[kind]
    return f'''--- !u!{kind} &{i} stripped
{name}:
  m_CorrespondingSourceObject: {{fileID: {sourceid}, guid: {guid}, type: 3}}
  m_PrefabInstance: {{fileID: {instance}}}
  m_PrefabAsset: {{fileID: 0}}
'''

player_guid=meta(assets/'player/player.prefab','PrefabImporter')
scene+=prefab_instance(10000,player_guid,mod(movement,player_guid,'cameraTransform',reference=11001))
scene+=stripped(4,10001,target_transform,player_guid,10000)
scene+=prefab_instance(11000,camera_guid,mod(1956070572,camera_guid,'m_Follow',reference=10001)+mod(1956070572,camera_guid,'m_LookAt',reference=10001))
scene+=stripped(4,11001,330585546,camera_guid,11000)
scene+=gameobject(12000,12001,'InputManager',(12002,))+transform(12001,12000)
scene+=mono(12002,12000,'c79eac3e7a4554d438d5b68c64a2ec6e')

for name,color in [('MovementGround',(0.28,0.35,0.4)),('MovementObstacle',(0.62,0.48,0.3))]:
    path=assets/f'player/Materials/{name}.mat'
    write(path,f'''%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!21 &2100000
Material:
  serializedVersion: 8
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name: {name}
  m_Shader: {{fileID: 4800000, guid: 933532a4fcc9baf4fa0491de14d08ed7, type: 3}}
  m_ValidKeywords: []
  m_InvalidKeywords: []
  m_LightmapFlags: 4
  m_EnableInstancingVariants: 0
  m_DoubleSidedGI: 0
  m_CustomRenderQueue: -1
  stringTagMap:
    RenderType: Opaque
  disabledShaderPasses: []
  m_SavedProperties:
    serializedVersion: 3
    m_TexEnvs: []
    m_Ints: []
    m_Floats:
    - _Surface: 0
    - _Cull: 2
    - _ZWrite: 1
    - _Smoothness: 0.2
    m_Colors:
    - _BaseColor: {{r: {color[0]}, g: {color[1]}, b: {color[2]}, a: 1}}
  m_BuildTextureStacks: []
''')
    meta(path,'NativeFormatImporter')

def cube(i,name,pos,size,material):
    global scene
    scene+=gameobject(i,i+1,name,(i+2,i+3,i+4))+transform(i+1,i,position=pos,scale=size)
    scene+=f'''--- !u!33 &{i+2}
MeshFilter:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {i}}}
  m_Mesh: {{fileID: 10202, guid: 0000000000000000e000000000000000, type: 0}}
--- !u!23 &{i+3}
MeshRenderer:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {i}}}
  m_Enabled: 1
  m_CastShadows: 1
  m_ReceiveShadows: 1
  m_DynamicOccludee: 1
  m_StaticShadowCaster: 0
  m_MotionVectors: 1
  m_LightProbeUsage: 1
  m_ReflectionProbeUsage: 1
  m_RayTracingMode: 2
  m_RayTraceProcedural: 0
  m_RenderingLayerMask: 1
  m_RendererPriority: 0
  m_Materials:
  - {{fileID: 2100000, guid: {guids['player/Materials/'+material+'.mat']}, type: 2}}
  m_StaticBatchInfo:
    firstSubMesh: 0
    subMeshCount: 0
  m_StaticBatchRoot: {{fileID: 0}}
  m_ProbeAnchor: {{fileID: 0}}
  m_LightProbeVolumeOverride: {{fileID: 0}}
  m_ScaleInLightmap: 1
  m_ReceiveGI: 1
  m_PreserveUVs: 0
  m_IgnoreNormalsForChartDetection: 0
  m_ImportantGI: 0
  m_StitchLightmapSeams: 1
  m_SelectedEditorRenderState: 3
  m_MinimumChartSize: 4
  m_AutoUVMaxDistance: 0.5
  m_AutoUVMaxAngle: 89
  m_LightmapParameters: {{fileID: 0}}
  m_SortingLayerID: 0
  m_SortingLayer: 0
  m_SortingOrder: 0
  m_AdditionalVertexStreams: {{fileID: 0}}
--- !u!65 &{i+4}
BoxCollider:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: {i}}}
  m_Material: {{fileID: 0}}
  m_IsTrigger: 0
  m_Enabled: 1
  serializedVersion: 3
  m_Size: {{x: 1, y: 1, z: 1}}
  m_Center: {{x: 0, y: 0, z: 0}}
'''

cube(13000,'Ground',(0,-0.25,0),(30,0.5,30),'MovementGround')
cube(14000,'Camera Occlusion Wall',(0,1.5,-7),(10,3,0.5),'MovementObstacle')
cube(15000,'Collision Block',(4,1,2),(2,2,2),'MovementObstacle')
cube(16000,'Low Step',(-4,0.1,2),(2,0.2,2),'MovementObstacle')
path=assets/'Scenes/PlayerMovement.unity'
write(path,scene);meta(path)
write(scratch/'guids.json',json.dumps(guids,indent=2))
print('Updated player input and generated wrapper; configured player, camera prefab, and independent movement scene.')
