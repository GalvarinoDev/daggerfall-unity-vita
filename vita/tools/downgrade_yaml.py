# Rewrites 2018.2+/2019 prefab and scene YAML into the 2017.4 format (matches what 2017.4 itself writes).
# No args: converts every .prefab/.unity under Assets that still has 2018+ fields. Idempotent.
import glob, re
prefab_guids = {re.search(r'^guid: (\w+)', open(m).read(), re.M).group(1)
                for m in glob.glob('Assets/**/*.prefab.meta', recursive=True)}
ROOT = '''--- !u!1001 &100100000
Prefab:
  m_ObjectHideFlags: 1
  serializedVersion: 2
  m_Modification:
    m_TransformParent: {fileID: 0}
    m_Modifications: []
    m_RemovedComponents: []
  m_ParentPrefab: {fileID: 0}
  m_RootGameObject: {fileID: %s}
  m_IsPrefabParent: 1
'''
# uGUI package script guid -> class fileID inside 2017.4's UnityEngine.UI.dll (written by the 2017.4 editor itself)
UI_DLL = 'f70555f144d8491a825f0804e09c671c'
UGUI = {'76c392e42b5098c458856cdf6ecaaaa1': -619905303,  # EventSystem
        '4f231c4fb786f3946a6b90b886c48677': 1077351063,  # StandaloneInputModule
        '2d49b7c1bcd2e07499844da127be038d': 1997211142,  # TouchInputModule
        '0cd44c1031e13a943bb63640046fad76': 1980459831,  # CanvasScaler
        'dc42784cf147c0c48a680349fa168899': 1301386320}  # GraphicRaycaster
files = glob.glob('Assets/**/*.prefab', recursive=True) + glob.glob('Assets/**/*.unity', recursive=True)
for path in files:
    t = open(path, newline='').read()
    if 'm_CorrespondingSourceObject' not in t and 'type: 3' not in t: continue
    nl = '\r\n' if '\r\n' in t else '\n'
    t = t.replace('\r\n', '\n')
    old = t
    # 2018.3+ prefab assets have no legacy Prefab root object; 2017 needs it
    if path.endswith('.prefab') and '--- !u!1001 &100100000' not in t:
        root = re.search(r'^--- !u!(?:4|224) &\d+\n\w+:\n(?:  .*\n|    .*\n)*?  m_GameObject: \{fileID: (\d+)\}\n(?:  .*\n)*?  m_Father: \{fileID: 0\}', t, re.M)
        t = t.replace('m_PrefabInstance: {fileID: 0}', 'm_PrefabInstance: {fileID: 100100000}')
        head, sep, body = t.partition('--- ')
        t = head + ROOT % root.group(1) + sep + body
    t = re.sub(r'^PrefabInstance:\n((?:  .*\n|    .*\n)*?  m_SourcePrefab: .*\n)', r'Prefab:\n\1  m_IsPrefabParent: 0\n', t, flags=re.M)
    t = re.sub(r'^(GameObject:\n(?:  .*\n)*?  serializedVersion: )6$', r'\g<1>5', t, flags=re.M)
    t = re.sub(r'^  m_PrefabAsset: \{fileID: 0\}\n', '', t, flags=re.M)
    for a, b in (('m_CorrespondingSourceObject:', 'm_PrefabParentObject:'), ('m_PrefabInstance:', 'm_PrefabInternal:'),
                 ('m_SourcePrefab:', 'm_ParentPrefab:'), ('m_IsPrefabAsset:', 'm_IsPrefabParent:')):
        t = t.replace(a, b)
    # prefabs are native assets (type 3) since 2018.3; 2017 references them as type 2
    t = re.sub(r'guid: (\w{32}),(\s+)type: 3', lambda m: m.group(0) if m.group(1) not in prefab_guids
               else 'guid: %s,%stype: 2' % (m.group(1), m.group(2)), t)
    t = re.sub(r'\{fileID: 11500000, guid: (\w{32}),(\s+)type: 3\}', lambda m: m.group(0) if m.group(1) not in UGUI
               else '{fileID: %d, guid: %s,%stype: 3}' % (UGUI[m.group(1)], UI_DLL, m.group(2)), t)
    if t != old:
        open(path, 'w', newline='').write(t.replace('\n', nl))
        print(path)
