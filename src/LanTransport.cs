using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopIconFlick
{
    internal sealed class LanAddress
    {
        public readonly IPAddress Address;
        public readonly IPAddress Mask;
        private readonly string adapterName;

        public LanAddress(IPAddress address, IPAddress mask, string adapterName = null)
        {
            Address = address;
            Mask = mask;
            this.adapterName = adapterName;
        }

        public bool Contains(IPAddress remote)
        {
            if (remote.AddressFamily != AddressFamily.InterNetwork)
                return false;
            byte[] host = Address.GetAddressBytes(), mask = Mask.GetAddressBytes(), peer = remote.GetAddressBytes();
            for (int i = 0; i < host.Length; i++)
                if ((host[i] & mask[i]) != (peer[i] & mask[i]))
                    return false;
            return true;
        }

        public static IList<LanAddress> Available()
        {
            var addresses = new List<LanAddress>();
            var secondary = new List<LanAddress>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                IPInterfaceProperties properties = adapter.GetIPProperties();
                IList<LanAddress> destination = properties.GatewayAddresses.Count > 0 ? addresses : secondary;
                foreach (UnicastIPAddressInformation item in properties.UnicastAddresses)
                    if (item.Address.AddressFamily == AddressFamily.InterNetwork && item.IPv4Mask != null &&
                        !IPAddress.IsLoopback(item.Address) && !item.Address.Equals(IPAddress.Any))
                        destination.Add(new LanAddress(item.Address, item.IPv4Mask, adapter.Name));
            }
            addresses.AddRange(secondary);
            return addresses;
        }

        public override string ToString()
        {
            return string.IsNullOrEmpty(adapterName) ? Address.ToString() : Address + " · " + adapterName;
        }
    }

    internal enum LanMessageKind { Hello = 1, Welcome, Error, Snapshot, Ping, Pong, Ready, Score, Forfeit }
    internal enum LanError { None, Version, Name, Full, RoundInProgress, Protocol }

    [DataContract]
    internal sealed partial class LanMessage
    {
        [DataMember] public LanMessageKind Kind { get; set; }
        [DataMember(EmitDefaultValue = false)] public int Version { get; set; }
        [DataMember(EmitDefaultValue = false)] public string Name { get; set; }
        [DataMember(EmitDefaultValue = false)] public LanError Error { get; set; }
    }

    internal static class LanProtocol
    {
        public const int Version = 1;
        public const int DefaultPort = 47618;
        public const int NicknameLimit = 20;
        public const int MaxFrameSize = 16 * 1024;

        public static LanError CheckHello(LanMessage message)
        {
            if (message.Kind != LanMessageKind.Hello)
                return LanError.Protocol;
            if (message.Version != Version)
                return LanError.Version;
            return IsNickname(message.Name) ? LanError.None : LanError.Name;
        }

        public static bool IsNickname(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > NicknameLimit || name != name.Trim())
                return false;
            foreach (char character in name)
                if (char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)
                    return false;
            return true;
        }

        public static async Task Connect(TcpClient client, IPEndPoint endpoint, int timeoutMilliseconds)
        {
            Task connection = client.ConnectAsync(endpoint.Address, endpoint.Port);
            using (var timeout = new CancellationTokenSource())
            {
                Task delay = Task.Delay(timeoutMilliseconds, timeout.Token);
                if (await Task.WhenAny(connection, delay).ConfigureAwait(false) != connection)
                {
                    client.Close();
                    try { await connection.ConfigureAwait(false); }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                    throw new TimeoutException();
                }
                timeout.Cancel();
                await connection.ConfigureAwait(false);
            }
        }

        public static byte[] Encode(LanMessage message)
        {
            using (var buffer = new MemoryStream())
            {
                buffer.Position = 4;
                new DataContractJsonSerializer(typeof(LanMessage)).WriteObject(buffer, message);
                int size = checked((int)buffer.Length - 4);
                if (size < 1 || size > MaxFrameSize)
                    throw new InvalidDataException("消息长度无效。");
                byte[] frame = buffer.ToArray();
                frame[0] = (byte)(size >> 24);
                frame[1] = (byte)(size >> 16);
                frame[2] = (byte)(size >> 8);
                frame[3] = (byte)size;
                return frame;
            }
        }

        public static async Task<LanMessage> Read(Stream stream)
        {
            var header = new byte[4];
            if (!await ReadExactly(stream, header).ConfigureAwait(false))
                return null;
            int size = header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3];
            if (size < 1 || size > MaxFrameSize)
                throw new InvalidDataException("消息长度无效。");
            var body = new byte[size];
            if (!await ReadExactly(stream, body).ConfigureAwait(false))
                throw new EndOfStreamException();
            using (var buffer = new MemoryStream(body, false))
            {
                var message = (LanMessage)new DataContractJsonSerializer(typeof(LanMessage)).ReadObject(buffer);
                if (message == null || !Enum.IsDefined(typeof(LanMessageKind), message.Kind))
                    throw new InvalidDataException("消息类型无效。");
                return message;
            }
        }

        private static async Task<bool> ReadExactly(Stream stream, byte[] buffer)
        {
            int used = 0;
            while (used < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, used, buffer.Length - used).ConfigureAwait(false);
                if (read == 0)
                {
                    if (used == 0)
                        return false;
                    throw new EndOfStreamException();
                }
                used += read;
            }
            return true;
        }
    }

    internal sealed class LanPeer : IDisposable
    {
        private sealed class Outgoing
        {
            public readonly LanMessage Message;
            public readonly bool CloseAfter;

            public Outgoing(LanMessage message, bool closeAfter)
            {
                Message = message;
                CloseAfter = closeAfter;
            }
        }

        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly object gate = new object();
        private readonly Queue<Outgoing> queue = new Queue<Outgoing>();
        private readonly SemaphoreSlim queued = new SemaphoreSlim(0);
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private long lastReceived = Stopwatch.GetTimestamp();
        private bool closed;

        public LanPeer(TcpClient client)
        {
            this.client = client;
            client.NoDelay = true;
            stream = client.GetStream();
        }

        public bool IsSilent(double seconds)
        {
            return (Stopwatch.GetTimestamp() - Interlocked.Read(ref lastReceived)) >= seconds * Stopwatch.Frequency;
        }

        public void Start(Action<LanMessage> received, Action disconnected)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(Read(received), Write()).ConfigureAwait(false);
                }
                finally
                {
                    lock (gate)
                    {
                        queued.Dispose();
                        stop.Dispose();
                    }
                    disconnected();
                }
            });
        }

        private async Task Read(Action<LanMessage> received)
        {
            try
            {
                LanMessage message;
                while ((message = await LanProtocol.Read(stream).ConfigureAwait(false)) != null)
                {
                    Interlocked.Exchange(ref lastReceived, Stopwatch.GetTimestamp());
                    received(message);
                }
            }
            catch (Exception error)
            {
                if (!IsConnectionError(error))
                    throw;
            }
            finally { Dispose(); }
        }

        public void Send(LanMessage message, bool closeAfter = false)
        {
            lock (gate)
            {
                if (closed)
                    return;
                if (queue.Count < 64)
                {
                    queue.Enqueue(new Outgoing(message, closeAfter));
                    queued.Release();
                    return;
                }
            }
            Dispose();
        }

        private async Task Write()
        {
            try
            {
                while (true)
                {
                    await queued.WaitAsync(stop.Token).ConfigureAwait(false);
                    Outgoing item;
                    lock (gate)
                        item = queue.Dequeue();
                    byte[] frame = LanProtocol.Encode(item.Message);
                    await stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                    if (item.CloseAfter)
                        return;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (!IsConnectionError(error))
                    throw;
            }
            finally { Dispose(); }
        }

        private static bool IsConnectionError(Exception error)
        {
            return error is IOException || error is InvalidDataException || error is SocketException || error is ObjectDisposedException ||
                error is SerializationException || error is System.Xml.XmlException;
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (closed)
                    return;
                closed = true;
                stop.Cancel();
                client.Close();
            }
        }
    }
}
