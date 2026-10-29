import json
import re
from pathlib import Path
from PIL import Image
import yaml

root = Path(r'E:\github\RPG-2')
task = root / '.codex-task-snapshot/ui-weapon-icons-20261026'
records = json.loads((task / 'inventory-asset-records.json').read_text(encoding='utf-8'))

def documents(path):
    text = path.read_text(encoding='utf-8-sig')
    text = re.sub(r'^%.*\n', '', text, flags=re.M)
    text = re.sub(r'^--- !u!\d+ &(-?\d+)(?: stripped)?$', r'---\nfile_id: \1', text, flags=re.M)
    return list(yaml.safe_load_all(text))

def guid(path):
    return re.search(r'^guid: (\w+)', path.read_text(encoding='utf-8'), re.M).group(1)

catalog = documents(root / 'Assets/backpack/Data/Items/物品清单目录.asset')[0]['MonoBehaviour']['manifests']
tags = documents(root / 'Assets/backpack/Data/TagRegistry.asset')[0]['MonoBehaviour']['tags']
assert len(catalog) == 10
assert len({x['guid'] for x in catalog}) == len(catalog)
assert len(set(tags)) == len(tags)
assert 'FragmentTags.Default.Description' in tags
assert guid(root / 'Assets/backpack/Scripts/Items/Manifest/ItemManifest.cs.meta') == '4c34e53e651b4930ac2714e72c40d43d'
assert 'm_Name: Hand_R_Socket' in (root / 'Assets/player/player.prefab').read_text(encoding='utf-8')
known_guids = {}
for directory in ('Assets/UI素材', 'Assets/backpack/Data/Items/枪械', 'Assets/backpack/Prefabs/Weapons'):
    for file in (root / directory).rglob('*'):
        if file.suffix == '.meta':
            ident = guid(file)
            assert ident not in known_guids, (file, known_guids.get(ident))
            known_guids[ident] = str(file)
        elif file.is_file():
            assert Path(str(file) + '.meta').exists(), file

report = []
for r in records:
    item = documents(root / r['item'])[0]['MonoBehaviour']
    assert item['m_Name'] == r['name']
    assert item['itemType']['name'] == r['item_type']
    assert r['item_type'] in tags
    assert re.fullmatch(r'[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*', r['item_type'])
    assert sum(x['guid'] == r['item_guid'] for x in catalog) == 1
    assert guid(Path(str(root / r['item']) + '.meta')) == r['item_guid']
    refs = item['references']['RefIds']
    assert {x['rid'] for x in item['fragments']} == {x['rid'] for x in refs}
    assert len(refs) == len({x['rid'] for x in refs}) == 4
    fragments = {x['type']['class']: x['data'] for x in refs}
    assert set(fragments) == {'GridFragment', 'ImageFragment', 'EquipmentFragment', 'TextFragment'}
    assert all(x['type']['ns'] == 'RPG4.Backpack.Items' and x['type']['asm'] == 'RPG4.Backpack.Items' for x in refs)
    assert fragments['GridFragment']['width'] == 4 and fragments['GridFragment']['height'] == 2
    assert fragments['ImageFragment']['icon'] == dict(fileID=21300000, guid=r['icon_guid'], type=3)
    assert fragments['ImageFragment']['iconDimensions'] == dict(x=1024, y=512)
    assert fragments['EquipmentFragment']['equipPrefab'] == dict(fileID=6363982380300774935, guid=r['prefab_guid'], type=3)
    assert fragments['EquipmentFragment']['socketAttachPoint'] == 'Hand_R_Socket'
    assert fragments['EquipmentFragment']['modifiers'] == []
    assert fragments['TextFragment']['text']
    prefab_docs = documents(root / r['prefab'])
    assert any(d['file_id'] == 6363982380300774935 and 'GameObject' in d for d in prefab_docs)
    mesh = next(d['MeshRenderer'] for d in prefab_docs if 'MeshRenderer' in d)
    assert mesh['m_Materials'] == [dict(fileID=2100000, guid=r['material_guid'], type=2)]
    assert mesh['m_Materials'][0]['guid'] in {guid(p) for p in (root / 'Assets/CombatGirlsCharacterPack/RifleGirl/Animations/Materials/Weapon').glob('*.mat.meta')}
    assert guid(Path(str(root / r['prefab']) + '.meta')) == r['prefab_guid']
    assert guid(Path(str(root / r['icon']) + '.meta')) == r['icon_guid']
    meta = yaml.safe_load(Path(str(root / r['icon']) + '.meta').read_text(encoding='utf-8'))['TextureImporter']
    assert meta['textureType'] == 8 and meta['spriteMode'] == 1 and meta['spriteMeshType'] == 1
    assert meta['spritePixelsToUnits'] == 256 and meta['maxTextureSize'] == 1024 and meta['nPOTScale'] == 0
    assert meta['alphaIsTransparency'] == 1 and meta['mipmaps']['enableMipMap'] == 0
    assert all(p['textureCompression'] == 0 and p['maxTextureSize'] == 1024 for p in meta['platformSettings'])
    with Image.open(root / r['icon']) as im:
        assert im.size == (1024, 512) and im.mode == 'RGBA'
        alpha = im.getchannel('A')
        assert alpha.getextrema() == (0, 255)
        assert all(alpha.getpixel(p) == 0 for p in ((0,0), (1023,0), (0,511), (1023,511)))
        bounds = alpha.point(lambda x: 255 if x > 12 else 0).getbbox()
        assert bounds[0] >= 24 and bounds[1] >= 24 and bounds[2] <= 1000 and bounds[3] <= 488
        report.append(dict(name=r['name'], pixels=im.size, grid=[4,2], visible_bounds=bounds, max_stack=1, references='pass'))

result = dict(status='pass', verification='static asset inspection only; no Unity or project logic executed', item_count=len(records), catalog_count=len(catalog), items=report)
(task / 'static-check-report.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(dict(status=result['status'], items=len(records), catalog_entries=len(catalog), images='4 RGBA 1024x512', references='pass', unity_executed=False)))
