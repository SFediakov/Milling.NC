#if !defined(_WIN32)
/* CRTSCTS, B230400 and above, TIOCEXCL and O_CLOEXEC are outside strict C11. */
#define _DEFAULT_SOURCE
#endif

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "miller_serial.h"

#define MS_ERROR_SIZE 512
#define MS_NAME_MAX 250
#define MS_WRITE_TIMEOUT_MS 2000

static _Thread_local char ms_error_text[MS_ERROR_SIZE];

static int32_t ms_fail(int32_t status, const char* format, ...)
{
    va_list args;
    va_start(args, format);
    vsnprintf(ms_error_text, MS_ERROR_SIZE, format, args);
    va_end(args);
    return status;
}

MS_API const char* ms_last_error(void) { return ms_error_text; }

/* The length keeps growing when the buffer is full, so the caller learns the size it needs. */
static void ms_append(char* buffer, int32_t capacity, int32_t* length, const char* name)
{
    if (*length > 0) {
        if (*length < capacity) {
            buffer[*length] = '\n';
        }
        (*length)++;
    }
    for (const char* c = name; *c != '\0'; c++) {
        if (*length < capacity) {
            buffer[*length] = *c;
        }
        (*length)++;
    }
}

static void ms_terminate(char* buffer, int32_t capacity, int32_t length)
{
    buffer[length < capacity ? length : capacity - 1] = '\0';
}

static int32_t ms_check_open_arguments(const char* name, int32_t baud, ms_port** port)
{
    if (port == NULL) {
        return ms_fail(MS_ERR_ARGUMENT, "ms_open needs a result pointer.");
    }
    *port = NULL;
    if (name == NULL || name[0] == '\0' || strlen(name) > MS_NAME_MAX) {
        return ms_fail(MS_ERR_ARGUMENT, "The port name must have 1 to %d characters.", MS_NAME_MAX);
    }
    if (baud <= 0) {
        return ms_fail(MS_ERR_ARGUMENT, "The baud rate must be positive, got %d.", (int)baud);
    }
    return MS_OK;
}

#if defined(_WIN32)

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#define MS_DEVICE_PREFIX "\\\\.\\"
#define MS_REGISTRY_TEXT 256

struct ms_port {
    HANDLE handle;
    DWORD read_timeout;
};

/* The ports Windows has enumerated: the values under HARDWARE\DEVICEMAP\SERIALCOMM. The key is
 * missing on a machine that never had a serial port. */
MS_API int32_t ms_list(char* buffer, int32_t capacity)
{
    if (buffer == NULL || capacity < 1) {
        return ms_fail(MS_ERR_ARGUMENT, "ms_list needs a buffer.");
    }
    int32_t length = 0;
    HKEY key;
    LONG opened = RegOpenKeyExA(HKEY_LOCAL_MACHINE, "HARDWARE\\DEVICEMAP\\SERIALCOMM", 0, KEY_READ, &key);
    if (opened != ERROR_SUCCESS && opened != ERROR_FILE_NOT_FOUND) {
        return ms_fail(MS_ERR_IO, "The serial port list cannot be read (Windows error %ld).", (long)opened);
    }
    if (opened == ERROR_SUCCESS) {
        for (DWORD index = 0;; index++) {
            char value_name[MS_REGISTRY_TEXT];
            char data[MS_REGISTRY_TEXT];
            DWORD name_size = sizeof value_name;
            DWORD data_size = sizeof data - 1;
            DWORD type = 0;
            LONG status = RegEnumValueA(key, index, value_name, &name_size, NULL, &type, (LPBYTE)data, &data_size);
            if (status == ERROR_NO_MORE_ITEMS) {
                break;
            }
            if (status != ERROR_SUCCESS || type != REG_SZ) {
                continue;
            }
            data[data_size] = '\0';
            if (data[0] != '\0') {
                ms_append(buffer, capacity, &length, data);
            }
        }
        RegCloseKey(key);
    }
    ms_terminate(buffer, capacity, length);
    return length;
}

/* ReadIntervalTimeout MAXDWORD with multiplier MAXDWORD returns as soon as one byte is there, or
 * after the constant when none arrives; with multiplier and constant 0 it returns at once. */
static int ms_set_read_timeout(ms_port* port, int32_t timeout_ms)
{
    DWORD wanted = timeout_ms > 0 ? (DWORD)timeout_ms : 0;
    if (wanted == port->read_timeout) {
        return 1;
    }
    COMMTIMEOUTS timeouts;
    memset(&timeouts, 0, sizeof timeouts);
    timeouts.ReadIntervalTimeout = MAXDWORD;
    timeouts.ReadTotalTimeoutMultiplier = wanted > 0 ? MAXDWORD : 0;
    timeouts.ReadTotalTimeoutConstant = wanted;
    timeouts.WriteTotalTimeoutConstant = MS_WRITE_TIMEOUT_MS;
    if (!SetCommTimeouts(port->handle, &timeouts)) {
        return 0;
    }
    port->read_timeout = wanted;
    return 1;
}

MS_API int32_t ms_open(const char* name, int32_t baud, ms_port** port)
{
    int32_t checked = ms_check_open_arguments(name, baud, port);
    if (checked != MS_OK) {
        return checked;
    }
    char path[MS_NAME_MAX + sizeof MS_DEVICE_PREFIX];
    int prefixed = strncmp(name, MS_DEVICE_PREFIX, strlen(MS_DEVICE_PREFIX)) == 0;
    snprintf(path, sizeof path, "%s%s", prefixed ? "" : MS_DEVICE_PREFIX, name);
    HANDLE handle = CreateFileA(path, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (handle == INVALID_HANDLE_VALUE) {
        DWORD error = GetLastError();
        return ms_fail(MS_ERR_OPEN, "%s cannot be opened (%s, Windows error %lu).", name,
            error == ERROR_ACCESS_DENIED ? "in use by another program" : error == ERROR_FILE_NOT_FOUND ? "no such port" : "open failed",
            (unsigned long)error);
    }
    DCB dcb;
    memset(&dcb, 0, sizeof dcb);
    dcb.DCBlength = sizeof dcb;
    if (!GetCommState(handle, &dcb)) {
        DWORD error = GetLastError();
        CloseHandle(handle);
        return ms_fail(MS_ERR_OPEN, "%s is not a serial port (Windows error %lu).", name, (unsigned long)error);
    }
    dcb.BaudRate = (DWORD)baud;
    dcb.ByteSize = 8;
    dcb.Parity = NOPARITY;
    dcb.StopBits = ONESTOPBIT;
    dcb.fBinary = TRUE;
    dcb.fParity = FALSE;
    dcb.fOutxCtsFlow = FALSE;
    dcb.fOutxDsrFlow = FALSE;
    dcb.fDtrControl = DTR_CONTROL_ENABLE;
    dcb.fDsrSensitivity = FALSE;
    dcb.fTXContinueOnXoff = TRUE;
    dcb.fOutX = FALSE;
    dcb.fInX = FALSE;
    dcb.fErrorChar = FALSE;
    dcb.fNull = FALSE;
    dcb.fRtsControl = RTS_CONTROL_ENABLE;
    dcb.fAbortOnError = FALSE;
    if (!SetCommState(handle, &dcb)) {
        DWORD error = GetLastError();
        CloseHandle(handle);
        return ms_fail(MS_ERR_OPEN, "%s does not accept %d baud (Windows error %lu).", name, (int)baud, (unsigned long)error);
    }
    ms_port* result = (ms_port*)calloc(1, sizeof *result);
    if (result == NULL) {
        CloseHandle(handle);
        return ms_fail(MS_ERR_MEMORY, "Out of memory opening %s.", name);
    }
    result->handle = handle;
    result->read_timeout = MAXDWORD;
    if (!ms_set_read_timeout(result, 0)) {
        DWORD error = GetLastError();
        CloseHandle(handle);
        free(result);
        return ms_fail(MS_ERR_OPEN, "%s rejects the timeouts (Windows error %lu).", name, (unsigned long)error);
    }
    PurgeComm(handle, PURGE_RXCLEAR | PURGE_TXCLEAR);
    *port = result;
    return MS_OK;
}

MS_API int32_t ms_read(ms_port* port, uint8_t* buffer, int32_t capacity, int32_t timeout_ms)
{
    if (port == NULL || buffer == NULL || capacity < 1) {
        return ms_fail(MS_ERR_ARGUMENT, "ms_read needs a port and a buffer.");
    }
    if (!ms_set_read_timeout(port, timeout_ms)) {
        return ms_fail(MS_ERR_IO, "The port rejects the read timeout (Windows error %lu).", (unsigned long)GetLastError());
    }
    DWORD read = 0;
    if (!ReadFile(port->handle, buffer, (DWORD)capacity, &read, NULL)) {
        return ms_fail(MS_ERR_IO, "Reading the port failed; it may have been removed (Windows error %lu).", (unsigned long)GetLastError());
    }
    return (int32_t)read;
}

MS_API int32_t ms_write(ms_port* port, const uint8_t* data, int32_t length)
{
    if (port == NULL || data == NULL || length < 0) {
        return ms_fail(MS_ERR_ARGUMENT, "ms_write needs a port and data.");
    }
    int32_t done = 0;
    while (done < length) {
        DWORD written = 0;
        if (!WriteFile(port->handle, data + done, (DWORD)(length - done), &written, NULL)) {
            return ms_fail(MS_ERR_IO, "Writing the port failed; it may have been removed (Windows error %lu).", (unsigned long)GetLastError());
        }
        if (written == 0) {
            return ms_fail(MS_ERR_IO, "Writing the port timed out after %d ms.", MS_WRITE_TIMEOUT_MS);
        }
        done += (int32_t)written;
    }
    return done;
}

MS_API void ms_close(ms_port* port)
{
    if (port == NULL) {
        return;
    }
    CloseHandle(port->handle);
    free(port);
}

#else

#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <poll.h>
#include <sys/ioctl.h>
#include <termios.h>
#include <unistd.h>

#define MS_DEVICE_DIRECTORY "/dev"

struct ms_port {
    int fd;
};

/* USB serial adapters (CH340, FTDI, CP210x), USB CDC boards, the Raspberry Pi UART and Bluetooth
 * serial links. The 32 fixed ttyS entries exist without hardware and are left out; such a port is
 * typed by its path. */
static const char* const ms_prefixes[] = { "ttyUSB", "ttyACM", "ttyAMA", "rfcomm" };

MS_API int32_t ms_list(char* buffer, int32_t capacity)
{
    if (buffer == NULL || capacity < 1) {
        return ms_fail(MS_ERR_ARGUMENT, "ms_list needs a buffer.");
    }
    int32_t length = 0;
    DIR* directory = opendir(MS_DEVICE_DIRECTORY);
    if (directory == NULL) {
        return ms_fail(MS_ERR_IO, "%s cannot be read: %s.", MS_DEVICE_DIRECTORY, strerror(errno));
    }
    for (struct dirent* entry = readdir(directory); entry != NULL; entry = readdir(directory)) {
        for (size_t k = 0; k < sizeof ms_prefixes / sizeof ms_prefixes[0]; k++) {
            if (strncmp(entry->d_name, ms_prefixes[k], strlen(ms_prefixes[k])) == 0) {
                char path[MS_NAME_MAX + 8];
                snprintf(path, sizeof path, "%s/%s", MS_DEVICE_DIRECTORY, entry->d_name);
                ms_append(buffer, capacity, &length, path);
                break;
            }
        }
    }
    closedir(directory);
    ms_terminate(buffer, capacity, length);
    return length;
}

static int ms_speed(int32_t baud, speed_t* speed)
{
    switch (baud) {
    case 9600: *speed = B9600; return 1;
    case 19200: *speed = B19200; return 1;
    case 38400: *speed = B38400; return 1;
    case 57600: *speed = B57600; return 1;
    case 115200: *speed = B115200; return 1;
    case 230400: *speed = B230400; return 1;
    case 460800: *speed = B460800; return 1;
    case 921600: *speed = B921600; return 1;
    default: return 0;
    }
}

MS_API int32_t ms_open(const char* name, int32_t baud, ms_port** port)
{
    int32_t checked = ms_check_open_arguments(name, baud, port);
    if (checked != MS_OK) {
        return checked;
    }
    speed_t speed;
    if (!ms_speed(baud, &speed)) {
        return ms_fail(MS_ERR_ARGUMENT, "%d baud is not a standard rate.", (int)baud);
    }
    int fd = open(name, O_RDWR | O_NOCTTY | O_NONBLOCK | O_CLOEXEC);
    if (fd < 0) {
        return ms_fail(MS_ERR_OPEN, "%s cannot be opened: %s.", name, strerror(errno));
    }
    if (ioctl(fd, TIOCEXCL) != 0) {
        int error = errno;
        close(fd);
        return ms_fail(MS_ERR_OPEN, "%s cannot be reserved: %s.", name, strerror(error));
    }
    struct termios tty;
    if (tcgetattr(fd, &tty) != 0) {
        int error = errno;
        close(fd);
        return ms_fail(MS_ERR_OPEN, "%s is not a serial port: %s.", name, strerror(error));
    }
    tty.c_iflag &= ~(tcflag_t)(IGNBRK | BRKINT | PARMRK | ISTRIP | INLCR | IGNCR | ICRNL | IXON | IXOFF | IXANY);
    tty.c_oflag &= ~(tcflag_t)OPOST;
    tty.c_lflag &= ~(tcflag_t)(ECHO | ECHONL | ICANON | ISIG | IEXTEN);
    tty.c_cflag &= ~(tcflag_t)(CSIZE | PARENB | CSTOPB | CRTSCTS);
    tty.c_cflag |= (tcflag_t)(CS8 | CREAD | CLOCAL);
    tty.c_cc[VMIN] = 0;
    tty.c_cc[VTIME] = 0;
    if (cfsetispeed(&tty, speed) != 0 || cfsetospeed(&tty, speed) != 0 || tcsetattr(fd, TCSANOW, &tty) != 0) {
        int error = errno;
        close(fd);
        return ms_fail(MS_ERR_OPEN, "%s does not accept %d baud: %s.", name, (int)baud, strerror(error));
    }
    tcflush(fd, TCIOFLUSH);
    ms_port* result = (ms_port*)calloc(1, sizeof *result);
    if (result == NULL) {
        close(fd);
        return ms_fail(MS_ERR_MEMORY, "Out of memory opening %s.", name);
    }
    result->fd = fd;
    *port = result;
    return MS_OK;
}

MS_API int32_t ms_read(ms_port* port, uint8_t* buffer, int32_t capacity, int32_t timeout_ms)
{
    if (port == NULL || buffer == NULL || capacity < 1) {
        return ms_fail(MS_ERR_ARGUMENT, "ms_read needs a port and a buffer.");
    }
    struct pollfd wait = { port->fd, POLLIN, 0 };
    int ready = poll(&wait, 1, timeout_ms > 0 ? (int)timeout_ms : 0);
    if (ready < 0) {
        return errno == EINTR ? 0 : ms_fail(MS_ERR_IO, "Waiting on the port failed: %s.", strerror(errno));
    }
    if (ready == 0) {
        return 0;
    }
    if ((wait.revents & POLLIN) == 0) {
        return ms_fail(MS_ERR_IO, "The port was closed or removed.");
    }
    ssize_t count = read(port->fd, buffer, (size_t)capacity);
    if (count < 0) {
        return errno == EAGAIN || errno == EINTR ? 0 : ms_fail(MS_ERR_IO, "Reading the port failed: %s.", strerror(errno));
    }
    if (count == 0) {
        return ms_fail(MS_ERR_IO, "The port was closed or removed.");
    }
    return (int32_t)count;
}

MS_API int32_t ms_write(ms_port* port, const uint8_t* data, int32_t length)
{
    if (port == NULL || data == NULL || length < 0) {
        return ms_fail(MS_ERR_ARGUMENT, "ms_write needs a port and data.");
    }
    int32_t done = 0;
    while (done < length) {
        ssize_t count = write(port->fd, data + done, (size_t)(length - done));
        if (count > 0) {
            done += (int32_t)count;
            continue;
        }
        if (count < 0 && errno != EAGAIN && errno != EINTR) {
            return ms_fail(MS_ERR_IO, "Writing the port failed: %s.", strerror(errno));
        }
        struct pollfd wait = { port->fd, POLLOUT, 0 };
        int ready = poll(&wait, 1, MS_WRITE_TIMEOUT_MS);
        if (ready == 0) {
            return ms_fail(MS_ERR_IO, "Writing the port timed out after %d ms.", MS_WRITE_TIMEOUT_MS);
        }
        if (ready < 0 && errno != EINTR) {
            return ms_fail(MS_ERR_IO, "Waiting on the port failed: %s.", strerror(errno));
        }
        if (ready > 0 && (wait.revents & (POLLERR | POLLHUP | POLLNVAL)) != 0) {
            return ms_fail(MS_ERR_IO, "The port was closed or removed.");
        }
    }
    return done;
}

MS_API void ms_close(ms_port* port)
{
    if (port == NULL) {
        return;
    }
    close(port->fd);
    free(port);
}

#endif
