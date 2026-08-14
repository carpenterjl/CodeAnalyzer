#define UART_BASE 0x40001000
int uart_init(void);
int uart_write(const unsigned char *data, int len);
int uart_read(unsigned char *out);
void uart_flush(void);
