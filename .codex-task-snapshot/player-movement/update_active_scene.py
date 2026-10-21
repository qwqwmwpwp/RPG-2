from pathlib import Path
import re

scratch = Path(__file__).parent
assets = scratch.parents[1] / 'Assets'
path = assets / 'backpack/Scenes/New Scene.unity'
original = path.read_bytes()
backup = scratch / 'New Scene.unity.before-camera-update'
assert not backup.exists(), 'The current-scene update has already been applied.'
backup.write_bytes(original)
newline = '\r\n' if b'\r\n' in original else '\n'
s = original.decode('utf-8-sig').replace('\r\n', '\n')

def guid(path):
    return re.search(r'^guid: (\w+)', Path(str(path)+'.meta').read_text(), re.M).group(1)

def override(source_id, source_guid, field, reference):
    return f'''    - target: {{fileID: {source_id}, guid: {source_guid}, type: 3}}
      propertyPath: {field}
      value: 
      objectReference: {{fileID: {reference}}}
'''

camera_guid = guid(assets / 'player/PlayerCamera.prefab')
player_guid = guid(assets / 'player/player.prefab')
camera_instance = 629847864733125438
player_instance = 7331079584931666036
camera_transform, camera_component, target = 740000001, 740000002, 740000003
for i in [camera_transform, camera_component, target]:
    assert not re.search(r'^--- !u!\d+ &'+str(i)+r'\b', s, re.M)

camera = f'''--- !u!1001 &{camera_instance}
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {{fileID: 0}}
    m_Modifications:
''' + override(1956070572, camera_guid, 'm_Follow', target) + override(1956070572, camera_guid, 'm_LookAt', target) + f'''    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {{fileID: 100100000, guid: {camera_guid}, type: 3}}
'''
s, count = re.subn(r'^--- !u!1001 &'+str(camera_instance)+r'\n.*?(?=^--- !u!|\Z)', lambda _: camera, s, count=1, flags=re.M|re.S)
assert count == 1

pattern = r'^--- !u!1001 &'+str(player_instance)+r'\n.*?(?=^--- !u!|\Z)'
player = re.search(pattern, s, re.M|re.S).group(0)
assert player_guid in player
assert 'propertyPath: cameraTransform' not in player
added = override(710000005, player_guid, 'cameraTransform', camera_transform)
added += override(610000000000000001, player_guid, 'interactionCamera', camera_component)
player = player.replace('    m_RemovedComponents:', added+'    m_RemovedComponents:', 1)
s, count = re.subn(pattern, lambda _: player, s, count=1, flags=re.M|re.S)
assert count == 1

# Keep the same scene roots and prefab instance IDs; bind only the required component references.
stripped = ''
for kind, name, local_id, source_id, source_guid, instance in [
    (4, 'Transform', camera_transform, 330585546, camera_guid, camera_instance),
    (20, 'Camera', camera_component, 330585545, camera_guid, camera_instance),
    (4, 'Transform', target, 710000002, player_guid, player_instance),
]:
    stripped += f'''--- !u!{kind} &{local_id} stripped
{name}:
  m_CorrespondingSourceObject: {{fileID: {source_id}, guid: {source_guid}, type: 3}}
  m_PrefabInstance: {{fileID: {instance}}}
  m_PrefabAsset: {{fileID: 0}}
'''
root_header = '--- !u!1660057539 &9223372036854775807\n'
assert s.count(root_header) == 1
s = s.replace(root_header, stripped+root_header, 1)

pattern = r'^--- !u!4 &696164342\n.*?(?=^--- !u!|\Z)'
plane = re.search(pattern, s, re.M|re.S).group(0)
assert '  m_LocalScale: {x: 1, y: 1, z: 1}' in plane
plane = plane.replace('  m_LocalScale: {x: 1, y: 1, z: 1}', '  m_LocalScale: {x: 3, y: 1, z: 3}', 1)
s = re.sub(pattern, lambda _: plane, s, count=1, flags=re.M|re.S)
path.write_bytes(s.replace('\n', newline).encode('utf-8'))
print('Updated active scene: Cinemachine instance, Follow/LookAt, movement and interaction camera references, ground 30 x 30 metres.')
