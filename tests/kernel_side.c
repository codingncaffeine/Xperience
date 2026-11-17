// SPDX-License-Identifier: GPL-2.0-only
/* Copyright (c) 2026 codingncaffeine
 *
 * The kernel's side of the HID-BPF interface, played in user space so the
 * driver's entry points can be run and their output inspected. This file sees
 * the kernel's types and no C library header; harness.c is the other way round.
 */
#include <vmlinux.h>
#include "hid_bpf.h"

int probe(struct hid_bpf_probe_args *ctx);
int xppen_artist16_3rd_rdesc_fixup(struct hid_bpf_ctx *hctx);
int xppen_artist16_3rd_device_event(struct hid_bpf_ctx *hctx);
extern char UDEV_PROP_XPERIENCE_PEN_UPPER_BUTTON[16];
extern char UDEV_PROP_XPERIENCE_PEN_TILT[16];

static struct hid_device device;
static struct hid_bpf_ctx context;
static __u8 *buffer;
static unsigned long buffer_size;

static __u8 sent[64];
static int sent_length;

__u8 *hid_bpf_get_data(struct hid_bpf_ctx *ctx, unsigned int offset, const size_t size)
{
	if (offset + size > buffer_size)
		return (__u8 *)0;
	return buffer + offset;
}

struct hid_bpf_ctx *hid_bpf_allocate_context(unsigned int hid_id)
{
	return &context;
}

void hid_bpf_release_context(struct hid_bpf_ctx *ctx)
{
}

int hid_bpf_hw_output_report(struct hid_bpf_ctx *ctx, __u8 *buf, size_t size)
{
	if (size > sizeof(sent))
		return -22;
	__builtin_memcpy(sent, buf, size);
	sent_length = size;
	return size;
}

static void use(unsigned char *data, unsigned long capacity, int size)
{
	buffer = data;
	buffer_size = capacity;
	context.hid = &device;
	context.allocated_size = capacity;
	context.size = size;
}

/* Returns probe's verdict; *output and *output_length describe the report it wrote, if any. */
int run_probe(unsigned int rdesc_size, const unsigned char **output, int *output_length)
{
	static struct hid_bpf_probe_args args;

	sent_length = -1;
	use((unsigned char *)0, 0, 0);
	__builtin_memset(&args, 0, sizeof(args));
	args.hid = 1;
	args.rdesc_size = rdesc_size;
	args.retval = -1;
	probe(&args);
	*output = sent;
	*output_length = sent_length;
	return args.retval;
}

/* Returns the new descriptor size (0 = unchanged); *name is the device name afterwards. */
int run_rdesc_fixup(unsigned char *data, unsigned long capacity, int size, const char **name)
{
	int ret;

	__builtin_memset(&device, 0, sizeof(device));
	use(data, capacity, size);
	ret = xppen_artist16_3rd_rdesc_fixup(&context);
	*name = device.name;
	return ret;
}

/* Returns the new report size (0 = unchanged). */
int run_device_event(unsigned char *data, unsigned long capacity, int size)
{
	use(data, capacity, size);
	return xppen_artist16_3rd_device_event(&context);
}

/* Plays udev-hid-bpf filling in a property at load time; an empty value leaves it unset. */
static void set_property(char *property, unsigned int size, const char *value)
{
	unsigned int i = 0;

	__builtin_memset(property, 0, size);
	for (; value[i] && i < size - 1; i++)
		property[i] = value[i];
}

void set_pen_upper_button(const char *value)
{
	set_property(UDEV_PROP_XPERIENCE_PEN_UPPER_BUTTON, sizeof(UDEV_PROP_XPERIENCE_PEN_UPPER_BUTTON), value);
}

void set_pen_tilt(const char *value)
{
	set_property(UDEV_PROP_XPERIENCE_PEN_TILT, sizeof(UDEV_PROP_XPERIENCE_PEN_TILT), value);
}
