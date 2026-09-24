#ifndef MILLER_SERIAL_H
#define MILLER_SERIAL_H

/* Serial ports for the machine connection of Miller: list, open at a baud rate (8 data bits, no
 * parity, one stop bit, no flow control, DTR and RTS on), read with a timeout, write, close. The
 * operating system is chosen when the library is compiled, the API is the same on Windows and
 * Linux. A function returns MS_OK, a byte count or a negative status and ms_last_error() then
 * describes the failure. One thread uses a port at a time: the caller reads and writes from one
 * I/O thread, because Windows serializes reads and writes on a synchronous handle anyway. */

#include <stdint.h>

#if defined(_WIN32)
#if defined(MS_BUILDING)
#define MS_API __declspec(dllexport)
#else
#define MS_API __declspec(dllimport)
#endif
#else
#define MS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define MS_OK 0
#define MS_ERR_ARGUMENT (-1)
#define MS_ERR_OPEN (-2)
#define MS_ERR_IO (-3)
#define MS_ERR_MEMORY (-4)

typedef struct ms_port ms_port;

MS_API const char* ms_last_error(void);

/* Writes the names of the serial ports present, separated by '\n' and ended by NUL, into buffer.
 * Returns the length of the whole list without the NUL; when it is capacity or more, nothing
 * usable was written and the caller retries with a larger buffer. */
MS_API int32_t ms_list(char* buffer, int32_t capacity);

MS_API int32_t ms_open(const char* name, int32_t baud, ms_port** port);

/* Bytes read (0 when nothing arrived within timeout_ms) or a negative status when the port failed
 * or was removed. */
MS_API int32_t ms_read(ms_port* port, uint8_t* buffer, int32_t capacity, int32_t timeout_ms);

/* Writes all bytes or returns a negative status. */
MS_API int32_t ms_write(ms_port* port, const uint8_t* data, int32_t length);

MS_API void ms_close(ms_port* port);

#ifdef __cplusplus
}
#endif

#endif
