// SPDX-License-Identifier: GPL-2.0-only
/*
 * A stand-in for the XPPen Artist 16 3rd on a virtual USB bus (raw-gadget on dummy_hcd), for the test machine
 * in tools/rig. It answers the host with the descriptors and strings captured from the real tablet, and like
 * the real one it sends its 14-byte raw reports only after it was told 02 b0 04 and stops after 02 b0 02.
 *
 *   fake-tablet IF0.rdesc.hex IF1.rdesc.hex IF2.rdesc.hex
 *   With FAKE_STRING_100=none in its environment the tablet does not answer string 100; with
 *   FAKE_STRING_100=HEX it answers with those bytes instead (length and type included).
 *   It reads commands from standard input, one per line:
 *       send HEX         the tablet sends this report on its vendor interface (dropped outside raw mode)
 *       quit
 *   and writes what happens to standard output, one line each:
 *       ready            configured by the host
 *       string N         the host asked for string N
 *       command HEX      the host sent this to the vendor interface (interrupt OUT or SET_REPORT)
 *       raw on|off       the tablet entered or left raw mode
 *       sent HEX | dropped HEX
 *       reset | disconnect
 */
#include <errno.h>
#include <fcntl.h>
#include <linux/usb/ch9.h>
#include <linux/usb/raw_gadget.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/ioctl.h>
#include <unistd.h>

#define HID_DT_REPORT_TYPE 0x22

// Read from the real tablet: /sys/bus/usb/devices/1-2/descriptors on 2026-11-17 (full speed, USB 1.1).
static const uint8_t device_descriptor[] = {
	0x12, 0x01, 0x10, 0x01, 0x00, 0x00, 0x00, 0x40, 0xbd, 0x28, 0x7e, 0x09, 0x00, 0x00, 0x01, 0x02, 0x03, 0x01,
};
static const uint8_t config_descriptor[] = {
	0x09, 0x02, 0x5b, 0x00, 0x03, 0x01, 0x00, 0xa0, 0x23,
	// Interface 0: boot mouse, 177-byte report descriptor, EP 0x81 IN 8 bytes.
	0x09, 0x04, 0x00, 0x00, 0x01, 0x03, 0x01, 0x02, 0x00,
	0x09, 0x21, 0x00, 0x01, 0x00, 0x01, 0x22, 0xb1, 0x00,
	0x07, 0x05, 0x81, 0x03, 0x08, 0x00, 0x01,
	// Interface 1: the pen in its power-on mode, 113 bytes, EP 0x82 IN 64 bytes.
	0x09, 0x04, 0x01, 0x00, 0x01, 0x03, 0x00, 0x02, 0x00,
	0x09, 0x21, 0x00, 0x01, 0x00, 0x01, 0x22, 0x71, 0x00,
	0x07, 0x05, 0x82, 0x03, 0x40, 0x00, 0x01,
	// Interface 2: vendor, 36 bytes, EP 0x83 IN and 0x03 OUT, 14 bytes each.
	0x09, 0x04, 0x02, 0x00, 0x02, 0x03, 0x00, 0x00, 0x00,
	0x09, 0x21, 0x00, 0x01, 0x00, 0x01, 0x22, 0x24, 0x00,
	0x07, 0x05, 0x83, 0x03, 0x0e, 0x00, 0x01,
	0x07, 0x05, 0x03, 0x03, 0x0e, 0x00, 0x0a,
};

static uint8_t report_descriptors[3][256];
static size_t report_descriptor_sizes[3];

// Strings 100 and 110 are given as the tablet sends them, length and type included.
static uint8_t string_100[255] = { 0x0e, 0x03, 0x67, 0x0a, 0xd7, 0x95, 0x08, 0x02, 0xff, 0x3f, 0xd8, 0x13, 0x01, 0x00 };
static size_t string_100_size = 14;
static const uint8_t string_110[] = { 0x09, 0x03, 0x00, 0x30, 0x00, 0x11, 0x08, 0x02, 0x00 };
static const char *const strings[] = {
	[1] = "XPPen", [2] = "Artist 16 3rd", [3] = "00000",
	[4] = "UG903_LDN1516F-A", [5] = "2026-01-05_Release001",
};

static int fd;
static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t configured_cond = PTHREAD_COND_INITIALIZER;
static int ep_in = -1, ep_out = -1;
static bool raw_mode;

static void say(const char *format, ...)
{
	va_list args;
	pthread_mutex_lock(&lock);
	va_start(args, format);
	vprintf(format, args);
	va_end(args);
	putchar('\n');
	fflush(stdout);
	pthread_mutex_unlock(&lock);
}

static void hex(char *out, const uint8_t *data, size_t length)
{
	for (size_t i = 0; i < length; i++)
		sprintf(out + 2 * i, "%02x", data[i]);
	out[2 * length] = 0;
}

static void die(const char *what)
{
	fprintf(stderr, "fake-tablet: %s: %s\n", what, strerror(errno));
	exit(1);
}

struct io {
	struct usb_raw_ep_io io;
	uint8_t data[512];
};

static void ep0_write(const void *data, size_t length, size_t asked)
{
	struct io io = { 0 };
	if (length > asked)
		length = asked;
	io.io.length = length;
	memcpy(io.data, data, length);
	if (ioctl(fd, USB_RAW_IOCTL_EP0_WRITE, &io) < 0)
		die("ep0 write");
}

static size_t ep0_read(uint8_t *data, size_t length)
{
	struct io io = { 0 };
	io.io.length = length;
	int got = ioctl(fd, USB_RAW_IOCTL_EP0_READ, &io);
	if (got < 0)
		die("ep0 read");
	if (data)
		memcpy(data, io.data, got);
	return got;
}

static void ep0_stall(void)
{
	ioctl(fd, USB_RAW_IOCTL_EP0_STALL, 0);
}

// What the host sends to the vendor interface: 02 b0 04 starts raw mode, 02 b0 02 ends it.
static void command(const uint8_t *data, size_t length)
{
	char text[2 * 512 + 1];
	hex(text, data, length);
	say("command %s", text);
	if (length >= 3 && data[0] == 0x02 && data[1] == 0xb0 && (data[2] == 0x04 || data[2] == 0x02)) {
		bool on = data[2] == 0x04;
		pthread_mutex_lock(&lock);
		bool changed = raw_mode != on;
		raw_mode = on;
		pthread_mutex_unlock(&lock);
		if (changed)
			say("raw %s", on ? "on" : "off");
	}
}

static void send_string(unsigned index, size_t asked)
{
	uint8_t buffer[255];
	say("string %u", index);
	if (index == 0) {
		const uint8_t languages[] = { 4, USB_DT_STRING, 0x09, 0x04 };
		ep0_write(languages, sizeof(languages), asked);
	} else if (index == 100) {
		if (string_100_size)
			ep0_write(string_100, string_100_size, asked);
		else
			ep0_stall();
	} else if (index == 110) {
		ep0_write(string_110, sizeof(string_110), asked);
	} else if (index < sizeof(strings) / sizeof(strings[0]) && strings[index]) {
		size_t length = strlen(strings[index]);
		buffer[0] = 2 + 2 * length;
		buffer[1] = USB_DT_STRING;
		for (size_t i = 0; i < length; i++) {
			buffer[2 + 2 * i] = strings[index][i];
			buffer[3 + 2 * i] = 0;
		}
		ep0_write(buffer, buffer[0], asked);
	} else {
		ep0_stall();
	}
}

static void enable_endpoints(void)
{
	pthread_mutex_lock(&lock);
	if (ep_in >= 0) {
		ioctl(fd, USB_RAW_IOCTL_EP_DISABLE, ep_in);
		ioctl(fd, USB_RAW_IOCTL_EP_DISABLE, ep_out);
		ep_in = ep_out = -1;
	}
	pthread_mutex_unlock(&lock);
	// The two plain interfaces' endpoints are enabled too, so the host can poll them; they never send.
	static const size_t offsets[] = { 27, 52, 77, 84 };
	int handles[4];
	for (int i = 0; i < 4; i++) {
		struct usb_endpoint_descriptor endpoint = { 0 };
		memcpy(&endpoint, config_descriptor + offsets[i], USB_DT_ENDPOINT_SIZE);
		handles[i] = ioctl(fd, USB_RAW_IOCTL_EP_ENABLE, &endpoint);
		if (handles[i] < 0)
			die("enable endpoint");
	}
	if (ioctl(fd, USB_RAW_IOCTL_VBUS_DRAW, config_descriptor[8]) < 0)
		die("vbus draw");
	if (ioctl(fd, USB_RAW_IOCTL_CONFIGURE, 0) < 0)
		die("configure");
	pthread_mutex_lock(&lock);
	ep_in = handles[2];
	ep_out = handles[3];
	pthread_cond_broadcast(&configured_cond);
	pthread_mutex_unlock(&lock);
}

static void control(const struct usb_ctrlrequest *request)
{
	unsigned value = request->wValue, index = request->wIndex, length = request->wLength;
	uint8_t data[512];

	if ((request->bRequestType & USB_TYPE_MASK) == USB_TYPE_STANDARD) {
		switch (request->bRequest) {
		case USB_REQ_GET_DESCRIPTOR:
			switch (value >> 8) {
			case USB_DT_DEVICE:
				ep0_write(device_descriptor, sizeof(device_descriptor), length);
				return;
			case USB_DT_CONFIG:
				ep0_write(config_descriptor, sizeof(config_descriptor), length);
				return;
			case USB_DT_STRING:
				send_string(value & 0xff, length);
				return;
			case HID_DT_REPORT_TYPE:
				if (index < 3) {
					ep0_write(report_descriptors[index], report_descriptor_sizes[index], length);
					return;
				}
				break;
			}
			ep0_stall();
			return;
		case USB_REQ_SET_CONFIGURATION:
			enable_endpoints();
			ep0_read(NULL, 0);
			say("ready");
			return;
		case USB_REQ_GET_CONFIGURATION:
			data[0] = 1;
			ep0_write(data, 1, length);
			return;
		case USB_REQ_SET_INTERFACE:
			ep0_read(NULL, 0);
			return;
		case USB_REQ_GET_INTERFACE:
			data[0] = 0;
			ep0_write(data, 1, length);
			return;
		case USB_REQ_GET_STATUS:
			data[0] = data[1] = 0;
			ep0_write(data, 2, length);
			return;
		}
		ep0_stall();
		return;
	}

	if ((request->bRequestType & USB_TYPE_MASK) == USB_TYPE_CLASS) {
		switch (request->bRequest) {
		case 0x0a: // SET_IDLE
		case 0x0b: // SET_PROTOCOL
			ep0_read(NULL, 0);
			return;
		case 0x09: // SET_REPORT
			if (length > sizeof(data)) {
				ep0_stall();
				return;
			}
			length = ep0_read(data, length);
			if (index == 2)
				command(data, length);
			return;
		}
	}
	ep0_stall();
}

static void *read_commands(void *unused)
{
	(void)unused;
	for (;;) {
		pthread_mutex_lock(&lock);
		while (ep_out < 0)
			pthread_cond_wait(&configured_cond, &lock);
		int handle = ep_out;
		pthread_mutex_unlock(&lock);
		struct io io = { 0 };
		io.io.ep = handle;
		io.io.length = 14;
		int got = ioctl(fd, USB_RAW_IOCTL_EP_READ, &io);
		if (got < 0) {
			// The endpoint goes away when the host configures the tablet again; wait for the new one.
			pthread_mutex_lock(&lock);
			if (ep_out == handle)
				ep_out = -1;
			pthread_mutex_unlock(&lock);
			continue;
		}
		command(io.data, got);
	}
	return NULL;
}

static void *read_input(void *unused)
{
	(void)unused;
	char line[1024];
	while (fgets(line, sizeof(line), stdin)) {
		line[strcspn(line, "\r\n")] = 0;
		if (!strcmp(line, "quit"))
			exit(0);
		if (strncmp(line, "send ", 5) != 0) {
			say("unknown %s", line);
			continue;
		}
		struct io io = { 0 };
		size_t length = 0;
		for (const char *p = line + 5; p[0] && p[1] && length < sizeof(io.data); p += 2) {
			unsigned byte;
			if (sscanf(p, "%2x", &byte) != 1)
				break;
			io.data[length++] = byte;
		}
		pthread_mutex_lock(&lock);
		while (ep_in < 0)
			pthread_cond_wait(&configured_cond, &lock);
		bool on = raw_mode;
		io.io.ep = ep_in;
		pthread_mutex_unlock(&lock);
		if (!on) {
			say("dropped %s", line + 5);
			continue;
		}
		io.io.length = length;
		if (ioctl(fd, USB_RAW_IOCTL_EP_WRITE, &io) < 0)
			say("failed %s %s", line + 5, strerror(errno));
		else
			say("sent %s", line + 5);
	}
	exit(0);
}

static size_t read_hex_file(const char *path, uint8_t *out, size_t room)
{
	FILE *file = fopen(path, "r");
	if (!file)
		die(path);
	size_t length = 0;
	unsigned byte;
	while (length < room && fscanf(file, " %2x", &byte) == 1)
		out[length++] = byte;
	fclose(file);
	return length;
}

int main(int argc, char **argv)
{
	if (argc != 4) {
		fprintf(stderr, "usage: %s IF0.rdesc.hex IF1.rdesc.hex IF2.rdesc.hex\n", argv[0]);
		return 2;
	}
	for (int i = 0; i < 3; i++)
		report_descriptor_sizes[i] = read_hex_file(argv[1 + i], report_descriptors[i], sizeof(report_descriptors[i]));
	if (report_descriptor_sizes[0] != 177 || report_descriptor_sizes[1] != 113 || report_descriptor_sizes[2] != 36) {
		fprintf(stderr, "fake-tablet: the report descriptors are not the tablet's (177, 113, 36 bytes)\n");
		return 2;
	}

	const char *answer = getenv("FAKE_STRING_100");
	if (answer && !strcmp(answer, "none")) {
		string_100_size = 0;
	} else if (answer) {
		string_100_size = 0;
		unsigned byte;
		for (const char *p = answer; p[0] && p[1] && string_100_size < sizeof(string_100); p += 2) {
			if (sscanf(p, "%2x", &byte) != 1)
				break;
			string_100[string_100_size++] = byte;
		}
	}

	fd = open("/dev/raw-gadget", O_RDWR);
	if (fd < 0)
		die("/dev/raw-gadget");
	struct usb_raw_init init = { 0 };
	strcpy((char *)init.driver_name, "dummy_udc");
	strcpy((char *)init.device_name, "dummy_udc.0");
	init.speed = USB_SPEED_FULL;
	if (ioctl(fd, USB_RAW_IOCTL_INIT, &init) < 0)
		die("init");
	if (ioctl(fd, USB_RAW_IOCTL_RUN, 0) < 0)
		die("run");

	pthread_t commands, input;
	pthread_create(&commands, NULL, read_commands, NULL);
	pthread_create(&input, NULL, read_input, NULL);

	for (;;) {
		struct {
			struct usb_raw_event event;
			struct usb_ctrlrequest request;
		} event = { 0 };
		event.event.length = sizeof(event.request);
		if (ioctl(fd, USB_RAW_IOCTL_EVENT_FETCH, &event) < 0)
			die("event fetch");
		switch (event.event.type) {
		case USB_RAW_EVENT_CONTROL:
			control(&event.request);
			break;
		case USB_RAW_EVENT_RESET:
			say("reset");
			break;
		case USB_RAW_EVENT_DISCONNECT:
			say("disconnect");
			break;
		}
	}
}
