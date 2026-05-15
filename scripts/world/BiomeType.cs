namespace SmurfulationC.World
{
    public enum BiomeType
    {
        Pondsea,    // world-level water body (sea or inland pond); impassable; no colony landing
        Coast,
        Island,     // land tile surrounded by Pondsea on all cardinal sides; always IsCoastal
        Desert,
        Plains,
        Swamp,
        Forest,
        Hills,
        Mountains,
        Peaks,
        MagicGrove,
    }
}
