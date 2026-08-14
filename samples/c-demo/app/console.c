#include "uart.h"

void console_print(const unsigned char *text, int len) {
    uart_write(text, len);
    uart_flush();
}

int console_poll(unsigned char *out) {
    return uart_read(out);
}
