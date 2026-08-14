#include "uart.h"

#define UART_BAUD 115200
#define UART_FIFO_DEPTH 16

struct uart_state {
    int baud;
    int tx_pending;
    unsigned char fifo[UART_FIFO_DEPTH];
};

static struct uart_state g_uart;

static int uart_configure(int baud) {
    g_uart.baud = baud;
    return hal_write_reg(UART_BASE, baud);
}

int uart_init(void) {
    uart_configure(UART_BAUD);
    return hal_enable(UART_BASE);
}

int uart_write(const unsigned char *data, int len) {
    int i;
    for (i = 0; i < len; i++) {
        hal_write_reg(UART_BASE, data[i]);
    }
    g_uart.tx_pending = len;
    return len;
}

int uart_read(unsigned char *out) {
    return hal_read_reg(UART_BASE, out);
}

void uart_flush(void) {
    while (g_uart.tx_pending > 0) {
        g_uart.tx_pending--;
    }
}
