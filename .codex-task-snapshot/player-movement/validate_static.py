from pathlib import Path
import json
import re
import sys

scratch=Path(__file__).parent
assets=scratch.parents[1]/'Assets'
sys.path.insert(0,str(scratch/'pydeps'))
import yaml

def load(path):
    text=path.read_text(encoding='utf-8-sig')
    docs={}
    for raw in re.split(r'(?=^--- !u!)',text,flags=re.M)[1:]:
        header,body=raw.split('\n',1)
        i=int(re.search(r'&(-?\d+)',header).group(1))
        assert i not in docs, (path,'duplicate fileID',i)
        docs[i]=yaml.safe_load(body)
    def walk(node):
        if isinstance(node,dict):
            if node.get('fileID') and 'guid' not in node:
                assert node['fileID'] in docs, (path,'unresolved local reference',node)
            for v in node.values():walk(v)
        elif isinstance(node,list):
            for v in node:walk(v)
    for obj in docs.values():walk(obj)
    return docs

prefabs={p:load(assets/p) for p in ['player/player.prefab','player/PlayerCamera.prefab','Scenes/PlayerMovement.unity']}
player=prefabs['player/player.prefab'];cam=prefabs['player/PlayerCamera.prefab'];scene=prefabs['Scenes/PlayerMovement.unity']
assert not any('Rigidbody' in o or 'CapsuleCollider' in o for o in player.values())
assert player[9113369961250505187]['CharacterController']['m_Height']==2
assert player[710000002]['Transform']['m_Father']['fileID']==710000003
assert player[710000005]['MonoBehaviour']['animator']['fileID']==710000004
mods=player[8501678942199572015]['PrefabInstance']['m_Modification']['m_Modifications']
assert next(m for m in mods if m['propertyPath']=='m_ApplyRootMotion')['value']==0
assert next(m for m in mods if m['propertyPath']=='m_UpdateMode')['value']==0
assert next(m for m in mods if m['propertyPath']=='m_TagString')['value']=='Player'
fl=cam[1956070572]['MonoBehaviour']
assert fl['m_BindingMode']==4
assert len(fl['m_Rigs'])==3 and len(fl['m_Orbits'])==3
assert fl['m_YAxisRecentering']['m_enabled']==0
assert fl['m_RecenterToTargetHeading']['m_enabled']==0
for axis in ['m_XAxis','m_YAxis']:
    assert fl[axis]['m_SpeedMode']==1 and fl[axis]['m_InputAxisName'] is None
assert cam[330585548]['MonoBehaviour']['m_UpdateMethod']==2
assert cam[1956070575]['MonoBehaviour']['m_Strategy']==0
assert cam[1956070575]['MonoBehaviour']['m_IgnoreTag']=='Player'
assert cam[330585545]['Camera']['near clip plane']==0.1
assert len([o for o in cam.values() if 'Camera' in o])==1
assert 'Light' in scene[410087040]

# Check each scene prefab override against the actual prefab object and field.
for i in [10000,11000]:
    inst=scene[i]['PrefabInstance'];guid=inst['m_SourcePrefab']['guid']
    source=player if i==10000 else cam
    for m in inst['m_Modification']['m_Modifications']:
        assert m['target']['guid']==guid
        obj=next(iter(source[m['target']['fileID']].values()))
        assert m['propertyPath'] in obj
assert scene[10001]['Transform']['m_CorrespondingSourceObject']['fileID']==710000002
assert scene[11001]['Transform']['m_CorrespondingSourceObject']['fileID']==330585546

data=json.loads((assets/'input/InputActions.inputactions').read_text())
original=json.loads((scratch/'InputActions.inputactions.before').read_text())
assert data['maps'][0]==original['maps'][0]
assert data['maps'][1]['actions'][:1]==original['maps'][1]['actions']
assert data['maps'][1]['bindings'][:5]==original['maps'][1]['bindings']
wrapper=(assets/'Scripts/Input/InputActions.cs').read_text()
embedded=re.search(r'InputActionAsset.FromJson\(@"(.*?)"\);',wrapper,re.S).group(1).replace('""','"')
assert json.loads(embedded)==data
for action in data['maps'][1]['actions']:
    name=action['name'];callback='On'+name.title()
    assert f'public InputAction @{name} =>' in wrapper
    assert f'private readonly InputAction m_player_{name};' in wrapper
    assert f'void {callback}(InputAction.CallbackContext context);' in wrapper
    for phase in ['started','performed','canceled']:
        for op in ['+=','-=']:
            assert f'@{name}.{phase} {op} instance.{callback};' in wrapper

guid_paths={}
needed=set()
for rel in prefabs:
    needed.update(re.findall(r'guid: ([0-9a-f]{32})',(assets/rel).read_text()))
for p in (assets/'player/Materials').glob('*.mat'):
    load(p)
    needed.update(re.findall(r'guid: ([0-9a-f]{32})',p.read_text()))
needed={g for g in needed if not g.startswith('0000000000000000')}
package=assets.parent/'Library/PackageCache'
for root in [assets,package/'com.unity.cinemachine@2.10.7',package/'com.unity.render-pipelines.universal@14.0.12']:
    for p in root.rglob('*.meta'):
        m=re.search(r'^guid: ([0-9a-f]{32})',p.read_text(encoding='utf-8-sig',errors='replace'),re.M)
        if m and m.group(1) in needed:
            assert m.group(1) not in guid_paths,('duplicate GUID',p,guid_paths[m.group(1)])
            guid_paths[m.group(1)]=p
assert needed==set(guid_paths),('unresolved GUIDs',needed-set(guid_paths))
for root in [assets/'Scripts/PlayerMovement',assets/'player/Materials']:
    for p in [root,*root.iterdir()]:
        if p.suffix!='.meta':assert Path(str(p)+'.meta').exists(),('missing .meta',p)
asm=json.loads((assets/'Scripts/PlayerMovement/RPG4.PlayerMovement.asmdef').read_text())
assert set(asm['references'])=={'RPG4.Input','Unity.InputSystem','Cinemachine'}
print('PASS: YAML and local fileIDs; player controller/Animator; FreeLook/Brain/Collider; scene prefab overrides; shared input asset/wrapper; '+str(len(needed))+' external GUIDs; assembly references and meta files.')
print('Documents:',{p:len(docs) for p,docs in prefabs.items()})
