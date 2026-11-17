#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-only
"""Judges the driver on tablets of other shapes (tests/shapes.vectors), with hid-tools as the parser.

    check_shapes.py <harness output> <shapes.vectors>

For each "@tablet" shape the descriptor the driver built must say what the tablet answered: its ranges and
size, a button for each of its keys (1-10, then 0x31 and on), a dial for each dial it has and none for one it
has not, and no pad at all for a pen-only tablet. Every report is then decoded with that descriptor and held to
its line. Faults planted in the output must each be caught, so a pass means the checks can see.
"""
import sys

import hidtools.hid as hid

TILT_MAX = 60


def read_shapes(path):
    """[(label, properties, [(name, raw, expectation words)])] in file order."""
    shapes, properties = [], {}
    for line in open(path):
        line = line.strip()
        if line.startswith("@tablet "):
            properties = dict(word.split("=") for word in line.split()[1:])
        elif line.startswith("@stand-in "):
            properties.update(word.split("=") for word in line.split()[1:])
        elif line.startswith("@describe "):
            shapes.append((line.split()[1], dict(properties), []))
        elif line and not line.startswith("#") and "|" in line:
            name, raw, expect = (part.strip() for part in line.split("|"))
            shapes[-1][2].append((name, raw.lower(), expect.split()))
    return shapes


def read_output(path):
    descriptors, events = {}, {}
    for line in open(path).read().splitlines():
        parts = line.split()
        if parts[0] == "shape":
            descriptors[parts[1]] = parts[4]
        elif parts[0] == "event":
            events[parts[1]] = (int(parts[2]), parts[3])
    return descriptors, events


def key_number(usage):
    """The key a Button usage stands for: 1-10 as they are, 0x31 and on as 11 and on."""
    return usage if usage <= 10 else usage - 0x30 + 10


def decode(descriptor, data):
    report = descriptor.input_reports[data[0]]
    values, keys = {}, []
    for field in report:
        if field.is_const:
            continue
        value = field.get_values(list(data))[0]
        if field.usage_page_name == "Button":
            if value:
                keys.append(key_number(field.usage & 0xFFFF))
        else:
            values[field.usage_name] = value
    values["keys"] = keys
    return values


def judge(shapes, descriptors, events):
    faults = []
    for label, tablet, vectors in shapes:
        if label not in descriptors:
            faults.append(f"{label}: no descriptor was built")
            continue
        parsed = hid.ReportDescriptor.from_bytes(bytes.fromhex(descriptors[label]))
        keys, dials = int(tablet["KEYS"]), int(tablet["DIALS"])
        x_max, y_max = int(tablet["X_MAX"]), int(tablet["Y_MAX"])
        pressure, per_inch = int(tablet["PRESSURE_MAX"]), int(tablet["RESOLUTION"])

        # The USB HID driver reads as many bytes as the longest report described: one longer than the tablet's
        # raw reports makes every transfer of them wait for bytes that never come.
        raw_length = int(tablet.get("LENGTH", 14))
        longer = {rid: r.size for rid, r in parsed.input_reports.items() if rid != 0x02 and r.size > raw_length}
        if longer:
            faults.append(f"{label}: reports longer than the tablet's {raw_length}-byte ones: {longer}")

        pen = {f.usage_name: f for f in parsed.input_reports.get(0x07, []) if not f.is_const}
        ranges = {name: (f.logical_min, f.logical_max) for name, f in pen.items()
                  if name in ("X", "Y", "Tip Pressure", "X Tilt", "Y Tilt")}
        want = {"X": (0, x_max), "Y": (0, y_max), "Tip Pressure": (0, pressure),
                "X Tilt": (-TILT_MAX, TILT_MAX), "Y Tilt": (-TILT_MAX, TILT_MAX)}
        if ranges != want:
            faults.append(f"{label}: pen ranges {ranges}, want {want}")
        for axis in ("X", "Y"):
            f = pen.get(axis)
            if f is None:
                continue
            millimeters = f.physical_max * 10 ** (f.unit_exp + 1) if f.unit == 0x11 else 0
            per_mm = f.logical_max / millimeters if millimeters else 0
            if abs(per_mm - per_inch / 25.4) > 0.05 * per_inch / 25.4:
                faults.append(f"{label}: pen {axis} {per_mm:.2f} units per mm, want {per_inch / 25.4:.2f}")

        pad = parsed.input_reports.get(0xF0)
        if not keys and not dials:
            if pad is not None:
                faults.append(f"{label}: a pen-only tablet has a pad")
        elif pad is None:
            faults.append(f"{label}: no pad")
        else:
            fields = [f for f in pad if not f.is_const]
            buttons = sorted(key_number(f.usage & 0xFFFF) for f in fields if f.usage_page_name == "Button")
            if buttons != list(range(1, keys + 1)):
                faults.append(f"{label}: pad keys {buttons}, want 1..{keys}")
            names = {f.usage_name for f in fields}
            if ("Wheel" in names) != (dials >= 1) or ("AC Pan" in names) != (dials == 2):
                faults.append(f"{label}: pad dials {sorted(names & {'Wheel', 'AC Pan'})} for {dials} dials")
            if pad.size != 9:
                faults.append(f"{label}: pad report is {pad.size} bytes, want 9")

        for name, raw, expect in vectors:
            if name not in events:
                faults.append(f"{name}: not run")
                continue
            result, data = events[name]
            kind, wanted = expect[0], dict(item.split("=") for item in expect[1:])
            try:
                got = decode(parsed, bytes.fromhex(data))
            except Exception as error:  # a report the descriptor cannot explain is a fault
                faults.append(f"{name}: cannot be decoded ({error})")
                continue
            if kind == "pad":
                want = {"keys": [int(k) for k in wanted["keys"].split(",") if k]}
                for field, word in (("Wheel", "wheel"), ("AC Pan", "pan")):
                    if word in wanted:
                        want[field] = int(wanted[word])
                got = {k: v for k, v in got.items() if k in want}
            else:
                want = {"keys": [], "In Range": int(wanted["in"]), "Tip Switch": int(wanted["tip"]),
                        "Barrel Switch": int(wanted["b1"]), "Secondary Barrel Switch": int(wanted["b2"]),
                        "Invert": int(wanted["inv"]), "Eraser": int(wanted["era"]),
                        "X": int(wanted["x"]), "Y": int(wanted["y"]), "Tip Pressure": int(wanted["p"]),
                        "X Tilt": int(wanted["tx"]), "Y Tilt": int(wanted["ty"])}
            if got != want:
                faults.append(f"{name}: got/want {({k: (got.get(k), v) for k, v in want.items() if got.get(k) != v})}")
    return faults


def damaged(descriptors, events, kind, name, old, new):
    descriptors, events = dict(descriptors), dict(events)
    if kind == "shape":
        assert old in descriptors[name], f"control expects {old} in {name}"
        descriptors[name] = descriptors[name].replace(old, new, 1)
    else:
        result, data = events[name]
        assert old in data, f"control expects {old} in {data}"
        events[name] = (result, data.replace(old, new, 1))
    return descriptors, events


CONTROLS = [
    # (what is damaged, kind, name, old text, new text, a word the fault must contain); events are the
    # driver's output: the pad as f0 f0 keys keys 00 00 00 dial1 dial2, the pen as 07 state x x x y y y p p tilt tilt
    ("a pen report too long for a tablet of short reports", "shape", "star-03", "7510", "7518", "star-03"),
    ("the second dial dropped from a two-dial tablet", "shape", "innovator-16", "0a3802", "0a3702", "innovator-16"),
    ("the tenth key lost", "event", "a16g2-key1-key10", "f00102", "f00100", "a16g2-key1-key10"),
    ("X loses its third byte on a 12-byte tablet", "event", "a16g2-far-corner", "660a01", "660a00", "a16g2-far-corner"),
    ("pen-type bits read as pressure", "event", "decom-pen-type-bits", "ff1f", "ffff", "decom-pen-type-bits"),
    ("a ring code turns the dial", "event", "inno-ring-code-41", "f0f000000000000000", "f0f000000000000100", "inno-ring-code-41"),
    ("the second dial at 10 missed", "event", "inno-dial2-up-10", "000001", "000000", "inno-dial2-up-10"),
    ("ff turns the dial the wrong way", "event", "a12p-dial-down-ff", "0000ff", "000001", "a12p-dial-down-ff"),
]


def main():
    output_path, vectors_path = sys.argv[1:3]
    shapes = read_shapes(vectors_path)
    descriptors, events = read_output(output_path)
    faults = judge(shapes, descriptors, events)
    for fault in faults:
        print("FAULT", fault)
    missed = 0
    for what, kind, name, old, new, word in CONTROLS:
        if not [f for f in judge(shapes, *damaged(descriptors, events, kind, name, old, new)) if word in f]:
            missed += 1
            print(f"CONTROL MISSED: '{what}' was not caught")
    reports = sum(len(vectors) for _, _, vectors in shapes)
    print(f"{len(shapes)} tablet shapes, {reports} reports judged, {len(faults)} faults; "
          f"{len(CONTROLS) - missed} of {len(CONTROLS)} planted faults caught")
    return 1 if faults or missed else 0


if __name__ == "__main__":
    sys.exit(main())
