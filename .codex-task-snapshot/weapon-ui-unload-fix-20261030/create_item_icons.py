from pathlib import Path
from PIL import Image, ImageDraw
import re,uuid

ROOT=Path(__file__).resolve().parents[2]
OUT=ROOT/'Assets/UI素材/枪械'
S=4
SILVER=(221,232,238,255)
LIGHT=(247,252,254,255)
MID=(121,148,163,255)
DARK=(31,52,64,255)
DEEP=(11,24,34,255)
CYAN=(72,207,220,255)
def canvas(w,h): return Image.new('RGBA',(w*S,h*S),(0,0,0,0))
def draw(im):return ImageDraw.Draw(im)
def box(coords):return tuple(round(v*S) for v in coords)
def poly(d,coords,fill,outline=None,width=1):
    points=[(int(x*S),int(y*S)) for x,y in coords]
    d.polygon(points,fill=fill)
    if outline:d.line(points+[points[0]],fill=outline,width=width*S,joint='curve')
def line(d,points,fill,width=1):d.line([(int(x*S),int(y*S)) for x,y in points],fill=fill,width=width*S,joint='curve')
def ellipse(d,coords,fill,outline=None,width=1):d.ellipse(box(coords),fill=fill,outline=outline,width=width*S)
def save(im,name,size):
    im=im.resize(size,Image.Resampling.LANCZOS)
    im.save(OUT/(name+'.png'),optimize=True)
    return im

# Compact ported cylinder, diagonal silhouette and an open front bore.
im=canvas(256,256);d=draw(im)
poly(d,[(42,118),(178,84),(209,134),(72,172)],DARK,SILVER,6)
ellipse(d,(31,111,79,176),MID,SILVER,6)
poly(d,[(57,119),(174,90),(192,113),(71,148)],MID)
line(d,[(62,119),(171,91)],LIGHT,5)
line(d,[(74,166),(191,135)],CYAN,5)
ellipse(d,(166,78,222,144),SILVER,LIGHT,4)
ellipse(d,(177,89,213,134),DEEP,MID,5)
ellipse(d,(183,96,208,128),DEEP,CYAN,3)
for x,y in [(96,118),(124,111),(151,104)]:
    ellipse(d,(x-6,y-1,x+7,y+13),DEEP,SILVER,2)
line(d,[(45,133),(54,153)],CYAN,5)
muzzle=save(im,'Icon_Muzzle_Basic',(256,256))

# Rifle magazine: narrow feed lips, a curved body, reinforcement ribs and baseplate.
im=canvas(128,256);d=draw(im)
poly(d,[(29,26),(94,26),(96,107),(91,150),(78,194),(65,226),(15,207),(32,168),(40,127),(38,75)],DARK,SILVER,6)
poly(d,[(35,35),(86,35),(88,110),(82,151),(68,195),(58,214),(26,202),(42,169),(49,128),(47,78)],MID)
poly(d,[(33,18),(94,18),(94,42),(31,42)],DARK,SILVER,5)
line(d,[(42,21),(44,37)],CYAN,5)
line(d,[(80,21),(82,37)],LIGHT,4)
for y,x1,x2 in [(70,49,85),(100,51,86),(130,51,84),(158,47,78),(184,37,70)]:
    line(d,[(x1,y),(x2,y+7)],DEEP,7)
    line(d,[(x1,y-3),(x2,y+4)],SILVER,3)
poly(d,[(17,200),(69,220),(62,238),(9,218)],DARK,SILVER,5)
line(d,[(17,212),(58,228)],CYAN,6)
magazine=save(im,'Icon_Magazine_Rifle30',(128,256))

# Three pointed rifle cartridges; cyan case bands unify the inventory style.
im=canvas(256,256);d=draw(im)
for cx,top,bottom in [(62,48,218),(128,22,234),(194,48,218)]:
    neck=top+61;shoulder=top+79
    poly(d,[(cx,top),(cx-15,top+30),(cx-15,neck),(cx-23,shoulder),(cx-23,bottom-10),(cx+23,bottom-10),(cx+23,shoulder),(cx+15,neck),(cx+15,top+30)],MID,SILVER,5)
    poly(d,[(cx,top+8),(cx-8,top+32),(cx-8,neck),(cx+1,neck),(cx+1,top+29)],LIGHT)
    line(d,[(cx-18,shoulder+7),(cx-18,bottom-21)],SILVER,4)
    line(d,[(cx-18,shoulder),(cx+18,shoulder)],CYAN,5)
    poly(d,[(cx-27,bottom-13),(cx+27,bottom-13),(cx+27,bottom),(cx-27,bottom)],DARK,SILVER,4)
    line(d,[(cx-20,bottom-6),(cx+20,bottom-6)],CYAN,3)
ammo=save(im,'Icon_Ammo_Rifle_Standard',(256,256))

template=(OUT/'Icon_Rifle_Real_Metal.png.meta').read_text(encoding='utf-8-sig')
entries=[
 ('配件/基础枪口装置.asset','Icon_Muzzle_Basic',(256,256),2903831765711323146),
 ('弹匣/步枪30发弹匣.asset','Icon_Magazine_Rifle30',(128,256),2903831765711323143),
 ('弹药/步枪标准弹.asset','Icon_Ammo_Rifle_Standard',(256,256),2903831765711323140)]
for assetrel,name,(w,h),rid in entries:
    metapath=OUT/(name+'.png.meta')
    guid=re.search(r'^guid: (\w+)',metapath.read_text(),re.M)[1] if metapath.exists() else uuid.uuid4().hex
    meta=re.sub(r'(?m)^guid: \w+',f'guid: {guid}',template)
    meta=re.sub(r'(?m)^    spriteID: \w+',f'    spriteID: {uuid.uuid4().hex}',meta)
    metapath.write_text(meta,encoding='utf-8',newline='\n')
    p=ROOT/'Assets/backpack/Data/Items'/assetrel
    text=p.read_text(encoding='utf-8-sig')
    assert 'class: ImageFragment' not in text,assetrel
    assert f'    - rid: {rid}\n' not in text,assetrel
    text=text.replace('  references:\n',f'  - rid: {rid}\n  references:\n',1)
    text+=f'''    - rid: {rid}
      type: {{class: ImageFragment, ns: RPG4.Backpack.Items, asm: RPG4.Backpack.Items}}
      data:
        fragmentTag:
          name: FragmentTags.Default.IconFragment
        icon: {{fileID: 21300000, guid: {guid}, type: 3}}
        iconDimensions: {{x: {w}, y: {h}}}
'''
    p.write_text(text,encoding='utf-8',newline='\r\n')
    print(assetrel,'->',name+'.png',guid)

# Temporary review sheet displays full icons and actual 48px-wide inventory thumbnails.
sheet=Image.new('RGB',(660,400),(10,18,23));sd=ImageDraw.Draw(sheet)
for index,(label,icon) in enumerate([('MUZZLE',muzzle),('MAGAZINE',magazine),('AMMO',ammo)]):
    x=index*220
    sd.text((x+16,12),label,fill=(221,232,238))
    preview=icon.copy();preview.thumbnail((180,240),Image.Resampling.LANCZOS)
    sheet.paste(preview,(x+(220-preview.width)//2,42),preview)
    thumb=icon.copy();thumb.thumbnail((48,96),Image.Resampling.LANCZOS)
    sheet.paste(thumb,(x+(220-thumb.width)//2,292),thumb)
    sd.text((x+16,382),'48px inventory width',fill=(121,148,163))
sheet.save(Path(__file__).parent/'icon-review.png')
