# Parity with XPPen's own app

What XPPen's PenTablet app for Linux (version 4.0.15) offers for the Artist 16 3rd, and where each of those
things is in Xperience. The list was made from that app itself, its interface text, its settings files and what
its program does for this tablet, not from the manual: the manual describes the Windows build, which shows
pages the Linux build does not have.

**Nothing that needs the Xperience driver has been tried on a tablet yet.** "Yes" below means built and covered
by tests; the rows that depend on the driver say so.

| Mark | Meaning |
|---|---|
| Yes | In Xperience, working the same way. |
| Yes, differently | In Xperience, with a difference worth knowing; the note says what. |
| Not yet | XPPen's app has it for this tablet and Xperience does not. |

## Tablet page

| XPPen's app | Xperience | Note |
|---|---|---|
| Picture of the tablet; click a key or roller to change it | Yes | Needs the driver to take effect. |
| A profile per application | Yes, differently | Applications are picked from the ones running, or by the name the desktop knows them by, not by browsing to a program file. |
| Eye button: hide or show what each key does | Yes | |
| Reset the keys, after asking | Yes | |
| Calibration button | Yes | |
| Keys mirror and the two rollers trade places when the tablet is turned 90° or 180° | Yes | Assignments are kept in the order the keys run as the tablet is held. |

## What a key or pen button can do

| XPPen's app | Xperience | Note |
|---|---|---|
| Shortcut keys, up to 20 in a row; one is held, several are typed in turn | Yes | A single shortcut is handed to the desktop and works without Xperience running. |
| "Windows +": add the Super key | Yes | |
| Mouse control: Shift, Ctrl, Left Alt, Right Alt, Space with left, double, middle or right click, scroll up or down | Yes, differently | Plain clicks are the desktop's and land at the pen. Double-click, scrolling and clicks with Right Alt or Space are Xperience's and land at the mouse pointer. |
| Dual-function: two shortcuts taking turns | Yes | |
| Run application | Yes | |
| Show driver panel (shows the window, or puts it away) | Yes | |
| Switch monitor (each monitor, then all of them) | Yes | |
| Precision mode | Yes, differently | XPPen maps the tablet onto a fixed 320 × 210 pixel box at the pointer, left with the key or Esc. Xperience maps it onto a third of the screen area around the pen, left with the key. |
| [B]/[E]: brush and eraser shortcuts in turn | Yes | |
| Pen/Eraser on a pen button: switch between pen and eraser | Yes | Set under Pen, Options, as what the button is to the driver. Needs the driver. |
| Pen/Eraser on a tablet key | Not yet | |
| Eraser while a pen button is held | Yes | The upper button does this out of the box. |
| Switch roller 1 / roller 2 function | Yes | |
| Disable | Yes | |
| A name of your own for the action | Yes | |
| Default | Yes | |

## Rollers

| XPPen's app | Xperience | Note |
|---|---|---|
| Four functions per roller: zoom, scroll, brush size, rotate; each can be switched off or customized | Yes | Any function can go in any of the four places. |
| Customized: a shortcut for each direction and a name | Yes | |
| Customized: middle click, scroll up or scroll down for a direction | Yes, differently | Scrolling is a function of its own. Middle click on a turn is not there. |
| The switch key skips functions that are switched off | Yes | |
| The second roller starts on brush size and goes round the other way | Yes | |
| On-screen note of the function switched to | Yes | Shown the way the desktop shows volume changes. |

Which way is "up" on each roller has not been seen on a tablet yet.

## Work area

| XPPen's app | Xperience | Note |
|---|---|---|
| Monitor list, and all monitors | Yes | Also: whichever monitor has the active window. |
| Set full screen | Yes | |
| Customize screen area: drag, click two corners on the screen, or numbers | Yes | Clicking the corners needs the pen on one monitor. |
| Identify | Yes | |
| Rotation 0°, 90°, 180°, 270° | Yes | 90° and 270° have not been seen on a tablet yet. |
| Full area | Yes | |
| Customize active area: drag, tap two corners with the pen, or numbers | Yes | |
| Proportion | Yes | Also the other way round: "Match pen shape" narrows the screen area instead of the tablet's. |
| Calibration | Yes, differently | Five crosses, and it works with a custom area. XPPen's sets full screen and full area first. |
| Reset the work area | Yes, differently | The monitor and the rotation are kept. |

XPPen's app goes back to the full area whenever the rotation changes; Xperience keeps the area.

## Pen

| XPPen's app | Xperience | Note |
|---|---|---|
| Two pen buttons, each with any action | Yes, differently | XPPen starts the upper button as a left double-click. Xperience starts it as the eraser while held, which is what the tablet does with no driver at all. |
| Pressure curve with three points | Yes | Seven ready-made curves as well. |
| A patch to try the pressure on, and the current pressure | Yes | |
| Pressure on or off | Yes | Off makes every touch a full-pressure stroke, as in XPPen's app. |
| Tilt on or off | Yes | Needs the driver. |
| Mouse mode and its speed | Yes | |
| Reset the pen settings | Yes | |

## Settings

| XPPen's app | Xperience | Note |
|---|---|---|
| Switch off every key | Yes | |
| Show what a key does when it is pressed | Yes | |
| Export and import settings | Yes, differently | Xperience's own file. XPPen's settings files are not read. |
| Restore defaults | Yes, differently | The monitor and the rotation are kept. |
| Diagnostic tool: product, pen position and pressure | Yes | Under Settings, Diagnostics and Pen right now. |
| Diagnostic tool: firmware version | Not yet | |
| Tray icon with show and exit | Yes | |
| Fifteen languages | Not yet | English only. |
| Other XPPen tablets, and several at once | Not yet | The Artist 16 3rd only. |
| Feedback link, usage statistics sent to XPPen, check for a new version | No | Left out on purpose. |

## Not in XPPen's Linux app for this tablet

These exist in XPPen's Windows app, for other tablets, or in the manual, but the Linux build does not offer
them for the Artist 16 3rd, so there was nothing to carry over: the display settings page, the floating menu,
show desktop and screen keyboard as key actions, the overlay previewing all keys, the click mode of the pen
buttons, storing settings on the tablet, firmware update, lights, touch, the virtual tablet, several pens, the
pen roller and Windows Ink.

## In Xperience and not in XPPen's Linux app

- A floating menu of eight buttons, and "show desktop" and "screen keyboard" as key actions.
- Brightness of the tablet's screen, through the desktop.
- Shortcuts and plain clicks kept by the desktop itself, so they work without Xperience running.
- Each pen button as a button, an eraser while held, or a pen/eraser switch, decided in the driver.
- A record of what the tablet reports (Settings, Record), for checking pressure, tilt and keys after the fact.
- A switch that keeps XPPen's own app from starting at login without uninstalling it.
