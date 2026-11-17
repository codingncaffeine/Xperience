// SPDX-License-Identifier: MIT
// Prints the kernel's numbers that the settings program uses, from the kernel's own headers, one NAME=VALUE
// per line. The program's tests compare what it has against these.
#include <stddef.h>
#include <stdio.h>
#include <linux/input.h>
#include <linux/uinput.h>

#define NUMBER(name) printf(#name "=%ld\n", (long)(name))
#define KEY(name) printf("KEY_" #name "=%ld\n", (long)(KEY_##name))

int main(void)
{
	NUMBER(UI_SET_EVBIT); NUMBER(UI_SET_KEYBIT); NUMBER(UI_SET_RELBIT);
	NUMBER(UI_DEV_SETUP); NUMBER(UI_DEV_CREATE); NUMBER(UI_DEV_DESTROY);
	printf("EVIOCGABS_X=%ld\n", (long)EVIOCGABS(ABS_X));
	printf("EVIOCGABS_Y=%ld\n", (long)EVIOCGABS(ABS_Y));
	printf("sizeof_input_event=%ld\n", (long)sizeof(struct input_event));
	printf("sizeof_uinput_setup=%ld\n", (long)sizeof(struct uinput_setup));
	printf("sizeof_input_absinfo=%ld\n", (long)sizeof(struct input_absinfo));
	printf("offsetof_event_type=%ld\n", (long)offsetof(struct input_event, type));
	printf("offsetof_event_code=%ld\n", (long)offsetof(struct input_event, code));
	printf("offsetof_event_value=%ld\n", (long)offsetof(struct input_event, value));
	printf("offsetof_setup_name=%ld\n", (long)offsetof(struct uinput_setup, name));
	printf("offsetof_absinfo_maximum=%ld\n", (long)offsetof(struct input_absinfo, maximum));
	NUMBER(UINPUT_MAX_NAME_SIZE); NUMBER(BUS_VIRTUAL);
	NUMBER(EV_SYN); NUMBER(EV_KEY); NUMBER(EV_REL);
	NUMBER(REL_X); NUMBER(REL_Y); NUMBER(REL_WHEEL); NUMBER(REL_HWHEEL);
	NUMBER(BTN_MISC); NUMBER(BTN_LEFT); NUMBER(BTN_RIGHT); NUMBER(BTN_MIDDLE); NUMBER(BTN_STYLUS); NUMBER(BTN_STYLUS2);
	KEY(LEFTCTRL); KEY(LEFTSHIFT); KEY(LEFTALT); KEY(RIGHTALT); KEY(LEFTMETA);
	KEY(A); KEY(B); KEY(C); KEY(D); KEY(E); KEY(F); KEY(G); KEY(H); KEY(I); KEY(J); KEY(K); KEY(L); KEY(M);
	KEY(N); KEY(O); KEY(P); KEY(Q); KEY(R); KEY(S); KEY(T); KEY(U); KEY(V); KEY(W); KEY(X); KEY(Y); KEY(Z);
	KEY(0); KEY(1); KEY(2); KEY(3); KEY(4); KEY(5); KEY(6); KEY(7); KEY(8); KEY(9);
	KEY(F1); KEY(F2); KEY(F3); KEY(F4); KEY(F5); KEY(F6); KEY(F7); KEY(F8); KEY(F9); KEY(F10); KEY(F11); KEY(F12);
	KEY(SPACE); KEY(TAB); KEY(ENTER); KEY(ESC); KEY(BACKSPACE); KEY(DELETE); KEY(INSERT); KEY(HOME); KEY(END);
	KEY(PAGEUP); KEY(PAGEDOWN); KEY(UP); KEY(DOWN); KEY(LEFT); KEY(RIGHT);
	KEY(MINUS); KEY(EQUAL); KEY(LEFTBRACE); KEY(RIGHTBRACE); KEY(SEMICOLON); KEY(APOSTROPHE); KEY(GRAVE);
	KEY(BACKSLASH); KEY(COMMA); KEY(DOT); KEY(SLASH);
	return 0;
}
