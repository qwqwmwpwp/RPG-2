import json
from pathlib import Path
from PIL import Image,ImageDraw
root=Path(r'E:\github\RPG-2'); out=root/'.codex-task-snapshot/ui-weapon-icons-20261026'
g=json.loads((out/'geometry.json').read_text());verts=g['verts'];polys=g['polys'];uv=g['uv']
tex=Image.open(root/'Assets/CombatGirlsCharacterPack/RifleGirl/Texture/Weapon/Rifle_Weapon_Style_Wood/Rifle_Weapon_Style_Wood.png').convert('RGB')
im=Image.new('RGB',(1024,480),(220,226,234));d=ImageDraw.Draw(im)
ys=[v[1] for v in verts];zs=[v[2] for v in verts];my=(max(ys)+min(ys))/2;mz=(max(zs)+min(zs))/2;s=920/(max(ys)-min(ys))
for p in sorted(polys,key=lambda p:sum(verts[i][0] for i,_ in p)/len(p)):
    tx=sum(uv[j][0] for _,j in p)/len(p);ty=sum(uv[j][1] for _,j in p)/len(p)
    col=tex.getpixel((min(tex.width-1,max(0,int(tx*tex.width))),min(tex.height-1,max(0,int((1-ty)*tex.height)))))
    d.polygon([(512-(verts[i][1]-my)*s,240-(verts[i][2]-mz)*s) for i,_ in p],fill=col)
im.save(out/'rifle-side-reference.png')
