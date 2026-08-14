#include "uart.h"

int selftest_uart(void) {
    unsigned char probe[2];
    uart_init();
    uart_write(probe, 2);
    return uart_read(probe);
}
