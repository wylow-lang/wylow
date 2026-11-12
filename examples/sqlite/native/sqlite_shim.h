// SQLite's bind functions for text and blobs take a destructor, and SQLITE_TRANSIENT casts -1 to
// that function pointer type, which an extern block cannot express. These shims pass it.
#pragma once

#include <sqlite3.h>

extern "C" {
int sqlite_shim_bind_text(sqlite3_stmt* statement, int index, const char* text, int length);
int sqlite_shim_bind_blob(sqlite3_stmt* statement, int index, const void* data, int length);
}
