// SPDX-License-Identifier: GPL-2.0-only
/*
 * The first program of the test machine in tools/rig, built static and placed alone in its initramfs. It loads
 * the modules for 9p and overlayfs from /modules, mounts the host's root read-only under a tmpfs overlay, adds
 * the repository and the output folder at the paths the kernel command line gives, and starts the guest script
 * there. Nothing the guest writes reaches the host except in the output folder.
 *
 * Command line: rigrepo=PATH (the repository, at the same path as on the host) rigscript=PATH
 */
#define _GNU_SOURCE
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mount.h>
#include <sys/reboot.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <unistd.h>

#ifndef MODULE_INIT_COMPRESSED_FILE
#define MODULE_INIT_COMPRESSED_FILE 4
#endif

static void stop(const char *what)
{
	fprintf(stderr, "rig-init: %s: %s\n", what, strerror(errno));
	fflush(stderr);
	sync();
	reboot(RB_POWER_OFF);
	for (;;)
		pause();
}

static void load(const char *name)
{
	char path[256];
	snprintf(path, sizeof(path), "/modules/%s.ko.zst", name);
	int module = open(path, O_RDONLY | O_CLOEXEC);
	if (module < 0)
		stop(path);
	if (syscall(SYS_finit_module, module, "", MODULE_INIT_COMPRESSED_FILE) < 0 && errno != EEXIST)
		stop(path);
	close(module);
}

static void mkdirs(const char *path)
{
	char copy[4096];
	snprintf(copy, sizeof(copy), "%s", path);
	for (char *p = copy + 1; *p; p++) {
		if (*p != '/')
			continue;
		*p = 0;
		mkdir(copy, 0755);
		*p = '/';
	}
	mkdir(copy, 0755);
}

static void share(const char *tag, const char *where, unsigned long flags)
{
	mkdirs(where);
	if (mount(tag, where, "9p", flags, "trans=virtio,version=9p2000.L,msize=512000,cache=loose") < 0)
		stop(where);
}

int main(void)
{
	mkdir("/dev", 0755);
	mkdir("/proc", 0755);
	if (mount("devtmpfs", "/dev", "devtmpfs", 0, NULL) < 0)
		stop("/dev");
	int console = open("/dev/console", O_RDWR);
	if (console >= 0) {
		dup2(console, 0);
		dup2(console, 1);
		dup2(console, 2);
	}
	if (mount("proc", "/proc", "proc", 0, NULL) < 0)
		stop("/proc");

	const char *modules[] = { "netfs", "9pnet", "9pnet_virtio", "9p", "overlay" };
	for (size_t i = 0; i < sizeof(modules) / sizeof(modules[0]); i++)
		load(modules[i]);

	const char *repo = getenv("rigrepo"), *script = getenv("rigscript");
	if (!repo || !script) {
		errno = EINVAL;
		stop("rigrepo= and rigscript= are needed on the command line");
	}

	share("hostroot", "/lower", MS_RDONLY);
	mkdirs("/upper");
	if (mount("tmpfs", "/upper", "tmpfs", 0, "size=512m,mode=0755") < 0)
		stop("/upper");
	mkdirs("/upper/data");
	mkdirs("/upper/work");
	mkdirs("/root");
	if (mount("overlay", "/root", "overlay", 0, "lowerdir=/lower,upperdir=/upper/data,workdir=/upper/work") < 0)
		stop("overlay");

	char where[4096];
	snprintf(where, sizeof(where), "/root%s", repo);
	share("repo", where, MS_RDONLY);
	share("out", "/root/rig-out", 0);

	umount2("/proc", MNT_DETACH);
	umount2("/dev", MNT_DETACH);
	if (chdir("/root") < 0 || mount(".", "/", NULL, MS_MOVE, NULL) < 0 || chroot(".") < 0 || chdir("/") < 0)
		stop("switch root");
	execl("/usr/bin/bash", "bash", script, (char *)NULL);
	stop(script);
	return 1;
}
