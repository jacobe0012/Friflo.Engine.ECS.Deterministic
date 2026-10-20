using System;
using System.Collections.Generic;
using xpTURN.Klotho.Network;

namespace xpTURN.Klotho.Core
{
    /// <summary>Keeps the short receive window between handshake completion and the service's first update.</summary>
    internal sealed class ConnectionReceiveBuffer : IDisposable
    {
        private const int MaxPackets = 1024;
        private const int MaxBytes = 4 * 1024 * 1024;

        private readonly INetworkTransport _transport;
        private readonly Queue<Packet> _packets = new Queue<Packet>();
        private int _bytes;
        private bool _disposed;
        private Exception _failure;

        private readonly struct Packet
        {
            internal readonly int PeerId;
            internal readonly byte[] Data;

            internal Packet(int peerId, byte[] data)
            {
                PeerId = peerId;
                Data = data;
            }
        }

        internal ConnectionReceiveBuffer(INetworkTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _transport.OnDataReceived += Capture;
            _transport.OnDisconnected += Disconnected;
        }

        private void Capture(int peerId, byte[] data, int length)
        {
            if (_disposed) return;
            if (data == null || length < 0 || length > data.Length)
            {
                Fail(new InvalidOperationException("invalid transport payload during connection handoff"));
                return;
            }
            if (_packets.Count >= MaxPackets || length > MaxBytes - _bytes)
            {
                Fail(new InvalidOperationException("connection handoff exceeded its pending-message limit"));
                return;
            }

            // LiteNetLib and the loopback transports can reuse this buffer as soon as the callback returns.
            var copy = new byte[length];
            Array.Copy(data, copy, length);
            _packets.Enqueue(new Packet(peerId, copy));
            _bytes += length;
        }

        private void Disconnected(DisconnectReason reason)
        {
            Fail(new InvalidOperationException($"transport disconnected during connection handoff: {reason}"));
        }

        private void Fail(Exception error)
        {
            if (_failure == null) _failure = error;
            Dispose();
        }

        internal void ThrowIfFailed()
        {
            if (_failure != null)
                throw new InvalidOperationException("connection receive handoff failed", _failure);
        }

        internal void Drain(Action<int, byte[], int> receive)
        {
            ThrowIfFailed();
            try
            {
                // Keep capturing while replaying. A callback that pumps the transport must append new
                // packets behind the old ones rather than dispatch them out of order through the service.
                while (!_disposed && _packets.Count > 0)
                {
                    var packet = _packets.Dequeue();
                    _bytes -= packet.Data.Length;
                    receive(packet.PeerId, packet.Data, packet.Data.Length);
                    ThrowIfFailed();
                }
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transport.OnDataReceived -= Capture;
            _transport.OnDisconnected -= Disconnected;
            _packets.Clear();
            _bytes = 0;
        }
    }
}
