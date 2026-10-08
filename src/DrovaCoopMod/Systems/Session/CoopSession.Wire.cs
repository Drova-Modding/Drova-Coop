using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using MelonLoader;

namespace DrovaCoopMod.Systems.Session
{
    // The routing helpers systems send and queue their messages through.
    public static partial class CoopSession
    {
        /// <summary>Records the matching unregister for <see cref="Shutdown"/>.</summary>
        internal static void Register<T>(ushort id, Action<INetPeer, T> handler) where T : struct, INetMessage
        {
            NetworkAccess.RegisterWithId(id, handler);
            Unregisters.Add(NetworkAccess.Unregister<T>);
        }

        /// <summary>The host queues a client's message and relays it to everyone but the sender. A client queues only the host's.</summary>
        internal static void RelayAndQueue<T>(INetPeer peer, T message, Queue<T> queue, int cap, string what)
            where T : struct, INetMessage
        {
            if (State == CoopSessionState.Hosting)
            {
                Enqueue(queue, message, cap, what);
                SendToClientsExcept(message, peer.Id);
                return;
            }

            QueueFromHost(message, queue, cap, what);
        }

        /// <summary>A message only the host may send.</summary>
        internal static void QueueFromHost<T>(T message, Queue<T> queue, int cap, string what) where T : struct
        {
            if (!IsFromHost()) return;

            Enqueue(queue, message, cap, what);
        }

        /// <summary>Queues something nothing will repeat. Full, it refuses the newest.</summary>
        /// <returns>Whether the message was queued.</returns>
        internal static bool Enqueue<T>(Queue<T> queue, in T message, int cap, string? what = null) where T : struct
        {
            if (queue.Count >= cap)
            {
                if (what != null) MelonLogger.Warning($"[Coop] more than {cap} {what} are waiting; the newest was refused.");

                return false;
            }

            queue.Enqueue(message);
            return true;
        }

        /// <summary>Queues something the next message supersedes. Full, it drops the oldest.</summary>
        internal static void EnqueueLatest<T>(Queue<T> queue, in T message, int cap) where T : struct
        {
            if (queue.Count >= cap)
            {
                queue.Dequeue();
            }

            queue.Enqueue(message);
        }

        internal static void SendIfLive<T>(in T message) where T : struct, INetMessage
        {
            if (IsLive) NetworkAccess.SendToAll(message);
        }

        internal static void SendIfHost<T>(in T message) where T : struct, INetMessage
        {
            if (State == CoopSessionState.Hosting) NetworkAccess.SendToAll(message);
        }

        /// <summary>Host only: who is behind a connection. The sender of a message is the connection, never what it says.</summary>
        internal static bool TryGetSender(INetPeer peer, out PlayerId player) => PeerToPlayer.TryGetValue(peer.Id, out player);

        // In a relay session a client can receive packets from other clients too.
        internal static bool IsFromHost()
        {
            return NetworkAccess.Role == NetRole.Client && State == CoopSessionState.Joined;
        }

        // Host only: the peer-to-player table is the host's alone.
        internal static bool TrySendToPlayer<T>(T message, PlayerId player, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            int peerId = -1;
            foreach (KeyValuePair<int, PlayerId> mapping in PeerToPlayer)
            {
                if (mapping.Value != player) continue;

                peerId = mapping.Key;
                break;
            }

            if (peerId < 0) return false;

            PeerBuffer.Clear();
            NetworkAccess.GetPeers(PeerBuffer);

            foreach (INetPeer candidate in PeerBuffer)
            {
                if (candidate.Id != peerId) continue;

                NetworkAccess.Send(candidate, message, delivery);
                return true;
            }

            return false;
        }

        // The API's relay broadcast has no exclusion list, so the host unicasts.
        internal static void SendToClientsExcept<T>(T message, int exceptPeerId, Delivery delivery = Delivery.Reliable)
            where T : struct, INetMessage
        {
            PeerBuffer.Clear();
            NetworkAccess.GetPeers(PeerBuffer);

            foreach (INetPeer other in PeerBuffer)
            {
                if (other.Id == exceptPeerId) continue;

                NetworkAccess.Send(other, message, delivery);
            }
        }
    }
}
