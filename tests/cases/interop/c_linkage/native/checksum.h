/* The header of interop/c_linkage. It declares a function of a C source without extern "C", so a
   C++ file that includes it looks for a C++ function of that name. */
#ifndef CHECKSUM_H
#define CHECKSUM_H

int checksum_of(int value);

#endif
