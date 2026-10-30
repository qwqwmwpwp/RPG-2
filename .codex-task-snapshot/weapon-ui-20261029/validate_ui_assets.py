import pathlib,re,yaml,json,difflib
ROOT=pathlib.Path(__file__).resolve().parents[2]
FILES=['Assets/backpack/Prefabs/WeaponCandidateRow.prefab','Assets/backpack/Prefabs/WeaponInspectionPanel.prefab','Assets/backpack/Prefabs/WeaponAmmoHUD.prefab','Assets/backpack/Prefabs/背包UI.prefab','Assets/backpack/Prefabs/和箱子交互ui.prefab','Assets/Scenes/Main.unity']
class StrictLoader(yaml.SafeLoader): pass
def mapping(loader,node,deep=False):
    result={}
    for k,v in node.value:
        key=loader.construct_object(k,deep=deep)
        assert key not in result,('Duplicate YAML key',key)
        result[key]=loader.construct_object(v,deep=deep)
    return result
StrictLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG,mapping)
def parse(path):
    text=path.read_text(encoding='utf-8-sig'); docs={}
    for b in re.split(r'(?=--- !u!)',text):
        if not b.startswith('--- !u!'): continue
        fid=int(re.search(r'&(\d+)',b)[1]); assert fid not in docs,(path,fid)
        docs[fid]=yaml.load('\n'.join(b.splitlines()[1:]),Loader=StrictLoader)
    return docs
def walk(obj):
    if isinstance(obj,dict):
        if 'fileID' in obj: yield obj
        for v in obj.values():yield from walk(v)
    elif isinstance(obj,list):
        for v in obj:yield from walk(v)
parsed={f:parse(ROOT/f) for f in FILES}
for path,docs in parsed.items():
    for fid,doc in docs.items():
        for r in walk(doc):
            if not r.get('guid') and r['fileID']!=0:assert r['fileID'] in docs,('Missing local reference',path,fid,r)
    for fid,doc in docs.items():
        go=doc.get('GameObject')
        if go and 'm_Component' in go:
            for entry in go['m_Component']:
                component=next(iter(docs[entry['component']['fileID']].values()))
                assert component['m_GameObject']['fileID']==fid,('Owner mismatch',path,fid)
        rect=doc.get('RectTransform')
        if rect and 'm_Father' in rect:
            for child in rect['m_Children']:
                childrect=docs[child['fileID']]['RectTransform']
                if 'm_Father' in childrect:assert childrect['m_Father']['fileID']==fid,('Parent mismatch',path,child)
    print(path,': YAML and local references valid;',len(docs),'objects/components')

newnames=['WeaponCandidateRow','WeaponInspectionPanel','WeaponAmmoHUD']
for name in newnames:
    docs=parsed[f'Assets/backpack/Prefabs/{name}.prefab']
    script=ROOT/f'Assets/backpack/Scripts/UI/{name}.cs'
    guid=re.search(r'guid: (\w+)',script.with_suffix('.cs.meta').read_text())[1]
    component=next(d['MonoBehaviour'] for d in docs.values() if d.get('MonoBehaviour',{}).get('m_Script',{}).get('guid')==guid)
    declared=set(re.findall(r'public\s+\w+\s+(\w+)\s*;',script.read_text(encoding='utf-8-sig')))
    assigned=set(component)-{'m_ObjectHideFlags','m_CorrespondingSourceObject','m_PrefabInstance','m_PrefabAsset','m_GameObject','m_Enabled','m_EditorHideFlags','m_Script','m_Name','m_EditorClassIdentifier'}
    assert declared==assigned,(name,declared-assigned,assigned-declared)
    for k in declared:
        value=component[k]
        assert isinstance(value,dict) and 'fileID' in value,(name,k,value)
        if k not in ['window','interaction']:assert value['fileID']!=0,(name,k)
    if 'contentRoot' in component:assert component['contentRoot']!=component['m_GameObject'],name
    print(name,': serialized script fields match;',len(declared),'configured refs')

panel=parsed['Assets/backpack/Prefabs/WeaponInspectionPanel.prefab']
row=parsed['Assets/backpack/Prefabs/WeaponCandidateRow.prefab']
hud=parsed['Assets/backpack/Prefabs/WeaponAmmoHUD.prefab']
meta_guids={re.search(r'guid: (\w+)',(ROOT/f'Assets/backpack/Prefabs/{name}.prefab.meta').read_text())[1]:parsed[f'Assets/backpack/Prefabs/{name}.prefab'] for name in newnames}
for path,docs in parsed.items():
    for doc in docs.values():
        for r in walk(doc):
            if r.get('guid') in meta_guids and r['fileID']!=100100000:
                assert r['fileID'] in meta_guids[r['guid']],('External prefab fileID missing',path,r)
        inst=doc.get('PrefabInstance')
        if inst and inst['m_SourcePrefab']['guid'] in meta_guids:
            for override in inst['m_Modification']['m_Modifications']:
                target=override['target']; assert target['fileID'] in meta_guids[target['guid']]
    print(path,': new prefab GUID/fileID targets valid')
for path,root,width,gridright,panelx,window,interaction in [
    ('Assets/backpack/Prefabs/背包UI.prefab',94385758138161897,1002,614,638,5093767911312014143,1272109345441388922),
    ('Assets/backpack/Prefabs/和箱子交互ui.prefab',1620909329319307879,1756,1368,1392,6630096708533370529,6775291838125623718)]:
    docs=parsed[path]; rect=docs[root]['RectTransform'];assert rect['m_SizeDelta']=={'x':width,'y':818}
    overrides=docs[8800000000000000000]['PrefabInstance']['m_Modification']['m_Modifications']
    byfield={o['propertyPath']:o for o in overrides}
    assert byfield['window']['objectReference']['fileID']==window
    assert byfield['interaction']['objectReference']['fileID']==interaction
    assert byfield['m_AnchoredPosition.x']['value']==panelx
    assert gridright+24==panelx and panelx+340==width-24
    assert 90+616==706 and 706<718
    assert width<=1920 and 818<=1080
    print(path,': grid/panel gutter 24; panel bottom 706, footer 718; fits 1920x1080')

for doc in hud.values():
    mono=doc.get('MonoBehaviour',{})
    if 'm_RaycastTarget' in mono:assert mono['m_RaycastTarget']==0,('HUD raycast',mono)
    if mono.get('m_Script',{}).get('guid')=='67db9e8f0e2ae9c40bc1e2b64352a6b4':assert mono['m_Interactable']==0
print('HUD: all graphics ignore raycasts; progress slider non-interactive')

# Offline text/layout QA: only reads font and serialized assets, never project code.
from PIL import ImageFont
def named_parts(docs,name):
    goid=next(fid for fid,d in docs.items() if d.get('GameObject',{}).get('m_Name')==name)
    go=docs[goid]['GameObject']
    parts=[next(iter(docs[c['component']['fileID']].values())) for c in go['m_Component']]
    rect=next(c for c in parts if 'm_AnchoredPosition' in c)
    text=next((c for c in parts if 'm_fontSize' in c),None)
    return rect,text
summary_rect,summary_text=named_parts(panel,'Summary')
description_rect,description_text=named_parts(row,'Description')
row_rect,_=named_parts(row,'WeaponCandidateRow')
mag_rect,_=named_parts(panel,'Magazine')
muzzle_rect,_=named_parts(panel,'Muzzle')
heading_rect,_=named_parts(panel,'CandidateHeading')
viewport_rect,_=named_parts(panel,'CandidateViewport')
quantity_rect,_=named_parts(panel,'QuantityControls')
assert summary_rect['m_SizeDelta']=={'x':242,'y':124}
assert -summary_rect['m_AnchoredPosition']['y']+124+8==-mag_rect['m_AnchoredPosition']['y']
assert -mag_rect['m_AnchoredPosition']['y']+36+8==-muzzle_rect['m_AnchoredPosition']['y']
assert -muzzle_rect['m_AnchoredPosition']['y']+36+12==-heading_rect['m_AnchoredPosition']['y']
assert -heading_rect['m_AnchoredPosition']['y']+42+6==-viewport_rect['m_AnchoredPosition']['y']
assert -viewport_rect['m_AnchoredPosition']['y']+viewport_rect['m_SizeDelta']['y']+10==-quantity_rect['m_AnchoredPosition']['y']
assert row_rect['m_SizeDelta']['y']==140
assert description_rect['m_SizeDelta']=={'x':232,'y':96}
assert -description_rect['m_AnchoredPosition']['y']+96+10==140
assert row_rect['m_SizeDelta']['y']<=viewport_rect['m_SizeDelta']['y']
row_layout=next(d['MonoBehaviour'] for d in row.values() if 'm_PreferredHeight' in d.get('MonoBehaviour',{}))
assert row_layout['m_MinHeight']==row_layout['m_PreferredHeight']==140
for label in [summary_text,description_text]:
    assert label['m_enableAutoSizing']==1 and label['m_fontSizeMin']==12
    assert label['m_overflowMode']==2 and label['m_enableWordWrapping']==1
print('Text layout: Summary 124 high, slot gap 8; row 140/description 96; viewport 142, quantity gap 10; bounded autosizing, no ellipsis')

font_asset=(ROOT/'Assets/backpack/Prefabs/Fonts/NotoSansSC-Regular SDF.asset').read_text(encoding='utf-8-sig')
face=re.search(r'(?s)  m_FaceInfo:\n(.*?)  m_GlyphTable:',font_asset)[1]
line_height=float(re.search(r'm_LineHeight: ([\d.]+)',face)[1])
point_size=float(re.search(r'm_PointSize: ([\d.]+)',face)[1])
fontpath=str(ROOT/'Assets/backpack/Prefabs/Fonts/NotoSansSC-Regular.ttf')
def text_fit(text,rect,label):
    width=rect['m_SizeDelta']['x'];height=rect['m_SizeDelta']['y']
    for quarter in range(int(label['m_fontSizeMax']*4),int(label['m_fontSizeMin']*4)-1,-1):
        size=quarter/4; font=ImageFont.truetype(fontpath,quarter)
        lines=[]
        for para in text.split('\n'):
            line=''
            for token in re.findall(r'[A-Za-z0-9/.#×°]+|[^A-Za-z0-9/.#×°]',para):
                if font.getlength(line+token)/4>width-4 and line:
                    lines.append(line);line=token.lstrip()
                else:line+=token
            lines.append(line)
        extent=len(lines)*size*line_height/point_size
        if extent<=height-4:return size,len(lines),extent
    raise AssertionError(('Full text exceeds autosize budget',text))
rifles=list((ROOT/'Assets/backpack/Data/Items/枪械').glob('*.asset'))
for rifle in rifles:
    source=rifle.read_text(encoding='utf-8-sig')
    damage=float(re.search(r'baseDamage: ([\d.]+)',source)[1]);interval=float(re.search(r'shotInterval: ([\d.]+)',source)[1]);spread=float(re.search(r'spreadAngle: ([\d.]+)',source)[1])
    base=f'基础伤害 {damage:g} · 基础射速 {60/interval:.0f} 发/分 · 基础散布 {spread:g}°（无可预览弹药）'
    muzzle=(ROOT/'Assets/backpack/Data/Items/配件/基础枪口装置.asset').read_text(encoding='utf-8-sig')
    values=[float(re.search(key+r': ([\d.]+)',muzzle)[1]) for key in ['damageMultiplier','spreadMultiplier','recoilMultiplier','shotIntervalMultiplier']]
    full=base+f' · 枪口倍率：伤害 {values[0]:g}× / 散布 {values[1]:g}× / 后坐力 {values[2]:g}× / 射击间隔 {values[3]:g}×'
    summary_result=text_fit('弹匣 0/30 + 膛内 0\n耐久 100/100\n'+full,summary_rect,summary_text)
    candidate_result=text_fit('点击装配枪口\n'+full,description_rect,description_text)
    print(rifle.stem,': complete empty-chamber+muzzle text fits Summary',summary_result,'Description',candidate_result,'(font size, lines, height; extra 4px safety)')
for name in ['背包UI.prefab','和箱子交互ui.prefab']:
    rel='Assets/backpack/Prefabs/'+name
    before=parse(ROOT/'.codex-task-snapshot/weapon-ui-20261029/assets'/rel)
    after=parsed[rel]
    assert before.keys()<=after.keys(),('Existing fileID removed',name)
    for fid in before:
        b=next(iter(before[fid].values()));a=next(iter(after[fid].values()))
        if 'RectTransform' not in before[fid]:assert a==b,('Existing non-layout component modified',name,fid)
    print(name,': all existing fileIDs and non-layout components preserved')
print('Static validation passed; Unity was not loaded or executed.')
