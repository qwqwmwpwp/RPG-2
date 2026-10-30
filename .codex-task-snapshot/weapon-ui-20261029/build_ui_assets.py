import re, json, pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
PREF = ROOT / 'Assets/backpack/Prefabs'
original = (ROOT / '.codex-task-snapshot/weapon-ui-20261029/assets/Assets/backpack/Prefabs/背包UI.prefab').read_text(encoding='utf-8-sig')
blocks = {int(re.search(r'&(\d+)', b)[1]): b for b in re.split(r'(?=--- !u!)', original) if b.startswith('--- !u!')}
IMAGE = blocks[7600000000000000013]
TEXT = blocks[7600000000000000023]
BUTTON = blocks[7600000000000000044] if 7600000000000000044 in blocks else next(b for b in blocks.values() if 'guid: 4e29b1a8efbd4b44bb3f3716e73f07ff' in b)
BUTTON = BUTTON[:BUTTON.index('  m_OnClick:')] + '  m_OnClick:\n    m_PersistentCalls:\n      m_Calls: []\n'
HEADER = '%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n'
COMMON = '  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n'
GUIDS = {'row': '5a9912ecc3044fba935f67a691c746cc', 'panel': '214bd7e1228343aab181b15e28945d45', 'hud': '7cdf28f1085944a6ab5a04a011161076'}
def ref(fid): return '{fileID: '+str(fid)+'}'
def q(s): return json.dumps(s, ensure_ascii=True)
def replace(b, key, value): return re.sub(r'(?m)^  '+re.escape(key)+r':.*$', lambda m: '  '+key+': '+str(value), b)
def script_guid(name):
    p=ROOT/f'Assets/backpack/Scripts/UI/{name}.cs.meta'
    return re.search(r'guid: (\w+)', p.read_text())[1]

class Asset:
    def __init__(self, start): self.next=start; self.objects=[]; self.components=[]
    def alloc(self): self.next+=1; return self.next
    def node(self,name,parent=None,x=0,y=0,w=0,h=0,anchor=(0,1),pivot=(0,1),stretch=False):
        n={'go':self.alloc(),'rect':self.alloc(),'name':name,'parent':parent,'children':[],'components':[],'x':x,'y':y,'w':w,'h':h,'anchor':anchor,'pivot':pivot,'stretch':stretch}
        if parent: parent['children'].append(n['rect'])
        self.objects.append(n); return n
    def mono(self,n,guid,fields):
        fid=self.alloc(); n['components'].append(fid)
        self.components.append(f'--- !u!114 &{fid}\nMonoBehaviour:\n'+COMMON+f'  m_GameObject: {ref(n["go"])}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {{fileID: 11500000, guid: {guid}, type: 3}}\n  m_Name: \n  m_EditorClassIdentifier: \n'+fields)
        return fid
    def template(self,n,template,changes):
        fid=self.alloc(); n['components'].append(fid)
        b=re.sub(r'^--- !u!114 &\d+',f'--- !u!114 &{fid}',template)
        b=replace(b,'m_GameObject',ref(n['go']))
        for key,val in changes.items(): b=replace(b,key,val)
        self.components.append(b); return fid
    def renderer(self,n):
        fid=self.alloc(); n['components'].append(fid)
        self.components.append(f'--- !u!222 &{fid}\nCanvasRenderer:\n'+COMMON+f'  m_GameObject: {ref(n["go"])}\n  m_CullTransparentMesh: 1\n')
    def image(self,n,color='{r: 0.045, g: 0.07, b: 0.085, a: 0.98}',raycast=0,sprite=None):
        self.renderer(n)
        fields={'m_Color':color,'m_RaycastTarget':raycast}
        if sprite: fields['m_Sprite']=sprite; fields['m_PreserveAspect']=1
        return self.template(n,IMAGE,fields)
    def text(self,name,parent,text,x,y,w,h,size=16,color='{r: 0.77, g: 0.84, b: 0.87, a: 1}',center=False,wrap=True):
        n=self.node(name,parent,x,y,w,h); self.renderer(n)
        bounded=name in ['Summary','Description']
        fid=self.template(n,TEXT,{'m_text':q(text),'m_fontSize':size,'m_fontSizeBase':size,'m_Color':color,'m_fontColor':color,'m_enableWordWrapping':int(wrap),'m_enableAutoSizing':int(bounded),'m_fontSizeMin':12 if bounded else 18,'m_fontSizeMax':size if bounded else 72,'m_overflowMode':2 if bounded else 0,'m_HorizontalAlignment':2 if center else 1,'m_VerticalAlignment':512 if center else 256})
        return n,fid
    def button(self,name,parent,text,x,y,w,h):
        n=self.node(name,parent,x,y,w,h)
        image=self.image(n,'{r: 0.1, g: 0.19, b: 0.23, a: 1}',1)
        btn=self.template(n,BUTTON,{'m_TargetGraphic':ref(image)})
        _,label=self.text('Label',n,text,6,-2,w-12,h-4,15,center=True)
        return n,btn,label
    def slider(self,name,parent,x,y,w,h,interactable=True):
        n=self.node(name,parent,x,y,w,h)
        self.image(n,'{r: 0.085, g: 0.12, b: 0.15, a: 1}',int(interactable))
        fill=self.node('Fill',n,0,0,w,h,anchor=(0,0.5),pivot=(0,0.5)); self.image(fill,'{r: 0.27, g: 0.73, b: 0.79, a: 1}')
        handle=None; image=0
        if interactable:
            handle=self.node('Handle',n,0,0,14,h+8,anchor=(0,0.5),pivot=(0.5,0.5)); image=self.image(handle,'{r: 0.86, g: 0.95, b: 0.97, a: 1}',1)
        fid=self.mono(n,'67db9e8f0e2ae9c40bc1e2b64352a6b4',f'''  m_Navigation:
    m_Mode: 0
    m_WrapAround: 0
    m_SelectOnUp: {{fileID: 0}}
    m_SelectOnDown: {{fileID: 0}}
    m_SelectOnLeft: {{fileID: 0}}
    m_SelectOnRight: {{fileID: 0}}
  m_Transition: 0
  m_Interactable: {int(interactable)}
  m_TargetGraphic: {ref(image)}
  m_FillRect: {ref(fill['rect'])}
  m_HandleRect: {ref(handle['rect'] if handle else 0)}
  m_Direction: 0
  m_MinValue: 0
  m_MaxValue: 1
  m_WholeNumbers: {int(interactable)}
  m_Value: 0
  m_OnValueChanged:
    m_PersistentCalls:
      m_Calls: []
''')
        return n,fid
    def write(self,name):
        output=HEADER
        for n in self.objects:
            comps=[n['rect']]+n['components']; x,y=n['anchor']; px,py=n['pivot']
            output+=f'--- !u!1 &{n["go"]}\nGameObject:\n'+COMMON+'  serializedVersion: 6\n  m_Component:\n'+''.join(f'  - component: {ref(i)}\n' for i in comps)+f'  m_Layer: 5\n  m_Name: {q(n["name"])}\n  m_TagString: Untagged\n  m_Icon: {{fileID: 0}}\n  m_NavMeshLayer: 0\n  m_StaticEditorFlags: 0\n  m_IsActive: 1\n'
            output+=f'--- !u!224 &{n["rect"]}\nRectTransform:\n'+COMMON+f'  m_GameObject: {ref(n["go"])}\n  m_LocalRotation: {{x: 0, y: 0, z: 0, w: 1}}\n  m_LocalPosition: {{x: 0, y: 0, z: 0}}\n  m_LocalScale: {{x: 1, y: 1, z: 1}}\n  m_ConstrainProportionsScale: 0\n'
            output+=('  m_Children:\n'+''.join(f'  - {ref(i)}\n' for i in n['children'])) if n['children'] else '  m_Children: []\n'
            output+=f'  m_Father: {ref(n["parent"]["rect"] if n["parent"] else 0)}\n  m_LocalEulerAnglesHint: {{x: 0, y: 0, z: 0}}\n  m_AnchorMin: {{x: {x}, y: {y}}}\n  m_AnchorMax: {{x: {1 if n["stretch"] else x}, y: {1 if n["stretch"] else y}}}\n  m_AnchoredPosition: {{x: {n["x"]}, y: {n["y"]}}}\n  m_SizeDelta: {{x: {n["w"]}, y: {n["h"]}}}\n  m_Pivot: {{x: {px}, y: {py}}}\n'
        output+=''.join(self.components)
        (PREF/f'{name}.prefab').write_text(output,encoding='utf-8',newline='\n')
        guid=GUIDS[{'WeaponCandidateRow':'row','WeaponInspectionPanel':'panel','WeaponAmmoHUD':'hud'}[name]]
        (PREF/f'{name}.prefab.meta').write_text(f'fileFormatVersion: 2\nguid: {guid}\nPrefabImporter:\n  externalObjects: {{}}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n',encoding='utf-8')

def build_row():
    a=Asset(910000000); root=a.node('WeaponCandidateRow',w=308,h=140)
    image=a.image(root,'{r: 0.075, g: 0.13, b: 0.16, a: 1}',1)
    button=a.template(root,BUTTON,{'m_TargetGraphic':ref(image)})
    iconnode=a.node('Icon',root,8,-10,48,48); icon=a.image(iconnode,'{r: 1, g: 1, b: 1, a: 1}')
    _,name=a.text('ItemName',root,'候选物品',64,-8,232,24,16)
    _,description=a.text('Description',root,'点击选择可装配物品',64,-34,232,96,13)
    a.mono(root,'306cc8c2b49d7114eaa3623786fc2126','  m_IgnoreLayout: 0\n  m_MinWidth: -1\n  m_MinHeight: 140\n  m_PreferredWidth: 308\n  m_PreferredHeight: 140\n  m_FlexibleWidth: 1\n  m_FlexibleHeight: 0\n  m_LayoutPriority: 1\n')
    controller=a.mono(root,script_guid('WeaponCandidateRow'),f'  selectButton: {ref(button)}\n  itemName: {ref(name)}\n  description: {ref(description)}\n  icon: {ref(icon)}\n')
    a.write('WeaponCandidateRow'); return root,controller

def build_panel(row):
    a=Asset(920000000); root=a.node('WeaponInspectionPanel',w=340,h=616)
    a.image(root,'{r: 0.035, g: 0.055, b: 0.07, a: 1}',1)
    bar=a.node('Accent',root,0,0,3,616); a.image(bar,'{r: 0.27, g: 0.73, b: 0.79, a: 1}')
    _,title=a.text('Title',root,'武器检查',16,-12,308,30,21)
    content=a.node('Content',root,0,-48,340,508)
    inode=a.node('Icon',content,16,0,56,50); icon=a.image(inode,'{r: 1, g: 1, b: 1, a: 1}')
    _,summary=a.text('Summary',content,'选择枪械或弹匣查看属性与装配',82,0,242,124,14)
    _,mag,maglabel=a.button('Magazine',content,'弹匣槽',16,-132,224,36)
    _,rm,_=a.button('RemoveMagazine',content,'卸下',248,-132,76,36)
    _,muzzle,muzzlelabel=a.button('Muzzle',content,'枪口槽',16,-176,224,36)
    _,rmu,_=a.button('RemoveMuzzle',content,'卸下',248,-176,76,36)
    _,heading=a.text('CandidateHeading',content,'选择可用物品',16,-224,308,42,16)
    viewport=a.node('CandidateViewport',content,16,-272,308,142)
    a.image(viewport,'{r: 0.025, g: 0.04, b: 0.055, a: 1}',1)
    a.mono(viewport,'3312d7739989d2b4e91e6319e9a96d76','  m_Padding: {x: 0, y: 0, z: 0, w: 0}\n  m_Softness: {x: 0, y: 0}\n')
    candidates=a.node('Candidates',viewport,0,0,308,0)
    a.mono(candidates,'59f8146938fff824cb5fd77236b75775','''  m_Padding:
    m_Left: 0
    m_Right: 0
    m_Top: 0
    m_Bottom: 0
  m_ChildAlignment: 0
  m_Spacing: 6
  m_ChildForceExpandWidth: 1
  m_ChildForceExpandHeight: 0
  m_ChildControlWidth: 1
  m_ChildControlHeight: 1
  m_ChildScaleWidth: 0
  m_ChildScaleHeight: 0
  m_ReverseArrangement: 0
''')
    a.mono(candidates,'3245ec927659c4140ac4f8d17403cc18','  m_HorizontalFit: 0\n  m_VerticalFit: 2\n')
    a.mono(viewport,'1aa08ab6e0800fa44ae55d278d1423e3',f'''  m_Content: {ref(candidates['rect'])}
  m_Horizontal: 0
  m_Vertical: 1
  m_MovementType: 2
  m_Elasticity: 0.1
  m_Inertia: 0
  m_DecelerationRate: 0.135
  m_ScrollSensitivity: 32
  m_Viewport: {ref(viewport['rect'])}
  m_HorizontalScrollbar: {{fileID: 0}}
  m_VerticalScrollbar: {{fileID: 0}}
  m_HorizontalScrollbarVisibility: 0
  m_VerticalScrollbarVisibility: 0
  m_HorizontalScrollbarSpacing: 0
  m_VerticalScrollbarSpacing: 0
  m_OnValueChanged:
    m_PersistentCalls:
      m_Calls: []
''')
    quantity=a.node('QuantityControls',content,16,-424,308,84)
    _,qlabel=a.text('QuantityLabel',quantity,'装填数量  0',0,0,308,32,13)
    _,slider=a.slider('Quantity',quantity,8,-50,174,8)
    _,load,_=a.button('Load',quantity,'装填弹匣',200,-38,108,36)
    _,status=a.text('Status',root,'默认查看当前装备枪械；悬停枪械或弹匣按 F 检查。',16,-564,308,44,14,color='{r: 0.42, g: 0.77, b: 0.82, a: 1}')
    fields={'window':0,'interaction':0,'panelRect':root['rect'],'contentRoot':content['go'],'title':title,'summary':summary,'magazineLabel':maglabel,'muzzleLabel':muzzlelabel,'candidateHeading':heading,'status':status,'icon':icon,'magazineButton':mag,'muzzleButton':muzzle,'removeMagazineButton':rm,'removeMuzzleButton':rmu,'candidateRoot':candidates['rect'],'loadQuantity':slider,'loadQuantityLabel':qlabel,'loadButton':load,'quantityRoot':quantity['go']}
    controller=a.mono(root,script_guid('WeaponInspectionPanel'),''.join('  '+k+': '+ref(v)+'\n' for k,v in fields.items())+f'  candidatePrefab: {{fileID: {row}, guid: {GUIDS["row"]}, type: 3}}\n')
    a.write('WeaponInspectionPanel'); return root,controller

def build_hud():
    a=Asset(930000000); root=a.node('WeaponAmmoHUD',x=-32,y=32,w=344,h=196,anchor=(1,0),pivot=(1,0))
    content=a.node('Content',root,w=344,h=196); a.image(content,'{r: 0.025, g: 0.045, b: 0.06, a: 0.9}')
    accent=a.node('Accent',content,0,0,344,3); a.image(accent,'{r: 0.27, g: 0.73, b: 0.79, a: 1}')
    _,name=a.text('WeaponName',content,'当前武器',16,-14,228,40,16)
    inode=a.node('WeaponIcon',content,252,-16,76,42); icon=a.image(inode,'{r: 1, g: 1, b: 1, a: 1}')
    _,rounds=a.text('TotalRounds',content,'0',16,-60,170,48,44)
    a.text('RoundsCaption',content,'枪内总弹数',190,-78,138,24,14)
    _,reserve=a.text('Reserve',content,'备用弹匣 0 · 散装弹 0',16,-110,312,24,15)
    _,durability=a.text('Durability',content,'耐久 100 / 100',16,-136,312,22,13)
    _,action=a.text('ActionStatus',content,'',16,-160,312,22,14)
    _,progress=a.slider('ReloadProgress',content,16,-188,312,4,False)
    fields={'contentRoot':content['go'],'weaponName':name,'rounds':rounds,'reserve':reserve,'durability':durability,'actionLabel':action,'icon':icon,'reloadProgress':progress}
    controller=a.mono(root,script_guid('WeaponAmmoHUD'),''.join('  '+k+': '+ref(v)+'\n' for k,v in fields.items()))
    a.write('WeaponAmmoHUD'); return root,controller

def edit_block(text,fid,changes):
    pat=r'(?ms)^--- !u!\d+ &'+str(fid)+r'(?: stripped)?\n.*?(?=^--- !u!|\Z)'
    def edit(m):
        b=m[0]
        for key,value in changes.items(): b=replace(b,key,value)
        return b
    result,n=re.subn(pat,edit,text); assert n==1,(fid,n); return result

def add_instance(text,parent,rootfid,controller,instanceguid,instanceid,x,y,window=0,interaction=0):
    localrect=instanceid+1
    modifications=f'''    - target: {{fileID: {rootfid}, guid: {instanceguid}, type: 3}}
      propertyPath: m_AnchoredPosition.x
      value: {x}
      objectReference: {{fileID: 0}}
    - target: {{fileID: {rootfid}, guid: {instanceguid}, type: 3}}
      propertyPath: m_AnchoredPosition.y
      value: {y}
      objectReference: {{fileID: 0}}
'''
    for field,value in [('window',window),('interaction',interaction)]:
        if value:
            modifications+=f'    - target: {{fileID: {controller}, guid: {instanceguid}, type: 3}}\n      propertyPath: {field}\n      value: \n      objectReference: {ref(value)}\n'
    b=f'''--- !u!1001 &{instanceid}
PrefabInstance:
  m_ObjectHideFlags: 0
  serializedVersion: 2
  m_Modification:
    serializedVersion: 3
    m_TransformParent: {ref(parent)}
    m_Modifications:
'''+modifications+f'''    m_RemovedComponents: []
    m_RemovedGameObjects: []
    m_AddedGameObjects: []
    m_AddedComponents: []
  m_SourcePrefab: {{fileID: 100100000, guid: {instanceguid}, type: 3}}
--- !u!224 &{localrect} stripped
RectTransform:
  m_CorrespondingSourceObject: {{fileID: {rootfid}, guid: {instanceguid}, type: 3}}
  m_PrefabInstance: {ref(instanceid)}
  m_PrefabAsset: {{fileID: 0}}
'''
    pat=r'(?ms)(^--- !u!224 &'+str(parent)+r'\n.*?  m_Children:)(.*?)(  m_Father:)'
    def child(m):
        children=m[2]
        if children.startswith(' []'): children='\n'
        return m[1]+children+f'  - {ref(localrect)}\n'+m[3]
    text,n=re.subn(pat,child,text); assert n==1
    return text+b

def integrate(panelroot,panelcontroller,hudroot,hudcontroller):
    for name,root,width,prefix,window,interaction,panelx in [
        ('背包UI',94385758138161897,1002,7600000000000000000,5093767911312014143,1272109345441388922,638),
        ('和箱子交互ui',1620909329319307879,1756,7700000000000000000,6630096708533370529,6775291838125623718,1392)]:
        path=PREF/f'{name}.prefab'; text=path.read_text(encoding='utf-8-sig')
        assert '8800000000000000000' not in text
        text=edit_block(text,root,{'m_SizeDelta':f'{{x: {width}, y: 818}}'})
        text=edit_block(text,prefix+11,{'m_SizeDelta':f'{{x: {width}, y: 3}}'})
        text=edit_block(text,prefix+21,{'m_SizeDelta':f'{{x: {width-154}, y: 34}}'})
        text=edit_block(text,prefix+31,{'m_SizeDelta':f'{{x: {width-64}, y: 22}}'})
        text=edit_block(text,prefix+41,{'m_AnchoredPosition':f'{{x: {width-102}, y: -20}}'})
        footer=prefix+(91 if name=='背包UI' else 101)
        text=edit_block(text,footer,{'m_SizeDelta':f'{{x: {width-48}, y: 1}}','m_AnchoredPosition':'{x: 24, y: -718}'})
        text=edit_block(text,footer+10,{'m_SizeDelta':f'{{x: {width-48}, y: 44}}','m_AnchoredPosition':'{x: 24, y: -730}'})
        text=edit_block(text,footer+20,{'m_SizeDelta':f'{{x: {width-48}, y: 24}}','m_AnchoredPosition':'{x: 24, y: -782}'})
        if name!='背包UI':
            for rid in [prefix+71,prefix+91]: text=edit_block(text,rid,{'m_SizeDelta':'{x: 568, y: 540}'})
        text=add_instance(text,root,panelroot,panelcontroller,GUIDS['panel'],8800000000000000000,panelx,-90,window,interaction)
        path.write_text(text,encoding='utf-8',newline='\r\n')
    path=ROOT/'Assets/Scenes/Main.unity'; text=path.read_text(encoding='utf-8-sig')
    assert '8800000000000000100' not in text
    text=add_instance(text,435649771,hudroot,hudcontroller,GUIDS['hud'],8800000000000000100,-32,32)
    # Keep SceneRoots as the final scene document.
    roots=re.search(r'(?ms)^--- !u!1660057539 .*?(?=^--- !u!|\Z)',text)[0]
    text=text.replace(roots,'')+roots
    path.write_text(text,encoding='utf-8',newline='\n')

if __name__=='__main__':
    row,rc=build_row(); panel,pc=build_panel(rc); hud,hc=build_hud()
    integrate(panel['rect'],pc,hud['rect'],hc)
    print(json.dumps({'panelRoot':panel['rect'],'panelController':pc,'candidateComponent':rc,'hudRoot':hud['rect'],'hudController':hc}))
