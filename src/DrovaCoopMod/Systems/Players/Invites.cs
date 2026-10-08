using Drova_Modding_API.Access;
using Drova_Modding_API.Systems.Networking;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Systems.GameArena;
using DrovaCoopMod.Systems.Session;
using Il2CppDrova;
using Il2CppDrova.GUI;
using DrovaCoopMod.UI;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.Systems.Players
{
    /// <summary>
    /// A duel challenge or arena team invite from <see cref="TeammateMenu"/>, answered in a popup.
    /// The host carries out a yes, since it alone marks arenas and keeps teams.
    /// </summary>
    internal sealed class Invites : ICoopSystem
    {
        /// <summary>Invites answered yes on this machine. For tests.</summary>
        internal static int Accepted { get; private set; }

        /// <summary>Sends an invite.</summary>
        internal static void Ask(PlayerId to, byte kind)
        {
            SendInvite(new InviteMessage { Kind = kind, Stage = InviteMessage.StageAsk, To = to });
            Announce(kind == InviteMessage.KindDuel ? $"You challenged {CoopSession.NameOf(to)} to a duel." : $"You invited {CoopSession.NameOf(to)} into your arena team.");
        }

        /// <summary>A step arrived for this player.</summary>
        private static void Take(in InviteMessage message)
        {
            bool duel = message.Kind == InviteMessage.KindDuel;
            string who = CoopSession.NameOf(message.From);

            switch (message.Stage)
            {
                case InviteMessage.StageAsk:
                    InviteMessage asked = message;
                    string text = duel ? $"{who} challenges you to a duel. Fight?" : $"{who} invites you into their arena team. Join?";
                    if (!GUIGameHandler.TryGet(out GUIGameHandler gui) || gui == null)
                    {
                        Answer(asked, false);
                        return;
                    }

                    GUI_IConfirmPopUp popup = gui.OpenGenericConfirmPopUp();
                    popup.SetDescText(text);
                    popup.SetYesText(duel ? "Fight" : "Join");
                    popup.SetNoText("Decline");
                    popup.AddCallback(new Action<ConfirmPopUpResultArgs>(result => Answer(asked, result.Result == ConfirmPopUpResult.Yes)));
                    break;

                case InviteMessage.StageYes:
                    Announce(duel ? $"{who} accepts the duel!" : $"{who} joins your arena team.");
                    break;

                case InviteMessage.StageNo:
                    Announce(duel ? $"{who} declines the duel." : $"{who} declines your invite.");
                    break;
            }
        }

        private static void Answer(InviteMessage asked, bool yes)
        {
            if (yes) Accepted++;

            InviteMessage answer = new() { Kind = asked.Kind, Stage = yes ? InviteMessage.StageYes : InviteMessage.StageNo, To = asked.From };
            SendInvite(answer);

            // A client's yes is carried out where the host relays it.
            if (yes && CoopSession.State == CoopSessionState.Hosting)
            {
                HostCarriesOut(answer with { From = CoopSession.LocalPlayerId });
            }
        }

        /// <summary>Host: does what an accepted invite asks. <paramref name="yes"/>.From said yes to <paramref name="yes"/>.To.</summary>
        private static void HostCarriesOut(in InviteMessage yes)
        {
            if (CoopSession.State != CoopSessionState.Hosting) return;

            try
            {
                if (yes.Kind == InviteMessage.KindTeam)
                {
                    Arena.JoinTeamOf(yes.From, yes.To);
                    return;
                }

                // One arena at a time: a duel marked over a running one would move it and wipe its score.
                if (Arena.Active)
                {
                    MelonLogger.Msg("[Invite] a duel was accepted while an arena stands; fight in that one.");
                    return;
                }

                if (!TryPosition(yes.From, out Vector2 one) || !TryPosition(yes.To, out Vector2 other)) return;

                Arena.SetTeam(yes.From, 0);
                Arena.SetTeam(yes.To, 0);
                MelonLogger.Msg("[Invite] " + Arena.MarkAt((one + other) / 2f, 0f));
            }
            catch (Exception e)
            {
                MelonLogger.Error("[Invite] carrying out an accepted invite failed: " + e);
            }
        }

        private static bool TryPosition(PlayerId player, out Vector2 position)
        {
            position = Vector2.zero;

            if (player == CoopSession.LocalPlayerId)
            {
                if (!PlayerAccess.TryGetPlayer(out Actor self)) return false;
                position = self.transform.position;
                return true;
            }

            if (!RemotePlayers.Bodies.TryGetValue(player, out RemotePlayer? body) || body?.Body == null) return false;
            position = body.Body.transform.position;
            return true;
        }


        private static void Announce(string text)
        {
            Overlay.Show(text);

            MelonLogger.Msg("[Invite] " + text);
        }

        void ICoopSystem.RegisterMessages()
        {
            CoopSession.Register<InviteMessage>(MessageIds.Invite, OnInvite);
        }

        /// <summary>An invite step for one player, routed like a trade.</summary>
        private static void SendInvite(InviteMessage message)
        {
            message.From = CoopSession.LocalPlayerId;
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                if (message.To == CoopSession.LocalPlayerId) Take(message);
                else CoopSession.TrySendToPlayer(message, message.To);
            }
            else if (CoopSession.IsLive)
            {
                NetworkAccess.SendToAll(message);
            }
        }

        private static void OnInvite(INetPeer peer, InviteMessage message)
        {
            if (CoopSession.State == CoopSessionState.Hosting)
            {
                if (!CoopSession.TryGetSender(peer, out PlayerId sender) || message.To == sender) return;
                message.From = sender;

                if (message.To == CoopSession.LocalPlayerId) Take(message);
                else CoopSession.TrySendToPlayer(message, message.To);

                // A yes starts something only the host can do, wherever the two players are.
                if (message.Stage == InviteMessage.StageYes) HostCarriesOut(message);
                return;
            }

            if (CoopSession.IsFromHost() && message.To == CoopSession.LocalPlayerId) Take(message);
        }
    }
}
