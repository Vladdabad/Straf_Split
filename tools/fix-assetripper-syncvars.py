"""
Repair the FishNet code-generator members that AssetRipper could not round-trip.

STRAFTAT's shipped Assembly-CSharp.dll was post-processed by FishNet's ILPostProcessor,
which injects a `sync___set_value_<field>(value, asServer)` method for every [SyncVar].
AssetRipper decompiled the *call sites* but not the *declarations*, and inlined the method
body into the SyncAccessor property using `value` for BOTH parameters. That single gap
produces CS1061 / CS0117 / CS0019 / CS1503. Separately it widened the `byte` index
parameter of ReadSyncVar___<Type> to `uint`, which breaks the SyncVarReadDelegate
binding (CS0123).

Signatures confirmed by reflecting FishNet.Runtime.dll:
    delegate bool SyncVarReadDelegate(PooledReader reader, byte index, bool asServer)
    void SyncVar<T>.SetValue(T nextValue, bool calledByUser)

Run with --apply to write; default is a dry run.
"""

import re
import sys
from pathlib import Path

ROOT = Path(r"D:\Straftat splitscreen\UnityProject\ExportedProject\Assets\Scripts\Assembly-CSharp")
APPLY = "--apply" in sys.argv

# public SyncVar<float> syncVar___health;
DECL = re.compile(
    r"^(?P<ind>[ \t]*)public SyncVar<(?P<type>.+?)> syncVar___(?P<name>\w+);[ \t\r]*$", re.M
)

# The SyncAccessor getter tells us the real backing field name.
def accessor_field(text, name, type_):
    m = re.search(
        r"public\s+" + re.escape(type_) + r"\s+SyncAccessor_" + re.escape(name) +
        r"\s*\r?\n[ \t]*\{\s*\r?\n[ \t]*get\s*\r?\n[ \t]*\{\s*\r?\n[ \t]*return\s+(\w+);",
        text,
    )
    return m.group(1) if m else name


# The mis-inlined setter body:
#     if (value || !base.IsServer)
#     {
#         health = value;
#     }
#     syncVar___health.SetValue(value, value);
INLINED_SETTER = re.compile(
    r"(?P<ind>[ \t]*)if \(value \|\| !base\.IsServer\)[ \t]*\r?\n"
    r"(?P=ind)\{[ \t]*\r?\n"
    r"(?P=ind)\t[^\r\n]*\r?\n"
    r"(?P=ind)\}[ \t]*\r?\n"
    r"(?P=ind)syncVar___(?P<name>\w+)\.SetValue\(value, value\);"
)

# public virtual bool ReadSyncVar___PlayerHealth(PooledReader PooledReader0, uint UInt321, bool Boolean2)
READ_SIG = re.compile(
    r"(bool ReadSyncVar___\w+\(PooledReader \w+, )uint( \w+, bool \w+\))"
)

stats = {"methods": 0, "setters": 0, "readsig": 0, "cases": 0, "files": 0}

for path in sorted(ROOT.rglob("*.cs")):
    raw = path.read_bytes()
    bom = raw.startswith(b"\xef\xbb\xbf")
    text = raw.decode("utf-8-sig")
    original = text

    # --- 1. Re-declare the generated setters, one per SyncVar that is actually called. ---
    # Insert immediately after the field declaration so the method lands in the right class.
    additions = []
    for m in DECL.finditer(text):
        name, type_, ind = m.group("name"), m.group("type"), m.group("ind")
        if f"sync___set_value_{name}(" not in text:
            continue
        field = accessor_field(text, name, type_)
        method = (
            f"\n{ind}// Reconstructed FishNet ILPostProcessor output (AssetRipper drops these).\n"
            f"{ind}public void sync___set_value_{name}({type_} value, bool asServer)\n"
            f"{ind}{{\n"
            f"{ind}\tif (asServer || !base.IsServer)\n"
            f"{ind}\t{{\n"
            f"{ind}\t\t{field} = value;\n"
            f"{ind}\t}}\n"
            f"{ind}\tsyncVar___{name}.SetValue(value, asServer);\n"
            f"{ind}}}"
        )
        additions.append((m.end(), method))

    for pos, method in reversed(additions):
        text = text[:pos] + method + text[pos:]
    stats["methods"] += len(additions)

    # --- 2. Point the SyncAccessor setter at the real method instead of the bad inline. ---
    def fix_setter(m):
        stats["setters"] += 1
        return f"{m.group('ind')}sync___set_value_{m.group('name')}(value, asServer: true);"

    text = INLINED_SETTER.sub(fix_setter, text)

    # --- 3. ReadSyncVar___X index parameter is byte, not uint. ---
    text, n = READ_SIG.subn(r"\1byte\2", text)
    stats["readsig"] += n
    if n:
        # `case 6u:` cannot label a byte switch; only int constants convert implicitly.
        # Scope this strictly to the ReadSyncVar body -- ComputeStringHash switches in the
        # same file genuinely switch on uint and need literals above int.MaxValue to keep
        # their `u` suffix.
        for sig in list(re.finditer(r"bool ReadSyncVar___\w+\(PooledReader \w+, byte \w+, bool \w+\)", text)):
            open_brace = text.index("{", sig.end())
            depth, i = 0, open_brace
            while i < len(text):
                if text[i] == "{":
                    depth += 1
                elif text[i] == "}":
                    depth -= 1
                    if depth == 0:
                        break
                i += 1
            body = text[open_brace:i]

            def strip_u(mm):
                stats["cases"] += 1
                return f"case {mm.group(1)}:"

            fixed_body = re.sub(r"case (\d+)[uU]:", strip_u, body)
            text = text[:open_brace] + fixed_body + text[i:]

    if text != original:
        stats["files"] += 1
        rel = path.relative_to(ROOT)
        print(f"  {rel}")
        if APPLY:
            path.write_bytes((b"\xef\xbb\xbf" if bom else b"") + text.encode("utf-8"))

print()
print(f"files touched      : {stats['files']}")
print(f"setter methods added: {stats['methods']}")
print(f"accessors repaired  : {stats['setters']}")
print(f"ReadSyncVar sigs    : {stats['readsig']}")
print(f"case labels fixed   : {stats['cases']}")
print("DRY RUN -- pass --apply to write" if not APPLY else "APPLIED")
