from pathlib import Path
import struct
import zlib

class Fbx:
    def __init__(self, path):
        self.data = Path(path).read_bytes()
        self.pos = 27
        self.wide = struct.unpack_from('<I', self.data, 23)[0] >= 7500
        self.nodes = []
        while True:
            node = self.node()
            if node is None: break
            self.nodes.append(node)

    def read(self, fmt):
        values = struct.unpack_from('<' + fmt, self.data, self.pos)
        self.pos += struct.calcsize('<' + fmt)
        return values[0] if len(values) == 1 else values

    def prop(self):
        kind = chr(self.read('B'))
        if kind in {'Y','C','I','F','D','L'}:
            return self.read({'Y':'h','C':'?','I':'i','F':'f','D':'d','L':'q'}[kind])
        if kind in {'S','R'}:
            size = self.read('I'); value = self.data[self.pos:self.pos+size]; self.pos += size
            return value.decode('utf-8',errors='replace') if kind == 'S' else value
        count, encoding, size = self.read('III')
        value = self.data[self.pos:self.pos+size]; self.pos += size
        if encoding: value = zlib.decompress(value)
        return struct.unpack('<' + str(count) + {'f':'f','d':'d','i':'i','l':'q','b':'B','c':'B'}[kind], value)

    def node(self):
        end, count, _ = self.read('QQQ' if self.wide else 'III')
        length = self.read('B')
        if end == 0: return None
        name = self.data[self.pos:self.pos+length].decode(); self.pos += length
        props = [self.prop() for _ in range(count)]
        children = []
        while self.pos < end:
            child = self.node()
            if child is None: break
            children.append(child)
        self.pos = end
        return name, props, children

def child(node, name):
    return next((n for n in node[2] if n[0] == name), None)

def inspect(path):
    fbx = Fbx(path)
    objects = next(n for n in fbx.nodes if n[0] == 'Objects')
    connections = next(n for n in fbx.nodes if n[0] == 'Connections')
    by_id = {n[1][0]: n for n in objects[2] if n[1]}
    models = {n[1][0]: n for n in objects[2] if n[0] == 'Model'}
    selected = {'spine_01','spine_02','spine_03','clavicle_r','upperarm_r','lowerarm_r','hand_r'}
    curve_nodes = {}
    curves = {}
    for _, p, _ in connections[2]:
        if p[0] != 'OP': continue
        source, target, channel = p[1:4]
        if source not in by_id: continue
        if by_id[source][0] == 'AnimationCurveNode' and target in models and channel == 'Lcl Rotation':
            curve_nodes[target] = source
        elif by_id[source][0] == 'AnimationCurve':
            curves[(target,channel)] = by_id[source]
    print(Path(path).name)
    for mid, model in models.items():
        name = model[1][1].split('\x00')[0].split('::')[-1]
        if name not in selected: continue
        rows = []
        cn = curve_nodes.get(mid)
        for axis in 'XYZ':
            curve = curves.get((cn,'d|'+axis))
            if curve is None: rows.append(None); continue
            values = child(curve,'KeyValueFloat')[1][0]
            times = child(curve,'KeyTime')[1][0]
            rows.append(tuple(round(v,3) for v in (values[0], min(values), max(values), values[-1])))
        print(name, 'XYZ(first,min,max,last):',rows)

if __name__ == '__main__':
    for name in ['R_AimIdle','R_Shoot','R_AimIdle_AutoShoot']:
        inspect(Path('Assets/CombatGirlsCharacterPack/RifleGirl/Animations/Aiming')/(name+'.fbx'))
