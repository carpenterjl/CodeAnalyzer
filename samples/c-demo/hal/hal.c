int hal_write_reg(int base, int value) {
    volatile int *reg = (volatile int *)base;
    *reg = value;
    return 0;
}

int hal_read_reg(int base, unsigned char *out) {
    volatile int *reg = (volatile int *)base;
    if (out) {
        *out = (unsigned char)*reg;
    }
    return 0;
}

int hal_enable(int base) {
    return hal_write_reg(base, 1);
}

void hal_reset(void) {
    hal_write_reg(0, 0);
}
