from pathlib import Path
import re

root = Path(r'E:/github/RPG-2')
folder = root / 'Assets/_Project/Code/CharacterAnimation'
intent_script = '594c8466812546c2b8faf39b6e77b334'
pairs = [('Default', '6fa8bddfb5404c5f8c5664544e3dd114'), ('Network', '1adb3655298d4600ac6a7c5a44f2c517')]
for name, guid in pairs:
    solver = folder / f'{name}CharacterIK.asset'
    text = solver.read_text(encoding='utf-8')
    assert 'schemaVersion:' not in text
    header, entries = text.split('  actions:\n', 1)
    blocks = re.findall(r'^  - id: (.*?)(?=^  - id: |\Z)', entries, re.M | re.S)
    policies = {}
    intent_entries = []
    for block in blocks:
        action_id, body = block.split('\n', 1)
        policies[action_id] = {k: re.search(rf'^    {k}: (.+)$', body, re.M).group(1) for k in ('allowAim','allowBodyTurn','yawOffset')}
        intent_body = re.sub(r'^    (allowAim|allowBodyTurn|yawOffset): .*\n', '', body, flags=re.M)
        intent_entries.append(f'  - id: {action_id}\n{intent_body}')
    global_yaw = re.search(r'^  yawOffset: (.+)$', header, re.M).group(1)
    waist = re.search(r'^  waistPitchOffset: (.+)$', header, re.M).group(1)
    intent_header = re.sub(r'guid: [a-f0-9]{32}', f'guid: {intent_script}', header, count=1)
    intent_header = intent_header[:intent_header.index('  modules:')]
    intent_header = intent_header.replace(f'm_Name: {name}CharacterIK', f'm_Name: {name}CharacterIKIntent')
    (folder / f'{name}CharacterIKIntent.asset').write_text(intent_header + f'  schemaVersion: 1\n  waistPitchOffset: {waist}\n  actions:\n' + ''.join(intent_entries), encoding='utf-8', newline='\n')
    (folder / f'{name}CharacterIKIntent.asset.meta').write_text(f'fileFormatVersion: 2\nguid: {guid}\nNativeFormatImporter:\n  externalObjects: {{}}\n  mainObjectFileID: 11400000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n', encoding='utf-8', newline='\n')
    header = re.sub(r'^  (yawOffset|waistPitchOffset): .*\n', '', header, flags=re.M)
    solver.write_text(header + '  schemaVersion: 1\n', encoding='utf-8', newline='\n')
    animation = folder / f'{name}CharacterAnimation.asset'
    animation_text = animation.read_text(encoding='utf-8')
    assert 'posePolicyVersion:' not in animation_text
    animation_text = animation_text.replace('  channels:\n', f'  posePolicyVersion: 1\n  yawOffset: {global_yaw}\n  channels:\n', 1)
    for action_id, values in policies.items():
        prefix = f'  - id: {action_id}\n'
        assert prefix in animation_text
        animation_text = animation_text.replace(prefix, prefix + ''.join(f'    {k}: {v}\n' for k,v in values.items()), 1)
    animation.write_text(animation_text, encoding='utf-8', newline='\n')

prefab = root / 'Assets/_Project/Prefabs/CharacterPresentation.prefab'
text = prefab.read_text(encoding='utf-8')
line = '  ikProfile: {fileID: 11400000, guid: b4a726c20d404ae68b0398c54e43ff10, type: 2}\n'
assert text.count(line) == 1
text = text.replace(line, line + f'  intentProfile: {{fileID: 11400000, guid: {pairs[0][1]}, type: 2}}\n')
prefab.write_text(text, encoding='utf-8', newline='\n')
prefab = root / 'Assets/_Project/Prefabs/OnlinePlayer.prefab'
text = prefab.read_text(encoding='utf-8')
anchor = '      - target: {fileID: 610000000000000001, guid: f2d4f027e36129f4e9c02cfb0488b20d, type: 3}\n'
assert text.count(anchor) == 1
text = text.replace(anchor, '      - target: {fileID: 860000000000000001, guid: f2d4f027e36129f4e9c02cfb0488b20d, type: 3}\n        propertyPath: intentProfile\n        value:\n' + f'        objectReference: {{fileID: 11400000, guid: {pairs[1][1]}, type: 2}}\n' + anchor)
prefab.write_text(text, encoding='utf-8', newline='\r\n')

for filename, line in [('Player.cs','        public bool WeaponIKEnabled => presenter != null && presenter.WeaponIKEnabled;\n'),('PlayerPresenter.cs','        internal bool WeaponIKEnabled => enabled && animationSystem != null && animationSystem.WeaponIKEnabled;\n')]:
    p=root / 'Assets/_Project/Code/PlayerMovement' / filename
    text=p.read_text(encoding='utf-8')
    assert text.count(line)==1
    p.write_text(text.replace(line,''), encoding='utf-8', newline='\r\n')

p=root / 'Assets/CombatGirlsCharacterPack/Biperworks_Tools/CombatGirls_Weapon_Control/Character_Weapon_Controller.cs'
text=p.read_text(encoding='utf-8')
text=text.replace('    private RPG4.PlayerMovement.Player player;', '    // Model ownership is static; never derive legacy writes from solved IK output.\n    private bool presentationOwnsPose;')
text=text.replace('        player = GetComponentInParent<RPG4.PlayerMovement.Player>();','        presentationOwnsPose = GetComponent<RPG4.CharacterAnimation.CharacterPresentationSystem>() != null;')
start=text.index('        // A zero-weight layer may skip IK callbacks; discard the old blend even then.')
end=text.index('        if (defaultEventSetting == null', start)
text=text[:start]+'        if (presentationOwnsPose) return;\n\n'+text[end:]
text=text.replace('        if (string.IsNullOrEmpty(triggerEventName)) return;', '        if (presentationOwnsPose || string.IsNullOrEmpty(triggerEventName)) return;')
start=text.index('        if (player != null && !player.WeaponIKEnabled)')
end=text.index('        // Iterate through all tracked IK modules',start)
text=text[:start]+'        if (presentationOwnsPose) return;\n\n'+text[end:]
p.write_text(text, encoding='utf-8', newline='\r\n')
