Xperience
=========

Settings for the XPPen Artist 16 3rd pen display on Linux, and the driver for its keys and dials.

Run ./xperience from this folder (or "xperience" once installed from a package).

What it needs
-------------
- KDE Plasma 6 on Wayland. The pen's settings are the desktop's own, and Xperience reads and writes them there.
- For the keys, the dials and the pen-button options: the package udev-hid-bpf, and a kernel with HID-BPF
  (6.3 or newer). Settings, Driver installs the driver; that asks for administrator rights once.
- XPPen's own app must not be running while the driver is in charge. Nothing of it is needed; it can be
  uninstalled.

Without the driver the pen works as the kernel provides it, and its mapping, rotation, pressure and lower
button can be set; the keys and dials then type the shortcuts fixed in the tablet.

The record
----------
Xperience writes down what the tablet reports and what was done with it in
~/.local/state/xperience/xperience.log. It stays on this computer.

https://github.com/codingncaffeine/Xperience
