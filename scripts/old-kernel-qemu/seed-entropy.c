/*
 * Credits the guest kernel's input pool, so getrandom stops blocking: Linux 4.9 in an idle VM without a hardware RNG
 * never initializes the pool by itself, and the tests that open WebSockets then hang. For the test VMs that
 * scripts/old-kernel-qemu/run.sh boots, never for a real system: the bytes come from /dev/urandom before the pool is
 * initialized.
 */
#include <fcntl.h>
#include <linux/random.h>
#include <stdio.h>
#include <sys/ioctl.h>
#include <unistd.h>

int main(void)
{
    struct {
        int entropy_count;
        int buf_size;
        unsigned char buf[64];
    } e;
    int urandom = open("/dev/urandom", O_RDONLY);
    int random;

    if (urandom < 0 || read(urandom, e.buf, sizeof e.buf) != (ssize_t)sizeof e.buf) {
        perror("seed-entropy: /dev/urandom");
        return 1;
    }
    close(urandom);

    e.entropy_count = 8 * (int)sizeof e.buf;
    e.buf_size = (int)sizeof e.buf;
    random = open("/dev/random", O_WRONLY);
    if (random < 0 || ioctl(random, RNDADDENTROPY, &e) != 0) {
        perror("seed-entropy: RNDADDENTROPY");
        return 1;
    }
    close(random);
    return 0;
}
