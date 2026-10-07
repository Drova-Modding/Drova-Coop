using DrovaCoopMod.Messages;

namespace DrovaCoopMod.Session
{
    /// <summary>A creature claim, release or drive change from the host, queued together to keep their order.</summary>
    internal readonly struct CreatureOrder
    {
        internal const byte KindBind = 0;
        internal const byte KindRelease = 1;
        internal const byte KindDrive = 2;

        internal CreatureOrder(EnemyBindMessage bind)
        {
            Kind = KindBind;
            Bind = bind;
        }

        internal CreatureOrder(EnemyReleaseMessage release)
        {
            Kind = KindRelease;
            Release = release;
        }

        internal CreatureOrder(EnemyDriveMessage drive)
        {
            Kind = KindDrive;
            Drive = drive;
        }

        internal byte Kind { get; }

        internal EnemyBindMessage Bind { get; }

        internal EnemyReleaseMessage Release { get; }

        internal EnemyDriveMessage Drive { get; }
    }
}
