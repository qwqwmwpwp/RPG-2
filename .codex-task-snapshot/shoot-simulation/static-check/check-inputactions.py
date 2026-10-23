import json
import re
from pathlib import Path


root = Path(__file__).resolve().parents[3]
asset_path = root / "Assets/input/InputActions.inputactions"
wrapper_path = root / "Assets/Scripts/Input/InputActions.cs"
player_input_path = root / "Assets/Scripts/Input/PlayerInput.cs"
report_path = Path(__file__).resolve().parent / "inputactions-report.json"

asset = json.loads(asset_path.read_text(encoding="utf-8-sig"))
wrapper_source = wrapper_path.read_text(encoding="utf-8-sig")
player_input_source = player_input_path.read_text(encoding="utf-8-sig")
embedded_match = re.search(r'FromJson\(@"(.*?)"\);', wrapper_source, re.S)
assert embedded_match, "Could not locate generated wrapper's embedded JSON."
embedded_asset = json.loads(embedded_match.group(1).replace('""', '"'))
assert embedded_asset == asset, "Embedded wrapper JSON does not match the inputactions asset."

player_map = next(action_map for action_map in asset["maps"] if action_map["name"] == "player")
fire_actions = [action for action in player_map["actions"] if action["name"] == "fire"]
fire_bindings = [binding for binding in player_map["bindings"] if binding["action"] == "fire"]
assert len(fire_actions) == 1 and fire_actions[0]["type"] == "Button"
assert len(fire_bindings) == 1 and fire_bindings[0]["path"] == "<Mouse>/leftButton"

callback_checks = {
    "action lookup": r'm_player_fire = m_player\.FindAction\("fire", throwIfNotFound: true\);',
    "generated field": r'private readonly InputAction m_player_fire;',
    "generated accessor": r'public InputAction @fire => m_Wrapper\.m_player_fire;',
    "interface method": r'void OnFire\(InputAction\.CallbackContext context\);',
    "started callback added": r'@fire\.started \+= instance\.OnFire;',
    "performed callback added": r'@fire\.performed \+= instance\.OnFire;',
    "canceled callback added": r'@fire\.canceled \+= instance\.OnFire;',
    "started callback removed": r'@fire\.started -= instance\.OnFire;',
    "performed callback removed": r'@fire\.performed -= instance\.OnFire;',
    "canceled callback removed": r'@fire\.canceled -= instance\.OnFire;',
}
for label, pattern in callback_checks.items():
    assert len(re.findall(pattern, wrapper_source)) == 1, f"{label} must appear exactly once."

api_checks = {
    "FirePressed public pulse": r'public bool FirePressed\s*=>',
    "FireHeld public snapshot": r'public bool FireHeld\s*=>',
    "fireArmed state": r'private bool fireArmed;',
    "capture-frame consumption": r'cursorCapturedThisFrame',
}
for label, pattern in api_checks.items():
    assert re.search(pattern, player_input_source), f"Missing {label}."

report = {
    "result": "passed",
    "checks": [
        "InputActions.inputactions parses as JSON.",
        "Generated wrapper embedded JSON matches the asset exactly.",
        "player.fire is one Button action with one <Mouse>/leftButton binding.",
        "Generated fire lookup, field, accessor, callback interface, and started/performed/canceled registration and removal are present once each.",
        "PlayerInput exposes FirePressed and FireHeld and contains fireArmed/capture-frame gating declarations.",
    ],
    "callbackDeclarationCount": len(callback_checks),
    "limitations": "Static text and JSON checks only; no Unity editor, project logic, tests, analyzers, or source generators were run.",
}
report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(f"Input action static consistency passed. Report: {report_path}")
