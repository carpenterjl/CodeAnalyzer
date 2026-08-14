#include "uart.h"

#define BANNER_LEN 8

static unsigned char banner[BANNER_LEN];

int board_init(void) {
    uart_init();
    spi_init();
    return 0;
}

int main(void) {
    board_init();
    uart_write(banner, BANNER_LEN);
    uart_flush();
    spi_log_status();
    hal_reset();
    return 0;
}
