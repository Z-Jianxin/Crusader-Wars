"""Check the CK3 rebase against an installed game, without launching or editing it.

Usage: python Tests/validate_ck3_sources.py --game-dir ".../Crusader Kings III/game"
These checks establish source parity, not engine/runtime compatibility.
"""

import argparse
from pathlib import Path
import re


ROOT = Path(__file__).resolve().parents[1]
MOD = ROOT / "ck3_mods/crusader_conflicts"
GUI_OVERRIDES = (
    "frontend_ingame_menu.gui", "window_resign_confirmation.gui",
    "window_save_game.gui", "shared/dialogs.gui", "window_combat.gui",
    "window_siege.gui",
)
TOKEN = re.compile(r'#[^\n]*|"(?:\\.|[^"\\])*"|[{}=]|[^\s{}=#"]+')


def tokens(text):
    return [m.group() for m in TOKEN.finditer(text) if not m.group().startswith("#")]


def definitions(text):
    """Extract balanced top-level definitions while ignoring quoted/comment braces."""
    source = tokens(text)
    result = {}
    i = 0
    while i < len(source):
        if source[i + 1:i + 3] != ["=", "{"]:
            i += 1
            continue
        name = source[i]
        start = i
        i += 3
        depth = 1
        while depth:
            depth += (source[i] == "{") - (source[i] == "}")
            i += 1
        assert name not in result, name
        result[name] = source[start:i]
    return result


def without_cc_gui(text):
    out = []
    inside = False
    for line in text.splitlines():
        if re.search(r"###\s+START (?:ALL )?CRUSADER CONFLICTS EDITS", line):
            assert not inside
            inside = True
        elif re.search(r"###\s+END (?:ALL )?CRUSADER CONFLICTS EDITS", line):
            assert inside
            inside = False
        elif not inside:
            out.append(line)
    assert not inside
    return "\n".join(out)


def check(game):
    for relative in GUI_OVERRIDES:
        current = (MOD / "gui" / relative).read_text(encoding="utf-8-sig")
        native = (game / "gui" / relative).read_text(encoding="utf-8-sig")
        assert "None None" not in current, relative
        assert tokens(without_cc_gui(current)) == tokens(native), relative

    native_types = {}
    for path in (game / "common/accolade_types").glob("*.txt"):
        native_types.update(definitions(path.read_text(encoding="utf-8-sig")))
    path = MOD / "common/accolade_types/05_crusader_conflicts_accolades.txt"
    current_text = path.read_text(encoding="utf-8-sig")
    current = definitions(current_text)
    constants = dict(re.findall(r"(?m)^(@\w+)\s*=\s*(.*?)\s*$", current_text))
    assert set(re.findall(r"@\w+", current_text)) <= constants.keys()
    for native_path in (game / "common/accolade_types").glob("*.txt"):
        for name, value in re.findall(r"(?m)^(@\w+)\s*=\s*([^\n#]*?)\s*(?:#.*)?$",
                                      native_path.read_text(encoding="utf-8-sig")):
            if name in constants:
                assert constants[name] == value, name
    assert len(current) == 30
    count = 0
    for name, definition in current.items():
        flags = [x for x in definition if x.startswith("cc_accolade_")]
        assert len(flags) == 3, (name, flags)
        assert {x.rsplit("_", 1)[1] for x in flags} == {"1", "2", "3"}
        count += len(flags)
        clean = [x for x in definition if not x.startswith("cc_accolade_")]
        # Added parameters may leave an empty wrapper; native empty wrappers
        # are normalized the same way so the remaining definitions must match.
        def normalize(seq):
            return re.sub(r"accolade_parameters = \{ \}", "", " ".join(seq)).split()
        assert normalize(clean) == normalize(native_types[name]), name
    print(f"PASS: {len(GUI_OVERRIDES)} native GUI bodies retained; "
          f"{len(current)} native accolade definitions retained with {count} CC labels.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", type=Path, required=True)
    check(parser.parse_args().game_dir)
