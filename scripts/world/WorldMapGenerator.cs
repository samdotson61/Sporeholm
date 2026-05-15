using System;
using System.Collections.Generic;
using Godot;

namespace SmurfulationC.World
{
    // Generates a sparse world map from four independent noise layers.
    // Grid size is variable (8–128); bias params nudge each noise channel ±35%.
    public static class WorldMapGenerator
    {
        public const int DefaultGridSize = 32;
        public const int MaxGridSize     = 128;

        public static WorldTile[,] Generate(int seed, int gridSize = DefaultGridSize,
            float elevBias = 0f, float rainBias = 0f, float tempBias = 0f, float magicBias = 0f)
        {
            gridSize = Mathf.Clamp(gridSize, 8, MaxGridSize);
            var tiles = new WorldTile[gridSize, gridSize];

            var elevNoise  = MakeNoise(seed,     0.06f);
            var rainNoise  = MakeNoise(seed + 1, 0.05f);
            var tempNoise  = MakeNoise(seed + 2, 0.04f);
            var magicNoise = MakeNoise(seed + 3, 0.08f);

            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    float lat = 1f - Mathf.Abs((float)y / gridSize - 0.5f) * 2f;

                    float elev  = Mathf.Clamp(Normalize(elevNoise.GetNoise2D(x, y))  + elevBias  * 0.35f, 0f, 1f);
                    float rain  = Mathf.Clamp(Normalize(rainNoise.GetNoise2D(x, y))  + rainBias  * 0.35f, 0f, 1f);
                    float temp  = Mathf.Clamp(lat * 0.7f + Normalize(tempNoise.GetNoise2D(x, y)) * 0.3f + tempBias * 0.35f, 0f, 1f);
                    float magic = Mathf.Clamp(Normalize(magicNoise.GetNoise2D(x, y)) + magicBias * 0.35f, 0f, 1f);

                    var biome = ScoreBiome(elev, rain, temp, magic);

                    tiles[x, y] = new WorldTile
                    {
                        Elevation    = elev,
                        Rainfall     = rain,
                        Temperature  = temp,
                        MagicDensity = magic,
                        Biome        = biome,
                        LocalSeed    = seed ^ (x * 1000 + y * 37 + 13),
                        Passable     = biome != BiomeType.Pondsea && biome != BiomeType.Peaks,
                    };
                }
            }

            // ── Post-pass A: Two-tier Pondsea generation ─────────────────────────
            // Mega-clusters (20–80 tiles) are the primary water bodies; count scales
            // sharply with rainfall so dry worlds have none and wet worlds have 1–2.
            // Satellite clusters (2–8 tiles) add small inland ponds.
            // Post-pass A.3 then bridges any two significant clusters (≥6 combined
            // tiles) that lie within a 30×30-tile window into one connected body.
            // Post-pass A.5 seeds 0–6 island tiles within existing Pondsea.

            float avgRain = 0f;
            for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                    avgRain += tiles[x, y].Rainfall;
            avgRain /= gridSize * gridSize;

            var postRng = new Random(seed ^ 0x7F4C2A);
            int[] dx4 = { -1, 1,  0, 0 };
            int[] dy4 = {  0, 0, -1, 1 };

            // Mega-cluster count: none at low rain, 1 at moderate, 2 only at max.
            int megaCount;
            if      (avgRain < 0.22f)  megaCount = 0;
            else if (avgRain < 0.38f)  megaCount = postRng.Next(0, 2);  // 0 or 1
            else if (rainBias >= 0.9f) megaCount = 2;
            else                       megaCount = 1;

            int megaTileCap  = rainBias >= 0.9f ? 80 : 40;
            int megaTargetMin = megaTileCap / 2;

            // Satellite cluster count: 0 at very dry, up to 3 when wet.
            int satCount = avgRain < 0.30f ? 0 : Math.Min((int)(avgRain * 3.5f), 3);

            // ── Local BFS grower ─────────────────────────────────────────────────
            void GrowCluster(int gsx, int gsy, int gtarget)
            {
                var gq = new Queue<(int x, int y)>();
                gq.Enqueue((gsx, gsy));
                int gPlaced = 0;
                while (gq.Count > 0 && gPlaced < gtarget)
                {
                    var (gcx, gcy) = gq.Dequeue();
                    if (tiles[gcx, gcy].Biome == BiomeType.Pondsea) continue;
                    var gcb = tiles[gcx, gcy].Biome;
                    if (gcb == BiomeType.Mountains || gcb == BiomeType.Peaks) continue;
                    var gwt = tiles[gcx, gcy];
                    gwt.Biome    = BiomeType.Pondsea;
                    gwt.Passable = false;
                    tiles[gcx, gcy] = gwt;
                    gPlaced++;
                    int[] gOrd = { 0, 1, 2, 3 };
                    for (int gi = 3; gi > 0; gi--)
                    {
                        int gj = postRng.Next(gi + 1);
                        (gOrd[gi], gOrd[gj]) = (gOrd[gj], gOrd[gi]);
                    }
                    foreach (int gd in gOrd)
                    {
                        int gnx = gcx + dx4[gd], gny = gcy + dy4[gd];
                        if (gnx < 1 || gnx >= gridSize - 1 || gny < 1 || gny >= gridSize - 1) continue;
                        var gnb = tiles[gnx, gny].Biome;
                        if (gnb == BiomeType.Pondsea || gnb == BiomeType.Mountains || gnb == BiomeType.Peaks) continue;
                        gq.Enqueue((gnx, gny));
                    }
                }
            }

            bool FindSeed(int margin, out int fsx, out int fsy)
            {
                fsx = fsy = 0;
                for (int fa = 0; fa < 30; fa++)
                {
                    int ftx = postRng.Next(margin, gridSize - margin);
                    int fty = postRng.Next(margin, gridSize - margin);
                    var ftb = tiles[ftx, fty].Biome;
                    if (ftb != BiomeType.Mountains && ftb != BiomeType.Peaks && ftb != BiomeType.Pondsea)
                    { fsx = ftx; fsy = fty; return true; }
                }
                return false;
            }

            for (int mc = 0; mc < megaCount; mc++)
            {
                if (!FindSeed(3, out int msx, out int msy)) continue;
                GrowCluster(msx, msy, postRng.Next(megaTargetMin, megaTileCap + 1));
            }

            for (int sc = 0; sc < satCount; sc++)
            {
                if (!FindSeed(2, out int ssx, out int ssy)) continue;
                GrowCluster(ssx, ssy, postRng.Next(2, 9));
            }

            // ── Post-pass A.3: Merge nearby Pondsea clusters ─────────────────────
            // Flood-fill labels every connected Pondsea component.
            // In each overlapping 30×30 window, any two components with ≥3 tiles
            // each (≥6 combined) are bridged by a rectilinear carve between their
            // closest tile pair, merging them into one connected body.

            var lbl  = new int[gridSize, gridSize];
            int nLbl = 1;
            var compMap = new Dictionary<int, List<(int x, int y)>>();

            for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                {
                    if (tiles[x, y].Biome != BiomeType.Pondsea || lbl[x, y] != 0) continue;
                    var bq   = new Queue<(int, int)>();
                    var comp = new List<(int x, int y)>();
                    bq.Enqueue((x, y));
                    lbl[x, y] = nLbl;
                    while (bq.Count > 0)
                    {
                        var (lcx, lcy) = bq.Dequeue();
                        comp.Add((lcx, lcy));
                        for (int d = 0; d < 4; d++)
                        {
                            int lnx = lcx + dx4[d], lny = lcy + dy4[d];
                            if (lnx < 0 || lnx >= gridSize || lny < 0 || lny >= gridSize) continue;
                            if (tiles[lnx, lny].Biome == BiomeType.Pondsea && lbl[lnx, lny] == 0)
                            { lbl[lnx, lny] = nLbl; bq.Enqueue((lnx, lny)); }
                        }
                    }
                    compMap[nLbl] = comp;
                    nLbl++;
                }

            int win = Math.Min(30, gridSize);
            var bridgedPairs = new HashSet<(int, int)>();

            for (int wy = 0; wy < gridSize; wy += win / 2)
                for (int wx = 0; wx < gridSize; wx += win / 2)
                {
                    var inWin = new Dictionary<int, int>();
                    for (int iy = wy; iy < Math.Min(wy + win, gridSize); iy++)
                        for (int ix = wx; ix < Math.Min(wx + win, gridSize); ix++)
                        {
                            int lb = lbl[ix, iy];
                            if (lb == 0) continue;
                            inWin.TryGetValue(lb, out int ic); inWin[lb] = ic + 1;
                        }

                    var sigIds = new List<int>();
                    foreach (var (wid, wcnt) in inWin)
                        if (wcnt >= 3 && compMap.ContainsKey(wid)) sigIds.Add(wid);
                    if (sigIds.Count < 2) continue;

                    int idA = sigIds[0], idB = sigIds[1];
                    var bpKey = idA < idB ? (idA, idB) : (idB, idA);
                    if (bridgedPairs.Contains(bpKey)) continue;
                    bridgedPairs.Add(bpKey);
                    if (compMap[idA].Count + compMap[idB].Count > megaTileCap * 2) continue;

                    // Find closest tile pair (sample large clusters for speed).
                    var tA = compMap[idA]; var tB = compMap[idB];
                    int sA = Math.Min(tA.Count, 60), sB = Math.Min(tB.Count, 60);
                    int bestDist = int.MaxValue;
                    int bax = tA[0].x, bay = tA[0].y, bbx = tB[0].x, bby = tB[0].y;
                    for (int ai = 0; ai < sA; ai++)
                    {
                        var (tax, tay) = tA[tA.Count * ai / sA];
                        for (int bi = 0; bi < sB; bi++)
                        {
                            var (tbx, tby) = tB[tB.Count * bi / sB];
                            int dist = Math.Abs(tax - tbx) + Math.Abs(tay - tby);
                            if (dist < bestDist) { bestDist = dist; bax = tax; bay = tay; bbx = tbx; bby = tby; }
                        }
                    }
                    if (bestDist > win) continue;

                    // Rectilinear carve from A toward B.
                    int mpx = bax, mpy = bay;
                    while (mpx != bbx || mpy != bby)
                    {
                        int msx2 = Math.Sign(bbx - mpx), msy2 = Math.Sign(bby - mpy);
                        if (msx2 != 0 && msy2 != 0)
                        {
                            if (Math.Abs(bbx - mpx) >= Math.Abs(bby - mpy)) msy2 = 0;
                            else msx2 = 0;
                        }
                        mpx += msx2; mpy += msy2;
                        if (mpx < 1 || mpx >= gridSize - 1 || mpy < 1 || mpy >= gridSize - 1) break;
                        var mnb = tiles[mpx, mpy].Biome;
                        if (mnb == BiomeType.Mountains || mnb == BiomeType.Peaks) break;
                        if (mnb != BiomeType.Pondsea)
                        {
                            var mwt = tiles[mpx, mpy];
                            mwt.Biome    = BiomeType.Pondsea;
                            mwt.Passable = false;
                            tiles[mpx, mpy] = mwt;
                            lbl[mpx, mpy]   = idA;
                            compMap[idA].Add((mpx, mpy));
                        }
                    }
                    foreach (var mt in compMap[idB]) { lbl[mt.x, mt.y] = idA; compMap[idA].Add(mt); }
                    compMap.Remove(idB);
                }

            // ── Post-pass A.5: Island seeding within Pondsea ─────────────────────
            // Places island tiles (land on all 4 cardinal sides surrounded by Pondsea)
            // adjacent to existing Pondsea bodies. Count scales with rainfall:
            //   rainBias ≥ 0.9 → 2–6;   rainBias ≥ 0.6 → 1–3;
            //   avgRain ≥ 0.5  → 0–2;   otherwise → 0.

            int islandTarget;
            if      (rainBias >= 0.9f) islandTarget = postRng.Next(2, 7);
            else if (rainBias >= 0.6f) islandTarget = postRng.Next(1, 4);
            else if (avgRain  >= 0.5f) islandTarget = postRng.Next(0, 3);
            else                       islandTarget = 0;

            if (islandTarget > 0)
            {
                // Build pool of Pondsea tiles to use as placement anchors.
                var pondPool = new List<(int x, int y)>(gridSize * gridSize / 8);
                for (int y = 0; y < gridSize; y++)
                    for (int x = 0; x < gridSize; x++)
                        if (tiles[x, y].Biome == BiomeType.Pondsea)
                            pondPool.Add((x, y));

                var islandRng2 = new Random(seed ^ 0x4A8B3C);
                int islandsPlaced = 0;

                for (int attempt = 0; attempt < 200 && islandsPlaced < islandTarget; attempt++)
                {
                    if (pondPool.Count == 0) break;
                    var (ppx, ppy) = pondPool[islandRng2.Next(pondPool.Count)];

                    int[] shuf = { 0, 1, 2, 3 };
                    for (int si = 3; si > 0; si--)
                    {
                        int sj = islandRng2.Next(si + 1);
                        (shuf[si], shuf[sj]) = (shuf[sj], shuf[si]);
                    }
                    foreach (int sd in shuf)
                    {
                        int iix = ppx + dx4[sd], iiy = ppy + dy4[sd];
                        if (iix < 2 || iix >= gridSize - 2 || iiy < 2 || iiy >= gridSize - 2) continue;
                        var iib = tiles[iix, iiy].Biome;
                        if (iib == BiomeType.Pondsea || iib == BiomeType.Mountains || iib == BiomeType.Peaks) continue;

                        bool iok = true;
                        for (int id2 = 0; id2 < 4 && iok; id2++)
                        {
                            int inx = iix + dx4[id2], iny = iiy + dy4[id2];
                            if (inx < 1 || inx >= gridSize - 1 || iny < 1 || iny >= gridSize - 1) { iok = false; break; }
                            var inb = tiles[inx, iny].Biome;
                            if (inb == BiomeType.Mountains || inb == BiomeType.Peaks) { iok = false; break; }
                        }
                        if (!iok) continue;

                        for (int id2 = 0; id2 < 4; id2++)
                        {
                            int inx = iix + dx4[id2], iny = iiy + dy4[id2];
                            if (tiles[inx, iny].Biome != BiomeType.Pondsea)
                            {
                                var int2 = tiles[inx, iny];
                                int2.Biome    = BiomeType.Pondsea;
                                int2.Passable = false;
                                tiles[inx, iny] = int2;
                                pondPool.Add((inx, iny));
                            }
                        }
                        islandsPlaced++;
                        break;
                    }
                }
            }

            // ── Post-pass A.7: River seeding (Phase 2.6) ─────────────────────────
            // Rivers are land tiles cardinally adjacent to a Pondsea cluster — they
            // serve as the "mouth" tile from which a meandering channel will be
            // carved on the local map. Count scales with rainfall so dry worlds
            // have none, default worlds get 1–3, wet worlds up to 6. River tiles
            // remain passable (colony can land on them, gets freshwater bonus on
            // the local map) and keep their original biome — they're a flag, not
            // a biome of their own.

            // v0.4.31 — bumped river targets (the v0.2.6 numbers gave so few
            // rivers that Sam went several worlds without seeing one). Even
            // dry worlds now get one river so the local-map river carving
            // and the §2.6 fertility / vegetation tables actually exercise
            // on a typical playthrough. Wet worlds spike to 4–8 to give
            // a proper braided river-system look on the world map.
            int riverTarget;
            if      (avgRain < 0.22f)   riverTarget = 1;
            else if (avgRain < 0.38f)   riverTarget = postRng.Next(1, 3);   // 1–2
            else if (rainBias >= 0.9f)  riverTarget = postRng.Next(4, 9);   // 4–8
            else                        riverTarget = postRng.Next(2, 6);   // 2–5

            if (riverTarget > 0)
            {
                // v0.4.31 — candidates are passable land tiles touching either
                // Pondsea (coastal river mouth) OR Mountains/Peaks (foothills
                // where snowmelt drains into the lowlands). Old code only
                // accepted coastal candidates; on inland-heavy worlds (no
                // Pondsea touching the inhabited region) zero candidates
                // existed and `riverTarget` rolled in vain. The Pass 4h
                // River carving in LocalMapGenerator already excludes
                // Mountains/Peaks themselves from carving (rivers don't drain
                // through bedrock), so the source is always the lowland tile
                // adjacent to the elevation, not the elevation itself.
                var riverCandidates = new List<(int x, int y)>();
                for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                {
                    if (!tiles[x, y].Passable) continue;
                    // Skip Mountain/Peaks lowland-flagged tiles directly —
                    // candidates must be the LOWLAND tile next to elevation,
                    // not the elevation itself.
                    if (tiles[x, y].Biome == BiomeType.Mountains
                        || tiles[x, y].Biome == BiomeType.Peaks) continue;
                    bool touchesWaterOrPeaks = false;
                    for (int d = 0; d < 4 && !touchesWaterOrPeaks; d++)
                    {
                        int nx = x + dx4[d], ny = y + dy4[d];
                        if (nx < 0 || nx >= gridSize || ny < 0 || ny >= gridSize) continue;
                        var nb = tiles[nx, ny].Biome;
                        if (nb == BiomeType.Pondsea
                            || nb == BiomeType.Mountains
                            || nb == BiomeType.Peaks) touchesWaterOrPeaks = true;
                    }
                    if (touchesWaterOrPeaks) riverCandidates.Add((x, y));
                }

                // Deterministic shuffle then take up to riverTarget.
                for (int i = riverCandidates.Count - 1; i > 0; i--)
                {
                    int j = postRng.Next(i + 1);
                    (riverCandidates[i], riverCandidates[j]) = (riverCandidates[j], riverCandidates[i]);
                }

                // v0.4.37 — extend each river seed into a multi-tile snaking
                // chain instead of flagging just one tile. From the seed
                // tile, walk 3-8 cardinal steps inland (away from the
                // touching water / mountain neighbour) flagging every passable
                // non-Peaks tile we cross. Result: rivers read as connected
                // ribbons across the world preview, not isolated stripes.
                // The local-map carving runs per flagged tile so the in-game
                // local-level river width is unaffected — every chain tile
                // still produces its own carved channel.
                int placed = 0;
                foreach (var (rx, ry) in riverCandidates)
                {
                    if (placed >= riverTarget) break;
                    // Pick an outward direction: opposite of the first
                    // touching Pondsea / elevation neighbour found. So a
                    // tile touching Pondsea on its east side gets a river
                    // walking west.
                    int outDir = -1;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = rx + dx4[d], ny = ry + dy4[d];
                        if (nx < 0 || nx >= gridSize || ny < 0 || ny >= gridSize) continue;
                        var nb = tiles[nx, ny].Biome;
                        if (nb == BiomeType.Pondsea
                            || nb == BiomeType.Mountains
                            || nb == BiomeType.Peaks)
                        {
                            outDir = d ^ 1;  // opposite cardinal (0<->1, 2<->3)
                            break;
                        }
                    }
                    int chainLen = postRng.Next(3, 9);   // 3–8 tiles per river chain
                    int cx = rx, cy = ry;
                    for (int step = 0; step < chainLen; step++)
                    {
                        if (cx < 0 || cx >= gridSize || cy < 0 || cy >= gridSize) break;
                        var ct = tiles[cx, cy];
                        if (ct.Biome == BiomeType.Pondsea
                            || ct.Biome == BiomeType.Mountains
                            || ct.Biome == BiomeType.Peaks) break;
                        if (!ct.Passable) break;
                        if (ct.HasRiver) break;   // don't overwrite an existing chain
                        ct.HasRiver  = true;
                        tiles[cx, cy] = ct;
                        // Step in the chosen outward direction, with a small
                        // chance per step of veering ±90° so the chain
                        // snakes naturally instead of running ruler-straight.
                        if (outDir < 0) break;
                        if (postRng.Next(100) < 35)
                        {
                            int turn = postRng.Next(2) == 0 ? -1 : 1;
                            // Cardinal rotation table: 0=N,1=S,2=W,3=E (matches dx4/dy4 order).
                            // Rotate ±90°: N↔W/E, S↔W/E, W↔N/S, E↔N/S.
                            outDir = outDir switch
                            {
                                0 => (turn < 0 ? 2 : 3),
                                1 => (turn < 0 ? 3 : 2),
                                2 => (turn < 0 ? 1 : 0),
                                3 => (turn < 0 ? 0 : 1),
                                _ => outDir,
                            };
                        }
                        cx += dx4[outDir];
                        cy += dy4[outDir];
                    }
                    placed++;
                }

                // v0.4.39 — flag orphan river tiles. After all river chains
                // are placed, any HasRiver tile whose four cardinal neighbours
                // are NOT HasRiver is a single-cell seed — the chain walk
                // bailed immediately (Pondsea/Mountain/edge on the first
                // step, or the outward direction was clamped). These get a
                // dedicated "Creek" subtype on the local map (multiple thin
                // rocky shallow streams) instead of the full river carve.
                for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                {
                    if (!tiles[x, y].HasRiver) continue;
                    bool hasRiverNeighbour = false;
                    for (int d = 0; d < 4 && !hasRiverNeighbour; d++)
                    {
                        int nx = x + dx4[d], ny = y + dy4[d];
                        if (nx < 0 || nx >= gridSize || ny < 0 || ny >= gridSize) continue;
                        if (tiles[nx, ny].HasRiver) hasRiverNeighbour = true;
                    }
                    if (!hasRiverNeighbour)
                    {
                        var rt = tiles[x, y];
                        rt.IsRiverOrphan = true;
                        tiles[x, y] = rt;
                    }
                }
            }

            // ── Post-pass B: IsCoastal marking ────────────────────────────────────
            // Any passable tile cardinally adjacent to a Pondsea tile gets IsCoastal = true.

            for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                {
                    if (tiles[x, y].Biome != BiomeType.Pondsea) continue;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + dx4[d], ny = y + dy4[d];
                        if (nx < 0 || nx >= gridSize || ny < 0 || ny >= gridSize) continue;
                        var nt = tiles[nx, ny];
                        if (!nt.Passable) continue;
                        nt.IsCoastal  = true;
                        tiles[nx, ny] = nt;
                    }
                }

            // ── Post-pass B.5: Demote orphan Coast tiles ─────────────────────────
            // ScoreBiome assigns Coast purely from elevation (< 0.22) in the initial
            // pass, which runs before Pondsea is generated. Many Coast tiles end up
            // far from any water once Post-pass A is done — beach terrain with no sea
            // in sight, which looks wrong and contradicts the biome's meaning. Any
            // Coast tile that didn't get IsCoastal = true in Post-pass B is demoted
            // here to the biome it would have had if Coast hadn't claimed it on
            // elevation alone (mirrors the elev ≥ 0.22 branch of ScoreBiome via
            // ClassifyNonCoastal). Coast tiles that ARE adjacent to Pondsea keep
            // their biome — those are correctly placed.

            for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                {
                    var t = tiles[x, y];
                    if (t.Biome != BiomeType.Coast || t.IsCoastal) continue;
                    t.Biome     = ClassifyNonCoastal(t.Rainfall, t.Temperature, t.MagicDensity);
                    tiles[x, y] = t;
                }

            // ── Post-pass C: Island detection ─────────────────────────────────────
            // A passable tile whose 4 cardinal neighbors are all Pondsea (or map
            // edge) becomes Island biome. Island tiles are inherently Coastal.

            for (int y = 0; y < gridSize; y++)
                for (int x = 0; x < gridSize; x++)
                {
                    var t = tiles[x, y];
                    if (!t.Passable) continue;
                    bool surrounded = true;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + dx4[d], ny = y + dy4[d];
                        if (nx < 0 || nx >= gridSize || ny < 0 || ny >= gridSize) continue;
                        var nb = tiles[nx, ny].Biome;
                        if (nb != BiomeType.Pondsea)
                        { surrounded = false; break; }
                    }
                    if (!surrounded) continue;
                    t.Biome     = BiomeType.Island;
                    t.IsCoastal = true;
                    tiles[x, y] = t;
                }

            return tiles;
        }

        private static FastNoiseLite MakeNoise(int seed, float frequency)
        {
            var n = new FastNoiseLite
            {
                NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
                Seed      = seed,
                Frequency = frequency,
            };
            return n;
        }

        private static float Normalize(float v) => Mathf.Clamp((v + 1f) * 0.5f, 0f, 1f);

        private static BiomeType ScoreBiome(float elev, float rain, float temp, float magic)
        {
            if (elev < 0.22f) return BiomeType.Coast;
            if (elev > 0.82f) return BiomeType.Peaks;
            if (elev > 0.65f) return BiomeType.Mountains;
            if (elev > 0.45f) return BiomeType.Hills;

            if (rain < 0.25f) return BiomeType.Desert;
            if (rain > 0.75f && temp > 0.45f) return BiomeType.Swamp;
            if (rain > 0.52f)
            {
                if (magic > 0.68f) return BiomeType.MagicGrove;
                return BiomeType.Forest;
            }
            return BiomeType.Plains;
        }

        // Fallback classifier for orphan Coast tiles in Post-pass B.5 (Coast tiles
        // that didn't end up adjacent to Pondsea). Mirrors the elev ≥ 0.22 branch
        // of ScoreBiome — the elevation tiers are skipped because every orphan-
        // Coast tile is low-elevation by construction.
        private static BiomeType ClassifyNonCoastal(float rain, float temp, float magic)
        {
            if (rain < 0.25f) return BiomeType.Desert;
            if (rain > 0.75f && temp > 0.45f) return BiomeType.Swamp;
            if (rain > 0.52f)
            {
                if (magic > 0.68f) return BiomeType.MagicGrove;
                return BiomeType.Forest;
            }
            return BiomeType.Plains;
        }
    }
}
