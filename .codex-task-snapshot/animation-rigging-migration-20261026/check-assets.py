from pathlib import Path
import difflib
import re
import yaml

ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).parent / 'baseline'

class UniqueLoader(yaml.SafeLoader):
    pass

def mapping(loader, node, deep=False):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        assert key not in result, f'Duplicate YAML key: {key}'
        result[key] = loader.construct_object(value_node, deep=deep)
    return result

UniqueLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)

def load(path):
    text = (ROOT / path).read_text(encoding='utf-8-sig')
    result = {}
    for m in re.finditer(r'^--- !u!(\d+) &(-?\d+)[^\n]*\n(.*?)(?=^--- !u!|\Z)', text, re.M | re.S):
        fid = int(m[2])
        assert fid not in result, f'Duplicate fileID in {path}: {fid}'
        result[fid] = yaml.load(m[3], Loader=UniqueLoader)
    return text, result

text, objects = load('Assets/player/player.prefab')
_, scene = load('Assets/Scenes/Main.unity')
_, controller = load('Assets/player/player.controller')
for m in re.finditer(r'\{fileID: (-?\d+)\}', text):
    fid = int(m[1])
    assert fid == 0 or fid in objects, f'Unresolved local fileID: {fid}'

def component(fid):
    return objects[fid]['MonoBehaviour']

def parent(fid):
    return objects[fid]['Transform'].get('m_Father', {}).get('fileID', 0)

def descendant(child, ancestor):
    seen = set()
    while child and child != ancestor:
        assert child not in seen, 'Transform cycle'
        seen.add(child)
        child = parent(child)
    return child == ancestor

for fid, data in objects.items():
    if 'Transform' in data and 'm_Father' in data['Transform']:
        p = parent(fid)
        if p:
            assert {'fileID': fid} in objects[p]['Transform']['m_Children'], f'Missing reciprocal parent link: {fid}'
    if 'GameObject' in data:
        for ref in data['GameObject']['m_Component']:
            body = next(iter(objects[ref['component']['fileID']].values()))
            assert body['m_GameObject']['fileID'] == fid, 'Component owner mismatch'

root = 2672218029476637041
assert parent(720000002) == root
builder = component(720000000)
assert [layer['m_Rig']['fileID'] for layer in builder['m_RigLayers']] == [720000005,720000009,720000013]
assert all(layer['m_Active'] for layer in builder['m_RigLayers'])
for rig_id, constraint_id in [(720000005,720000006),(720000009,720000010),(720000013,720000014)]:
    assert component(rig_id)['m_GameObject'] == component(constraint_id)['m_GameObject']

body = component(720000006)['m_Data']
assert body['characterRoot']['fileID'] == root
assert parent(body['skeletonRoot']['fileID']) == root
assert descendant(body['spine']['fileID'],body['skeletonRoot']['fileID'])
assert descendant(body['chest']['fileID'],body['spine']['fileID'])
assert body['skeletonLocalRotation'] == {'x':0,'y':0,'z':0,'w':1}
assert body['waistFraction'] == 0.8
hand = component(720000014)['m_Data']
assert parent(hand['m_Mid']['fileID']) == hand['m_Root']['fileID']
assert parent(hand['m_Tip']['fileID']) == hand['m_Mid']['fileID']
assert hand['m_Target']['fileID'] == 7850900430847778050
assert descendant(hand['m_Hint']['fileID'],body['chest']['fileID'])
sources = component(720000010)['m_Data']['m_SourceObjects']
assert sources['m_Length'] == 3
assert [sources[f'm_Item{i}']['weight'] for i in range(3)] == [1,0,0]
assert all(sources[f'm_Item{i}']['transform']['fileID'] in objects for i in range(3))
assert component(5612448861134227116)['weaponSocket']['fileID'] == 720000010
assert component(710000009)['bodyAim']['fileID'] == 720000006
assert component(710000009)['waistFraction'] == 0.8
legacy = objects[464032024294814340]['PrefabInstance']['m_Modification']['m_Modifications']
for field in ['m_Enabled','m_Active']:
    assert any(m['target']['fileID']==6093372014242540394 and m['propertyPath']==field and str(m['value'])=='0' for m in legacy)
assert 'guid: dabaf2f856c5ede49beb8a6f964f4422' not in text
assert 'finalIkLayer' not in (ROOT/'Assets/Scenes/Main.unity').read_text(encoding='utf-8-sig')
for value in controller.values():
    if 'AnimatorController' in value:
        assert all(layer['m_IKPass']==0 for layer in value['AnimatorController']['m_AnimatorLayers'])

runtime = ROOT/'Library/PackageCache/com.unity.animation.rigging@1.2.1/Runtime/AnimationRig'
bindings = {
    720000000: runtime/'RigBuilder.cs.meta',
    720000005: runtime/'Rig.cs.meta',
    720000006: ROOT/'Assets/Scripts/PlayerMovement/PlayerBodyAimConstraint.cs.meta',
    720000010: runtime/'Constraints/MultiParentConstraint.cs.meta',
    720000014: runtime/'Constraints/TwoBoneIKConstraint.cs.meta',
    5612448861134227116: ROOT/'Assets/Scripts/PlayerMovement/PlayerWeaponRig.cs.meta',
}
for fid, meta in bindings.items():
    guid = re.search(r'^guid: (\w+)',meta.read_text(encoding='utf-8-sig'),re.M)[1]
    assert component(fid)['m_Script']['guid'] == guid, 'Script GUID mismatch'

# Review only this task's additions, because the checkout already contains user changes.
changed = []
for baseline in BASE.rglob('*'):
    if not baseline.is_file(): continue
    path = baseline.relative_to(BASE)
    old = baseline.read_text(encoding='utf-8-sig').splitlines()
    new = (ROOT/path).read_text(encoding='utf-8-sig').splitlines()
    if old == new: continue
    changed.append(str(path))
    for line in difflib.unified_diff(old,new):
        if line.startswith('+') and not line.startswith('+++'):
            assert line.rstrip() == line, f'Trailing whitespace added by this task: {path}'
print(f'PASS: parsed prefab ({len(objects)} objects), scene and Animator YAML.')
print('PASS: Rig order, reciprocal hierarchy, arm chain, targets, script GUIDs and legacy disablement.')
print('PASS: no unresolved local references, duplicate IDs/keys or whitespace in task additions.')
print('Modified existing files:',len(changed))
