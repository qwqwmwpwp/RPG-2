from pathlib import Path
from PIL import Image,ImageDraw
import re,yaml
ROOT=Path(__file__).resolve().parents[2]
TASK=Path(__file__).parent
ENTRIES=[('配件/基础枪口装置.asset','Icon_Muzzle_Basic',(256,256)),('弹匣/步枪30发弹匣.asset','Icon_Magazine_Rifle30',(128,256)),('弹药/步枪标准弹.asset','Icon_Ammo_Rifle_Standard',(256,256))]
def load_asset(path):
    lines=path.read_text(encoding='utf-8-sig').splitlines()
    return yaml.safe_load('\n'.join(x for x in lines if not x.startswith(('%','--- !u!'))))['MonoBehaviour']
def luminance(rgb):
    channels=[v/255 for v in rgb]
    return sum(w*(v/12.92 if v<=0.04045 else ((v+0.055)/1.055)**2.4) for v,w in zip(channels,[.2126,.7152,.0722]))
bg=(10,18,23);bg_lum=luminance(bg)
guids=[]
sheet=Image.new('RGB',(660,410),bg);sd=ImageDraw.Draw(sheet)
for index,(rel,name,size) in enumerate(ENTRIES):
    asset=ROOT/'Assets/backpack/Data/Items'/rel
    before=load_asset(TASK/'Assets/backpack/Data/Items'/rel);after=load_asset(asset)
    oldrefs=before['references']['RefIds'];newrefs=after['references']['RefIds']
    listed=[v['rid'] for v in after['fragments']];defined=[v['rid'] for v in newrefs]
    assert len(listed)==len(set(listed))==len(defined)==len(set(defined)) and set(listed)==set(defined)
    icons=[v for v in newrefs if v['type']['class']=='ImageFragment'];assert len(icons)==1
    icon=icons[0];assert icon['type']=={'class':'ImageFragment','ns':'RPG4.Backpack.Items','asm':'RPG4.Backpack.Items'}
    data=icon['data'];assert data['fragmentTag']['name']=='FragmentTags.Default.IconFragment'
    assert data['iconDimensions']=={'x':size[0],'y':size[1]}
    newrid=icon['rid'];after['references']['RefIds']=[v for v in newrefs if v['rid']!=newrid]
    after['fragments']=[v for v in after['fragments'] if v['rid']!=newrid]
    assert after==before,('Existing serialized business field changed',rel)
    png=ROOT/'Assets/UI素材/枪械'/(name+'.png')
    meta=yaml.safe_load(png.with_suffix('.png.meta').read_text(encoding='utf-8-sig'))
    guid=meta['guid'];guids.append(guid)
    assert re.fullmatch(r'[0-9a-f]{32}',guid)
    assert data['icon']=={'fileID':21300000,'guid':guid,'type':3}
    importer=meta['TextureImporter']
    assert importer['spriteMode']==1 and importer['textureType']==8 and importer['alphaIsTransparency']==1
    assert importer['alphaUsage']==1 and importer['spriteSheet']['sprites']==[] and importer['spriteSheet']['internalID']==0
    assert re.fullmatch(r'[0-9a-f]{32}',str(importer['spriteSheet']['spriteID']))
    image=Image.open(png);assert image.mode=='RGBA' and image.size==size
    alpha=image.getchannel('A');assert alpha.getextrema()==(0,255)
    bounds=alpha.getbbox();assert bounds and bounds[0]>0 and bounds[1]>0 and bounds[2]<size[0] and bounds[3]<size[1]
    opaque=[p[:3] for p in image.getdata() if p[3]>230]
    ratios=[(luminance(p)+.05)/(bg_lum+.05) for p in opaque]
    readable=sum(v>=4.5 for v in ratios)/len(ratios)
    assert readable>.65 and max(ratios)>12,(name,readable,max(ratios))
    # Small source images are only resampled into a temporary contact sheet for visual QA.
    x=index*220
    sd.text((x+12,12),name.replace('Icon_',''),fill=(221,232,238))
    large=image.copy();large.thumbnail((180,220),Image.Resampling.LANCZOS)
    sheet.paste(large,(x+(220-large.width)//2,40),large)
    for boxsize,y in [(48,282),(32,354)]:
        thumb=image.copy();thumb.thumbnail((boxsize,boxsize),Image.Resampling.LANCZOS)
        assert thumb.getchannel('A').getbbox() is not None
        sd.rectangle((x+86,y-1,x+86+boxsize,y+boxsize),outline=(40,65,78))
        sheet.paste(thumb,(x+86+(boxsize-thumb.width)//2,y+(boxsize-thumb.height)//2),thumb)
        sd.text((x+12,y+boxsize+4),str(boxsize)+'px square icon slot',fill=(121,148,163))
    print(name,': RGBA',size,'alpha bbox',bounds,'opaque pixels >=4.5:1',f'{readable:.1%}','max contrast',f'{max(ratios):.1f}:1','Sprite/rid/business fields OK')
assert len(set(guids))==3
# All metadata GUID reads are static; no Unity resource loading.
counts={g:0 for g in guids}
for path in (ROOT/'Assets').rglob('*.meta'):
    with path.open(encoding='utf-8-sig',errors='replace') as stream:
        for line in stream:
            if line.startswith('guid: '):
                g=line.split(':',1)[1].strip()
                if g in counts:counts[g]+=1
                break
assert all(n==1 for n in counts.values()),counts
sheet.save(TASK/'icon-review.png')
print('All icon GUIDs unique; three existing assets differ only by one added ImageFragment each. Unity was not invoked.')
