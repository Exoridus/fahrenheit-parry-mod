namespace Fahrenheit.Mods.Parry;

public static partial class ExternalMemoryOffsetMap
{
    public static class ActorArray
    {
        // Actor array root data used for world actor ids/coords in many tooling projects.
        public const int ActorArraySize = 0x01FC44E0;
        public const int ActorArrayPointer = 0x01FC44E4;
        public const int ActorStride = 0x0880;

        // Per-actor offsets from actor base + ActorStride * index.
        public const int OffsetActorId = 0x0000;
        public const int OffsetPosX = 0x000C;
        public const int OffsetPosZ = 0x0010;
        public const int OffsetPosY = 0x0014;
    }
}
