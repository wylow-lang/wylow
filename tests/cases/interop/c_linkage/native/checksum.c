/* The C source of interop/c_linkage, which defines the function with C linkage. */
#include "checksum.h"

int checksum_of(int value) { return value % 7; }
