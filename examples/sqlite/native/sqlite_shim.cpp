#include "sqlite_shim.h"

int sqlite_shim_bind_text(sqlite3_stmt* statement, int index, const char* text, int length) {
    return sqlite3_bind_text(statement, index, text, length, SQLITE_TRANSIENT);
}

int sqlite_shim_bind_blob(sqlite3_stmt* statement, int index, const void* data, int length) {
    return sqlite3_bind_blob(statement, index, data, length, SQLITE_TRANSIENT);
}
