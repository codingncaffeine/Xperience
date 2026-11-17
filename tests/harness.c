// SPDX-License-Identifier: GPL-2.0-only
/* Copyright (c) 2026 codingncaffeine
 *
 * Runs the driver in user space over the fixtures and the vectors file and
 * prints what it produced, one result per line, for check_driver.py to judge.
 *
 *   harness <if0.rdesc.hex> <if1.rdesc.hex> <if2.rdesc.hex> <vectors>
 */
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int run_probe(const unsigned char *rdesc, unsigned int rdesc_size, const unsigned char **output, int *output_length);
int set_tablet(const char *name, const char *value);
int run_rdesc_fixup(unsigned char *data, unsigned long capacity, int size, const char **name);
int run_device_event(unsigned char *data, unsigned long capacity, int size);
void set_pen_upper_button(const char *value);
void set_pen_lower_button(const char *value);
void set_pen_switch_keys(const char *value);
void set_pen_smoothing(const char *value);
void forget_pen_state(void);
void set_pen_tilt(const char *value);

#define DESCRIPTOR_CAPACITY 4096 /* the kernel hands the fixup a buffer this large */
#define REPORT_CAPACITY 16	 /* a 14-byte report, in the kernel's 8-byte steps */

static int from_hex(const char *text, unsigned char *out, int capacity)
{
	int length = 0;

	while (*text) {
		unsigned int byte;

		if (isspace((unsigned char)*text)) {
			text++;
			continue;
		}
		if (length == capacity || sscanf(text, "%2x", &byte) != 1)
			return -1;
		out[length++] = byte;
		text += 2;
	}
	return length;
}

static void print_hex(const unsigned char *data, int length)
{
	if (length <= 0)
		printf("-");
	for (int i = 0; i < length; i++)
		printf("%02x", data[i]);
}

static char *read_file(const char *path)
{
	FILE *file = fopen(path, "r");
	static char text[3 * DESCRIPTOR_CAPACITY];
	size_t length;

	if (!file) {
		perror(path);
		exit(2);
	}
	length = fread(text, 1, sizeof(text) - 1, file);
	text[length] = 0;
	fclose(file);
	return text;
}

/* The Artist 16 3rd's answer to the probe, which the vectors are written for. */
static void describe_artist16_3rd(void)
{
	set_tablet("PROBE", "ok");
	set_tablet("X_MAX", "68199");
	set_tablet("Y_MAX", "38359");
	set_tablet("PRESSURE_MAX", "16383");
	set_tablet("RESOLUTION", "5080");
	set_tablet("KEYS", "8");
	set_tablet("DIALS", "2");
}

static void probe_one(const char *label, const unsigned char *rdesc, unsigned int size)
{
	const unsigned char *output;
	int output_length;
	int verdict = run_probe(rdesc, size, &output, &output_length);

	printf("%s %u %d ", label, size, verdict);
	print_hex(output, output_length);
	printf("\n");
}

/* The raw report length of the tablet the shapes are of, id included; "@stand-in ... LENGTH=n" sets it. */
static int raw_length = 14;

static void fixup_one(const char *label, const char *path)
{
	static unsigned char data[DESCRIPTOR_CAPACITY];
	const char *name;
	int size, result;

	memset(data, 0, sizeof(data));
	size = from_hex(read_file(path), data, sizeof(data));
	if (size < 0) {
		fprintf(stderr, "%s: not hex\n", path);
		exit(2);
	}
	/* The vendor descriptor's two report counts (input and output) follow the tablet's report length. */
	if (!strncmp(label, "shape", 5))
		for (int i = 0; i + 1 < size; i++)
			if (data[i] == 0x95 && data[i + 1] == 0x0d)
				data[i + 1] = raw_length - 1;
	result = run_rdesc_fixup(data, sizeof(data), size, &name);
	printf("%s %d %d ", label, size, result);
	print_hex(data, result > 0 ? result : size);
	printf(" %s\n", *name ? name : "-");
}

int main(int argc, char **argv)
{
	char line[512];
	FILE *vectors;

	if (argc != 5) {
		fprintf(stderr, "usage: %s if0.rdesc.hex if1.rdesc.hex if2.rdesc.hex vectors\n", argv[0]);
		return 2;
	}

	static unsigned char rdesc[3][DESCRIPTOR_CAPACITY];
	int sizes[3];
	/* A pen display's touch screen: Digitizers / Touch Screen, which the driver must leave alone. */
	static const unsigned char touch[] = { 0x05, 0x0d, 0x09, 0x04, 0xa1, 0x01, 0x85, 0x01, 0xc0, 0x00 };

	for (int i = 0; i < 3; i++) {
		sizes[i] = from_hex(read_file(argv[1 + i]), rdesc[i], sizeof(rdesc[i]));
		if (sizes[i] < 0) {
			fprintf(stderr, "%s: not hex\n", argv[1 + i]);
			return 2;
		}
	}

	/* A tablet that said nothing: every interface is declined and no descriptor changes. */
	for (int i = 0; i < 3; i++)
		probe_one("probe-undescribed", rdesc[i], sizes[i]);
	fixup_one("rdesc-undescribed", argv[3]);

	describe_artist16_3rd();
	for (int i = 0; i < 3; i++)
		probe_one("probe", rdesc[i], sizes[i]);
	probe_one("probe", touch, sizeof(touch));

	for (int i = 1; i <= 3; i++)
		fixup_one("rdesc", argv[i]);

	vectors = fopen(argv[4], "r");
	if (!vectors) {
		perror(argv[4]);
		return 2;
	}
	while (fgets(line, sizeof(line), vectors)) {
		unsigned char report[REPORT_CAPACITY] = { 0 };
		char name[64], hex[128];
		int size, result;

		/*
		 * "@upper-button VALUE" sets the udev property for the vectors after it; "unset" clears it.
		 * Properties are only read when the program is loaded, so each of these also starts the
		 * program's own state afresh, as a reload does.
		 */
		if (sscanf(line, " @upper-button %63s", name) == 1) {
			set_pen_upper_button(strcmp(name, "unset") ? name : "");
			forget_pen_state();
			continue;
		}
		if (sscanf(line, " @lower-button %63s", name) == 1) {
			set_pen_lower_button(strcmp(name, "unset") ? name : "");
			forget_pen_state();
			continue;
		}
		if (sscanf(line, " @switch-keys %63s", name) == 1) {
			set_pen_switch_keys(strcmp(name, "unset") ? name : "");
			forget_pen_state();
			continue;
		}
		if (sscanf(line, " @smoothing %63s", name) == 1) {
			set_pen_smoothing(strcmp(name, "unset") ? name : "");
			forget_pen_state();
			continue;
		}
		/* "@tilt VALUE" does the same for the tilt property. */
		if (sscanf(line, " @tilt %63s", name) == 1) {
			set_pen_tilt(strcmp(name, "unset") ? name : "");
			forget_pen_state();
			continue;
		}
		/*
		 * "@tablet NAME=VALUE ..." sets what the tablet said it is (the XPERIENCE_TABLET_ properties less
		 * that prefix) for the vectors after it, as a tablet of that shape plugged in would.
		 */
		if (!strncmp(line, "@tablet ", 8)) {
			char *word = strtok(line + 8, " \t\n");

			for (; word; word = strtok(NULL, " \t\n")) {
				char *equals = strchr(word, '=');

				if (!equals) {
					fprintf(stderr, "@tablet %s: not NAME=VALUE\n", word);
					return 2;
				}
				*equals = 0;
				if (!set_tablet(word, equals + 1)) {
					fprintf(stderr, "@tablet: no property %s\n", word);
					return 2;
				}
			}
			forget_pen_state();
			continue;
		}
		/* "@stand-in ... LENGTH=n" gives the tablet's raw report length (the rest is for tools/rig). */
		if (!strncmp(line, "@stand-in ", 10)) {
			const char *length = strstr(line, "LENGTH=");

			raw_length = length ? atoi(length + 7) : 14;
			continue;
		}
		/* "@describe LABEL" prints the vendor descriptor the driver builds for the tablet as set. */
		if (sscanf(line, " @describe %63s", name) == 1) {
			char label[80];

			snprintf(label, sizeof(label), "shape %s", name);
			fixup_one(label, argv[3]);
			continue;
		}
		/* "@fresh" alone starts the program's state afresh without changing a property. */
		if (!strncmp(line, "@fresh", 6)) {
			forget_pen_state();
			continue;
		}
		if (line[0] == '#' || sscanf(line, " %63[^| ] | %127[0-9a-fA-F ]", name, hex) != 2)
			continue;
		size = from_hex(hex, report, sizeof(report));
		if (size < 0) {
			fprintf(stderr, "%s: not hex\n", name);
			return 2;
		}
		result = run_device_event(report, sizeof(report), size);
		printf("event %s %d ", name, result);
		print_hex(report, result > 0 ? result : size);
		printf("\n");
	}
	fclose(vectors);
	return 0;
}
