using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace DesktopIconFlick
{
    internal static partial class Tests
    {
        private sealed class FragmentedFrame : MemoryStream
        {
            public FragmentedFrame(byte[] bytes) : base(bytes) { }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
            {
                return base.ReadAsync(buffer, offset, Math.Min(1, count), cancellationToken);
            }
        }

        private static void LanFramesAndConnections()
        {
            var hello = new LanMessage { Kind = LanMessageKind.Hello, Version = LanProtocol.Version, Name = "玩家🙂" };
            using (var bytes = new FragmentedFrame(LanProtocol.Encode(hello)))
            {
                LanMessage parsed = LanProtocol.Read(bytes).GetAwaiter().GetResult();
                Require(parsed.Name == hello.Name && LanProtocol.CheckHello(parsed) == LanError.None, "fragmented UTF-8 LAN frame");
                Require(LanProtocol.Read(bytes).GetAwaiter().GetResult() == null, "clean end of LAN stream");
            }
            using (var oversized = new MemoryStream(new byte[] { 0, 0, 128, 0 }))
            {
                bool rejected = false;
                try { LanProtocol.Read(oversized).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { rejected = true; }
                Require(rejected, "oversized LAN frame rejected before payload");
            }
            using (var malformed = new MemoryStream(new byte[] { 0, 0, 0, 1, 123 }))
            {
                bool rejected = false;
                try { LanProtocol.Read(malformed).GetAwaiter().GetResult(); }
                catch (System.Runtime.Serialization.SerializationException) { rejected = true; }
                Require(rejected, "invalid JSON frame rejected");
            }
            hello.Version++;
            Require(LanProtocol.CheckHello(hello) == LanError.Version, "incompatible LAN protocol version");
            var subnet = new LanAddress(IPAddress.Parse("192.168.3.4"), IPAddress.Parse("255.255.255.0"));
            Require(subnet.Contains(IPAddress.Parse("192.168.3.254")) && !subnet.Contains(IPAddress.Parse("192.168.4.1")),
                "LAN listener restricts peers to selected subnet");

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            try
            {
                using (var client = new TcpClient(AddressFamily.InterNetwork))
                {
                    Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
                    AwaitLan(LanProtocol.Connect(client, endpoint, 2000));
                    var disconnected = new TaskCompletionSource<bool>();
                    using (var peer = new LanPeer(AwaitLan(accepting)))
                    {
                        peer.Start(message => peer.Send(new LanMessage
                        {
                            Kind = LanMessageKind.Error,
                            Error = LanProtocol.CheckHello(message)
                        }, true), () => disconnected.TrySetResult(true));
                        byte[] frame = LanProtocol.Encode(hello);
                        NetworkStream stream = client.GetStream();
                        stream.Write(frame, 0, frame.Length);
                        LanMessage reply = AwaitLan(LanProtocol.Read(stream));
                        Require(reply.Kind == LanMessageKind.Error && reply.Error == LanError.Version, "handshake rejection delivered before socket closure");
                        Require(AwaitLan(LanProtocol.Read(stream)) == null, "peer closes after rejection");
                        AwaitLan(disconnected.Task);
                    }
                }
            }
            finally { listener.Stop(); }
            var reopened = new TcpListener(endpoint);
            try { reopened.Start(); }
            finally { reopened.Stop(); }
        }

        private static void AwaitLan(Task task)
        {
            Require(Task.WhenAny(task, Task.Delay(3000)).GetAwaiter().GetResult() == task, "LAN operation timed out");
            task.GetAwaiter().GetResult();
        }

        private static T AwaitLan<T>(Task<T> task)
        {
            AwaitLan((Task)task);
            return task.GetAwaiter().GetResult();
        }
    }
}
