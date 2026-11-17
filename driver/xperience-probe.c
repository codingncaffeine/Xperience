// SPDX-License-Identifier: GPL-2.0-only
/*
 * Asks a plugged-in tablet what it is, for the driver. udev runs it when one of the tablet's HID devices
 * appears, before the driver is loaded, and keeps what it prints as properties of that device:
 *
 *   xperience-probe /sys/PATH/OF/THE/HID/DEVICE
 *   xperience-probe --models      prints the product ids the driver takes, as the kernel names them (097E)
 *
 *   XPERIENCE_TABLET_X_MAX=68199          the pen's range, in the tablet's own units
 *   XPERIENCE_TABLET_Y_MAX=38359
 *   XPERIENCE_TABLET_PRESSURE_MAX=16383
 *   XPERIENCE_TABLET_RESOLUTION=5080      units per inch
 *   XPERIENCE_TABLET_KEYS=8               keys on the frame
 *   XPERIENCE_TABLET_FRAME=2              what else the frame has, as the tablet numbers it
 *   XPERIENCE_TABLET_DIALS=2              dials on the frame, from models.tsv (a tablet does not say)
 *   XPERIENCE_TABLET_SCREEN=1             1 for a pen display, from models.tsv
 *   XPERIENCE_TABLET_PROBE=ok             or the reason nothing else was printed
 *
 * The answer is the tablet's USB string descriptor 100, read with the standard request, as the maker's own
 * app does before it switches the tablet to raw mode and as the kernel's hid-uclogic does after. Layout,
 * counting the descriptor's length byte as byte 0: X bytes 2,3 (bits 16..23 in byte 12), Y bytes 4,5 (bits
 * 16..23 in byte 13), keys byte 6, frame byte 7, pressure bytes 8,9, resolution bytes 10,11 (0 means 5080).
 * An answer that makes no sense, or a product not in models.tsv, prints no sizes, and the driver then leaves
 * the tablet in the mode it comes in.
 */
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <linux/usbdevice_fs.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#include "xperience-models.h"

#define REQUEST_TIMEOUT_MS 500

static int reason(const char *why)
{
	printf("XPERIENCE_TABLET_PROBE=%s\n", why);
	return 0;
}

static int read_number(const char *folder, const char *name, unsigned *value, int base)
{
	char path[4096];
	snprintf(path, sizeof(path), "%s/%s", folder, name);
	FILE *file = fopen(path, "r");
	if (!file)
		return -1;
	int got = fscanf(file, base == 16 ? "%x" : "%u", value);
	fclose(file);
	return got == 1 ? 0 : -1;
}

static int string_descriptor(int fd, unsigned index, unsigned language, unsigned char *out, size_t room)
{
	struct usbdevfs_ctrltransfer request = {
		.bRequestType = 0x80,
		.bRequest = 0x06,
		.wValue = 0x0300 | index,
		.wIndex = language,
		.wLength = room,
		.timeout = REQUEST_TIMEOUT_MS,
		.data = out,
	};
	return ioctl(fd, USBDEVFS_CONTROL, &request);
}

int main(int argc, char **argv)
{
	if (argc == 2 && !strcmp(argv[1], "--models")) {
		for (size_t i = 0; i < sizeof(xperience_models) / sizeof(xperience_models[0]); i++)
			printf("%04X\n", xperience_models[i].product);
		return 0;
	}
	if (argc != 2) {
		fprintf(stderr, "usage: %s /sys/PATH/OF/THE/HID/DEVICE\n", argv[0]);
		return 2;
	}

	// The USB device is the nearest folder above the HID device that has a bus and device number.
	// The path may be a link, such as /sys/bus/hid/devices/...; the folders above the real one are the parents.
	char folder[4096];
	if (!realpath(argv[1], folder))
		return reason("no-device");
	unsigned bus = 0, device = 0;
	for (;;) {
		if (read_number(folder, "busnum", &bus, 10) == 0 && read_number(folder, "devnum", &device, 10) == 0)
			break;
		char *slash = strrchr(folder, '/');
		if (!slash || slash == folder)
			return reason("no-usb-device");
		*slash = 0;
	}

	// Only the models in the list: the driver takes no other, and the list has what a tablet cannot say.
	unsigned product = 0;
	const struct xperience_model *model = NULL;
	read_number(folder, "idProduct", &product, 16);
	for (size_t i = 0; i < sizeof(xperience_models) / sizeof(xperience_models[0]); i++)
		if (xperience_models[i].product == product)
			model = &xperience_models[i];
	if (!model)
		return reason("not-supported");

	char node[64];
	snprintf(node, sizeof(node), "/dev/bus/usb/%03u/%03u", bus, device);
	int fd = open(node, O_RDWR | O_CLOEXEC);
	if (fd < 0)
		return reason(errno == EACCES ? "no-access" : "no-node");

	// The language the tablet lists first, as a host asks for its strings.
	unsigned char data[255];
	unsigned language = 0x0409;
	int got = string_descriptor(fd, 0, 0, data, sizeof(data));
	if (got >= 4 && data[1] == 0x03)
		language = data[2] | data[3] << 8;

	got = string_descriptor(fd, 100, language, data, sizeof(data));
	close(fd);
	if (got < 0)
		return reason("no-answer");
	if (got < 12 || data[0] < 12 || data[1] != 0x03)
		return reason("short-answer");

	unsigned length = data[0] < got ? data[0] : (unsigned)got;
	unsigned x_max = data[2] | data[3] << 8 | (length > 12 ? data[12] << 16 : 0);
	unsigned y_max = data[4] | data[5] << 8 | (length > 13 ? data[13] << 16 : 0);
	unsigned keys = data[6], frame = data[7];
	unsigned pressure_max = data[8] | data[9] << 8;
	unsigned resolution = data[10] | data[11] << 8;
	if (resolution == 0)
		resolution = 5080;

	// Sizes no tablet has mean the answer is something else: the frame's key bitmap holds 40 keys, and the
	// largest tablet on the maker's list is well under 1000 by 1000 millimeters.
	if (x_max == 0 || y_max == 0 || pressure_max == 0 || keys > 40 ||
	    x_max / resolution > 40 || y_max / resolution > 40)
		return reason("answer-makes-no-sense");

	printf("XPERIENCE_TABLET_X_MAX=%u\n", x_max);
	printf("XPERIENCE_TABLET_Y_MAX=%u\n", y_max);
	printf("XPERIENCE_TABLET_PRESSURE_MAX=%u\n", pressure_max);
	printf("XPERIENCE_TABLET_RESOLUTION=%u\n", resolution);
	printf("XPERIENCE_TABLET_KEYS=%u\n", keys);
	printf("XPERIENCE_TABLET_FRAME=%u\n", frame);
	printf("XPERIENCE_TABLET_DIALS=%u\n", model->dials);
	printf("XPERIENCE_TABLET_SCREEN=%u\n", model->screen);
	return reason("ok");
}
