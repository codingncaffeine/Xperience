# SPDX-License-Identifier: GPL-2.0-only
"""The judge inside the test machine of tools/rig (started by guest.sh, as root, on a throwaway system).

    check.py REPOSITORY OUTPUT-FOLDER

Plugs the stand-in tablet into the virtual USB bus, installs the driver with the real helper, plays the
driver's test vectors through the kernel and judges what reaches hidraw (decoded with the same rules as
tests/check_driver.py) and evdev (against what the real tablet showed in the session record). Then it
unplugs and replugs the tablet, installs over an interface another program left unbound, and removes the
driver. Writes record.txt (everything) and result.txt (the verdict); the exit code is the verdict.
"""
import fcntl
import glob
import os
import select
import struct
import subprocess
import sys
import threading
import time

REPO, OUT = sys.argv[1:3]
sys.path.insert(0, os.path.join(REPO, "tests"))
import check_driver  # noqa: E402
import check_shapes  # noqa: E402
import hidtools.hid as hid  # noqa: E402

FIXTURES = os.path.join(REPO, "tests", "fixtures", "artist16-3rd")
VECTORS = os.path.join(REPO, "tests", "artist16-3rd.vectors")
HELPER = os.path.join(REPO, "build", "rig", "xperience-driver-helper")
OBJECT = os.path.join(REPO, "build", "rig", "0010-XPPen__Artist16_3rd.bpf.o")
FAKE = os.path.join(REPO, "build", "rig", "fake-tablet")
PROBE = os.path.join(REPO, "build", "rig", "xperience-probe")

# What the Artist 16 3rd says it is (its string 100, read from the real tablet on 2026-11-17).
SELF_DESCRIPTION = {"XPERIENCE_TABLET_X_MAX": "68199", "XPERIENCE_TABLET_Y_MAX": "38359",
                    "XPERIENCE_TABLET_PRESSURE_MAX": "16383", "XPERIENCE_TABLET_RESOLUTION": "5080",
                    "XPERIENCE_TABLET_KEYS": "8", "XPERIENCE_TABLET_FRAME": "2", "XPERIENCE_TABLET_DIALS": "2",
                    "XPERIENCE_TABLET_SCREEN": "1", "XPERIENCE_TABLET_PROBE": "ok"}

# What the real tablet showed with the driver in charge (session record, 2026-11-17).
PEN_NAME, PAD_NAME = "XPPen Artist 16 3rd Stylus", "XPPen Artist 16 3rd Keypad"
ABS_X, ABS_Y, ABS_PRESSURE, ABS_TILT_X, ABS_TILT_Y = 0x00, 0x01, 0x18, 0x1A, 0x1B
PEN_RANGES = {ABS_X: (0, 68199), ABS_Y: (0, 38359), ABS_PRESSURE: (0, 16383), ABS_TILT_X: (-60, 60), ABS_TILT_Y: (-60, 60)}
BTN_0, BTN_TOUCH, BTN_STYLUS, BTN_STYLUS2 = 0x100, 0x14A, 0x14B, 0x14C
BTN_TOOL_PEN, BTN_TOOL_RUBBER = 0x140, 0x141
EV_KEY, EV_REL = 0x01, 0x02
REL_HWHEEL, REL_WHEEL = 0x06, 0x08

# The helper's values for a vectors file setting; "unset" is what the settings window installs by default.
DEFAULTS = {"upper-button": "eraser", "lower-button": "button", "tilt": "on", "switch-keys": "none", "smoothing": "1"}

record = open(os.path.join(OUT, "record.txt"), "w")
results = []
# For planted faults: the run ends at the first failed check that contains this text (tools/rig/controls.sh).
STOP_AT = (open(os.path.join(OUT, "stop-at")).read().strip() if os.path.exists(os.path.join(OUT, "stop-at")) else "")


class Stopped(Exception):
    pass


def note(text):
    record.write(f"{time.monotonic():9.3f} {text}\n")
    record.flush()


def check(ok, what):
    results.append((bool(ok), what))
    note(("PASS " if ok else "FAIL ") + what)
    if not ok and STOP_AT and STOP_AT in "FAIL " + what:
        raise Stopped()
    return ok


def wait(predicate, seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        value = predicate()
        if value:
            return value
        time.sleep(0.05)
    return predicate()


def settle():
    subprocess.run(["udevadm", "settle", "--timeout=10"], check=False)


class Tablet:
    """The stand-in tablet process and what it said."""

    def __init__(self, string_100=None, product=None, name=None, length=None):
        rdescs = [f"{FIXTURES}-if{i}.rdesc.hex" for i in range(3)]
        env = dict(os.environ)
        if string_100:
            env["FAKE_STRING_100"] = string_100
        for key, value in (("FAKE_PRODUCT", product), ("FAKE_NAME", name), ("FAKE_REPORT_LENGTH", length)):
            if value:
                env[key] = value
        self.process = subprocess.Popen([FAKE, *rdescs], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                         stderr=subprocess.STDOUT, text=True, bufsize=1, env=env)
        self.lines = []
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for line in self.process.stdout:
            line = line.rstrip("\n")
            self.lines.append(line)
            note(f"tablet: {line}")

    def mark(self):
        return len(self.lines)

    def said(self, line, since=0):
        return line in self.lines[since:]

    def send(self, data):
        self.process.stdin.write(f"send {data}\n")
        self.process.stdin.flush()

    def unplug(self):
        if self.process.poll() is None:
            self.process.stdin.write("quit\n")
            self.process.stdin.flush()
            self.process.wait(10)


def run(*args):
    done = subprocess.run(list(args), capture_output=True, text=True, check=False)
    note(f"$ {' '.join(args)} -> {done.returncode}")
    for line in (done.stdout + done.stderr).splitlines():
        note(f"  | {line}")
    return done.returncode


def tablet_properties(device):
    """The XPERIENCE_TABLET_* properties udev keeps for a device."""
    done = subprocess.run(["udevadm", "info", "--query=property", "--path", device], capture_output=True, text=True)
    return dict(line.split("=", 1) for line in done.stdout.splitlines() if line.startswith("XPERIENCE_TABLET_"))


def probe(device):
    done = subprocess.run([PROBE, device], capture_output=True, text=True)
    note(f"$ xperience-probe {os.path.basename(device)} -> {done.returncode}: {done.stdout.split()}")
    return dict(line.split("=", 1) for line in done.stdout.splitlines() if "=" in line)


def read(path, mode="r"):
    """A sysfs file, or None when its device went away while it was being looked at."""
    try:
        with open(path, mode) as file:
            return file.read()
    except OSError:
        return None


def hid_devices(product="097E"):
    """{interface number: sysfs folder} of the tablet's HID devices."""
    found = {}
    for device in glob.glob(f"/sys/bus/hid/devices/0003:28BD:{product}.*"):
        uevent = read(os.path.join(device, "uevent"))
        for line in (uevent or "").splitlines():
            if line.startswith("HID_PHYS=") and line[-7:-1] == "/input":
                found[int(line[-1])] = device
    return found


def inputs(device):
    """{name: /dev/input/eventN} of the input devices under a HID device."""
    found = {}
    for node in glob.glob(os.path.join(device, "input", "input*", "event*")):
        name = read(os.path.join(os.path.dirname(node), "name"))
        if name is not None:
            found[name.strip()] = "/dev/input/" + os.path.basename(node)
    return found


def hidraw(device):
    nodes = glob.glob(os.path.join(device, "hidraw", "hidraw*"))
    return "/dev/" + os.path.basename(nodes[0]) if nodes else None


def ioc_read(nr, size):
    return (2 << 30) | (size << 16) | (ord("E") << 8) | nr


def absinfo(fd, code):
    data = fcntl.ioctl(fd, ioc_read(0x40 + code, 24), bytes(24))
    return struct.unpack("6i", data)  # value, minimum, maximum, fuzz, flat, resolution


def keys_down(fd):
    data = fcntl.ioctl(fd, ioc_read(0x18, 96), bytes(96))
    return {i for i in range(768) if data[i // 8] >> (i % 8) & 1}


def drain(fd):
    """The (type, code, value) events waiting on an evdev or hidraw node."""
    events = []
    while select.select([fd], [], [], 0)[0]:
        try:
            data = os.read(fd, 24 * 64)
        except BlockingIOError:
            break
        for i in range(0, len(data) - 23, 24):
            _, _, kind, code, value = struct.unpack("qqHHi", data[i:i + 24])
            events.append((kind, code, value))
    return events


class Driven:
    """The tablet as the driver hands it on: its hidraw node, descriptor and evdev nodes."""

    def __init__(self):
        devices = hid_devices()
        self.vendor = devices[2]
        self.descriptor = hid.ReportDescriptor.from_bytes(open(os.path.join(self.vendor, "report_descriptor"), "rb").read())
        nodes = inputs(self.vendor)
        self.raw = os.open(hidraw(self.vendor), os.O_RDONLY | os.O_NONBLOCK)
        self.pen = os.open(nodes[PEN_NAME], os.O_RDONLY | os.O_NONBLOCK)
        self.pad = os.open(nodes[PAD_NAME], os.O_RDONLY | os.O_NONBLOCK)

    def close(self):
        for fd in (self.raw, self.pen, self.pad):
            os.close(fd)

    def play(self, tablet, data):
        """Sends one raw report; returns what hidraw handed on and the pad's relative events."""
        for fd in (self.raw, self.pen, self.pad):
            while select.select([fd], [], [], 0)[0]:
                try:
                    os.read(fd, 4096)
                except BlockingIOError:
                    break
        tablet.send(data)
        if not select.select([self.raw], [], [], 2)[0]:
            return None, []
        report = os.read(self.raw, 64)
        time.sleep(0.02)  # evdev is written in the same pass; this only lets the reader see it
        return report, drain(self.pad)


def install(settings):
    s = {**DEFAULTS, **{k: v for k, v in settings.items() if v != "unset"}}
    return run(HELPER, "install", OBJECT, s["upper-button"], s["tilt"], s["lower-button"], s["switch-keys"], s["smoothing"])


def helper_takes(settings):
    s = {**DEFAULTS, **{k: v for k, v in settings.items() if v != "unset"}}
    keys = s["switch-keys"]
    return (s["upper-button"] in ("button", "eraser", "toggle") and s["lower-button"] in ("button", "eraser", "toggle")
            and s["tilt"] in ("on", "off") and s["smoothing"] in "12345678" and len(s["smoothing"]) == 1
            and (keys == "none" or (keys.isdigit() and set(keys) <= set("12345678"))))


def segments():
    """The vectors in runs that share one set of settings; "@fresh" starts a new run too."""
    settings, run_, runs = {}, [], []
    for line in open(VECTORS):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("@"):
            if run_:
                runs.append((dict(settings), run_))
                run_ = []
            word, _, value = line[1:].partition(" ")
            if word != "fresh":
                settings[word] = value
            continue
        name, raw, expect = (part.strip() for part in line.split("|"))
        run_.append((name, raw.lower(), expect.split()))
    if run_:
        runs.append((dict(settings), run_))
    return runs


def judge_vector(driven, name, raw, expect, report, rel):
    kind, wanted = expect[0], dict(item.split("=") for item in expect[1:])
    if kind == "same" and int(raw[:2], 16) not in driven.descriptor.input_reports:
        # The driver hands it on as it is; the kernel then drops a report its descriptor does not have.
        return check(report is None, f"{name}: dropped by the kernel, as a report the descriptor does not have")
    if report is None:
        return check(False, f"{name}: nothing reached hidraw")
    if kind == "same":
        return check(report.hex() == raw, f"{name}: passed through untouched ({report.hex()})")
    try:
        _, got = check_driver.decode(driven.descriptor, report)
    except Exception as error:  # a report the descriptor cannot explain is a fault
        return check(False, f"{name}: hidraw {report.hex()} cannot be decoded ({error})")
    want = check_driver.expected(kind, wanted)
    diff = {k: (got.get(k), v) for k, v in want.items() if got.get(k) != v}
    ok = check(not diff, f"{name}: hidraw {report.hex()}" + (f" got/want {diff}" if diff else ""))

    # The same report as an application sees it.
    if kind == "pen":
        down = keys_down(driven.pen)
        problems = []
        if wanted["in"] == "1":
            for code, key in ((ABS_X, "x"), (ABS_Y, "y"), (ABS_PRESSURE, "p"), (ABS_TILT_X, "tx"), (ABS_TILT_Y, "ty")):
                value = absinfo(driven.pen, code)[0]
                if value != int(wanted[key]):
                    problems.append(f"{key} {value}!={wanted[key]}")
            tool = BTN_TOOL_RUBBER if wanted["inv"] == "1" else BTN_TOOL_PEN
            if tool not in down:
                problems.append(f"tool {sorted(down & {BTN_TOOL_PEN, BTN_TOOL_RUBBER})}")
        else:
            if down & {BTN_TOOL_PEN, BTN_TOOL_RUBBER}:
                problems.append("a tool still in range")
        for code, key in ((BTN_TOUCH, "tip"), (BTN_STYLUS, "b1"), (BTN_STYLUS2, "b2")):
            if (code in down) != (wanted[key] == "1"):
                problems.append(f"{key} {'down' if code in down else 'up'}")
        ok &= check(not problems, f"{name}: evdev pen" + (f" {problems}" if problems else ""))
    elif kind == "pad":
        down = keys_down(driven.pad)
        keys = {BTN_0 + int(k) - 1 for k in wanted["keys"].split(",") if k}
        pressed = {code for code in down if BTN_0 <= code < BTN_0 + 16}
        wheel = sum(v for t, c, v in rel if t == EV_REL and c == REL_WHEEL)
        pan = sum(v for t, c, v in rel if t == EV_REL and c == REL_HWHEEL)
        problems = []
        if pressed != keys:
            problems.append(f"keys {sorted(pressed)} want {sorted(keys)}")
        if (wheel, pan) != (int(wanted["wheel"]), int(wanted["pan"])):
            problems.append(f"wheel/pan {wheel},{pan} want {wanted['wheel']},{wanted['pan']}")
        ok &= check(not problems, f"{name}: evdev pad" + (f" {problems}" if problems else ""))
    return ok


def state(label):
    for number, device in sorted(hid_devices().items()):
        link = os.path.join(device, "driver")
        driver = os.path.basename(os.path.realpath(link)) if os.path.exists(link) else "-"
        descriptor = read(os.path.join(device, "report_descriptor"), "rb") or b""
        size = len(descriptor)
        with open(os.path.join(OUT, f"{label}-if{number}.rdesc"), "wb") as saved:
            saved.write(descriptor)
        note(f"{label}: interface {number} {os.path.basename(device)} driver {driver} rdesc {size} inputs {sorted(inputs(device))}")


def plain_inputs_alive():
    devices = hid_devices()
    return len(devices) == 3 and inputs(devices.get(0, "/x")) and inputs(devices.get(1, "/x"))


def driver_in_charge():
    devices = hid_devices()
    if len(devices) != 3:
        return False
    names = inputs(devices[2])
    return (not inputs(devices[0]) and not inputs(devices[1]) and PEN_NAME in names and PAD_NAME in names)


def main():
    note(f"kernel {os.uname().release}")
    tablet = Tablet()

    # 1. Plugged in, before any driver: as the tablet comes out of the box.
    check(wait(lambda: tablet.said("ready"), 15), "plug-in: the host configured the tablet")
    check(wait(lambda: len(hid_devices()) == 3, 15), "plug-in: three HID devices")
    settle()
    state("before")
    check(wait(plain_inputs_alive, 10), "plug-in: interfaces 0 and 1 are input devices of their own")
    check(not tablet.said("raw on"), "plug-in: nothing asked for raw mode before the driver was installed")
    for number, device in sorted(hid_devices().items()):
        check(probe(device) == SELF_DESCRIPTION, f"probe: interface {number} says what the tablet is, as the real one did")
    check(probe("/sys/bus/hid/devices/" + os.path.basename(hid_devices()[2])) == SELF_DESCRIPTION,
          "probe: also when given the device by its link")

    # 2. Install, with the settings window's defaults.
    mark = tablet.mark()
    check(install({}) == 0, "install: the helper succeeded")
    settle()
    state("installed")
    check(tablet.said("raw on", mark), "install: the driver switched the tablet to raw mode")
    check(wait(driver_in_charge, 10), "install: only the driver's pen and keypad are left")
    driven = Driven()
    for code, (low, high) in PEN_RANGES.items():
        info = absinfo(driven.pen, code)
        check(info[1:3] == (low, high), f"install: pen axis {code:#x} range {info[1]}..{info[2]}, the real tablet's {low}..{high}")
        note(f"pen axis {code:#x}: minimum {info[1]} maximum {info[2]} fuzz {info[3]} flat {info[4]} resolution {info[5]}")

    # 3. Every vector the helper can express, through the kernel.
    played = skipped = 0
    for number, (settings, vectors) in enumerate(segments()):
        if not helper_takes(settings):
            check(install(settings) == 2, f"settings {settings}: the helper refuses them")
            skipped += len(vectors)
            continue
        # Each run of vectors starts on a driver loaded afresh with its settings, as after the window saves them.
        if number:
            driven.close()
            check(install(settings) == 0, f"settings {settings}: installed")
            settle()
            if not check(wait(driver_in_charge, 10), f"settings {settings}: the driver is in charge"):
                break
            driven = Driven()
        for name, raw, expect in vectors:
            report, rel = driven.play(tablet, raw)
            judge_vector(driven, name, raw, expect, report, rel)
            played += 1
    note(f"vectors: {played} played, {skipped} left to the offline test (settings the helper refuses)")
    driven.close()
    check(install({}) == 0, "back to the defaults")
    settle()

    # 4. Unplugged and plugged back in: the installed rules load the driver by themselves.
    tablet.unplug()
    check(wait(lambda: not hid_devices(), 10), "unplug: the tablet's HID devices went away")
    tablet = Tablet()
    check(wait(lambda: tablet.said("ready"), 15), "replug: configured again")
    settle()
    check(wait(lambda: tablet.said("raw on"), 10), "replug: the driver switched the tablet to raw mode by itself")
    check(wait(driver_in_charge, 10), "replug: the driver's pen and keypad are back")
    state("replugged")
    for line in subprocess.run(["udevadm", "info", "--path", hid_devices()[2]], capture_output=True, text=True).stdout.splitlines():
        note(f"udev: {line}")
    note("rules: " + (read("/etc/udev/rules.d/70-xperience.rules") or "(none)").replace("\n", " / "))
    for number, device in sorted(hid_devices().items()):
        got = tablet_properties(device)
        check(got == SELF_DESCRIPTION, f"replug: udev ran the probe for interface {number}" + ("" if got == SELF_DESCRIPTION else f" (got {got})"))
    driven = Driven()
    report, rel = driven.play(tablet, "02a1670ad795ff1fc43c01000001")
    judge_vector(driven, "replug-pen-far-corner-full", "02a1670ad795ff1fc43c01000001",
                 "pen in=1 tip=1 b1=0 b2=0 inv=0 era=0 x=68199 y=38359 p=16383 tx=-60 ty=60".split(), report, rel)
    driven.close()

    # 5. Remove: the tablet goes back to the mode it comes in, with its own input devices.
    mark = tablet.mark()
    check(run(HELPER, "remove") == 0, "remove: the helper succeeded")
    settle()
    state("removed")
    check(tablet.said("raw off", mark), "remove: the tablet was told to leave raw mode")
    check(wait(plain_inputs_alive, 10), "remove: interfaces 0 and 1 are input devices again")
    check(not os.path.exists("/etc/udev/rules.d/70-xperience.rules"), "remove: our rules are gone")
    check(not glob.glob("/etc/udev-hid-bpf/0010-XPPen__Artist16_3rd*"), "remove: the driver file is gone")

    # 6. Install over a vendor interface another program took and did not give back (the user's desk,
    #    2026-11-17 08:07: the pen went dead).
    usb = [d for d in glob.glob("/sys/bus/usb/devices/*:1.2") if os.path.exists(os.path.join(d, "bInterfaceClass"))
           and open(os.path.join(os.path.dirname(os.path.realpath(d)), "idProduct")).read().strip() == "097e"]
    if check(len(usb) == 1, "unbound: found the tablet's third USB interface"):
        name = os.path.basename(usb[0])
        with open("/sys/bus/usb/drivers/usbhid/unbind", "w") as unbind:
            unbind.write(name)
        settle()
        check(len(hid_devices()) == 2, "unbound: the vendor interface has no HID device")
        check(install({}) == 0, "unbound: the helper succeeded")
        settle()
        check(wait(driver_in_charge, 10), "unbound: the helper gave the interface back and the driver is in charge")
        check(run(HELPER, "remove") == 0, "unbound: removed again")

    tablet.unplug()

    # 7. Tablets that do not say what they are, or say something no tablet is: no sizes reach the driver.
    check(install({}) == 0, "odd tablets: driver installed")
    for string_100, why in (("none", "no-answer"), ("0803670ad79508", "short-answer"),
                            ("0e030000000000000000000000000000", "answer-makes-no-sense")):
        check(wait(lambda: not hid_devices(), 10), f"odd tablet {why}: the last one is gone")
        tablet = Tablet(string_100)
        check(wait(lambda: tablet.said("ready"), 15), f"odd tablet {why}: configured")
        check(wait(lambda: len(hid_devices()) == 3, 10), f"odd tablet {why}: three HID devices")
        settle()
        for number, device in sorted(hid_devices().items()):
            got = tablet_properties(device)
            check(got == {"XPERIENCE_TABLET_PROBE": why}, f"odd tablet {why}: interface {number} has only the reason" + ("" if got == {"XPERIENCE_TABLET_PROBE": why} else f" (got {got})"))
        # The driver declines a tablet that did not say what it is: it stays as it comes out of the box.
        # The driver's own guard, not only the rules': loaded by hand with what this tablet answered.
        for device in hid_devices().values():
            run("udev-hid-bpf", "add", "--property", f"XPERIENCE_TABLET_PROBE={why}", device,
                "/etc/udev-hid-bpf/0010-XPPen__Artist16_3rd.bpf.o")
        settle()
        time.sleep(1)
        check(not tablet.said("raw on"), f"odd tablet {why}: never asked for raw mode")
        check(plain_inputs_alive(), f"odd tablet {why}: its own input devices are left working")
        tablet.unplug()
    check(run(HELPER, "remove") == 0, "odd tablets: removed again")

    # 8. Tablets of other shapes (tests/shapes.vectors), each plugged in as a stand-in of its own with the
    #    driver installed: the rules load it by what the tablet answers, and its reports go through the kernel.
    check(install({}) == 0, "shapes: driver installed")
    for label, answer, stand_in, vectors in shapes():
        play_shape(label, answer, stand_in, vectors)
    check(run(HELPER, "remove") == 0, "shapes: removed again")


def shapes():
    """[(label, @tablet answer, @stand-in, vectors)] from tests/shapes.vectors."""
    found, answer, stand_in = [], {}, {}
    for line in open(os.path.join(REPO, "tests", "shapes.vectors")):
        line = line.strip()
        if line.startswith("@tablet "):
            answer = dict(word.split("=") for word in line.split()[1:])
        elif line.startswith("@stand-in "):
            stand_in = dict(word.split("=") for word in line.split()[1:])
        elif line.startswith("@describe "):
            found.append((line.split()[1], dict(answer), stand_in, []))
        elif line and not line.startswith("#") and "|" in line:
            name, raw, expect = (part.strip() for part in line.split("|"))
            found[-1][3].append((name, raw.lower(), expect.split()))
    return found


def string_100(answer):
    """The string descriptor 100 a tablet with this answer sends."""
    x, y = int(answer["X_MAX"]), int(answer["Y_MAX"])
    p, r = int(answer["PRESSURE_MAX"]), int(answer["RESOLUTION"])
    data = [0x0E, 0x03, x & 0xFF, x >> 8 & 0xFF, y & 0xFF, y >> 8 & 0xFF, int(answer["KEYS"]), int(answer["DIALS"]),
            p & 0xFF, p >> 8, r & 0xFF, r >> 8, x >> 16, y >> 16]
    return bytes(data).hex()


def play_shape(label, answer, stand_in, vectors):
    product, name = stand_in["PRODUCT"], stand_in["NAME"].replace("_", " ")
    keys, dials = int(answer["KEYS"]), int(answer["DIALS"])
    # The kernel names input devices after the maker's and the product's USB strings.
    pen_name, pad_name = f"XPPen {name} Stylus", f"XPPen {name} Keypad"
    check(wait(lambda: not glob.glob("/sys/bus/hid/devices/0003:28BD:*"), 10), f"{label}: the last tablet is gone")
    tablet = Tablet(string_100(answer), product, name, stand_in["LENGTH"])
    try:
        check(wait(lambda: tablet.said("ready"), 15), f"{label}: configured")
        check(wait(lambda: tablet.said("raw on"), 10), f"{label}: the driver switched it to raw mode by itself")
        settle()

        def driven():
            for device in hid_devices(product).values():
                if pen_name in inputs(device):
                    return device
            return None
        vendor = wait(driven, 10)
        if not check(vendor, f"{label}: the driver's pen {pen_name!r} is there"):
            for device in hid_devices(product).values():
                note(f"{label}: {os.path.basename(device)} inputs {sorted(inputs(device))}")
            return
        nodes = inputs(vendor)
        check((pad_name in nodes) == bool(keys or dials), f"{label}: a pad only where there are keys or dials ({sorted(nodes)})")
        descriptor = hid.ReportDescriptor.from_bytes(read(os.path.join(vendor, "report_descriptor"), "rb"))
        raw = os.open(hidraw(vendor), os.O_RDONLY | os.O_NONBLOCK)
        pen = os.open(nodes[pen_name], os.O_RDONLY | os.O_NONBLOCK)
        pad = os.open(nodes[pad_name], os.O_RDONLY | os.O_NONBLOCK) if pad_name in nodes else None
        ranges = {code: absinfo(pen, code)[1:3] for code in (ABS_X, ABS_Y, ABS_PRESSURE)}
        want = {ABS_X: (0, int(answer["X_MAX"])), ABS_Y: (0, int(answer["Y_MAX"])), ABS_PRESSURE: (0, int(answer["PRESSURE_MAX"]))}
        check(ranges == want, f"{label}: pen ranges {ranges}")
        for vector, data, expect in vectors:
            for fd in (raw, pen, pad):
                if fd is not None:
                    drain(fd)
            tablet.send(data)
            if not select.select([raw], [], [], 2)[0]:
                check(False, f"{vector}: nothing reached hidraw")
                continue
            report = os.read(raw, 64)
            time.sleep(0.02)
            kind, wanted = expect[0], dict(item.split("=") for item in expect[1:])
            got = check_shapes.decode(descriptor, report)
            if kind == "pad":
                want_keys = [int(k) for k in wanted["keys"].split(",") if k]
                check(got["keys"] == want_keys, f"{vector}: hidraw keys {got['keys']}, want {want_keys}")
                rel = drain(pad)
                codes = {BTN_0 + k - 1 if k <= 10 else 0x130 + k - 11 for k in want_keys}
                pressed = {c for c in keys_down(pad) if BTN_0 <= c < 0x140}
                check(pressed == codes, f"{vector}: evdev pad keys {sorted(map(hex, pressed))}, want {sorted(map(hex, codes))}")
                for code, word in ((REL_WHEEL, "wheel"), (REL_HWHEEL, "pan")):
                    if word in wanted:
                        moved = sum(v for t, c, v in rel if t == EV_REL and c == code)
                        check(moved == int(wanted[word]), f"{vector}: evdev {word} {moved}, want {wanted[word]}")
            else:
                for field, word in (("X", "x"), ("Y", "y"), ("Tip Pressure", "p"), ("X Tilt", "tx"), ("Y Tilt", "ty")):
                    check(got.get(field) == int(wanted[word]), f"{vector}: hidraw {field} {got.get(field)}, want {wanted[word]}")
                if wanted["in"] == "1":
                    for code, word in ((ABS_X, "x"), (ABS_Y, "y"), (ABS_PRESSURE, "p")):
                        value = absinfo(pen, code)[0]
                        check(value == int(wanted[word]), f"{vector}: evdev {word} {value}, want {wanted[word]}")
        for fd in (raw, pen, pad):
            if fd is not None:
                os.close(fd)
    finally:
        tablet.unplug()


try:
    main()
except Stopped:
    note("stopped at the planted fault's line")
except Exception as error:  # the run itself broke: that is a failed verdict, with the reason
    import traceback
    note(traceback.format_exc())
    where = traceback.extract_tb(error.__traceback__)[-1]
    results.append((False, f"the judge stopped at check.py line {where.lineno} ({where.line}): {error}"))

failed = [what for ok, what in results if not ok]
with open(os.path.join(OUT, "result.txt"), "w") as result:
    result.write(f"rig: {len(results) - len(failed)} of {len(results)} checks passed\n")
    for what in failed:
        result.write(f"FAIL {what}\n")
sys.exit(1 if failed else 0)
