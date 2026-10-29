import json,math
import numpy as np
from pathlib import Path
from PIL import Image
root=Path(r'E:\github\RPG-2');out=root/'.codex-task-snapshot/ui-weapon-icons-20261026'
g=json.loads((out/'geometry.json').read_text());v=np.array(g['verts']);uv=np.array(g['uv'])
# Read only the FBX geometry and textures to create a shape reference. No Unity code is loaded.
W,H=1024,512
proj=np.column_stack((512-(v[:,1]-(v[:,1].min()+v[:,1].max())/2)*11,256-(v[:,2]-(v[:,2].min()+v[:,2].max())/2)*11))
for name in ['Style_Wood','Style_Meatl','Real_Wood','Real_Metal']:
    stem='Rifle_Weapon_'+name
    tex=np.array(Image.open(root/f'Assets/CombatGirlsCharacterPack/RifleGirl/Texture/Weapon/{stem}/{stem}.png').convert('RGB'))
    canvas=np.full((H,W,3),[221,227,235],dtype=np.uint8);depth=np.full((H,W),-np.inf)
    for poly in g['polys']:
        for j in range(1,len(poly)-1):
            face=[poly[0],poly[j],poly[j+1]];ids=[p[0] for p in face];tids=[p[1] for p in face]
            p=proj[ids];a,b,c=p
            x0,y0=np.maximum(np.floor(p.min(axis=0)).astype(int),[0,0]);x1,y1=np.minimum(np.ceil(p.max(axis=0)).astype(int),[W-1,H-1])
            if x0>x1 or y0>y1:continue
            det=(b[1]-c[1])*(a[0]-c[0])+(c[0]-b[0])*(a[1]-c[1])
            if abs(det)<1e-7:continue
            yy,xx=np.mgrid[y0:y1+1,x0:x1+1];xx=xx+.5;yy=yy+.5
            w0=((b[1]-c[1])*(xx-c[0])+(c[0]-b[0])*(yy-c[1]))/det
            w1=((c[1]-a[1])*(xx-c[0])+(a[0]-c[0])*(yy-c[1]))/det;w2=1-w0-w1
            z=w0*v[ids[0],0]+w1*v[ids[1],0]+w2*v[ids[2],0]
            mask=(w0>=0)&(w1>=0)&(w2>=0)&(z>depth[y0:y1+1,x0:x1+1])
            t=w0[...,None]*uv[tids[0]]+w1[...,None]*uv[tids[1]]+w2[...,None]*uv[tids[2]]
            tx=np.clip(((t[...,0]%1)*tex.shape[1]).astype(int),0,tex.shape[1]-1);ty=np.clip(((1-t[...,1])*tex.shape[0]).astype(int),0,tex.shape[0]-1)
            col=tex[ty,tx]
            canvas[y0:y1+1,x0:x1+1][mask]=col[mask];depth[y0:y1+1,x0:x1+1][mask]=z[mask]
    Image.fromarray(canvas).save(out/f'reference_{name}.png')

