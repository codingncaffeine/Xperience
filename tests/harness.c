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

int run_probe(unsigned int rdesc_size, const unsigned char **output, int *output_length);
int run_rdesc_fixup(unsigned char *data, unsigned long capacity, int size, const char **name);
int run_device_event(unsigned char *data, unsigned long capacity, int size);
void set_pen_upper_button(const char *value);
void set_pen_lower_button(const char *value);
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

static void probe_one(unsigned int size)
{
	const unsigned char *output;
	int output_length;
	int verdict = run_probe(size, &output, &output_length);

	printf("probe %u %d ", size, verdict);
	print_hex(output, output_length);
	printf("\n");
}

static void fixup_one(const char *path)
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
	result = run_rdesc_fixup(data, sizeof(data), size, &name);
	printf("rdesc %d %d ", size, result);
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

	probe_one(177);
	probe_one(113);
	probe_one(36);
	probe_one(64); /* a size this tablet does not have */

	for (int i = 1; i <= 3; i++)
		fixup_one(argv[i]);

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
		/* "@tilt VALUE" does the same for the tilt property. */
		if (sscanf(line, " @tilt %63s", name) == 1) {
			set_pen_tilt(strcmp(name, "unset") ? name : "");
			forget_pen_state();
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
