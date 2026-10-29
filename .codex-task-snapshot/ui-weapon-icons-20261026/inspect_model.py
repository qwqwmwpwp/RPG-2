import struct, zlib, math, json
from pathlib import Path
from PIL import Image, ImageDraw
root=Path(r'E:\github\RPG-2')
out=root/'.codex-task-snapshot/ui-weapon-icons-20261026'
data=(root/'Assets/CombatGirlsCharacterPack/RifleGirl/Models/Parts/Weapon_Rifle.fbx').read_bytes()
ver=struct.unpack_from('<I',data,23)[0]
wide=ver>=7500
fmt='<QQQB' if wide else '<IIIB'
header=25 if wide else 13

def node(pos):
    end,nprops,plen,nlen=struct.unpack_from(fmt,data,pos)
    if end==0:return None,pos+header
    pos+=header
    name=data[pos:pos+nlen].decode('utf8');pos+=nlen
    props=[]
    for _ in range(nprops):
        typ=chr(data[pos]);pos+=1
        if typ in 'YCFDIL':
            f={'Y':'h','C':'?','F':'f','D':'d','I':'i','L':'q'}[typ]
            v=struct.unpack_from('<'+f,data,pos)[0];pos+=struct.calcsize(f)
        elif typ in 'SR':
            n=struct.unpack_from('<I',data,pos)[0];pos+=4
            v=data[pos:pos+n];pos+=n
            if typ=='S':v=v.decode('utf8',errors='replace')
        elif typ in 'fdlibc':
            n,enc,size=struct.unpack_from('<III',data,pos);pos+=12
            raw=data[pos:pos+size];pos+=size
            if enc:raw=zlib.decompress(raw)
            f={'f':'f','d':'d','l':'q','i':'i','b':'?','c':'b'}[typ]
            v=list(struct.unpack('<'+str(n)+f,raw))
        else:raise ValueError(typ)
        props.append(v)
    children=[]
    while pos<end-header:
        child,pos=node(pos)
        if child:children.append(child)
    return {'name':name,'props':props,'children':children},end
nodes=[];pos=27
while True:
    n,pos=node(pos)
    if n is None:break
    nodes.append(n)

def find(nodes,name):
    for n in nodes:
        if n['name']==name:yield n
        yield from find(n['children'],name)
geo=list(find(nodes,'Geometry'))[0]
v=list(find([geo],'Vertices'))[0]['props'][0]
verts=list(zip(v[::3],v[1::3],v[2::3]))
inds=list(find([geo],'PolygonVertexIndex'))[0]['props'][0]
uvraw=list(find([geo],'UV'))[0]['props'][0]
uv=list(zip(uvraw[::2],uvraw[1::2]))
uvi=list(find([geo],'UVIndex'))[0]['props'][0]
polys=[];poly=[]
for k,i in enumerate(inds):
    poly.append(((-i-1 if i<0 else i),uvi[k]))
    if i<0:polys.append(poly);poly=[]
print(json.dumps({'version':ver,'vertices':len(verts),'polygons':len(polys),'bounds':[[min(v[i] for v in verts),max(v[i] for v in verts)] for i in range(3)]}))
tex=Image.open(root/'Assets/CombatGirlsCharacterPack/RifleGirl/Texture/Weapon/Rifle_Weapon_Style_Wood/Rifle_Weapon_Style_Wood.png').convert('RGB')
canvas=Image.new('RGB',(1200,900),(225,229,235));d=ImageDraw.Draw(canvas)
for row,(a,b,c,label) in enumerate([(0,1,2,'XY'),(0,2,1,'XZ'),(2,1,0,'ZY')]):
    lo=[min(v[i] for v in verts) for i in (a,b)];hi=[max(v[i] for v in verts) for i in (a,b)]
    scale=min(1100/(hi[0]-lo[0]),240/(hi[1]-lo[1]))
    mid=[(x+y)/2 for x,y in zip(lo,hi)]
    proj=lambda v:(600+(v[a]-mid[0])*scale,row*300+150-(v[b]-mid[1])*scale)
    for p in sorted(polys,key=lambda p:sum(verts[i][c] for i,_ in p)/len(p)):
        tx=sum(uv[j][0] for _,j in p)/len(p);ty=sum(uv[j][1] for _,j in p)/len(p)
        col=tex.getpixel((min(tex.width-1,max(0,int(tx*tex.width))),min(tex.height-1,max(0,int((1-ty)*tex.height)))))
        d.polygon([proj(verts[i]) for i,_ in p],fill=col)
    d.text((20,row*300+10),label,fill='black')
canvas.save(out/'model-reference.png')
(out/'geometry.json').write_text(json.dumps({'verts':verts,'polys':polys,'uv':uv}))
