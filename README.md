# Xperience

Linux support for the XPPen Artist 16 3rd pen display.

**Status: early.** The driver builds and passes its offline tests. It has not been run on the tablet yet.

## Driver

`driver/` is an HID-BPF program, loaded by [udev-hid-bpf](https://gitlab.freedesktop.org/libevdev/udev-hid-bpf).
Out of the box the tablet's keys and dials type fixed key combinations and the second pen button acts as an
eraser. With the program loaded the tablet shows up as:

- a pen with two buttons, an eraser end, 16383 pressure levels and tilt
- a pad with 8 keys and 2 dials

so the desktop's own tablet settings can map and rebind them.

```
tools/build-driver.sh    # needs clang and the libbpf headers; writes bin/0010-XPPen__Artist16_3rd.bpf.o
tools/test-driver.sh     # needs python3; no root and no tablet
```

## License

Each file names its license. The driver and its tests are GPL-2.0-only (`LICENSES/GPL-2.0-only.txt`).
