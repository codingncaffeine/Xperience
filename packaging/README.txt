Xperience
=========

Settings for XPPen pen tablets and pen displays on Linux (most of the lineup), and the driver for their keys and dials.

Run ./xperience from this folder (or "xperience" once installed from a package).

What it needs
-------------
- KDE Plasma 6 on Wayland. The pen's settings are the desktop's own, and Xperience reads and writes them there.
- For the keys, the dials and the pen-button options: a kernel with HID-BPF. Settings, Driver installs the
  driver; that asks for administrator rights once and brings along the package udev-hid-bpf when it is missing.
- XPPen's own app must not be running while the driver is in charge. Nothing of it is needed; it can be
  uninstalled.

Without the driver the pen works as the kernel provides it, and its mapping, rotation, pressure and lower
button can be set; the keys and dials then type the shortcuts fixed in the tablet.

The record
----------
Xperience writes down what the tablet reports and what was done with it in
~/.local/state/xperience/xperience.log. It stays on this computer.

https://github.com/codingncaffeine/Xperience
