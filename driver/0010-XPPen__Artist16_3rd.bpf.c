// SPDX-License-Identifier: GPL-2.0-only
/* Copyright (c) 2026 codingncaffeine
 *
 * XPPen Artist 16 3rd pen display (28bd:097e).
 *
 * The pad report keeps the layout the udev-hid-bpf driver for the Artist 12 3rd
 * (28bd:097d) uses for this firmware family, so both tablets look alike to
 * user space.
 */

#include "vmlinux.h"
#include "hid_bpf.h"
#include "hid_bpf_helpers.h"
#include "hid_report_helpers.h"
#include <bpf/bpf_tracing.h>

#define VID_UGEE 0x28BD /* shared with other vendors, so match on the product too */
#define PID_ARTIST_16_3RD 0x097E

HID_BPF_CONFIG(
	HID_DEVICE(BUS_USB, HID_GROUP_GENERIC, VID_UGEE, PID_ARTIST_16_3RD),
);

/*
 * Out of the box the tablet has three HID interfaces:
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
 * The active area is 341.0 x 191.8 mm at 200 units per mm, so X runs to
 * 68199 and does not fit in the 16 bits it has in one piece. HID cannot
 * describe a field split across a report, so the event hook moves the pieces
 * together before the report is parsed.
 *
 * Pad report:
 *   byte 2      the eight keys, one bit each
 *   byte 7      dials: bit 0 / bit 1 first dial one way / the other,
 *               bit 2 / bit 3 the same for the second dial
 *
 * This program replaces the descriptors of interfaces 0 and 1 with an inert
 * one and describes a real pad and a real pen on interface 2. The pen then
 * reaches the system through exactly one device.
 */

#define PAD_RDESC_LENGTH 177
#define PEN_RDESC_LENGTH 113
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

#define RAW_DIAL1_UP 0x01
#define RAW_DIAL1_DOWN 0x02
#define RAW_DIAL2_UP 0x04
#define RAW_DIAL2_DOWN 0x08

/* The reports as this program hands them on. */
#define PAD_REPORT_ID 0xf0
#define PAD_REPORT_LENGTH 9

#define PEN_REPORT_ID 7
#define PEN_REPORT_LENGTH 12

#define PEN_STATE_RAW_BITS 0x0f /* tip, both buttons, eraser end: same bits as raw */
#define PEN_STATE_ERASER_DOWN 0x10
#define PEN_STATE_IN_RANGE 0x20

#define PEN_X_MAX 68199
#define PEN_Y_MAX 38359
#define PEN_PRESSURE_MAX 16383
#define PEN_TILT_MAX 60

/* The active area in tenths of a millimeter. */
#define PEN_WIDTH 3410
#define PEN_HEIGHT 1918

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

static const __u8 fixed_rdesc_vendor[] = {
	UsagePage_GenericDesktop
	Usage_GD_Keypad
	CollectionApplication(
		ReportId(PAD_REPORT_ID)
		/* Byte 1: the raw report's kind byte */
		ReportCount(1)
		ReportSize(8)
		Input(Const)
		UsagePage_Digitizers
		Usage_Dig_TabletFunctionKeys
		CollectionPhysical(
			/* Byte 2: the eight keys */
			UsagePage_Button
			UsageMinimum_i8(0x01)
			UsageMaximum_i8(0x08)
			LogicalMinimum_i8(0)
			LogicalMaximum_i8(1)
			ReportCount(8)
			ReportSize(1)
			Input(Var|Abs)
			/* Byte 3: unused */
			ReportCount(8)
			Input(Const)
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
			/* Byte 7: first dial */
			Usage_GD_Wheel
			LogicalMinimum_i8(-127)
			LogicalMaximum_i8(127)
			ReportCount(1)
			ReportSize(8)
			Input(Var|Rel)
			/* Byte 8: second dial */
			UsagePage_Consumer
			Usage_Con_ACPan
			Input(Var|Rel)
		)
	)
	UsagePage_Digitizers
	Usage_Dig_Pen
	CollectionApplication(
		ReportId(PEN_REPORT_ID)
		Usage_Dig_Stylus
		CollectionPhysical(
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
			/* Bytes 2-4: X, bytes 5-7: Y */
			ReportSize(24)
			ReportCount(1)
			PushPop(
				UsagePage_GenericDesktop
				Unit(cm)
				UnitExponent(-2)
				PhysicalMinimum_i16(0)
				PhysicalMaximum_i16(PEN_WIDTH)
				LogicalMaximum_i32(PEN_X_MAX)
				Usage_GD_X
				Input(Var|Abs)
				PhysicalMaximum_i16(PEN_HEIGHT)
				LogicalMaximum_i32(PEN_Y_MAX)
				Usage_GD_Y
				Input(Var|Abs)
			)
			/* Bytes 8-9: pressure */
			Usage_Dig_TipPressure
			LogicalMaximum_i16(PEN_PRESSURE_MAX)
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
		)
	)
};

SEC(HID_BPF_RDESC_FIXUP)
int BPF_PROG(xppen_artist16_3rd_rdesc_fixup, struct hid_bpf_ctx *hctx)
{
	__u8 *data = hid_bpf_get_data(hctx, 0 /* offset */, HID_MAX_DESCRIPTOR_SIZE /* size */);
	__s32 rdesc_size = hctx->size;

	if (!data)
		return 0; /* EPERM check */

	if (rdesc_size == VENDOR_RDESC_LENGTH) {
		/*
		 * Added after the vendor descriptor, not in place of it: the
		 * command that enters raw mode is an output report of that
		 * descriptor.
		 */
		__builtin_memcpy(data + rdesc_size, fixed_rdesc_vendor, sizeof(fixed_rdesc_vendor));
		return sizeof(fixed_rdesc_vendor) + rdesc_size;
	}

	hid_set_name(hctx->hid, "Disabled by HID-BPF XPPen Artist 16 3rd");

	__builtin_memcpy(data, disabled_rdesc, sizeof(disabled_rdesc));
	return sizeof(disabled_rdesc);
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

/* One step of a dial as a signed byte: the raw report has a bit per direction. */
static __always_inline __u8 dial_step(__u8 bits, __u8 up, __u8 down)
{
	if (bits & up)
		return 1;
	if (bits & down)
		return 0xff;
	return 0;
}

SEC(HID_BPF_DEVICE_EVENT)
int BPF_PROG(xppen_artist16_3rd_device_event, struct hid_bpf_ctx *hctx)
{
	__u8 *data = hid_bpf_get_data(hctx, 0 /* offset */, RAW_REPORT_LENGTH /* size */);
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
		data[7] = dial_step(dials, RAW_DIAL1_UP, RAW_DIAL1_DOWN);
		data[8] = dial_step(dials, RAW_DIAL2_UP, RAW_DIAL2_DOWN);

		return PAD_REPORT_LENGTH;
	}

	if ((kind & 0xf0) == RAW_KIND_PEN || kind == RAW_KIND_PEN_GONE) {
		__u8 x_low = data[2], x_mid = data[3], x_high = data[10];
		__u8 y_low = data[4], y_mid = data[5], y_high = data[11];
		__u8 pressure_low = data[6];
		__u8 pressure_high = data[7] & 0x1f;
		__u8 tilt_x = data[8], tilt_y = data[9];
		__u8 state = 0;

		if (data[13] & 0x01)
			pressure_high |= 0x20;

		if (UDEV_PROP_XPERIENCE_PEN_TILT[0] == 'o' &&
		    UDEV_PROP_XPERIENCE_PEN_TILT[1] == 'f') {
			tilt_x = 0;
			tilt_y = 0;
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

	switch (ctx->rdesc_size) {
	case PAD_RDESC_LENGTH:
	case PEN_RDESC_LENGTH:
		ctx->retval = 0;
		break;
	case VENDOR_RDESC_LENGTH:
		hctx = hid_bpf_allocate_context(ctx->hid);
		if (!hctx) {
			ctx->retval = -EINVAL;
			break;
		}

		ctx->retval = enter_raw_mode(hctx);

		hid_bpf_release_context(hctx);
		break;
	default:
		ctx->retval = -EINVAL;
	}

	return 0;
}

char _license[] SEC("license") = "GPL";
