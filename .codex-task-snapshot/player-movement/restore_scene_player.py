from pathlib import Path
import re
import sys

scratch = Path(__file__).parent
assets = scratch.parents[1] / 'Assets'
sys.path.insert(0, str(scratch / 'pydeps'))
import yaml

path = assets / 'backpack/Scenes/New Scene.unity'
original = path.read_bytes()
backup = scratch / 'New Scene.unity.before-player-restore-20261022'
assert not backup.exists(), 'This scene restoration has already been applied.'
newline = '\r\n' if b'\r\n' in original else '\n'
s = original.decode('utf-8-sig').replace('\r\n', '\n')

def parse(text):
    result = {}
    for block in re.split(r'(?=^--- !u!)', text, flags=re.M)[1:]:
        header, body = block.split('\n', 1)
        i = int(re.search(r'&(-?\d+)', header).group(1))
        assert i not in result
        result[i] = yaml.safe_load(body)
    return result

def guid(path):
    return re.search(r'^guid: (\w+)', Path(str(path)+'.meta').read_text(), re.M).group(1)

before = parse(s)
player_guid = guid(assets / 'player/player.prefab')
camera_guid = guid(assets / 'player/PlayerCamera.prefab')
assert not any(obj.get('PrefabInstance', {}).get('m_SourcePrefab', {}).get('guid') == player_guid for obj in before.values())
player_instance = 7331079584931666036
camera_instance = 629847864733125438
camera_transform, camera_component, target = 740000001, 740000002, 740000003
assert all(i not in before for i in [player_instance, camera_transform, camera_component, target])
assert before[camera_instance]['PrefabInstance']['m_SourcePrefab']['guid'] == camera_guid

pattern = r'^--- !u!1001 &'+str(camera_instance)+r'\n.*?(?=^--- !u!|\Z)'
camera = re.search(pattern, s, re.M | re.S).group(0)
for field in ['m_Follow', 'm_LookAt']:
    camera, count = re.subn(r'(propertyPath: '+field+r'\n      value:[^\n]*\n      objectReference: )\{fileID: 0\}',
        lambda m: m.group(1)+'{fileID: '+str(target)+'}', camera, count=1)
    assert count == 1
s = re.sub(pattern, lambda _: camera, s, count=1, flags=re.M | re.S)

def override(source, field, value='', reference=0):
    return f'''    - target: {{fileID: {source}, guid: {player_guid}, type: 3}}
      propertyPath: {field}
      value:{' '+str(value) if value != '' else ''}
      objectReference: {{fileID: {reference}}}
'''

added = f'''--- !u!1001 &{player_instance}
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {{fileID: 0}}
    m_Modifications:
'''
# Bind the inherited root explicitly through the root Transform in the current prefab.
for axis in ['x', 'y', 'z']:
    added += override(710000003, 'm_LocalPosition.'+axis, 0)
for axis, value in [('x', 0), ('y', 0), ('z', 0), ('w', 1)]:
    added += override(710000003, 'm_LocalRotation.'+axis, value)
added += override(710000005, 'cameraTransform', reference=camera_transform)
added += override(610000000000000001, 'interactionCamera', reference=camera_component)
added += f'''    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {{fileID: 100100000, guid: {player_guid}, type: 3}}
'''
for kind, name, i, source, source_guid, instance in [
    (4, 'Transform', camera_transform, 330585546, camera_guid, camera_instance),
    (20, 'Camera', camera_component, 330585545, camera_guid, camera_instance),
    (4, 'Transform', target, 710000002, player_guid, player_instance),
]:
    added += f'''--- !u!{kind} &{i} stripped
{name}:
  m_CorrespondingSourceObject: {{fileID: {source}, guid: {source_guid}, type: 3}}
  m_PrefabInstance: {{fileID: {instance}}}
  m_PrefabAsset: {{fileID: 0}}
'''

root_header = '--- !u!1660057539 &9223372036854775807\n'
assert s.count(root_header) == 1
s = s.replace(root_header, added + root_header, 1)
assert s.endswith('\n')
s += f'  - {{fileID: {player_instance}}}\n'

after = parse(s)
changed = {i for i in before if before[i] != after[i]}
assert changed == {camera_instance, 9223372036854775807}
assert set(after)-set(before) == {player_instance, camera_transform, camera_component, target}
before_camera = before[camera_instance]['PrefabInstance']['m_Modification']['m_Modifications']
after_camera = after[camera_instance]['PrefabInstance']['m_Modification']['m_Modifications']
assert len(before_camera) == len(after_camera)
for a, b in zip(before_camera, after_camera):
    if a['propertyPath'] in ['m_Follow', 'm_LookAt']:
        expected = dict(a, objectReference={'fileID': target})
        assert expected == b
    else:
        assert a == b
backup.write_bytes(original)
path.write_bytes(s.replace('\n', newline).encode('utf-8'))
print('Restored Player at origin; bound FreeLook Follow/LookAt, movement camera and interaction camera. All existing camera tuning and scene layout preserved.')
