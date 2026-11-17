// SPDX-License-Identifier: GPL-2.0-only
/* Copyright (c) 2026 codingncaffeine
 *
 * XPPen tablets and pen displays of the family that switches to raw mode with
 * 02 b0 04: the Artist 16 3rd (28bd:097e) and the models in models.tsv. The
 * file keeps its first tablet's name because installed systems find it by it.
 *
 * The pad report keeps the layout the udev-hid-bpf driver for the Artist 12 3rd
 * (28bd:097d) uses for this firmware family, so those tablets look alike to
 * user space.
 *
 * What differs between the models is not written in here: when a tablet is
 * plugged in, xperience-probe asks it for its size, pressure range and keys
 * (USB string descriptor 100) and udev hands the answers to this program as
 * the XPERIENCE_TABLET_* properties below. Without them it takes no tablet.
 */

#include "vmlinux.h"
#include "hid_bpf.h"
#include "hid_bpf_helpers.h"
#include "hid_report_helpers.h"
#include <bpf/bpf_tracing.h>

#define VID_UGEE 0x28BD /* shared with other vendors, so match on the product too */

/* The products, from models.tsv: tools/build-driver.sh writes them as the loader's device list. */
#define XPERIENCE_DRIVER
#include "xperience-models.h"

/*
 * Out of the box the Artist 16 3rd has three HID interfaces (other models have
 * the same kinds, with descriptors of other sizes):
 *
 * - interface 0, 177-byte descriptor: a mouse and a keyboard. The eight keys
 *   and two dials type fixed key combinations here (B, Alt, Ctrl+S, ...), so
 *   nothing can tell a key on the tablet from a key on the keyboard.
 * - interface 1, 113-byte descriptor: the pen. It has one barrel switch and
 *   reports the second pen button as an eraser.
 * - interface 2, 36-byte descriptor: vendor reports, silent until asked.
 *
 * Writing 02 b0 04 to interface 2 switches the tablet to raw mode. Every
 * event then arrives on interface 2 as a 14-byte report with ID 2, and
 * byte 1 says what it is:
 *
 *   a0..af  pen in range   bit 0 tip, bit 1 lower button, bit 2 upper button,
 *                          bit 3 eraser end
 *   c0      pen left range
 *   f0      keys and dials
 *   others  status reports that carry no input
 *
 * Pen report:
 *   bytes 2-3   X, low 16 bits       byte 10  X, bits 16-23
 *   bytes 4-5   Y, low 16 bits       byte 11  Y, bits 16-23
 *   bytes 6-7   pressure, 13 bits    byte 13  bit 0 is pressure bit 13
 *   byte 8      X tilt in degrees    byte 9   Y tilt in degrees
 *
 * Reports of 12 bytes or more carry bits 16-23 of X and Y in bytes 10 and 11,
 * and those of 14 bytes bits 13-15 of the pressure in byte 13. The Artist 16
 * 3rd's active area is 341.0 x 191.8 mm at 200 units per mm, so X runs to
 * 68199 and does not fit in 16 bits. HID cannot describe a field split across a
 * report, so the event hook moves the pieces together before it is parsed.
 *
 * Pad report:
 *   bytes 2-3   the keys, one bit each (up to 16 here; the tablet has room for 40)
 *   byte 7      dials, as the maker's own software reads it: bit 0 / bit 1 (or
 *               all of ff) first dial one way / the other, bit 2 or 4 / bit 3
 *               or 5 the second dial; 40 and up are touch-ring codes, not dials
 *
 * This program replaces the descriptors of interfaces 0 and 1 with an inert
 * one and describes a real pad and a real pen on interface 2. The pen then
 * reaches the system through exactly one device.
 */

#define VENDOR_RDESC_LENGTH 36

#define RAW_REPORT_ID 0x02
#define RAW_REPORT_LENGTH 14

#define RAW_KIND_PAD 0xf0
#define RAW_KIND_PEN 0xa0 /* high nibble */
#define RAW_KIND_PEN_GONE 0xc0

#define RAW_PEN_TIP 0x01
#define RAW_PEN_LOWER_BUTTON 0x02
#define RAW_PEN_UPPER_BUTTON 0x04
#define RAW_PEN_ERASER 0x08

/* The reports as this program hands them on. */
#define PAD_REPORT_ID 0xf0
#define PAD_REPORT_LENGTH 9

#define PEN_REPORT_ID 7
#define PEN_REPORT_LENGTH 12
#define PEN_SHORT_REPORT_LENGTH 10

#define PEN_STATE_RAW_BITS 0x0f /* tip, both buttons, eraser end: same bits as raw */
#define PEN_STATE_ERASER_DOWN 0x10
#define PEN_STATE_IN_RANGE 0x20

#define PEN_TILT_MAX 60

#define MOST_KEYS 16

/*
 * What the tablet said it is, filled in by udev-hid-bpf from the properties
 * xperience-probe wrote: decimal numbers, and PROBE is "ok" when they are
 * there. DIALS comes from models.tsv, since a tablet does not say it.
 */
char UDEV_PROP_XPERIENCE_TABLET_PROBE[16];
char UDEV_PROP_XPERIENCE_TABLET_X_MAX[16];
char UDEV_PROP_XPERIENCE_TABLET_Y_MAX[16];
char UDEV_PROP_XPERIENCE_TABLET_PRESSURE_MAX[16];
char UDEV_PROP_XPERIENCE_TABLET_RESOLUTION[16];
char UDEV_PROP_XPERIENCE_TABLET_KEYS[16];
char UDEV_PROP_XPERIENCE_TABLET_DIALS[16];

/* A property as a number; anything that is not a digit ends it. */
static __always_inline __u32 number(const char *text)
{
	__u32 value = 0;
	int i;

	for (i = 0; i < 10; i++) {
		char digit = text[i];

		if (digit < '0' || digit > '9')
			break;
		value = value * 10 + (digit - '0');
	}
	return value;
}

/* True when the tablet answered and its answer can describe a pen. */
static __always_inline int tablet_described(void)
{
	return UDEV_PROP_XPERIENCE_TABLET_PROBE[0] == 'o' && UDEV_PROP_XPERIENCE_TABLET_PROBE[1] == 'k' &&
	       UDEV_PROP_XPERIENCE_TABLET_PROBE[2] == 0 && number(UDEV_PROP_XPERIENCE_TABLET_X_MAX) &&
	       number(UDEV_PROP_XPERIENCE_TABLET_Y_MAX) && number(UDEV_PROP_XPERIENCE_TABLET_PRESSURE_MAX) &&
	       number(UDEV_PROP_XPERIENCE_TABLET_RESOLUTION);
}

/*
 * What each pen button does in this program. "button" hands it on as a barrel
 * button for the desktop to bind. "eraser" turns the pen into its eraser while
 * the button is held, and "toggle" switches between pen and eraser each time
 * the button is pressed, staying switched until it is pressed again. Unset, or
 * set to anything else, the upper button is an eraser and the lower one a
 * button, which is how the tablet has them out of the box. udev-hid-bpf fills
 * these in from the udev properties of the same names when it loads the
 * program.
 */
char UDEV_PROP_XPERIENCE_PEN_UPPER_BUTTON[16];
char UDEV_PROP_XPERIENCE_PEN_LOWER_BUTTON[16];

/*
 * The tablet's keys that switch between pen and eraser when pressed, the way a
 * "toggle" pen button does: digits 1 to 8, counted from the top with the keys
 * on the left, so "3" is the third key and "36" the third and the sixth.
 * Unset, no key does. The key is still reported as a key.
 */
char UDEV_PROP_XPERIENCE_PEN_SWITCH_KEYS[16];

/*
 * How many of the pen's latest positions are averaged into the one handed on:
 * "2" to "8". It takes the tremble out of slow lines, and the line trails the
 * pen by about half that many reports. Unset, "1" or anything else hands every
 * position on as it comes.
 */
char UDEV_PROP_XPERIENCE_PEN_SMOOTHING[16];

#define SMOOTH_MOST 8

#define ROLE_BUTTON 0
#define ROLE_ERASER 1
#define ROLE_TOGGLE 2

/*
 * What the program remembers between reports; a reload starts both at zero.
 * The pen buttons as last seen, to tell a press from a button still held, and
 * whether a "toggle" button has the pen switched to its eraser. Both outlast
 * the pen leaving: a button held while the pen is lifted away and brought
 * back was pressed once.
 */
__u8 pen_buttons_held;
__u8 pen_switched_to_eraser;
/* The tablet's keys as last seen, for the same reason. */
__u8 pad_keys_held;
/* The positions being averaged, how many of them there are, and where the next one goes. */
__u32 smooth_x[SMOOTH_MOST];
__u32 smooth_y[SMOOTH_MOST];
__u8 smooth_count;
__u8 smooth_next;

/* The keys named by XPERIENCE_PEN_SWITCH_KEYS, one bit each, as the tablet reports them. */
static __always_inline __u8 switch_keys(void)
{
	__u8 mask = 0;
	int i;

	for (i = 0; i < 8; i++) {
		char digit = UDEV_PROP_XPERIENCE_PEN_SWITCH_KEYS[i];

		if (digit >= '1' && digit <= '8')
			mask |= 1 << (digit - '1');
	}
	return mask;
}

/*
 * Whether the pen reports how it leans. "off" has it report no tilt at all,
 * for when brushes that follow the tilt are not wanted. Anything else, or
 * nothing, leaves tilt as the tablet reports it.
 */
char UDEV_PROP_XPERIENCE_PEN_TILT[16];

static const __u8 disabled_rdesc[] = {
	/* Same report length as before, so the reports still fit. */
	FixedSizeVendorReport(RAW_REPORT_LENGTH)
};

/*
 * The descriptor added after the vendor one, in pieces. What depends on the
 * tablet (how many keys and dials, its size and pressure range) is written
 * between them by the fixup below.
 */
static const __u8 pad_start[] = {
	UsagePage_GenericDesktop
	Usage_GD_Keypad
	0xa1, 0x01, /* Collection (Application) */
		ReportId(PAD_REPORT_ID)
		/* Byte 1: the raw report's kind byte */
		ReportCount(1)
		ReportSize(8)
		Input(Const)
		UsagePage_Digitizers
		Usage_Dig_TabletFunctionKeys
		0xa1, 0x00, /* Collection (Physical) */
};

/* Bytes 2-3, the keys, are written by the fixup. Then: */
static const __u8 pad_middle[] = {
			/*
			 * Bytes 4-6: a stylus button and an X/Y pair that never
			 * change. User space takes a device with buttons, a
			 * stylus button and absolute axes for a tablet pad.
			 */
			UsagePage_Digitizers
			Usage_Dig_BarrelSwitch
			ReportCount(1)
			ReportSize(1)
			Input(Var|Abs)
			ReportCount(7)
			Input(Const)
			UsagePage_GenericDesktop
			Usage_GD_X
			Usage_GD_Y
			ReportCount(2)
			ReportSize(8)
			Input(Var|Abs)
};

/* Byte 7: the first dial, or nothing. */
static const __u8 pad_dial1[] = {
			Usage_GD_Wheel
			LogicalMinimum_i8(-127)
			LogicalMaximum_i8(127)
			ReportCount(1)
			ReportSize(8)
			Input(Var|Rel)
};
static const __u8 pad_no_dial1[] = {
			ReportCount(1)
			ReportSize(8)
			Input(Const)
};

/* Byte 8: the second dial, or nothing. */
static const __u8 pad_dial2[] = {
			UsagePage_Consumer
			Usage_Con_ACPan
			Input(Var|Rel)
};
static const __u8 pad_no_dial2[] = {
			Input(Const)
};

static const __u8 pad_end[] = {
		0xc0, /* End Collection (Physical) */
	0xc0, /* End Collection (Application) */
};

static const __u8 pen_start[] = {
	UsagePage_Digitizers
	Usage_Dig_Pen
	0xa1, 0x01, /* Collection (Application) */
		ReportId(PEN_REPORT_ID)
		Usage_Dig_Stylus
		0xa1, 0x00, /* Collection (Physical) */
			/*
			 * Byte 1: state. The eraser bits come before In Range
			 * because the kernel settles on pen or eraser when it
			 * reaches In Range.
			 */
			Usage_Dig_TipSwitch
			Usage_Dig_BarrelSwitch
			Usage_Dig_SecondaryBarrelSwitch
			Usage_Dig_Invert
			Usage_Dig_Eraser
			Usage_Dig_InRange
			LogicalMinimum_i8(0)
			LogicalMaximum_i8(1)
			ReportSize(1)
			ReportCount(6)
			Input(Var|Abs)
			ReportCount(2)
			Input(Const)
};
/*
 * The size of X and Y is written by the fixup: 24 bits (bytes 2-4 X, bytes 5-7
 * Y), or 16 (bytes 2-3, 4-5) for a tablet whose raw reports are too short to
 * carry a third byte. Then:
 */
static const __u8 pen_position[] = {
			0xa4, /* Push */
				UsagePage_GenericDesktop
				Unit(cm)
				UnitExponent(-2)
				PhysicalMinimum_i16(0)
};
/* The width and the X range are written by the fixup. Then: */
static const __u8 pen_x[] = {
				Usage_GD_X
				Input(Var|Abs)
};
/* The height and the Y range are written by the fixup. Then: */
static const __u8 pen_y[] = {
				Usage_GD_Y
				Input(Var|Abs)
			0xb4, /* Pop */
			/* Bytes 8-9: pressure */
			Usage_Dig_TipPressure
};
/* The pressure range is written by the fixup. Then: */
static const __u8 pen_end[] = {
			ReportSize(16)
			Input(Var|Abs)
			/*
			 * Bytes 10-11: tilt. Giving the unit lets the kernel
			 * report a resolution, so user space reads true degrees
			 * instead of assuming another tablet's range.
			 */
			PushPop(
				Unit(deg)
				UnitExponent(0)
				PhysicalMinimum_i8(-PEN_TILT_MAX)
				PhysicalMaximum_i8(PEN_TILT_MAX)
				LogicalMinimum_i8(-PEN_TILT_MAX)
				LogicalMaximum_i8(PEN_TILT_MAX)
				ReportSize(8)
				ReportCount(2)
				Usage_Dig_XTilt
				Usage_Dig_YTilt
				Input(Var|Abs)
			)
		0xc0, /* End Collection (Physical) */
	0xc0, /* End Collection (Application) */
};

/*
 * The tablet's vendor interface: a 36-byte descriptor of the vendor page ff0a
 * with report 02, the one raw mode is asked for and arrives on. Found by what
 * it says, so the interface's number does not matter.
 */
static __always_inline int is_vendor_interface(const __u8 *rdesc, __u32 size)
{
	return size == VENDOR_RDESC_LENGTH && rdesc[0] == 0x06 && rdesc[1] == 0x0a && rdesc[2] == 0xff &&
	       rdesc[7] == 0x85 && rdesc[8] == RAW_REPORT_ID && rdesc[13] == 0x95 && rdesc[14] >= 9;
}

/* A touch screen (Digitizers / Touch Screen) is the display's own and is left alone. */
static __always_inline int is_touch_screen(const __u8 *rdesc, __u32 size)
{
	__u32 i;

	for (i = 0; i < 512; i++) {
		if (i + 3 >= size)
			break;
		if (rdesc[i] == 0x05 && rdesc[i + 1] == 0x0d && rdesc[i + 2] == 0x09 && rdesc[i + 3] == 0x04)
			return 1;
	}
	return 0;
}

/* Writing into the descriptor: a byte, a piece, a little-endian 16- or 32-bit value. */
#define PUT(byte_)							\
	do {								\
		if (at >= HID_MAX_DESCRIPTOR_SIZE)			\
			return 0;					\
		data[at] = (byte_);					\
		at++;							\
	} while (0)
#define APPEND(piece_)							\
	do {								\
		if (at > HID_MAX_DESCRIPTOR_SIZE - sizeof(piece_))	\
			return 0;					\
		__builtin_memcpy_inline(data + at, piece_, sizeof(piece_)); \
		at += sizeof(piece_);					\
	} while (0)
#define PUT16(value_)							\
	do {								\
		PUT((value_) & 0xff);					\
		PUT(((value_) >> 8) & 0xff);				\
	} while (0)
#define PUT32(value_)							\
	do {								\
		PUT16((value_) & 0xffff);				\
		PUT16(((value_) >> 16) & 0xffff);			\
	} while (0)

SEC(HID_BPF_RDESC_FIXUP)
int BPF_PROG(xppen_artist16_3rd_rdesc_fixup, struct hid_bpf_ctx *hctx)
{
	__u8 *data = hid_bpf_get_data(hctx, 0 /* offset */, HID_MAX_DESCRIPTOR_SIZE /* size */);
	__s32 rdesc_size = hctx->size;
	__u32 x_max, y_max, pressure_max, resolution, keys, dials, first, at, raw_length;

	if (!data)
		return 0; /* EPERM check */

	if (!tablet_described() || rdesc_size <= 0 || rdesc_size > HID_MAX_DESCRIPTOR_SIZE)
		return 0;

	if (!is_vendor_interface(data, rdesc_size)) {
		if (is_touch_screen(data, rdesc_size))
			return 0;
		hid_set_name(hctx->hid, "Disabled by HID-BPF Xperience");
		__builtin_memcpy_inline(data, disabled_rdesc, sizeof(disabled_rdesc));
		return sizeof(disabled_rdesc);
	}

	x_max = number(UDEV_PROP_XPERIENCE_TABLET_X_MAX);
	y_max = number(UDEV_PROP_XPERIENCE_TABLET_Y_MAX);
	pressure_max = number(UDEV_PROP_XPERIENCE_TABLET_PRESSURE_MAX);
	resolution = number(UDEV_PROP_XPERIENCE_TABLET_RESOLUTION);
	keys = number(UDEV_PROP_XPERIENCE_TABLET_KEYS);
	dials = number(UDEV_PROP_XPERIENCE_TABLET_DIALS);
	if (keys > MOST_KEYS)
		keys = MOST_KEYS;
	if (dials > 2)
		dials = 2;
	/*
	 * The raw report's length, id included: the vendor descriptor's report
	 * count plus one. No report added here may be longer, because the USB HID
	 * driver reads as many bytes as the longest report described, and a
	 * transfer of shorter packets would never end.
	 */
	raw_length = data[14] + 1;

	/*
	 * Added after the vendor descriptor, not in place of it: the command that
	 * enters raw mode is an output report of that descriptor.
	 *
	 * Every copy is made in place: a program in the kernel has no memcpy to
	 * call, and a compiler asked for a plain copy is free to emit such a
	 * call, which the loader then cannot resolve.
	 */
	at = VENDOR_RDESC_LENGTH;

	/* A pen-only tablet gets no pad, so user space shows no keys it does not have. */
	if (keys || dials) {
		APPEND(pad_start);
		/*
		 * Bytes 2-3: the keys. The first ten are buttons 1-10 (BTN_0 to
		 * BTN_9); the rest are buttons 0x31 and on (BTN_A and on), which
		 * user space takes as more pad buttons. The bits left over are
		 * padding.
		 */
		if (keys) {
			first = keys < 10 ? keys : 10;
			PUT(0x05); PUT(0x09);	/* Usage Page (Button) */
			PUT(0x19); PUT(0x01);	/* Usage Minimum (1) */
			PUT(0x29); PUT(first);	/* Usage Maximum */
		}
		PUT(0x15); PUT(0x00);		/* Logical Minimum (0) */
		PUT(0x25); PUT(0x01);		/* Logical Maximum (1) */
		if (keys) {
			PUT(0x95); PUT(first);	/* Report Count */
			PUT(0x75); PUT(0x01);	/* Report Size (1) */
			PUT(0x81); PUT(0x02);	/* Input (Data, Var, Abs) */
			if (keys > 10) {
				PUT(0x19); PUT(0x31);		/* Usage Minimum (0x31) */
				PUT(0x29); PUT(0x30 + keys - 10); /* Usage Maximum */
				PUT(0x95); PUT(keys - 10);	/* Report Count */
				PUT(0x81); PUT(0x02);		/* Input (Data, Var, Abs) */
			}
		} else {
			PUT(0x75); PUT(0x01);	/* Report Size (1) */
		}
		if (keys < MOST_KEYS) {
			PUT(0x95); PUT(MOST_KEYS - keys); /* Report Count */
			PUT(0x81); PUT(0x01);	/* Input (Const) */
		}
		APPEND(pad_middle);
		if (dials >= 1)
			APPEND(pad_dial1);
		else
			APPEND(pad_no_dial1);
		if (dials == 2)
			APPEND(pad_dial2);
		else
			APPEND(pad_no_dial2);
		APPEND(pad_end);
	}

	/* The active area in tenths of a millimeter, from the units and the units per inch. */
	APPEND(pen_start);
	PUT(0x75); PUT(raw_length >= 12 ? 24 : 16);	/* Report Size */
	PUT(0x95); PUT(0x01);				/* Report Count (1) */
	APPEND(pen_position);
	PUT(0x46); PUT16((__u32)(((__u64)x_max * 254 + resolution / 2) / resolution)); /* Physical Maximum */
	PUT(0x27); PUT32(x_max);	/* Logical Maximum */
	APPEND(pen_x);
	PUT(0x46); PUT16((__u32)(((__u64)y_max * 254 + resolution / 2) / resolution)); /* Physical Maximum */
	PUT(0x27); PUT32(y_max);	/* Logical Maximum */
	APPEND(pen_y);
	PUT(0x26); PUT16(pressure_max);	/* Logical Maximum */
	APPEND(pen_end);

	return at;
}

static int enter_raw_mode(struct hid_bpf_ctx *hctx)
{
	static __u8 command[RAW_REPORT_LENGTH] = { RAW_REPORT_ID, 0xb0, 0x04 };
	int err;

	err = hid_bpf_hw_output_report(hctx, command, sizeof(command));
	if (err < 0)
		return err;

	return 0;
}

/*
 * Byte 7 of a key report as the two dials' steps, signed bytes: the codes the
 * maker's own software reads. Touch-ring codes (40 and up, but for ff) are no
 * dial and give nothing.
 */
static __always_inline void dial_steps(__u8 code, __u8 *first, __u8 *second)
{
	*first = 0;
	*second = 0;
	if (code == 0xff) {
		*first = 0xff;
		return;
	}
	if (code & 0xc0)
		return;
	if (code & 0x01)
		*first = 1;
	else if (code & 0x02)
		*first = 0xff;
	if (code & (0x04 | 0x10))
		*second = 1;
	else if (code & (0x08 | 0x20))
		*second = 0xff;
}

SEC(HID_BPF_DEVICE_EVENT)
int BPF_PROG(xppen_artist16_3rd_device_event, struct hid_bpf_ctx *hctx)
{
	/* Every report handed on fits in 12 bytes; the longest raw ones have 14. */
	__u8 *data = hid_bpf_get_data(hctx, 0 /* offset */, PEN_REPORT_LENGTH /* size */);
	__s32 size = hctx->size;
	__u8 kind;

	if (!data)
		return 0; /* EPERM check */

	if (data[0] != RAW_REPORT_ID)
		return 0;

	kind = data[1];

	if (kind == RAW_KIND_PAD) {
		__u8 dials = data[7];
		__u8 keys = data[2];

		if (keys & ~pad_keys_held & switch_keys())
			pen_switched_to_eraser = !pen_switched_to_eraser;
		pad_keys_held = keys;

		data[0] = PAD_REPORT_ID;
		/* The placeholders stay still whatever the tablet sends. */
		data[4] = 0;
		data[5] = 0;
		data[6] = 0;
		/* Each dial is read on its own, so turning both at once counts both. */
		dial_steps(dials, &data[7], &data[8]);

		return PAD_REPORT_LENGTH;
	}

	if ((kind & 0xf0) == RAW_KIND_PEN || kind == RAW_KIND_PEN_GONE) {
		__u8 x_low = data[2], x_mid = data[3], x_high = 0;
		__u8 y_low = data[4], y_mid = data[5], y_high = 0;
		__u8 pressure_low = data[6];
		__u8 pressure_high = data[7] & 0x1f;
		__u8 tilt_x = data[8], tilt_y = data[9];
		__u8 state = 0;

		/* Shorter reports have no bytes 10 and up; what they would hold is zero. */
		if (size >= 12) {
			x_high = data[10];
			y_high = data[11];
		}
		if (size >= RAW_REPORT_LENGTH) {
			__u8 *whole = hid_bpf_get_data(hctx, 0 /* offset */, RAW_REPORT_LENGTH /* size */);

			if (whole)
				pressure_high |= (whole[13] & 0x07) << 5;
		}

		if (UDEV_PROP_XPERIENCE_PEN_TILT[0] == 'o' &&
		    UDEV_PROP_XPERIENCE_PEN_TILT[1] == 'f') {
			tilt_x = 0;
			tilt_y = 0;
		}

		if (kind == RAW_KIND_PEN_GONE) {
			/* The next visit starts afresh: nothing is averaged across the gap. */
			smooth_count = 0;
			smooth_next = 0;
		} else {
			char window = UDEV_PROP_XPERIENCE_PEN_SMOOTHING[0];

			if (window >= '2' && window <= '0' + SMOOTH_MOST &&
			    UDEV_PROP_XPERIENCE_PEN_SMOOTHING[1] == 0) {
				__u32 x = x_low | x_mid << 8 | x_high << 16;
				__u32 y = y_low | y_mid << 8 | y_high << 16;
				__u32 sum_x = 0, sum_y = 0;
				__u8 size = window - '0';
				int i;

				smooth_x[smooth_next & (SMOOTH_MOST - 1)] = x;
				smooth_y[smooth_next & (SMOOTH_MOST - 1)] = y;
				smooth_next = (smooth_next + 1) % size;
				if (smooth_count < size)
					smooth_count++;
				for (i = 0; i < SMOOTH_MOST; i++) {
					if (i < smooth_count) {
						sum_x += smooth_x[i];
						sum_y += smooth_y[i];
					}
				}
				x = sum_x / smooth_count;
				y = sum_y / smooth_count;
				x_low = x;
				x_mid = x >> 8;
				x_high = x >> 16;
				y_low = y;
				y_mid = y >> 8;
				y_high = y >> 16;
			}
		}

		if (kind != RAW_KIND_PEN_GONE) {
			__u8 bits = kind & PEN_STATE_RAW_BITS;
			__u8 held = bits & (RAW_PEN_LOWER_BUTTON | RAW_PEN_UPPER_BUTTON);
			__u8 pressed = held & ~pen_buttons_held;
			__u8 upper = ROLE_ERASER, lower = ROLE_BUTTON;
			__u8 erase = 0;

			pen_buttons_held = held;

			if (UDEV_PROP_XPERIENCE_PEN_UPPER_BUTTON[0] == 'b')
				upper = ROLE_BUTTON;
			else if (UDEV_PROP_XPERIENCE_PEN_UPPER_BUTTON[0] == 't')
				upper = ROLE_TOGGLE;
			if (UDEV_PROP_XPERIENCE_PEN_LOWER_BUTTON[0] == 'e')
				lower = ROLE_ERASER;
			else if (UDEV_PROP_XPERIENCE_PEN_LOWER_BUTTON[0] == 't')
				lower = ROLE_TOGGLE;

			if (upper != ROLE_BUTTON) {
				if (upper == ROLE_ERASER && (bits & RAW_PEN_UPPER_BUTTON))
					erase = 1;
				if (upper == ROLE_TOGGLE && (pressed & RAW_PEN_UPPER_BUTTON))
					pen_switched_to_eraser = !pen_switched_to_eraser;
				bits &= ~RAW_PEN_UPPER_BUTTON;
			}
			if (lower != ROLE_BUTTON) {
				if (lower == ROLE_ERASER && (bits & RAW_PEN_LOWER_BUTTON))
					erase = 1;
				if (lower == ROLE_TOGGLE && (pressed & RAW_PEN_LOWER_BUTTON))
					pen_switched_to_eraser = !pen_switched_to_eraser;
				bits &= ~RAW_PEN_LOWER_BUTTON;
			}
			/* With nothing set to switch, nothing can switch back, so nothing stays switched. */
			if (upper != ROLE_TOGGLE && lower != ROLE_TOGGLE && !switch_keys())
				pen_switched_to_eraser = 0;
			if (erase || pen_switched_to_eraser)
				bits |= RAW_PEN_ERASER;

			state = bits | PEN_STATE_IN_RANGE;
			/*
			 * The kernel takes contact with the eraser from the
			 * Eraser usage and ignores the tip switch while the
			 * eraser is the tool in range.
			 */
			if ((bits & RAW_PEN_ERASER) && (bits & RAW_PEN_TIP))
				state |= PEN_STATE_ERASER_DOWN;
		}

		data[0] = PEN_REPORT_ID;
		data[1] = state;
		/* A tablet of short reports gets a short pen report: X and Y of 16 bits, as it sends them. */
		if (size < 12) {
			data[2] = x_low;
			data[3] = x_mid;
			data[4] = y_low;
			data[5] = y_mid;
			data[6] = pressure_low;
			data[7] = pressure_high;
			data[8] = tilt_x;
			data[9] = tilt_y;
			return PEN_SHORT_REPORT_LENGTH;
		}
		data[2] = x_low;
		data[3] = x_mid;
		data[4] = x_high;
		data[5] = y_low;
		data[6] = y_mid;
		data[7] = y_high;
		data[8] = pressure_low;
		data[9] = pressure_high;
		data[10] = tilt_x;
		data[11] = tilt_y;

		return PEN_REPORT_LENGTH;
	}

	/* A status report: left as the vendor report it is. */
	return 0;
}

HID_BPF_OPS(xppen_artist16_3rd) = {
	.hid_rdesc_fixup = (void *)xppen_artist16_3rd_rdesc_fixup,
	.hid_device_event = (void *)xppen_artist16_3rd_device_event,
};

SEC("syscall")
int probe(struct hid_bpf_probe_args *ctx)
{
	struct hid_bpf_ctx *hctx;

	/* A tablet that did not say what it is stays in the mode it comes in. */
	if (!tablet_described() || ctx->rdesc_size > sizeof(ctx->rdesc)) {
		ctx->retval = -EINVAL;
		return 0;
	}

	if (is_vendor_interface(ctx->rdesc, ctx->rdesc_size)) {
		hctx = hid_bpf_allocate_context(ctx->hid);
		if (!hctx) {
			ctx->retval = -EINVAL;
			return 0;
		}
		ctx->retval = enter_raw_mode(hctx);
		hid_bpf_release_context(hctx);
		return 0;
	}

	ctx->retval = is_touch_screen(ctx->rdesc, ctx->rdesc_size) ? -EINVAL : 0;
	return 0;
}

char _license[] SEC("license") = "GPL";
