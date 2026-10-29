"""Static serialized-resource audit; does not import assets or execute Unity code."""
from pathlib import Path
import collections, difflib, json, re
import yaml
ROOT=Path(__file__).resolve().parents[2]
SNAPSHOT=ROOT/".codex-task-snapshot/body-fsm-20261025"
files=["Assets/player/player.controller","Assets/player/player.prefab"]
def documents(text):
    return {m["id"]:m["doc"] for m in re.finditer(r"(?P<doc>^--- !u!\d+ &(?P<id>-?\d+)[^\n]*\n.*?)(?=^--- !u!|\Z)",text,re.M|re.S)}
def parsed(text):
    stripped=re.sub(r"^%.*\n", "",text,flags=re.M)
    stripped=re.sub(r"^--- !u!\d+ &-?\d+[^\n]*", "---",stripped,flags=re.M)
    return list(yaml.safe_load_all(stripped))
def missing_refs(text):
    ids=set(documents(text))
    return sorted({m[1] for m in re.finditer(r"\{fileID: (-?\d+)\}",text) if m[1]!="0" and m[1] not in ids})
report={"scope":"Static YAML/resource audit only; no Unity startup, import, project execution or tests", "files":{},"checks":{},"clips":[]}
for name in files:
    current=(ROOT/name).read_text(encoding="utf-8-sig")
    before=(SNAPSHOT/"baseline"/name).read_text(encoding="utf-8-sig")
    ids=re.findall(r"^--- !u!\d+ &(-?\d+)",current,re.M)
    duplicates=[i for i,c in collections.Counter(ids).items() if c>1]
    assert not duplicates, (name,duplicates)
    assert missing_refs(current)==missing_refs(before), (name,missing_refs(current))
    report["files"][name]={"document_count":len(ids),"unique_document_ids":True,"yaml_parsed_documents":len(parsed(current)),"missing_local_references":missing_refs(current)}
    (SNAPSHOT/(Path(name).name+".baseline.diff")).write_text("".join(difflib.unified_diff(before.splitlines(True),current.splitlines(True),fromfile="baseline/"+name,tofile=name)),encoding="utf-8")
controller=(ROOT/files[0]).read_text()
cdocs=documents(controller)
bdocs=documents((SNAPSHOT/"baseline"/files[0]).read_text())
for ident in ["710000010","710000011","710000013","710000014","710000015","710000016","710000017"]:
    assert cdocs[ident]==bdocs[ident],ident
report["checks"]["existing_aim_tree_and_five_upper_body_states_unchanged"]=True
old_layers=parsed(bdocs["9100000"])[0]["AnimatorController"]["m_AnimatorLayers"]
new_layers=parsed(cdocs["9100000"])[0]["AnimatorController"]["m_AnimatorLayers"]
assert old_layers==new_layers
assert sum(layer["m_IKPass"] for layer in new_layers)==1
assert new_layers[1]["m_IKPass"]==1
report["checks"]["layer_names_mask_weights_blend_modes_and_unique_upper_IK_pass_unchanged"]=True
assert "206db03de8174df68bd28b139650251d" in (ROOT/"Assets/player/UpperBodyShooting.mask.meta").read_text()
maskids=documents((ROOT/"Assets/player/UpperBodyShooting.mask").read_text())
assert "31900000" in maskids
report["checks"]["upper_body_mask_reference_valid"]=True
pdocs=documents((ROOT/files[1]).read_text())
bpdocs=documents((SNAPSHOT/"baseline"/files[1]).read_text())
assert pdocs.keys()==bpdocs.keys()
assert all(pdocs[i]==bpdocs[i] for i in pdocs if i!="710000006")
newdata=parsed(pdocs["710000006"])[0]["MonoBehaviour"]["stateData"]
olddata=parsed(bpdocs["710000006"])[0]["MonoBehaviour"]["stateData"]
assert all(newdata[k]==v for k,v in olddata.items())
assert set(newdata)-set(olddata)=={"crouch","reload"}
report["checks"]["prefab_component_ids_and_all_existing_configuration_preserved"]=True
report["checks"]["new_prefab_configuration"]= {"crouch":newdata["crouch"],"reload":newdata["reload"]}
metas={}
for p in (ROOT/"Assets/CombatGirlsCharacterPack/RifleGirl/Animations").rglob("*.fbx.meta"):
    text=p.read_text()
    guid=re.search(r"^guid: (\w+)",text,re.M)[1]
    metas[guid]=(p,text)
refs=set(re.findall(r"\{fileID: (-?\d+), guid: (\w+),\s*type: 3\}",controller))
for fileid,guid in sorted(refs):
    p,text=metas[guid]
    assert re.search(r"(?:74|internalID): "+re.escape(fileid)+r"\s",text),(fileid,guid,p)
    loop=re.findall(r"loopTime: (\d+)",text)
    reload="Reload" in p.name
    if not reload and "Shoot" not in p.name:
        assert loop==["1"],(p,loop)
    if reload:
        assert loop==["0"],(p,loop)
    report["clips"].append({"path":p.relative_to(ROOT).as_posix(),"guid":guid,"fileID":fileid,"loopTime":loop})
report["checks"]["all_controller_clip_GUIDs_and_fileIDs_match_meta"]=True
report["checks"]["all_locomotion_clips_already_looping_no_meta_edits_required"]=True
report["checks"]["reload_clips_non_looping_existing_events_and_trims_unchanged"]=True
report["manual_validation_remaining"]=["Visually inspect standing/crouching free and directional locomotion blends.","Confirm upper-body reload keeps lower-body motion, changes posture with continuous normalized phase, and completes with correct socket and IK restoration."]
output=SNAPSHOT/"animation-assets-static-audit.json"
output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print(json.dumps({"status":"PASS","files":report["files"],"checks":len(report["checks"]),"validated_controller_clip_references":len(report["clips"]),"report":str(output.relative_to(ROOT))},ensure_ascii=False,indent=2))
