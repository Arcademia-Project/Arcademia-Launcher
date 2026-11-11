#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

#define BUFFER_SIZE 8192

static wchar_t pipe_path[512];
static unsigned short bridge_port;

typedef struct
{
    HANDLE pipe;
    SOCKET socket;
} connection_t;

static BOOL overlapped_io(HANDLE pipe, char *buffer, DWORD length, DWORD *transferred, BOOL write)
{
    OVERLAPPED overlapped = {0};
    overlapped.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (overlapped.hEvent == NULL)
        return FALSE;

    BOOL ok = write
        ? WriteFile(pipe, buffer, length, transferred, &overlapped)
        : ReadFile(pipe, buffer, length, transferred, &overlapped);
    if (!ok && GetLastError() == ERROR_IO_PENDING)
        ok = GetOverlappedResult(pipe, &overlapped, transferred, TRUE);

    CloseHandle(overlapped.hEvent);
    return ok;
}

static DWORD WINAPI pipe_to_socket(LPVOID argument)
{
    connection_t *connection = argument;
    char buffer[BUFFER_SIZE];
    DWORD read;

    while (overlapped_io(connection->pipe, buffer, sizeof buffer, &read, FALSE) && read > 0)
    {
        DWORD sent = 0;
        while (sent < read)
        {
            int result = send(connection->socket, buffer + sent, (int)(read - sent), 0);
            if (result <= 0)
                goto done;
            sent += (DWORD)result;
        }
    }

done:
    shutdown(connection->socket, SD_SEND);
    return 0;
}

static DWORD WINAPI serve(LPVOID argument)
{
    connection_t connection = {argument, INVALID_SOCKET};
    struct sockaddr_in address = {0};
    address.sin_family = AF_INET;
    address.sin_port = htons(bridge_port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    connection.socket = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (connection.socket == INVALID_SOCKET || connect(connection.socket, (struct sockaddr *)&address, sizeof address) != 0)
    {
        fwprintf(stderr, L"[Bridge] Could not reach the launcher on port %u (%d)\n", bridge_port, WSAGetLastError());
        if (connection.socket != INVALID_SOCKET)
            closesocket(connection.socket);
        DisconnectNamedPipe(connection.pipe);
        CloseHandle(connection.pipe);
        return 0;
    }

    HANDLE upstream = CreateThread(NULL, 0, pipe_to_socket, &connection, 0, NULL);
    char buffer[BUFFER_SIZE];
    int received;

    while ((received = recv(connection.socket, buffer, sizeof buffer, 0)) > 0)
    {
        DWORD offset = 0;
        while (offset < (DWORD)received)
        {
            DWORD written;
            if (!overlapped_io(connection.pipe, buffer + offset, (DWORD)received - offset, &written, TRUE))
                goto done;
            offset += written;
        }
    }

done:
    DisconnectNamedPipe(connection.pipe);
    if (upstream != NULL)
    {
        WaitForSingleObject(upstream, INFINITE);
        CloseHandle(upstream);
    }
    closesocket(connection.socket);
    CloseHandle(connection.pipe);
    return 0;
}

static HANDLE create_instance(void)
{
    return CreateNamedPipeW(
        pipe_path,
        PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        PIPE_UNLIMITED_INSTANCES,
        65536,
        65536,
        0,
        NULL);
}

static DWORD WINAPI listen_for_clients(LPVOID argument)
{
    HANDLE pipe = argument;

    for (;;)
    {
        if (pipe == INVALID_HANDLE_VALUE)
        {
            Sleep(250);
            pipe = create_instance();
            continue;
        }

        OVERLAPPED overlapped = {0};
        overlapped.hEvent = CreateEventW(NULL, TRUE, FALSE, NULL);
        BOOL connected = ConnectNamedPipe(pipe, &overlapped);
        DWORD error = GetLastError();
        DWORD ignored;
        if (!connected && error == ERROR_IO_PENDING)
            connected = GetOverlappedResult(pipe, &overlapped, &ignored, TRUE);
        else if (!connected && error == ERROR_PIPE_CONNECTED)
            connected = TRUE;
        CloseHandle(overlapped.hEvent);

        HANDLE next = create_instance();
        if (connected)
        {
            HANDLE worker = CreateThread(NULL, 0, serve, pipe, 0, NULL);
            if (worker != NULL)
                CloseHandle(worker);
            else
                CloseHandle(pipe);
        }
        else
            CloseHandle(pipe);

        pipe = next;
    }
}

static wchar_t *build_command_line(int argc, wchar_t **argv)
{
    size_t capacity = 1;
    for (int i = 1; i < argc; i++)
        capacity += wcslen(argv[i]) * 2 + 3;

    wchar_t *line = calloc(capacity, sizeof(wchar_t));
    if (line == NULL)
        return NULL;

    wchar_t *out = line;
    for (int i = 1; i < argc; i++)
    {
        const wchar_t *arg = argv[i];
        if (i > 1)
            *out++ = L' ';

        BOOL quote = *arg == L'\0' || wcspbrk(arg, L" \t\"") != NULL;
        if (!quote)
        {
            while (*arg)
                *out++ = *arg++;
            continue;
        }

        *out++ = L'"';
        size_t backslashes = 0;
        for (; *arg; arg++)
        {
            if (*arg == L'\\')
            {
                backslashes++;
                continue;
            }

            if (*arg == L'"')
                backslashes = backslashes * 2 + 1;
            for (; backslashes > 0; backslashes--)
                *out++ = L'\\';
            *out++ = *arg;
        }
        for (backslashes *= 2; backslashes > 0; backslashes--)
            *out++ = L'\\';
        *out++ = L'"';
    }

    *out = L'\0';
    return line;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 2)
    {
        fwprintf(stderr, L"Usage: Arcademia-WineBridge.exe <game.exe> [arguments...]\n");
        return 2;
    }

    const wchar_t *pipe_name = _wgetenv(L"ARCADEMIA_PIPE");
    const wchar_t *port = _wgetenv(L"ARCADEMIA_BRIDGE_PORT");

    WSADATA wsa;
    if (pipe_name != NULL && port != NULL && WSAStartup(MAKEWORD(2, 2), &wsa) == 0)
    {
        bridge_port = (unsigned short)wcstoul(port, NULL, 10);
        _snwprintf(pipe_path, 511, L"\\\\.\\pipe\\%ls", pipe_name);

        HANDLE first = create_instance();
        if (first == INVALID_HANDLE_VALUE)
            fwprintf(stderr, L"[Bridge] Could not create %ls (%lu)\n", pipe_path, GetLastError());
        else
        {
            HANDLE listener = CreateThread(NULL, 0, listen_for_clients, first, 0, NULL);
            if (listener != NULL)
                CloseHandle(listener);
            fwprintf(stderr, L"[Bridge] Relaying %ls to 127.0.0.1:%u\n", pipe_path, bridge_port);
        }
    }
    else
        fwprintf(stderr, L"[Bridge] No launcher session, starting the game without a relay\n");
    fflush(stderr);

    wchar_t *command_line = build_command_line(argc, argv);
    STARTUPINFOW startup = {0};
    startup.cb = sizeof startup;
    PROCESS_INFORMATION process = {0};

    if (command_line == NULL || !CreateProcessW(NULL, command_line, NULL, NULL, TRUE, 0, NULL, NULL, &startup, &process))
    {
        fwprintf(stderr, L"[Bridge] Could not start %ls (%lu)\n", argv[1], GetLastError());
        return 1;
    }

    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exit_code = 1;
    GetExitCodeProcess(process.hProcess, &exit_code);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    free(command_line);
    return (int)exit_code;
}
