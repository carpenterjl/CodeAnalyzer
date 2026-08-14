#include "uart.h"

#define SPI_BASE 0x40002000
#define SPI_CLOCK_HZ 8000000

struct spi_config {
    int clock_hz;
    int mode;
};

static struct spi_config g_spi;

int spi_init(void) {
    g_spi.clock_hz = SPI_CLOCK_HZ;
    g_spi.mode = 0;
    return hal_enable(SPI_BASE);
}

int spi_transfer(unsigned char byte) {
    hal_write_reg(SPI_BASE, byte);
    return hal_read_reg(SPI_BASE, 0);
}

void spi_log_status(void) {
    unsigned char msg[4];
    uart_write(msg, 4);
}
