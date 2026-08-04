using System;
using xpTURN.Klotho.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;


using LiteNetLib;

namespace xpTURN.Klotho.LiteNetLib
{
    /// <summary>
    /// peerId ↔ NetPeer bidirectional mapping.
    /// </summary>
    public class LiteNetLibPeerMap
    {
        IKLogger _logger;
        ConcurrentDictionary<int, LiteNetPeer> _idToPeer = new(Environment.ProcessorCount, 64);
        ConcurrentDictionary<LiteNetPeer, int> _peerToId = new(Environment.ProcessorCount, 64);

        public LiteNetLibPeerMap(IKLogger logger)
        {
            _logger = logger;
        }

        internal int Register(LiteNetPeer peer)
        {
            if (!_idToPeer.TryAdd(peer.Id, peer))
            {
                _logger?.KError($"[LiteNetLibPeerMap] Peer already registered id={peer.Id}");
                return -1;
            }

            if (!_peerToId.TryAdd(peer, peer.Id))
            {
                _idToPeer.TryRemove(peer.Id, out _);
                _logger?.KError($"[LiteNetLibPeerMap] Peer object already registered (id={peer.Id})");
                return -1;
            }

            return peer.Id;
        }

        internal void Unregister(LiteNetPeer peer)
        {
            if (!_idToPeer.TryRemove(peer.Id, out _))
            {
                _logger?.KWarning($"[LiteNetLibPeerMap] Unregister failed: id={peer.Id} not found in _idToPeer");
            }

            if (!_peerToId.TryRemove(peer, out _))
            {
                _logger?.KWarning($"[LiteNetLibPeerMap] Unregister failed: peer object (id={peer.Id}) not found in _peerToId");
            }
        }

        internal bool TryGetPeer(int id, out LiteNetPeer peer)
        {
            return _idToPeer.TryGetValue(id, out peer);
        }

        internal bool TryGetId(LiteNetPeer peer, out int id)
        {
            return _peerToId.TryGetValue(peer, out id);
        }

        internal void Clear()
        {
            _idToPeer.Clear();
            _peerToId.Clear();
        }

        internal IEnumerable<int> GetAllPeerIds() => _idToPeer.Keys;
    }
}