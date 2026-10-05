using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using Il2CppDrova;
using Il2CppDrova.GUI;
using MelonLoader;
using UnityEngine;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// A duel challenge or an arena team invite between two players, asked from the talk menu on the teammate's body
    /// (<see cref="TeammateMenu"/>). The invited player answers in a popup; a yes is carried out by the host, who alone
    /// marks arenas and keeps the teams: a duel is an arena around the two of them, one screen across, with neither on a
    /// team; a team invite puts the invitee in the inviter's team (a new one when the inviter has none).
    /// </summary>
    internal static class Invites
    {
        /// <summary>Invites answered yes on this machine. For tests.</summary>
        internal static int Accepted { get; private set; }

        /// <summary>Sends an invite.</summary>
        internal static void Ask(PlayerId to, byte kind)
        {
            CoopSession.SendInvite(new InviteMessage { Kind = kind, Stage = InviteMessage.StageAsk, To = to });
            Announce(kind == InviteMessage.KindDuel ? $"You challenged {NameOf(to)} to a duel." : $"You invited {NameOf(to)} into your arena team.");
        }

        /// <summary>A step arrived for this player.</summary>
        internal static void Take(in InviteMessage message)
        {
            bool duel = message.Kind == InviteMessage.KindDuel;
            string who = NameOf(message.From);

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
            CoopSession.SendInvite(answer);

            // The host answering for itself carries its own yes out; a client's yes is carried out where the host relays it.
            if (yes && CoopSession.State == CoopSessionState.Hosting)
            {
                answer.From = CoopSession.LocalPlayerId;
                HostCarriesOut(answer);
            }
        }

        /// <summary>Host: does what an accepted invite asks. <paramref name="yes"/>.From said yes to <paramref name="yes"/>.To.</summary>
        internal static void HostCarriesOut(in InviteMessage yes)
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

                // A duel is one against one: neither fights for a team in it.
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

        private static string NameOf(PlayerId player)
        {
            return CoopSession.Players.TryGetValue(player, out string? name) && !string.IsNullOrEmpty(name) ? name : $"player {player.Value}";
        }

        private static void Announce(string text)
        {
            try
            {
                if (GUIGameHandler.TryGet(out GUIGameHandler gui) && gui != null) gui.GetOverlayTextHandler()?.CreateOverlayText(text);
            }
            catch (Exception) { }

            MelonLogger.Msg("[Invite] " + text);
        }
    }
}
