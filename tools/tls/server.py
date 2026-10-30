#!/usr/bin/env python3
"""A throwaway TLS server for the live TLS test (tools/test-tls.sh).

It reads the hello frame std:net sends, then either echoes raw bytes back
("echo" mode) or answers one plain HTTP request ("http" mode). "web" mode skips
the hello and is a plain HTTPS server, which is what http.get("https://...")
talks to. It exists so the handshake, the certificate checks, http.exchange and
https requests can be exercised against a real certificate without a network.
"""
import socket
import ssl
import struct
import sys
import threading


def read_exactly(conn, count):
    data = b""
    while len(data) < count:
        chunk = conn.recv(count - len(data))
        if not chunk:
            raise EOFError
        data += chunk
    return data


def serve(conn, mode):
    try:
        conn.settimeout(5)
        if mode == "web":
            pass  # no hello: this one is a plain HTTPS server
        else:
            size = struct.unpack(">I", read_exactly(conn, 4))[0]
            read_exactly(conn, size)  # the TERN hello

        if mode == "web":
            request = b""
            while b"\r\n\r\n" not in request:
                request += read_exactly(conn, 1)
            body = b"https works"
            conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: %d\r\n\r\n" % len(body) + body)
            while conn.recv(4096):
                pass
        elif mode == "echo":
            while True:
                chunk = conn.recv(4096)
                if not chunk:
                    return
                conn.sendall(chunk)
        else:
            request = b""
            while b"\r\n\r\n" not in request:
                request += read_exactly(conn, 1)
            body = b"https works"
            conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: %d\r\n\r\n" % len(body) + body)
            while conn.recv(4096):
                pass
    except Exception:
        pass
    finally:
        try:
            conn.close()
        except OSError:
            pass


def main():
    port = int(sys.argv[1])
    mode = sys.argv[2] if len(sys.argv) > 2 else "echo"
    certificate = sys.argv[3]
    key = sys.argv[4]

    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(certificate, key)

    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", port))
    server.listen(8)

    while True:
        raw, _ = server.accept()
        try:
            conn = context.wrap_socket(raw, server_side=True)
        except Exception:
            raw.close()
            continue
        threading.Thread(target=serve, args=(conn, mode), daemon=True).start()


if __name__ == "__main__":
    main()
