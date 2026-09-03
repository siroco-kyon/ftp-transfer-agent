"""テスト用の最小 SFTP サーバ (paramiko)。

Docker が無い環境でも SFTP の実サーバ検証を行うために使う。
OpenSSH と異なり posix-rename@openssh.com 拡張を告知しないため、
SftpClientWrapper の「削除 + リネーム」フォールバック経路も併せて検証できる。

使い方: python paramiko_sftpd.py <root> <port> <hostkey>
"""

import os
import socket
import sys
import threading

import paramiko

ROOT = os.path.abspath(sys.argv[1])
PORT = int(sys.argv[2])
HOSTKEY = sys.argv[3]

USERNAME = "testuser"
PASSWORD = "testpass"


class Server(paramiko.ServerInterface):
    def check_auth_password(self, username, password):
        if username == USERNAME and password == PASSWORD:
            return paramiko.AUTH_SUCCESSFUL
        return paramiko.AUTH_FAILED

    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED

    def get_allowed_auths(self, username):
        return "password"


class Handle(paramiko.SFTPHandle):
    def stat(self):
        try:
            return paramiko.SFTPAttributes.from_stat(os.fstat(self.readfile.fileno()))
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)

    def chattr(self, attr):
        return paramiko.SFTP_OK


class SftpServer(paramiko.SFTPServerInterface):
    def _real(self, path):
        return os.path.normpath(os.path.join(ROOT, path.lstrip("/")))

    def list_folder(self, path):
        real = self._real(path)
        try:
            entries = []
            for name in os.listdir(real):
                attr = paramiko.SFTPAttributes.from_stat(os.stat(os.path.join(real, name)))
                attr.filename = name
                entries.append(attr)
            return entries
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)

    def stat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(os.stat(self._real(path)))
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)

    def lstat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(os.lstat(self._real(path)))
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)

    def open(self, path, flags, attr):
        real = self._real(path)
        try:
            flags |= getattr(os, "O_BINARY", 0)
            fd = os.open(real, flags, 0o666)
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)

        if flags & os.O_WRONLY:
            mode = "ab" if (flags & os.O_APPEND) else "wb"
        elif flags & os.O_RDWR:
            mode = "a+b" if (flags & os.O_APPEND) else "r+b"
        else:
            mode = "rb"

        try:
            handle_file = os.fdopen(fd, mode)
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)

        handle = Handle(flags)
        handle.filename = real
        handle.readfile = handle_file
        handle.writefile = handle_file
        return handle

    def remove(self, path):
        try:
            os.remove(self._real(path))
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)
        return paramiko.SFTP_OK

    def rename(self, oldpath, newpath):
        try:
            os.rename(self._real(oldpath), self._real(newpath))
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)
        return paramiko.SFTP_OK

    def mkdir(self, path, attr):
        try:
            os.mkdir(self._real(path))
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)
        return paramiko.SFTP_OK

    def rmdir(self, path):
        try:
            os.rmdir(self._real(path))
        except OSError as e:
            return paramiko.SFTPServer.convert_errno(e.errno)
        return paramiko.SFTP_OK

    def chattr(self, path, attr):
        return paramiko.SFTP_OK


def serve(client):
    try:
        transport = paramiko.Transport(client)
        transport.add_server_key(paramiko.RSAKey(filename=HOSTKEY))
        transport.set_subsystem_handler("sftp", paramiko.SFTPServer, SftpServer)
        transport.start_server(server=Server())
        channel = transport.accept(30)
        if channel is None:
            transport.close()
            return
        while transport.is_active():
            transport.join(1)
    except Exception as exc:  # サーバスレッドの例外でプロセスごと落とさない
        print(f"handler error: {exc}", flush=True)


def main():
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", PORT))
    listener.listen(50)
    print(f"sftpd listening on {PORT} root={ROOT}", flush=True)
    while True:
        conn, _ = listener.accept()
        threading.Thread(target=serve, args=(conn,), daemon=True).start()


if __name__ == "__main__":
    main()
