#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-only
"""Judges what the driver produced, using hid-tools as an independent HID parser.

    check_driver.py <harness output> <vectors file> <if2 descriptor hex>

The harness runs the driver's entry points in user space. This script parses the
descriptors the driver installs, decodes every report the driver handed on with
them, and compares the result with the vectors file. It then damages the driver's
output in known ways and requires each damage to be caught, so a pass means the
comparison can see the faults it is there for.
"""
import sys

import hidtools.hid as hid

X_MAX, Y_MAX, PRESSURE_MAX, TILT_MAX = 68199, 38359, 16383, 60
UNITS_PER_MM = 200
RAW_MODE_COMMAND = "02b004" + "00" * 11
INPUT_PAGES = {"Generic Desktop", "Digitizers", "Button", "Keyboard", "Consumer Devices"}


def read_vectors(path):
    vectors = {}
    for line in open(path):
        line = line.strip()
        if not line or line.startswith(("#", "@")):
            continue
        name, raw, expect = (part.strip() for part in line.split("|"))
        vectors[name] = (raw.lower(), expect.split())
    return vectors


def read_output(path):
    return [line.split(None, 4) for line in open(path).read().splitlines() if line]


def decode(descriptor, data):
    report = descriptor.input_reports[data[0]]
    values = {}
    keys = []
    for field in report:
        if field.is_const:
            continue
        value = field.get_values(list(data))[0]
        if field.usage_page_name == "Button":
            if value:
                keys.append(int(field.usage_name[1:]))
        else:
            values[field.usage_name] = value
    values["keys"] = keys
    return report, values


def judge(output, vectors, original_vendor):
    """Returns a list of faults; empty means the driver's output is right."""
    faults = []
    probes = {int(l[1]): (int(l[2]), l[3]) for l in output if l[0] == "probe"}
    descriptors = {int(l[1]): (int(l[2]), l[3], l[4] if len(l) > 4 else "") for l in output if l[0] == "rdesc"}
    events = {l[1]: (int(l[2]), l[3]) for l in output if l[0] == "event"}

    # The driver takes the tablet's three interfaces and nothing else, and asks
    # for raw mode once, on the vendor interface.
    wanted = {177: (0, "-"), 113: (0, "-"), 36: (0, RAW_MODE_COMMAND), 64: (-22, "-")}
    if probes != wanted:
        faults.append(f"probe: got {probes}, want {wanted}")

    # The two plain interfaces must no longer describe any input.
    for size in (177, 113):
        result, data, name = descriptors.get(size, (0, "", ""))
        if result <= 0 or not name.startswith("Disabled by HID-BPF"):
            faults.append(f"rdesc {size}: not replaced or not renamed ({result}, {name!r})")
            continue
        parsed = hid.ReportDescriptor.from_bytes(bytes.fromhex(data))
        for report in parsed.input_reports.values():
            for field in report:
                if not field.is_const and field.usage_page_name in INPUT_PAGES:
                    faults.append(f"rdesc {size}: still describes {field.usage_page_name}/{field.usage_name}")

    result, data, _ = descriptors.get(36, (0, "", ""))
    if not data.startswith(original_vendor):
        faults.append("rdesc 36: the vendor descriptor is not kept at the front")
    if result * 2 != len(data) or result <= 36:
        faults.append(f"rdesc 36: size {result} does not match the data")
    vendor = hid.ReportDescriptor.from_bytes(bytes.fromhex(data))

    sizes = {rid: report.size for rid, report in vendor.input_reports.items()}
    if sizes != {0x02: 14, 0xF0: 9, 0x07: 12}:
        faults.append(f"rdesc 36: input reports {sizes}")
    if {rid: report.size for rid, report in vendor.output_reports.items()} != {0x02: 14}:
        faults.append("rdesc 36: the raw-mode output report is gone")

    if 0x07 in vendor.input_reports:
        fields = {f.usage_name: f for f in vendor.input_reports[0x07] if not f.is_const}
        ranges = {name: (f.logical_min, f.logical_max) for name, f in fields.items()}
        wanted = {
            "Tip Switch": (0, 1), "Barrel Switch": (0, 1), "Secondary Barrel Switch": (0, 1),
            "Invert": (0, 1), "Eraser": (0, 1), "In Range": (0, 1),
            "X": (0, X_MAX), "Y": (0, Y_MAX), "Tip Pressure": (0, PRESSURE_MAX),
            "X Tilt": (-TILT_MAX, TILT_MAX), "Y Tilt": (-TILT_MAX, TILT_MAX),
        }
        if ranges != wanted:
            faults.append(f"pen ranges: got {ranges}")
        order = [f.usage_name for f in vendor.input_reports[0x07] if not f.is_const]
        if "In Range" in order and not (order.index("Invert") < order.index("Eraser") < order.index("In Range")):
            faults.append(f"pen: eraser bits must come before In Range, got {order}")
        for axis in ("X", "Y"):
            f = fields.get(axis)
            if f is None:
                continue
            # Unit 0x11 is centimeters; the exponent scales the physical range.
            millimeters = f.physical_max * 10 ** (f.unit_exp + 1) if f.unit == 0x11 else 0
            per_mm = f.logical_max / millimeters if millimeters else 0
            if abs(per_mm - UNITS_PER_MM) > 0.5:
                faults.append(f"pen {axis}: {per_mm:.2f} units per mm, want {UNITS_PER_MM}")
        for axis in ("X Tilt", "Y Tilt"):
            f = fields.get(axis)
            if f is not None and (f.unit != 0x14 or (f.physical_min, f.physical_max) != (-TILT_MAX, TILT_MAX)):
                faults.append(f"pen {axis}: not given in degrees")

    if 0xF0 in vendor.input_reports:
        names = [f"{f.usage_page_name}/{f.usage_name}" for f in vendor.input_reports[0xF0] if not f.is_const]
        wanted = [f"Button/B{i}" for i in range(1, 9)] + [
            "Digitizers/Barrel Switch", "Generic Desktop/X", "Generic Desktop/Y",
            "Generic Desktop/Wheel", "Consumer Devices/AC Pan"]
        if names != wanted:
            faults.append(f"pad fields: got {names}")

    if set(events) != set(vectors):
        faults.append(f"events: ran {sorted(set(events) ^ set(vectors))} on one side only")

    for name, (raw, expect) in vectors.items():
        if name not in events:
            continue
        result, data = events[name]
        kind, wanted = expect[0], dict(item.split("=") for item in expect[1:])
        if kind == "same":
            if result != 0 or data != raw:
                faults.append(f"{name}: must pass through untouched, got {result} {data}")
            continue
        report_id, length = {"pad": (0xF0, 9), "pen": (0x07, 12)}[kind]
        handed_on = bytes.fromhex(data)
        if result != length or len(handed_on) != length or handed_on[0] != report_id:
            faults.append(f"{name}: want a {length}-byte report {report_id:#x}, got {result} {data}")
            continue
        try:
            _, got = decode(vendor, handed_on)
        except Exception as error:  # a report the descriptor cannot explain is a fault, not a crash
            faults.append(f"{name}: cannot be decoded ({error})")
            continue
        if kind == "pad":
            want = {"keys": [int(k) for k in wanted["keys"].split(",") if k],
                    "Wheel": int(wanted["wheel"]), "AC Pan": int(wanted["pan"]),
                    "Barrel Switch": 0, "X": 0, "Y": 0}
        else:
            want = {"keys": [], "In Range": int(wanted["in"]), "Tip Switch": int(wanted["tip"]),
                    "Barrel Switch": int(wanted["b1"]), "Secondary Barrel Switch": int(wanted["b2"]),
                    "Invert": int(wanted["inv"]), "Eraser": int(wanted["era"]),
                    "X": int(wanted["x"]), "Y": int(wanted["y"]), "Tip Pressure": int(wanted["p"]),
                    "X Tilt": int(wanted["tx"]), "Y Tilt": int(wanted["ty"])}
        if got != want:
            diff = {k: (got.get(k), v) for k, v in want.items() if got.get(k) != v}
            faults.append(f"{name}: got/want {diff}")
    return faults


def damaged(output, kind, name, change):
    """A copy of the output with one line's data passed through change()."""
    copy = []
    hit = False
    for line in output:
        line = list(line)
        if line[0] == kind and line[1] == name:
            line[3] = change(line[3])
            hit = True
        copy.append(line)
    assert hit, f"control aims at {kind} {name}, which is not in the output"
    return copy


def swap(old, new):
    def change(data):
        assert old in data, f"control expects {old} in {data}"
        return data.replace(old, new, 1)
    return change


CONTROLS = [
    # (what is damaged, line kind, line name, the damage, a word the fault must contain)
    ("X loses its high byte", "event", "pen-far-corner-full", swap("670a01", "670a00"), "pen-far-corner-full"),
    ("pressure loses bit 13", "event", "pen-far-corner-full", swap("ff3f", "ff1f"), "pen-far-corner-full"),
    ("tilt changes sign", "event", "pen-tilt", swap("14ec", "ec14"), "pen-tilt"),
    ("upper button left a plain button by default", "event", "pen-upper-button", swap("0728", "0724"), "pen-upper-button"),
    ("upper button still an eraser when set to button", "event", "upper-as-button", swap("0724", "0728"), "upper-as-button"),
    ("upper button reported as lower", "event", "upper-as-button", swap("0724", "0722"), "upper-as-button"),
    ("a mistyped setting changes the pen", "event", "upper-wrong-case", swap("0728", "0724"), "upper-wrong-case"),
    ("tilt reported while switched off", "event", "tilt-off", swap("100000000000", "1000000014ec"), "tilt-off"),
    ("tilt dropped while it is on", "event", "tilt-on-named", swap("14ec", "0000"), "tilt-on-named"),
    ("a mistyped tilt setting changes the pen", "event", "tilt-wrong-case", swap("14ec", "0000"), "tilt-wrong-case"),
    ("eraser contact not flagged", "event", "eraser-down", swap("0739", "0729"), "eraser-down"),
    ("pen left range but still in range", "event", "pen-gone", swap("0700", "0720"), "pen-gone"),
    ("dial turns the wrong way", "event", "dial1-up-recorded", swap("000100", "00ff00"), "dial1-up-recorded"),
    ("second dial lands on the first", "event", "dial2-up-recorded", swap("000001", "000100"), "dial2-up-recorded"),
    ("an extra key", "event", "key1", swap("f0f001", "f0f003"), "key1"),
    ("placeholder not held still", "event", "noise-in-placeholders", swap("f0f00100000000", "f0f00100010000"), "noise-in-placeholders"),
    ("a status report rewritten", "event", "status-f2", swap("02f2", "07f2"), "status-f2"),
    ("X range too small for the tablet", "rdesc", "36", swap("27670a0100", "27ffff0000"), "pen"),
    ("eraser bits after In Range", "rdesc", "36", swap("093c09450932", "0932093c0945"), "eraser bits"),
    ("plain pen interface left alive", "rdesc", "113", lambda _: open(FIXTURE_PEN).read().strip(), "still describes"),
]


def main():
    global FIXTURE_PEN
    output_path, vectors_path, vendor_path = sys.argv[1:4]
    FIXTURE_PEN = vendor_path.replace("if2", "if1")
    output = read_output(output_path)
    vectors = read_vectors(vectors_path)
    original_vendor = open(vendor_path).read().strip()

    faults = judge(output, vectors, original_vendor)
    for fault in faults:
        print("FAULT", fault)

    missed = 0
    for what, kind, name, change, word in CONTROLS:
        caught = [f for f in judge(damaged(output, kind, name, change), vectors, original_vendor) if word in f]
        if not caught:
            missed += 1
            print(f"CONTROL MISSED: '{what}' was not caught")

    events = sum(1 for line in output if line[0] == "event")
    print(f"{events} reports judged, {len(faults)} faults; {len(CONTROLS) - missed} of {len(CONTROLS)} planted faults caught")
    return 1 if faults or missed else 0


if __name__ == "__main__":
    sys.exit(main())
