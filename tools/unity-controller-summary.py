r"""Summarises a Unity AnimatorController (.controller YAML): parameters, layers, and for every
state machine its states with tag, motion, speed, and each transition with its conditions.

    python mods\Werewolf\tools\unity-controller-summary.py <file.controller>
"""
import re, sys

path = sys.argv[1]
text = open(path, encoding="utf-8", errors="replace").read()
docs = {}
for m in re.finditer(r"^--- !u!(\d+) &(-?\d+)\n(.*?)(?=^--- !u!|\Z)", text, re.S | re.M):
    docs[int(m.group(2))] = (int(m.group(1)), m.group(3))

def field(body, name):
    m = re.search(r"^\s*" + re.escape(name) + r":\s*(.*)$", body, re.M)
    return m.group(1).strip() if m else None

def fid(s):
    m = re.search(r"fileID:\s*(-?\d+)", s or "")
    return int(m.group(1)) if m else 0

def block(body, name):
    # a top-level key of the document (two-space indent) up to the next sibling key
    m = re.search(r"^  " + re.escape(name) + r":\s*(?:\[\]|\{\})?\n((?:(?:  - .*|    .*|\s*)\n)*)", body, re.M)
    return m.group(1) if m else ""

PTYPE = {1: "Float", 3: "Int", 4: "Bool", 9: "Trigger"}
MODE = {1: "If", 2: "IfNot", 3: "Greater", 4: "Less", 6: "Equals", 7: "NotEqual"}

controllers = [(f, b) for f, (c, b) in docs.items() if c == 91]
for f, b in controllers:
    print("CONTROLLER", field(b, "m_Name"))
    params = block(b, "m_AnimatorParameters")
    for pm in re.finditer(r"- m_Name:\s*(\S+)\n(?:.*\n)*?\s+m_Type:\s*(\d+)", params):
        print(f"  param {PTYPE.get(int(pm.group(2)), pm.group(2)):8s} {pm.group(1)}")
    layers = block(b, "m_AnimatorLayers")
    for i, lm in enumerate(re.finditer(r"- serializedVersion: \d+\n\s+m_Name:\s*(.*)\n\s+m_StateMachine:\s*(\{.*\})(?:.*\n)*?\s+m_BlendingMode:\s*(\d+)(?:.*\n)*?\s+m_DefaultWeight:\s*([0-9.]+)(?:.*\n)*?\s+m_IKPass:\s*(\d)", layers)):
        print(f"  layer {i}: '{lm.group(1)}' blending={'Additive' if lm.group(3)=='1' else 'Override'} weight={lm.group(4)} sm={fid(lm.group(2))}")

def motion_name(mref):
    m = re.search(r"fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-f]+))?", mref or "")
    if not m: return "-"
    f = int(m.group(1))
    if f == 0: return "(none)"
    if f in docs:
        cls, body = docs[f]
        if cls == 206:  # BlendTree
            kids = re.findall(r"m_Motion:\s*(\{.*\})", block(body, "m_Childs") or body)
            thr = re.findall(r"m_Threshold:\s*([-0-9.]+)", body)
            return f"BlendTree(type={field(body,'m_BlendType')} param={field(body,'m_BlendParameter')} " + ", ".join(f"{motion_name(k)}@{t}" for k, t in zip(kids, thr)) + ")"
        return field(body, "m_Name") or str(f)
    return f"clip:{m.group(2)[:8] if m.group(2) else f}"

def conditions(tbody):
    out = []
    for cm in re.finditer(r"m_ConditionMode:\s*(\d+)\n\s+m_ConditionEvent:\s*(\S+)\n\s+m_EventTreshold:\s*([-0-9.e]+)", tbody):
        mode = MODE.get(int(cm.group(1)), cm.group(1))
        out.append(f"{cm.group(2)} {mode} {cm.group(3)}" if mode not in ("If", "IfNot") else f"{mode} {cm.group(2)}")
    return " && ".join(out) if out else "(no conditions)"

def transition(tf, indent):
    cls, tb = docs[tf]
    dst = fid(field(tb, "m_DstState"))
    dsm = fid(field(tb, "m_DstStateMachine"))
    exit_ = field(tb, "m_IsExit")
    if dst in docs: target = "-> " + (field(docs[dst][1], "m_Name") or "?")
    elif dsm in docs: target = "-> SM " + (field(docs[dsm][1], "m_Name") or "?")
    elif exit_ == "1": target = "-> EXIT"
    else: target = "-> ?"
    het = field(tb, "m_HasExitTime")
    print(" " * indent + f"{target:28s} [{conditions(tb)}]  exitTime={field(tb,'m_ExitTime') if het=='1' else 'off'} dur={field(tb,'m_TransitionDuration')} fixed={field(tb,'m_HasFixedDuration')} offset={field(tb,'m_TransitionOffset')} interruption={field(tb,'m_InterruptionSource')} canSelf={field(tb,'m_CanTransitionToSelf')}")

def state_machine(smf, indent):
    cls, sb = docs[smf]
    print(" " * indent + f"STATE MACHINE '{field(sb,'m_Name')}' default={field(docs[fid(field(sb,'m_DefaultState'))][1],'m_Name') if fid(field(sb,'m_DefaultState')) in docs else '?'}")
    anyst = block(sb, "m_AnyStateTransitions")
    for t in re.findall(r"fileID:\s*(-?\d+)", anyst):
        print(" " * (indent + 2) + "ANY", end="")
        transition(int(t), 1)
    entry = block(sb, "m_EntryTransitions")
    for t in re.findall(r"fileID:\s*(-?\d+)", entry):
        print(" " * (indent + 2) + "ENTRY", end="")
        transition(int(t), 1)
    for sm in re.finditer(r"m_State:\s*\{fileID:\s*(-?\d+)\}", block(sb, "m_ChildStates")):
        sf = int(sm.group(1))
        if sf not in docs: continue
        st = docs[sf][1]
        tag = field(st, "m_Tag")
        print(" " * (indent + 2) + f"STATE '{field(st,'m_Name')}'  tag='{tag}'  motion={motion_name(field(st,'m_Motion'))}  speed={field(st,'m_Speed')} speedParam={field(st,'m_SpeedParameter') if field(st,'m_SpeedParameterActive')=='1' else '-'} mirror={field(st,'m_Mirror')} writeDefaults={field(st,'m_WriteDefaultValues')} cycleOffset={field(st,'m_CycleOffset')} behaviours={len(re.findall(r'fileID', block(st,'m_StateMachineBehaviours')))}")
        for t in re.findall(r"fileID:\s*(-?\d+)", block(st, "m_Transitions")):
            transition(int(t), indent + 6)
    for cm in re.finditer(r"m_StateMachine:\s*\{fileID:\s*(-?\d+)\}", block(sb, "m_ChildStateMachines")):
        state_machine(int(cm.group(1)), indent + 2)

for f, b in controllers:
    layers = block(b, "m_AnimatorLayers")
    for lm in re.finditer(r"m_StateMachine:\s*\{fileID:\s*(-?\d+)\}", layers):
        print()
        state_machine(int(lm.group(1)), 2)
    print()
    print("STATE MACHINE BEHAVIOURS:")
    for sf, (c, sb) in docs.items():
        if c == 114:
            print(f"  script guid={re.search(r'guid: ([0-9a-f]+)', field(sb,'m_Script') or '') and re.search(r'guid: ([0-9a-f]+)', field(sb,'m_Script')).group(1)}  fields: " + ", ".join(l.strip() for l in sb.splitlines() if l.startswith("  ") and not l.strip().startswith("m_")))
