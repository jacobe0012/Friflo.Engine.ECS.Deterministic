using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using xpTURN.Klotho.LiteNetLib;

// ReSharper disable once CheckNamespace
namespace Klotho.Friflo.Tests;

/// <summary>Session restarts must release the old UDP listener, not abandon its socket and threads.</summary>
[NonParallelizable]
public static class Test_TransportLifecycle
{
    [Test]
    public static void RepeatedListen_ReusesThePortAndDisconnectReleasesIt()
    {
        int port = UnusedPort();
        var transport = new LiteNetLibTransport(null);
        try {
            Assert.IsTrue(transport.Listen("127.0.0.1", port, 2), "the initial listener did not bind");
            Assert.IsTrue(transport.Listen("127.0.0.1", port, 2),
                "starting a second session abandoned the previous listener on the same port");
            transport.Disconnect();
            Assert.IsTrue(transport.Listen("127.0.0.1", port, 2),
                "a disconnected transport could not be reused");
        }
        finally {
            transport.Disconnect();
        }
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Assert.DoesNotThrow(() => socket.Bind(new IPEndPoint(IPAddress.Any, port)),
            "Disconnect left the UDP port owned by an unreachable manager");
    }

    [Test]
    public static void ConcurrentDisconnect_IsIdempotentAndReleasesThePort()
    {
        for (int iteration = 0; iteration < 8; iteration++) {
            int port = UnusedPort();
            var transport = new LiteNetLibTransport(null);
            try {
                Assert.IsTrue(transport.Listen("127.0.0.1", port, 2));
                using var ready = new Barrier(3);
                Action disconnect = () => {
                    if (!ready.SignalAndWait(TimeSpan.FromSeconds(5))) {
                        throw new TimeoutException("concurrent disconnect did not start");
                    }
                    transport.Disconnect();
                };
                var first = Task.Run(disconnect);
                var second = Task.Run(disconnect);
                Assert.IsTrue(ready.SignalAndWait(TimeSpan.FromSeconds(5)));
                Assert.IsTrue(Task.WaitAll(new[] { first, second }, TimeSpan.FromSeconds(5)),
                    "concurrent shutdown failed to join the transport threads");
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                Assert.DoesNotThrow(() => socket.Bind(new IPEndPoint(IPAddress.Any, port)),
                    "concurrent shutdown left the listening socket alive");
            }
            finally {
                transport.Disconnect();
            }
        }
    }

    [Test]
    public static void ConnectedClient_RepeatedDisconnectNotifiesOnce()
    {
        int port = UnusedPort();
        var server = new LiteNetLibTransport(null);
        var client = new LiteNetLibTransport(null);
        int notifications = 0;
        client.OnDisconnected += _ => Interlocked.Increment(ref notifications);
        try {
            Assert.IsTrue(server.Listen("127.0.0.1", port, 2));
            Assert.IsTrue(client.Connect("127.0.0.1", port));
            PumpConnected(server, client);

            client.Disconnect();
            client.Disconnect();
            client.PollEvents();

            Assert.IsFalse(client.IsConnected);
            Assert.AreEqual(1, notifications, "one local shutdown emitted duplicate disconnect notifications");
        }
        finally {
            client.Disconnect();
            server.Disconnect();
        }
    }

    [Test]
    public static void RepeatedConnect_ReplacesThePreviousConnection()
    {
        int port = UnusedPort();
        var server = new LiteNetLibTransport(null);
        var client = new LiteNetLibTransport(null);
        int connections = 0;
        client.OnConnected += () => connections++;
        try {
            Assert.IsTrue(server.Listen("127.0.0.1", port, 2));
            Assert.IsTrue(client.Connect("127.0.0.1", port));
            PumpConnected(server, client);
            Assert.AreEqual(1, connections);

            Assert.IsTrue(client.Connect("127.0.0.1", port));
            PumpConnected(server, client);

            Assert.AreEqual(2, connections, "the replacement manager did not register a fresh peer");
            Assert.IsTrue(client.IsConnected);
        }
        finally {
            client.Disconnect();
            server.Disconnect();
        }
    }

    private static void PumpConnected(LiteNetLibTransport server, LiteNetLibTransport client)
    {
        var elapsed = Stopwatch.StartNew();
        while (!client.IsConnected && elapsed.ElapsedMilliseconds < 5000) {
            server.PollEvents();
            client.PollEvents();
            Thread.Sleep(1);
        }
        Assert.IsTrue(client.IsConnected, "local UDP connection did not complete within five seconds");
    }

    private static int UnusedPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint).Port;
    }
}
