from pathlib import Path
import re
ROOT = Path(__file__).resolve().parents[2]
CONTROLLER = ROOT / "Assets/player/player.controller"
BASELINE = ROOT / ".codex-task-snapshot/body-fsm-20261025/baseline/Assets/player/player.controller"
text = BASELINE.read_text(encoding="utf-8-sig")
parts = re.split(r"(?=^--- !u!)", text, flags=re.M)
header = parts[0]
docs = {re.search(r"^--- !u!\d+ &(-?\d+)", p)[1]: p for p in parts[1:]}
def set_field(doc, key, value):
    return re.sub(r"^  " + re.escape(key) + r":.*$", "  " + key + ": " + value, doc, flags=re.M)
def state(source, ident, name, motion, parameter=None):
    doc = re.sub(r"^--- !u!1102 &-?\d+", "--- !u!1102 &" + str(ident), docs[str(source)])
    doc = set_field(doc, "m_Name", name)
    doc = re.sub(r"^  m_Motion:.*(?:\n    type: 3})?", "  m_Motion: " + motion, doc, flags=re.M)
    doc = set_field(doc, "m_SpeedParameterActive", "1" if parameter else "0")
    return set_field(doc, "m_SpeedParameter", parameter or "")
def ref(folder, filename):
    s = (ROOT / "Assets/CombatGirlsCharacterPack/RifleGirl/Animations" / folder / (filename + ".fbx.meta")).read_text()
    return "{fileID: " + re.search(r"74: (-?\d+)", s)[1] + ", guid: " + re.search(r"^guid: (\w+)", s, re.M)[1] + ", type: 3}"
def tree(ident, name, entries, direction=False):
    doc = """--- !u!206 &{ident}
BlendTree:
  m_ObjectHideFlags: 1
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_Name: {name}
  m_Childs:
""".format(ident=ident,name=name)
    for folder, filename, threshold, x, y in entries:
        doc += """  - serializedVersion: 2
    m_Motion: {motion}
    m_Threshold: {threshold}
    m_Position: {{x: {x}, y: {y}}}
    m_TimeScale: 1
    m_CycleOffset: 0
    m_DirectBlendParameter: 
    m_Mirror: 0
""".format(motion=ref(folder, filename), threshold=threshold,x=x,y=y)
    doc += """  m_BlendParameter: {parameter}
  m_BlendParameterY: {parameter_y}
  m_MinThreshold: 0
  m_MaxThreshold: 1
  m_UseAutomaticThresholds: 0
  m_NormalizedBlendValues: 0
  m_BlendType: {kind}
""".format(parameter="AimMoveX" if direction else "MoveSpeed",parameter_y="AimMoveY" if direction else "",kind=1 if direction else 0)
    return doc
# Retain the existing Idle/Run document IDs as the two free locomotion states.
docs["-658891758991643330"] = state(-658891758991643330,-658891758991643330,"FreeLocomotion","{fileID: 710000021}")
docs["-2978682099558440354"] = state(-2978682099558440354,-2978682099558440354,"CrouchFreeLocomotion","{fileID: 710000022}")
docs["710000020"] = state(710000010,710000020,"CrouchAimLocomotion","{fileID: 710000023}","AimLocomotionSpeed")
docs["710000021"] = tree(710000021,"FreeLocomotion",[("Normal","R_Idle",0,0,0),("Normal","R_Run",1,0,0)])
docs["710000022"] = tree(710000022,"CrouchFreeLocomotion",[("Normal","R_Crouch_Idle",0,0,0),("Normal","R_Crouch_Walk",0.5,0,0),("Normal","R_Crouch_Jog",1,0,0)])
docs["710000023"] = tree(710000023,"CrouchAimLocomotion",[("Aiming","R_Crouch_AimIdle",0,0,0),("Aiming","R_Crouch_AimWalk_F",0,0,1),("Aiming","R_Crouch_AimWalk_B",0,0,-1),("Aiming","R_Crouch_AimWalk_FL",0,-0.70710677,0.70710677),("Aiming","R_Crouch_AimWalk_FR",0,0.70710677,0.70710677),("Aiming","R_Crouch_AimWalk_BL",0,-0.70710677,-0.70710677),("Aiming","R_Crouch_AimWalk_BR",0,0.70710677,-0.70710677)],True)
# Keep the established shooting layer, mask and IK pass. Add only reload states.
docs["710000024"] = state(710000014,710000024,"Reload",ref("Aiming","R_Reload"),"ReloadSpeed")
docs["710000025"] = state(710000014,710000025,"CrouchReload",ref("Aiming","R_Crouch_Reload"),"ReloadSpeed")
docs["-2160913602538721414"] = docs["-2160913602538721414"].replace("  m_ChildStateMachines: []", "  - serializedVersion: 1\n    m_State: {fileID: 710000020}\n    m_Position: {x: 720, y: 240, z: 0}\n  m_ChildStateMachines: []",1)
docs["710000012"] = docs["710000012"].replace("  m_ChildStateMachines: []", "  - serializedVersion: 1\n    m_State: {fileID: 710000024}\n    m_Position: {x: 550, y: 280, z: 0}\n  - serializedVersion: 1\n    m_State: {fileID: 710000025}\n    m_Position: {x: 800, y: 280, z: 0}\n  m_ChildStateMachines: []",1)
parameters = ""
for name, default in [("MoveSpeed",0),("ReloadSpeed",1)]:
    parameters += """  - m_Name: {name}
    m_Type: 1
    m_DefaultFloat: {default}
    m_DefaultInt: 0
    m_DefaultBool: 0
    m_Controller: {{fileID: 9100000}}
""".format(name=name,default=default)
docs["9100000"] = docs["9100000"].replace("  m_AnimatorLayers:",parameters+"  m_AnimatorLayers:",1)
# Avoid introducing trailing whitespace in newly added documents.
for ident in ["710000020","710000021","710000022","710000023","710000024","710000025"]:
    docs[ident] = "\n".join(line.rstrip() for line in docs[ident].splitlines()) + "\n"
# Preserve the controller's original newline style.
with CONTROLLER.open("w", encoding="utf-8", newline="") as f:
    f.write((header + "".join(docs.values())).replace("\n","\r\n"))
print("Updated controller with four locomotion trees and two reload states.")
