namespace SmurfulationC.World
{
    public enum VegetationType
    {
        None,
        Underbrush,      // dense leaf litter; passable; no yield; hides smurfs
        SmurfberryBush,  // canonical Smurf food; yields Food
        SmallMushroom,   // secondary food; fast regrowth; yields Food
        LargeMushroom,   // structural material; impassable; yields Fungal Wood
        HerbCluster,     // dual-yield; yields Food + MagicEssence
        MagicFlower,     // rare; yields MagicEssence; slow regrowth
        MossPatch,        // passable cosmetic; no yield; future bedding resource
        SmallSandshroom,  // arid mushroom cluster; passable; yields Food (sparse)
        LargeSandshroom,  // large arid mushroom; impassable; yields Fungal Wood
        PalmShroom,       // coastal/island palm-mushroom; impassable; yields Fungal Wood (2/3 of LargeMushroom)
        PineShroom,       // coastal/island pine-mushroom cluster; passable; yields Food (1.5× SmallMushroom)
    }
}
