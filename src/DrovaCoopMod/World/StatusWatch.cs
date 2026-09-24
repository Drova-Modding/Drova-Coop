using Drova_Modding_API;
using Drova_Modding_API.Access;
using DrovaCoopMod.Messages;
using DrovaCoopMod.Session;
using DrovaCoopMod.Sync;
using Il2CppDrova;
using Il2CppDrova.StatusEffects;
using MelonLoader;

namespace DrovaCoopMod.World
{
    /// <summary>
    /// Watches the status effects this machine puts on other players' bodies, and tells them.
    ///
    /// An effect applied to a puppet is applied to nobody. Poison, slows, wounds and the rest are put
    /// on by whatever landed the hit, on the machine that landed it - so hitting a remote player poisons
    /// a body with no character behind it. Its owner feels the damage, because the effect ticks health
    /// off the puppet and <see cref="DamageWatch"/> reports every tick as an ordinary blow, but they get
    /// no icon, no duration and no way to know why they are dying. Naming the effect is what turns that
    /// into something their own game can apply properly.
    ///
    /// <c>AddEffectFromSourceIntern</c> is the funnel and it is the private one on purpose: both public
    /// ways in - one effect and a list of them - end here, so patching this catches applications that
    /// patching either public method would miss.
    ///
    /// Bleeding is deliberately not reported. It is the one effect that does not come from an ability
    /// but from the health change itself, and its severity depends on the damage that actually landed -
    /// which only the victim's machine can know. It crosses as a flag on the damage report instead, so
    /// the victim's own game sizes the wound; reporting it here as well would open a second one.
    /// </summary>
    internal static class StatusWatch
    {
        /// <summary>
        /// Applies the patch. Called once at melon init.
        /// </summary>
        internal static void Initialize(HarmonyLib.Harmony harmony)
        {
            Hooking.TryPostfix(harmony, typeof(StatusEffectHandler), "AddEffectFromSourceIntern",
                typeof(StatusWatch), nameof(AddEffectPostfix));
        }

        /// <summary>
        /// Reports an effect that landed on a remote player's body.
        ///
        /// Runs for every status effect applied to every actor in the game, so the guards are ordered
        /// cheapest first and the common answer costs a session-state comparison.
        /// </summary>
        private static void AddEffectPostfix(StatusEffectHandler __instance, StatusEffect effect, SpawnInfo info)
        {
            try
            {
                // The effect this mod itself just applied, arriving back through the funnel it was always
                // going to arrive back through. Without this the two machines would trade one poison
                // forever, each reading the other's application as a fresh one.
                if (RemoteApply<StatusScope>.InProgress) return;

                if (!CoopSession.IsLive) return;
                if (__instance == null || effect == null) return;

                if (!RemotePlayers.TryFindOwner(__instance._owner, out PlayerId victim)) return;

                // Only what the local player or a creature this machine drives did to them. A puppet
                // applies effects to itself all the time - it drinks the potions its owner drinks and casts
                // the spells its owner casts, because those are replicated - and its owner already has
                // every one of those from their own game. Reporting them would hand the buff back and apply
                // it twice. An effect with no named source is skipped for the same reason: there is nothing
                // to say it came from here.
                //
                // A driven creature is the case that was missing, and it is the same one the damage report
                // was missing: the far copy of that creature never swings, so the poison it puts on a
                // teammate is put on here or nowhere. The victim saw their health fall and never learned
                // why - and with the effect never crossing, they were not actually poisoned at all.
                byte origin = OriginOf(info);
                if (origin == OriginNone) return;

                // The prefab, not the running instance. What is handed to this method is already the
                // authored asset on the ordinary paths, but an effect that arrived by another route can be
                // a live copy, and only the asset carries the guid the far side can resolve.
                StatusEffect prefab = effect.GetPrefabEffect();
                string id = (prefab != null ? prefab : effect).GUID;
                if (string.IsNullOrEmpty(id)) return;

                if (IsBleeding(id)) return;

                CoopSession.ReportStatusEffect(victim, id, origin);
            }
            catch (Exception e)
            {
                // Swallowed rather than allowed out. This runs inside the game's own status effect path,
                // and an exception escaping it would take the effect with it - for every actor in the
                // world, not only for a puppet.
                MelonLogger.Error("[Status] watching a status effect failed: " + e);
            }
        }

        /// <summary>Nothing this machine is entitled to speak for put this effect on.</summary>
        private const byte OriginNone = 0xFF;

        /// <summary>
        /// Who put this effect on, as far as it concerns anybody else.
        ///
        /// Three answers, and the third is the common one: the person playing here, a creature this machine
        /// drives, or nobody worth reporting - the body doing to itself what its owner is already doing, an
        /// NPC both machines are running independently, or an effect with no named source at all.
        /// </summary>
        private static byte OriginOf(SpawnInfo info)
        {
            if (info == null || !info.HasOwner || info.Owner == null) return OriginNone;

            if (PlayerAccess.TryGetPlayer(out Actor player)
                // By pointer, for the usual reason: two proxies over one native object are different
                // managed instances, so the equality operator answers a question about destruction rather
                // than about identity.
                && info.Owner.Pointer == player.Pointer)
            {
                return DamageReportMessage.OriginPlayer;
            }

            // Only the host, and only for what it has claimed. See DamageWatch for why speaking for a
            // creature both machines still run separately would apply everything twice.
            if (CoopSession.State != CoopSessionState.Hosting) return OriginNone;

            Actor? owner = info.Owner.TryCast<Actor>();
            if (owner == null) return OriginNone;

            return EnemyRegistry.TryGetSessionId(owner, out _)
                ? DamageReportMessage.OriginCreature
                : OriginNone;
        }

        /// <summary>
        /// Whether an effect is the bleeding the health change applies by itself, which crosses as a flag
        /// on the damage report rather than as an effect of its own.
        /// </summary>
        private static bool IsBleeding(string effectId)
        {
            GameplaySettingsHandler? settings = ProviderAccess.GetGameplaySettingsHandler();
            if (settings == null) return false;

            StatusEffect bleeding = settings.BleedingStatusEffect;

            return bleeding != null && string.Equals(bleeding.GUID, effectId, StringComparison.Ordinal);
        }
    }
}
