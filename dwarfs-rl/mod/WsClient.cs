using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DwarfsMod
{
    // small websocket client (rfc 6455) cause nothing exists for .NET 3.5 that
    // does this. only covers what the bridge needs, text frames, ping/pong and
    // a clean close. the python side runs the standard websockets server so the
    // handshake and framing have to be done by the book
    public class WsClient
    {
        TcpClient tcp;
        NetworkStream stream;
        readonly Random rng = new Random();
        readonly object sendLock = new object();

        public bool Connected
        {
            get { return tcp != null && tcp.Connected; }
        }

        public bool Connect(string host, int port, string path)
        {
            try
            {
                tcp = new TcpClient();
                tcp.Connect(host, port);
                tcp.NoDelay = true; // lockstep traffic so latency beats packing
                stream = tcp.GetStream();

                byte[] keyBytes = new byte[16];
                rng.NextBytes(keyBytes);
                string key = Convert.ToBase64String(keyBytes);

                var req = new StringBuilder();
                req.Append("GET ").Append(path).Append(" HTTP/1.1\r\n");
                req.Append("Host: ").Append(host).Append(':').Append(port).Append("\r\n");
                req.Append("Upgrade: websocket\r\n");
                req.Append("Connection: Upgrade\r\n");
                req.Append("Sec-WebSocket-Key: ").Append(key).Append("\r\n");
                req.Append("Sec-WebSocket-Version: 13\r\n");
                req.Append("\r\n");
                byte[] reqBytes = Encoding.ASCII.GetBytes(req.ToString());
                stream.Write(reqBytes, 0, reqBytes.Length);

                string response = ReadHandshakeResponse();
                if (response == null || response.IndexOf(" 101 ") < 0)
                {
                    Drop();
                    return false;
                }

                // the server proves it speaks websocket by hashing our key
                string expected;
                using (var sha1 = SHA1.Create())
                {
                    byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(
                        key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
                    expected = Convert.ToBase64String(hash);
                }
                if (response.IndexOf(expected, StringComparison.Ordinal) < 0)
                {
                    Drop();
                    return false;
                }
                return true;
            }
            catch
            {
                Drop();
                return false;
            }
        }

        string ReadHandshakeResponse()
        {
            var sb = new StringBuilder();
            int b;
            // headers end at the first blank line
            while ((b = stream.ReadByte()) != -1)
            {
                sb.Append((char)b);
                if (sb.Length > 4 &&
                    sb[sb.Length - 4] == '\r' && sb[sb.Length - 3] == '\n' &&
                    sb[sb.Length - 2] == '\r' && sb[sb.Length - 1] == '\n')
                    return sb.ToString();
                if (sb.Length > 16384) break; // not a websocket server
            }
            return null;
        }

        public void SendText(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            lock (sendLock)
            {
                SendFrame(0x1, payload);
            }
        }

        // client frames must be masked per the spec
        void SendFrame(int opcode, byte[] payload)
        {
            var ms = new MemoryStream();
            ms.WriteByte((byte)(0x80 | opcode)); // FIN + opcode

            if (payload.Length < 126)
            {
                ms.WriteByte((byte)(0x80 | payload.Length));
            }
            else if (payload.Length <= 0xFFFF)
            {
                ms.WriteByte(0x80 | 126);
                ms.WriteByte((byte)(payload.Length >> 8));
                ms.WriteByte((byte)(payload.Length & 0xFF));
            }
            else
            {
                ms.WriteByte(0x80 | 127);
                long len = payload.Length;
                for (int i = 7; i >= 0; i--)
                    ms.WriteByte((byte)((len >> (8 * i)) & 0xFF));
            }

            byte[] mask = new byte[4];
            rng.NextBytes(mask);
            ms.Write(mask, 0, 4);

            byte[] masked = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++)
                masked[i] = (byte)(payload[i] ^ mask[i & 3]);
            ms.Write(masked, 0, masked.Length);

            byte[] frame = ms.ToArray();
            stream.Write(frame, 0, frame.Length);
            stream.Flush();
        }

        // blocks til a full text message shows up, returns null when the
        // connection is gone. pings get answered inline so callers never see them
        public string ReceiveText()
        {
            try
            {
                var message = new MemoryStream();
                while (true)
                {
                    int b0 = stream.ReadByte();
                    if (b0 == -1) return null;
                    int b1 = stream.ReadByte();
                    if (b1 == -1) return null;

                    bool fin = (b0 & 0x80) != 0;
                    int opcode = b0 & 0x0F;
                    bool masked = (b1 & 0x80) != 0;
                    long len = b1 & 0x7F;

                    if (len == 126)
                    {
                        len = (ReadByteStrict() << 8) | ReadByteStrict();
                    }
                    else if (len == 127)
                    {
                        len = 0;
                        for (int i = 0; i < 8; i++)
                            len = (len << 8) | (uint)ReadByteStrict();
                    }

                    byte[] mask = null;
                    if (masked) // servers shouldnt mask but whatever, handle it
                    {
                        mask = new byte[4];
                        ReadExact(mask, 4);
                    }

                    byte[] payload = new byte[len];
                    ReadExact(payload, (int)len);
                    if (mask != null)
                        for (int i = 0; i < payload.Length; i++)
                            payload[i] ^= mask[i & 3];

                    if (opcode == 0x8) // close, ack it and bail
                    {
                        lock (sendLock) { SendFrame(0x8, new byte[0]); }
                        Drop();
                        return null;
                    }
                    if (opcode == 0x9) // ping
                    {
                        lock (sendLock) { SendFrame(0xA, payload); }
                        continue;
                    }
                    if (opcode == 0xA) // pong, nobody asked
                        continue;

                    message.Write(payload, 0, payload.Length);
                    if (fin)
                        return Encoding.UTF8.GetString(message.ToArray());
                    // otherwise continuation frames follow, keep collecting
                }
            }
            catch
            {
                Drop();
                return null;
            }
        }

        int ReadByteStrict()
        {
            int b = stream.ReadByte();
            if (b == -1) throw new IOException("socket closed mid-frame");
            return b;
        }

        void ReadExact(byte[] buffer, int count)
        {
            int off = 0;
            while (off < count)
            {
                int n = stream.Read(buffer, off, count - off);
                if (n <= 0) throw new IOException("socket closed mid-frame");
                off += n;
            }
        }

        public void Drop()
        {
            try { if (stream != null) stream.Close(); } catch { }
            try { if (tcp != null) tcp.Close(); } catch { }
            stream = null;
            tcp = null;
        }
    }
}
