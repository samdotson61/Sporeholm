using Godot;
using System;
using System.Collections.Generic;

namespace SmurfulationC.World
{
	// Generates the dense local map from a world tile's seed using noise passes,
	// followed by guarantee passes for essential vegetation.
	//
	// Pass order:
	//   1 (+0)  — local elevation → terrain type
	//   2 (+7)  — detail/rainfall blend → Mud/Sand refinement, fertility
	//   4a (+17) — dead log blobs: directionally stretched noise → elongated strip formations
	//   4b (+23) — living wood blobs: same stretch technique, independent random angle
	//   4c (+29/+31) — debris scatter: small branch/bark fragments at higher frequency
	//   4c2       — wood-fill solidification: iterative majority-neighbour fill that closes
	//               passable gaps inside wood blobs and absorbs Pass 1 boulders trapped inside
	//   4d (+37) — boulder scatter: circular FBm clusters; ensures stone on most maps
	//   4e        — desert oasis pass: places puddle + grass ring clusters (Desert biome only)
	//   3 (+13) — vegetation density → VegetationSlot placement on remaining passable tiles
	//   5 (min) — guarantee pass: ensures every map has enough LargeMushroom for progression
	public static class LocalMapGenerator
	{
		// Minimum LargeMushroom tiles per map. Scales with map area; prevents soft-locks
		// on non-forest biomes where the biome table alone may not guarantee enough Fungal Wood.
		private static int MinLargeMushrooms(int width, int height) =>
			Math.Max(10, width * height / 400);

		// Minimum magic-essence vegetation (MagicFlower + HerbCluster) per map.
		// Ensures the Mage role always has attunement targets regardless of biome.
		private static int MinMagicVegetation(int width, int height) =>
			Math.Max(6, width * height / 650);

		// Returns the Mountain subtype (0 = Caves, 1 = Rocky Terrain, 2 = Mountain Face)
		// for a given world tile. Computed deterministically from LocalSeed so UI code
		// can label the level type before generating the local map. MUST stay in sync
		// with the subtype selection in the Pass 4h Mountain block below — same XOR
		// mask, same Random ctor, same Next(N) call.
		//
		// v0.4.45 — expanded from 3 to 6 subtypes for gameplay diversity matching
		// RimWorld's mountain biome variety. New subtypes: Solid Mountain (3),
		// Canyon (4), Crags (5). Peaks tiles are unlandable (preview-only) and
		// now default to Solid Mountain instead of Caves — a bedrock cliff
		// reads more like a snowy peak than an underground tunnel system.
		public static int GetMountainSubtype(WorldTile worldTile)
		{
			if (worldTile.Biome == BiomeType.Peaks) return 3;   // Solid Mountain
			return new Random(worldTile.LocalSeed ^ 0x5C3A7F1B).Next(6);
		}

		// v0.4.45 — friendly subtype name for the Landing Zone label. Mountains
		// stack as a single biome but visually present as one of six discrete
		// styles; this helper lets the UI distinguish them at glance time.
		public static string GetMountainSubtypeName(WorldTile worldTile)
		{
			if (worldTile.Biome != BiomeType.Mountains && worldTile.Biome != BiomeType.Peaks) return "";
			return GetMountainSubtype(worldTile) switch
			{
				0 => "Caves",
				1 => "Rocky Terrain",
				2 => "Mountain Face",
				3 => "Solid Mountain",
				4 => "Canyon",
				5 => "Crags",
				_ => "Mountains",
			};
		}

		public static LocalMap Generate(WorldTile worldTile,
			int width  = LocalMap.DefaultWidth,
			int height = LocalMap.DefaultHeight)
		{
			var map = new LocalMap(width, height) { Seed = worldTile.LocalSeed };

			// ── Passes 1 & 2: terrain ──────────────────────────────────────────────

			var elevNoise = new FastNoiseLite
			{
				NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
				Seed      = worldTile.LocalSeed,
				Frequency = 0.09f,
			};
			var detailNoise = new FastNoiseLite
			{
				NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
				Seed      = worldTile.LocalSeed + 7,
				Frequency = 0.16f,
			};

			for (int y = 0; y < map.Height; y++)
			{
				for (int x = 0; x < map.Width; x++)
				{
					float e = Normalize(elevNoise.GetNoise2D(x, y));
					float d = Normalize(detailNoise.GetNoise2D(x, y));

					float localElev = worldTile.Elevation * 0.55f + e * 0.45f;
					float localRain = worldTile.Rainfall  * 0.65f + d * 0.35f;

					var terrain   = SelectTerrain(localElev, localRain, worldTile.Biome);
					float fertility = Mathf.Clamp(localRain * (terrain == TerrainType.Grass ||
															   terrain == TerrainType.ForestFloor
															   ? 1.2f : 0.6f), 0f, 1f);

					map.Set(x, y, new LocalTile
					{
						Terrain  = terrain,
						Fertility = fertility,
						Passable  = LocalMap.IsPassableTerrain(terrain),
					});
				}
			}

			// ── Pass 4: dead log scatter ───────────────────────────────────────────
			// Converts some passable non-water tiles to DeadLog (impassable).
			// Runs before Pass 3 so vegetation is never placed on fresh dead logs.

			var deadLogNoise = new FastNoiseLite
			{
				NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
				Seed        = worldTile.LocalSeed + 17,
				Frequency   = 0.015f,                          // very low → 1–3 large blobs per map
				FractalType = FastNoiseLite.FractalTypeEnum.None,  // pure simplex — smooth blob edges, no octave roughness
			};
			float logThreshold = BiomeDeadLogThreshold(worldTile.Biome);

			// Single shared bias roll: 0 = dead wood dominant, 1 = living wood dominant,
			// ~0.5 = both sparse. Coupling the two rolls prevents both types from blooming
			// simultaneously — each map tilts toward one or the other.
			float woodBias       = (float)new Random((int)(worldTile.LocalSeed ^ 0xC3A4D8E1)).NextDouble();
			float deadFraction   = Mathf.Clamp(1f - woodBias * 2f, 0f, 1f);
			float livingFraction = Mathf.Clamp((woodBias - 0.5f) * 2f, 0f, 1f);

			if (logThreshold < 0.92f)
				logThreshold = Math.Max(logThreshold - deadFraction * 0.35f, 0.50f);

			// Per-map formation angle — each map's logs fall in a different direction.
			// Sampling with stretched rotated coordinates makes blobs elongated strips
			// rather than round blobs, matching the visual language of fallen logs.
			float deadAngle = (float)new Random(worldTile.LocalSeed ^ 0x1F7A3B).NextDouble() * Mathf.Pi;
			float dCos = Mathf.Cos(deadAngle);
			float dSin = Mathf.Sin(deadAngle);

			if (logThreshold < 1.0f)
			{
				for (int y = 0; y < map.Height; y++)
				{
					for (int x = 0; x < map.Width; x++)
					{
						var tile = map.Get(x, y);
						if (!tile.Passable) continue;
						float u = x * dCos + y * dSin;
						float v = (-x * dSin + y * dCos) / 3.5f;
						if (Normalize(deadLogNoise.GetNoise2D(u, v)) > logThreshold
							&& HasPassableNeighbor(map, x, y)
							&& IsSafePlacement(map, x, y))
						{
							tile.Terrain  = TerrainType.DeadLog;
							tile.Passable = false;
							map.Set(x, y, tile);
						}
					}
				}
			}

			// ── Pass 4b: living wood blobs ─────────────────────────────────────────
			// Rarer than dead logs. Same large-blob approach (low frequency, pure simplex).
			// Runs after dead logs so living wood never overwrites existing dead log tiles.

			var livingWoodNoise = new FastNoiseLite
			{
				NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
				Seed        = worldTile.LocalSeed + 23,
				Frequency   = 0.015f,
				FractalType = FastNoiseLite.FractalTypeEnum.None,
			};
			float lwThreshold = BiomeLivingWoodThreshold(worldTile.Biome);

			lwThreshold = Math.Max(lwThreshold - livingFraction * 0.28f, 0.60f);

			// Independent angle from dead logs — living tree stands rarely align with fallen logs.
			float livingAngle = (float)new Random(worldTile.LocalSeed ^ 0x6D4E2C).NextDouble() * Mathf.Pi;
			float lCos = Mathf.Cos(livingAngle);
			float lSin = Mathf.Sin(livingAngle);

			if (lwThreshold < 1.0f)
			{
				for (int y = 0; y < map.Height; y++)
				{
					for (int x = 0; x < map.Width; x++)
					{
						var tile = map.Get(x, y);
						if (!tile.Passable) continue;
						float u = x * lCos + y * lSin;
						float v = (-x * lSin + y * lCos) / 3.5f;
						if (Normalize(livingWoodNoise.GetNoise2D(u, v)) > lwThreshold
							&& HasPassableNeighbor(map, x, y)
							&& IsSafePlacement(map, x, y))
						{
							tile.Terrain  = TerrainType.LivingWood;
							tile.Passable = false;
							map.Set(x, y, tile);
						}
					}
				}
			}

			// ── Pass 4c: debris scatter ────────────────────────────────────────────
			// High-frequency noise scatters small branches and fallen bark of both types
			// across passable tiles, producing debris around and between main formations.
			// Only active in biomes where each wood type can spawn (threshold < 1.0).

			float dlScatterThres = BiomeDeadLogThreshold(worldTile.Biome)   < 0.92f ? 0.935f : 2.0f;
			float lwScatterThres = BiomeLivingWoodThreshold(worldTile.Biome) < 1.0f ? 0.965f : 2.0f;

			if (dlScatterThres < 1.0f || lwScatterThres < 1.0f)
			{
				var dlScatterNoise = new FastNoiseLite
				{
					NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
					Seed      = worldTile.LocalSeed + 29,
					Frequency = 0.05f,
				};
				var lwScatterNoise = new FastNoiseLite
				{
					NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
					Seed      = worldTile.LocalSeed + 31,
					Frequency = 0.05f,
				};

				for (int y = 0; y < map.Height; y++)
				{
					for (int x = 0; x < map.Width; x++)
					{
						var tile = map.Get(x, y);
						if (!tile.Passable) continue;

						// Living wood debris takes priority (checked first; rarer threshold keeps it sparse).
						if (lwScatterThres < 1.0f && Normalize(lwScatterNoise.GetNoise2D(x, y)) > lwScatterThres
							&& HasPassableNeighbor(map, x, y)
							&& IsSafePlacement(map, x, y))
						{
							tile.Terrain  = TerrainType.LivingWood;
							tile.Passable = false;
							map.Set(x, y, tile);
						}
						else if (dlScatterThres < 1.0f && Normalize(dlScatterNoise.GetNoise2D(x, y)) > dlScatterThres
							&& HasPassableNeighbor(map, x, y)
							&& IsSafePlacement(map, x, y))
						{
							tile.Terrain  = TerrainType.DeadLog;
							tile.Passable = false;
							map.Set(x, y, tile);
						}
					}
				}
			}

			// ── Pass 4c2: wood-fill solidification ────────────────────────────────
			// Two-stage solidification makes wood formations read as solid logs/stumps
			// across every biome — current and future — by deriving formation shape
			// purely from the wood noise mask rather than from what Pass 1 happened to
			// place first. Hills was the failing case: SelectTerrain places Boulder at
			// elev > 0.62 so Pass 1 generates dense boulder fields, and 4a/4b skip them
			// (those passes only convert passable tiles), leaving every boulder cluster
			// inside a wood blob's footprint as embedded grey rocks. The same flaw would
			// reappear in any biome that places boulders or other impassables in Pass 1.
			//
			// Stage 1 (mask force-convert): re-evaluate the 4a/4b directional-stretched
			// noise masks. Every non-wood, non-water tile inside a mask is force-set to
			// the corresponding wood type, regardless of its current terrain. This is
			// the only pass that overwrites Boulder tiles, and only inside the wood
			// blob's noise footprint — boulders out in the open are untouched. The
			// 4a/4b HasPassableNeighbor / IsSafePlacement guards are skipped here on
			// purpose: once the mask is finalised, interior wood tiles being surrounded
			// by other wood is the desired solid-formation shape, exactly how mountain
			// stone masses behave.
			//
			// Stage 2 (iterative neighbour fill): any non-wood tile (passable or
			// boulder) with 5+ wood neighbours becomes the dominant wood type. This
			// closes sub-threshold passable gaps inside the blob footprint (noise
			// values between roughly 0.5 and the wood threshold) and catches any
			// straggler tiles at mask boundaries that Stage 1 left mixed.
			//
			// Debris fragments from 4c are preserved: they live outside the main blob
			// mask (different noise function, much higher frequency), and an isolated
			// debris tile has 0–1 wood neighbours — well below Stage 2's threshold.
			//
			// Note: interior wood tiles end up surrounded by wood. The post-scatter
			// sweep skips DeadLog/LivingWood (v0.2.40), so those interior tiles persist
			// — smurfs walk around solid wood, not through it.

			// Stage 1a: mask force-convert — DeadLog
			if (logThreshold < 1.0f)
			{
				for (int y = 0; y < map.Height; y++)
				for (int x = 0; x < map.Width;  x++)
				{
					var tile = map.Get(x, y);
					if (tile.Terrain == TerrainType.Water) continue;
					if (tile.Terrain == TerrainType.DeadLog || tile.Terrain == TerrainType.LivingWood) continue;

					float u = x * dCos + y * dSin;
					float v = (-x * dSin + y * dCos) / 3.5f;
					if (Normalize(deadLogNoise.GetNoise2D(u, v)) > logThreshold)
					{
						tile.Terrain  = TerrainType.DeadLog;
						tile.Passable = false;
						map.Set(x, y, tile);
					}
				}
			}

			// Stage 1b: mask force-convert — LivingWood (does not overwrite DeadLog,
			// matching the 4a-before-4b precedence in the original scatter passes)
			if (lwThreshold < 1.0f)
			{
				for (int y = 0; y < map.Height; y++)
				for (int x = 0; x < map.Width;  x++)
				{
					var tile = map.Get(x, y);
					if (tile.Terrain == TerrainType.Water) continue;
					if (tile.Terrain == TerrainType.DeadLog || tile.Terrain == TerrainType.LivingWood) continue;

					float u = x * lCos + y * lSin;
					float v = (-x * lSin + y * lCos) / 3.5f;
					if (Normalize(livingWoodNoise.GetNoise2D(u, v)) > lwThreshold)
					{
						tile.Terrain  = TerrainType.LivingWood;
						tile.Passable = false;
						map.Set(x, y, tile);
					}
				}
			}

			// Stage 2: iterative neighbour fill (closes sub-threshold gaps and
			// absorbs any remaining boulders at mask edges)
			for (int iter = 0; iter < 4; iter++)
			{
				bool changed = false;
				for (int y = 0; y < map.Height; y++)
				for (int x = 0; x < map.Width;  x++)
				{
					var tile = map.Get(x, y);
					if (tile.Terrain == TerrainType.DeadLog || tile.Terrain == TerrainType.LivingWood) continue;
					if (!tile.Passable && tile.Terrain != TerrainType.Boulder) continue;

					int deadCount = 0, livingCount = 0;
					for (int dy = -1; dy <= 1; dy++)
					for (int dx = -1; dx <= 1; dx++)
					{
						if (dx == 0 && dy == 0) continue;
						int nx = x + dx, ny = y + dy;
						if (!map.InBounds(nx, ny)) continue;
						var nt = map.Get(nx, ny).Terrain;
						if      (nt == TerrainType.DeadLog)    deadCount++;
						else if (nt == TerrainType.LivingWood) livingCount++;
					}

					if (deadCount + livingCount >= 5)
					{
						tile.Terrain  = deadCount >= livingCount ? TerrainType.DeadLog : TerrainType.LivingWood;
						tile.Passable = false;
						map.Set(x, y, tile);
						changed = true;
					}
				}
				if (!changed) break;
			}

			// ── Pass 4d: boulder scatter ───────────────────────────────────────────
			// Ensures stone appears on most maps regardless of world-tile elevation.
			// Uses unmodified circular Simplex + 2 FBm octaves — the clustered blob shape
			// is appropriate for rock and visually contrasts with the elongated wood strips.
			// Skips tiles adjacent to any wood terrain so boulders never appear inside or
			// at the edges of dead log / living wood formations.
			// Thresholds are set above wood baseline values so stone stays rarer.

			float boulderScatterThres = BiomeBoulderScatterThreshold(worldTile.Biome);
			if (boulderScatterThres < 1.0f)
			{
				var boulderScatterNoise = new FastNoiseLite
				{
					NoiseType      = FastNoiseLite.NoiseTypeEnum.Simplex,
					Seed           = worldTile.LocalSeed + 37,
					Frequency      = 0.045f,
					FractalType    = FastNoiseLite.FractalTypeEnum.Fbm,
					FractalOctaves = 2,
				};
				for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width; x++)
					{
						var tile = map.Get(x, y);
						if (!tile.Passable) continue;
						if (Normalize(boulderScatterNoise.GetNoise2D(x, y)) > boulderScatterThres
							&& !IsAdjacentToWood(map, x, y)
							&& HasPassableNeighbor(map, x, y)
							&& IsSafePlacement(map, x, y))
						{
							tile.Terrain  = TerrainType.Boulder;
							tile.Passable = false;
							map.Set(x, y, tile);
						}
					}
			}

			// ── Post-scatter safety sweep ──────────────────────────────────────────
			// IsSafePlacement prevents scatter passes from enclosing existing impassable
			// tiles, but elevation-noise boulders from Pass 1 can still be enclosed if
			// the noise itself produces a dense cluster. This sweep converts isolated
			// Boulder tiles to biome floor so stone remains reachable.
			// DeadLog and LivingWood are intentionally skipped — carving passable holes
			// inside wood formations looks unnatural and those passes already guard with
			// IsSafePlacement. Runs before Pass 4h so mountain solid masses are unaffected.
			for (int y = 0; y < map.Height; y++)
			for (int x = 0; x < map.Width; x++)
			{
				var t = map.Get(x, y);
				if (t.Passable) continue;
				if (t.Terrain == TerrainType.DeadLog || t.Terrain == TerrainType.LivingWood) continue;
				if (!HasPassableNeighbor(map, x, y))
				{
					t.Terrain  = SelectBiomeFloor(worldTile.Biome);
					t.Passable = true;
					map.Set(x, y, t);
				}
			}

			// ── Pass 4h: Mountain generation (6 subtypes) ─────────────────────────
			// Subtype is derived from LocalSeed so each mountain tile is fixed but
			// varied. Every subtype path overwrites Pass 1 results completely so
			// the base elevation scatter does not bleed through.
			//
			//   Subtype 0 – Cave System    (water-carved tunnels in dense bedrock, ~16 % open)
			//   Subtype 1 – Rocky Terrain  (zone-based: solid blocks + boulder fields, ~30 % open)
			//   Subtype 2 – Mountain Face  (one side solid rock with 0–2 caves; other side open)
			//   Subtype 3 – Solid Mountain (RimWorld-style mostly-rock canvas, 80 % rock,
			//                small natural chambers, player excavates to expand)
			//   Subtype 4 – Canyon         (passable valley between two rock walls)
			//   Subtype 5 – Crags          (scattered tall rock pillars across passable highland)
			//
			// Peaks tiles are unlandable and default to Solid Mountain — a bedrock
			// cliff reads as a snowy peak in the preview thumbnail.

			if (worldTile.Biome is BiomeType.Mountains or BiomeType.Peaks)
			{
				bool isPeaks = worldTile.Biome == BiomeType.Peaks;
				int subtype  = isPeaks ? 3 : new Random(worldTile.LocalSeed ^ 0x5C3A7F1B).Next(6);

				// Per-map openness factor for the Rocky Terrain subtype only.
				// Uniform roll squared and inverted (1 − r²) biases the distribution
				// strongly toward 1, so most Rocky Terrain maps are path-rich and the
				// occasional dense rock-heavy generation still occurs but is rare.
				// 0 = original dense baseline; 1 = max open. Caves (subtype 0) ignore
				// this — caves are intentionally rock-dominant with narrow tunnels.
				// Mountain Face (subtype 2) ignores this — its open/wall split is
				// already governed by rockFrac. Peaks ignore it — preview-only maps.
				float opennessRoll = (float)new Random(worldTile.LocalSeed ^ 0x7C5F2A93).NextDouble();
				float openness     = isPeaks ? 0f : (1f - opennessRoll * opennessRoll);

				// ── Shared cleanup helper (called at end of every subtype) ──────────
				void CleanupIsolatedPassable()
				{
					for (int cy = 0; cy < map.Height; cy++)
					for (int cx = 0; cx < map.Width;  cx++)
					{
						var ct = map.Get(cx, cy);
						if (!ct.Passable || HasPassableNeighbor(map, cx, cy)) continue;
						ct.Terrain  = TerrainType.Boulder;
						ct.Passable = false;
						map.Set(cx, cy, ct);
					}
				}

				// ════════════════════════════════════════════════════════════════════
				// Subtype 0 – Cave System (water-carved, v0.4.28)
				// ════════════════════════════════════════════════════════════════════
				if (subtype == 0)
				{
					// Caves: dense bedrock cut by sinuous interconnected passages
					// modelled on real karst cave systems (Mammoth, Carlsbad, etc.)
					// where flowing water dissolves stone along joint planes,
					// producing branching dendritic networks of tunnels with
					// occasional rooms where multiple passages meet.
					//
					// Approach (replaced v0.4.27's chamber-blob + corridor scheme):
					//   • Primary: Ridged FBm — naturally produces vein/ridge
					//     patterns that read as flowing-water-carved tunnels.
					//     3 octaves so thin tributaries branch off thicker trunks.
					//   • Secondary: rare smooth-simplex peaks act as small
					//     intersection chambers where tunnels widen into rooms.
					//
					// Per-map openness factor does NOT apply — caves stay
					// intentionally rock-dominant (target ~14-18 % passable) for
					// claustrophobic underground exploration.
					var tunnelNoise = new FastNoiseLite
					{
						NoiseType         = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed              = worldTile.LocalSeed + 47,
						Frequency         = 0.038f,
						FractalType       = FastNoiseLite.FractalTypeEnum.Ridged,
						FractalOctaves    = 3,
						FractalLacunarity = 2.1f,
						FractalGain       = 0.55f,
					};
					var chamberNoise = new FastNoiseLite
					{
						NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed        = worldTile.LocalSeed + 41,
						Frequency   = 0.022f,
						FractalType = FastNoiseLite.FractalTypeEnum.None,
					};
					var outcropsNoise = new FastNoiseLite
					{
						NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed        = worldTile.LocalSeed + 53,
						Frequency   = 0.12f,
						FractalType = FastNoiseLite.FractalTypeEnum.None,
					};

					// Thresholds calibrated for the new ridged-tunnel topology:
					//   tunnelThres 0.72: ridged FBm peaks along ridge spines and
					//     drops off sharply, so this gates the upper ~12-14 % of
					//     the map into connected tunnel networks. Higher (0.80)
					//     for Peaks where the maps are even rockier.
					//   chamberThres 0.85: top ~5-7 % of smooth simplex — rare
					//     carved chambers where tunnels widen into rooms.
					//   outcropThres 0.78: top ~22 % of chamber tiles get a
					//     boulder (slightly lighter than v0.4.27's 0.82 since
					//     chambers are smaller now).
					float tunnelThres  = isPeaks ? 0.80f : 0.72f;
					float chamberThres = isPeaks ? 0.90f : 0.85f;
					float outcropThres = 0.78f;

					// Pass 1: carve tunnels (ridged FBm) and chambers.
					for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width;  x++)
					{
						float tN = Normalize(tunnelNoise.GetNoise2D(x, y));
						float cN = Normalize(chamberNoise.GetNoise2D(x, y));
						bool isPassable = tN > tunnelThres || cN > chamberThres;
						var t = map.Get(x, y);
						// Passable cave floor uses Mud — visually darker than
						// ForestFloor, and acts as the marker the vegetation pass
						// uses to gate caves to mushroom / magic / moss only.
						t.Terrain  = isPassable ? TerrainType.Mud : TerrainType.Boulder;
						t.Passable = isPassable;
						map.Set(x, y, t);
					}

					// Pass 2: boulder debris inside chambers only. Tunnels are
					// narrow — placing a boulder there can sever traversal even
					// when IsSafePlacement passes (the safety check protects
					// neighbour-impassable connectivity, not cross-tunnel pathing
					// for distant passable tiles). We gate the outcrop on
					// cN > chamberThres so only tiles inside the chamber blobs get
					// boulders. IsSafePlacement still runs as a second safety net.
					for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width;  x++)
					{
						var t = map.Get(x, y);
						if (!t.Passable) continue;
						float cN = Normalize(chamberNoise.GetNoise2D(x, y));
						if (cN <= chamberThres) continue;
						if (Normalize(outcropsNoise.GetNoise2D(x, y)) > outcropThres
							&& HasPassableNeighbor(map, x, y)
							&& IsSafePlacement(map, x, y))
						{
							t.Terrain  = TerrainType.Boulder;
							t.Passable = false;
							map.Set(x, y, t);
						}
					}

					CleanupIsolatedPassable();
				}

				// ════════════════════════════════════════════════════════════════════
				// Subtype 1 – Rocky Terrain (zone-based)
				// ════════════════════════════════════════════════════════════════════
				else if (subtype == 1)
				{
					var zoneNoise1 = new FastNoiseLite
					{
						NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed        = worldTile.LocalSeed + 47,
						Frequency   = 0.028f,
						FractalType = FastNoiseLite.FractalTypeEnum.None,
					};
					var massNoise1 = new FastNoiseLite
					{
						NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed        = worldTile.LocalSeed + 41,
						Frequency   = 0.04f,
						FractalType = FastNoiseLite.FractalTypeEnum.None,
					};

					// Zone thresholds, all scaled by per-map openness. With openness = 0
					// these match the original dense baseline (solid 0.20 / field 0.72 /
					// scatter 0.42). With openness = 1 the solid-rock zone shrinks from
					// 20 % to 5 %, and both fill densities drop ~30 percentage points,
					// producing a far more traversable rocky terrain map.
					float solidZone     = 0.20f - openness * 0.15f;  // 0.05 → 0.20
					float fieldThres1   = 0.72f - openness * 0.30f;  // 0.42 → 0.72
					float scatterThres1 = 0.42f - openness * 0.20f;  // 0.22 → 0.42

					for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width;  x++)
					{
						float zoneN = Normalize(zoneNoise1.GetNoise2D(x, y));
						float massN = Normalize(massNoise1.GetNoise2D(x, y));
						bool isRock;
						if      (zoneN < solidZone) isRock = true;
						else if (zoneN < 0.45f)     isRock = massN < fieldThres1;
						else                        isRock = massN < scatterThres1;
						var t = map.Get(x, y);
						t.Terrain  = isRock ? TerrainType.Boulder    : TerrainType.ForestFloor;
						t.Passable = !isRock;
						map.Set(x, y, t);
					}

					CleanupIsolatedPassable();
				}

				// ════════════════════════════════════════════════════════════════════
				// Subtype 2 – Mountain Face
				// One side (1/3–1/2) is a solid rock wall with 0–2 small caves.
				// The opposite side is open highland (Grass or ForestFloor).
				// ════════════════════════════════════════════════════════════════════
				else if (subtype == 2)
				{
					var faceRng = new Random(worldTile.LocalSeed ^ 0xAB3C7F);

					// v0.5.12 — continuous orientation angle (was: side =
					// 0/1/2/3 cardinal). Diagonal/oblique mountain faces
					// break the "same map type over and over" pattern Sam
					// reported, since pre-v0.5.12 every Mountain Face had
					// its rock-open boundary perpendicular to a map edge.
					// The dominant cardinal is still snapped from this
					// angle for cave-alignment purposes (cave centerline
					// math is axis-aligned for sensible "tunnel into the
					// rock" silhouettes).
					float faceAngle = (float)(faceRng.NextDouble() * Math.PI * 2.0);
					float dirX = (float)Math.Cos(faceAngle);
					float dirY = (float)Math.Sin(faceAngle);
					int side;
					if (Math.Abs(dirX) > Math.Abs(dirY))
						side = dirX > 0 ? 2 : 3;   // dirX>0 → rock on left (side=2), else right (side=3)
					else
						side = dirY > 0 ? 0 : 1;   // dirY>0 → rock on top (side=0), else bottom (side=1)

					// v0.5.12 — increased rock coverage (was 0.33-0.50, now
					// 0.45-0.65) per Sam's "increase rock coverage for the
					// Mountain Face." Combined with the multi-octave noise
					// below, the rock side now feels like a substantial
					// mountain rather than a thin band along one edge.
					float rockFrac    = 0.45f + (float)faceRng.NextDouble() * 0.20f;
					int   caveCount   = faceRng.Next(3);          // 0, 1, or 2
					bool  isForest    = worldTile.Rainfall > 0.5f;

					// v0.5.12 — multi-octave FBm with map-scaled frequency.
					// RimWorld parity (GenStep_ElevationFertility uses
					// 6-octave Perlin at frequency 0.021 with persistence
					// 0.5 / lacunarity 2.0). We use 4 octaves — fewer than
					// RimWorld because our maps are smaller, but enough to
					// produce visible ridges + spurs at the rock-open
					// boundary instead of the v0.4.x clean edge. Frequency
					// scales as 4 / max(W, H) so feature SCALE stays
					// consistent across map sizes — was hardcoded 0.05f
					// (correct for 80-tile maps, too zoomed-in on larger).
					int maxDim = Math.Max(map.Width, map.Height);
					float boundaryFreq = 4.0f / maxDim;
					var boundaryNoise = new FastNoiseLite
					{
						NoiseType         = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed              = worldTile.LocalSeed + 41,
						Frequency         = boundaryFreq,
						FractalType       = FastNoiseLite.FractalTypeEnum.Fbm,
						FractalOctaves    = 4,
						FractalLacunarity = 2.0f,
						FractalGain       = 0.5f,
					};

					// ── Rock/land assignment ─────────────────────────────────────
					// v0.5.12 — projection-based pos. Each tile's "depth into
					// rock" comes from projecting onto the chosen direction
					// vector (continuous angle, not cardinal). Normalized to
					// [0, 1] via the projection's own range so axis-aligned
					// and diagonal angles produce equivalent gradients.
					float ccx = map.Width  * 0.5f;
					float ccy = map.Height * 0.5f;
					float maxProj = (map.Width * 0.5f) * Math.Abs(dirX)
					             + (map.Height * 0.5f) * Math.Abs(dirY);
					for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width;  x++)
					{
						// pos: 0 = deep rock side, 1 = far open side
						float rawProj = (x - ccx) * dirX + (y - ccy) * dirY;
						float pos = (rawProj + maxProj) / (2f * maxProj);
						float bn = Normalize(boundaryNoise.GetNoise2D(x, y));
						// v0.5.12 — boundary jitter widened ±5% → ±15%.
						// Combined with the FBm noise this produces visible
						// ridges and peninsulas at the rock-open boundary
						// instead of a clean line, breaking the symmetric
						// look that pre-v0.5.12 Mountain Face had.
						float thresh = rockFrac + (bn - 0.5f) * 0.30f;
						var t = map.Get(x, y);
						if (pos < thresh)
						{
							t.Terrain  = TerrainType.Boulder;
							t.Passable = false;
						}
						else
						{
							t.Terrain  = isForest ? TerrainType.ForestFloor : TerrainType.Grass;
							t.Passable = true;
						}
						map.Set(x, y, t);
					}

					// ── Cave carving into the rock face ──────────────────────────
					// Caves are ellipses elongated along the rock-depth axis so they
					// look like tunnels entering the mountain from the open side.
					float depth  = side < 2 ? map.Height : map.Width;
					float spread = side < 2 ? map.Width  : map.Height;
					for (int c = 0; c < caveCount; c++)
					{
						// Cave centre: 20–40 % into the rock from its face (guarantees
						// the ellipse always extends back to the open land boundary).
						float cDepth  = (0.20f + (float)faceRng.NextDouble() * 0.20f) * depth * rockFrac;
						float cSpread = (0.15f + (float)faceRng.NextDouble() * 0.70f) * spread;
						int   rMain   = 9  + faceRng.Next(6);   // depth radius  9–14 tiles
						int   rPerp   = 5  + faceRng.Next(5);   // spread radius 5–9 tiles

						// Pixel centre coords based on which side the rock wall is on
						float cx = side switch
						{
							0 or 1 => cSpread,
							2      => cDepth,
							_      => map.Width - 1 - cDepth,
						};
						float cy = side switch
						{
							0      => cDepth,
							1      => map.Height - 1 - cDepth,
							_      => cSpread,
						};

						for (int y = 0; y < map.Height; y++)
						for (int x = 0; x < map.Width;  x++)
						{
							// Ellipse: depth axis uses rMain, spread axis uses rPerp.
							float nx = (side < 2)
								? (x - cx) / (float)rPerp
								: (x - cx) / (float)rMain;
							float ny = (side < 2)
								? (y - cy) / (float)rMain
								: (y - cy) / (float)rPerp;
							// Add slight noise to cave wall for organic shape
							float sh = Normalize(boundaryNoise.GetNoise2D(x * 1.7f, y * 1.7f));
							float nf = 0.85f + sh * 0.30f;   // 0.85–1.15× radius scale
							if (nx * nx / (nf * nf) + ny * ny / (nf * nf) < 1.0f)
							{
								var t = map.Get(x, y);
								t.Terrain  = TerrainType.ForestFloor;
								t.Passable = true;
								map.Set(x, y, t);
							}
						}
					}

					// ── Light boulder scatter on the open highland ────────────────
					var scatterNoise2 = new FastNoiseLite
					{
						NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed        = worldTile.LocalSeed + 59,
						Frequency   = 0.07f,
						FractalType = FastNoiseLite.FractalTypeEnum.None,
					};
					for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width;  x++)
					{
						var t = map.Get(x, y);
						if (!t.Passable) continue;
						if (Normalize(scatterNoise2.GetNoise2D(x, y)) > 0.88f
							&& HasPassableNeighbor(map, x, y)
							&& IsSafePlacement(map, x, y))
						{
							t.Terrain  = TerrainType.Boulder;
							t.Passable = false;
							map.Set(x, y, t);
						}
					}

					CleanupIsolatedPassable();
				}

				// ════════════════════════════════════════════════════════════════════
				// Subtype 3 – Solid Mountain (v0.4.45)
				// RimWorld-style. ~80 % solid bedrock with small natural chambers
				// scattered as carve-points; the player lands on a narrow open
				// patch and must excavate to expand. Generated via cellular
				// automata (Conway-like 4-5 rule) starting from a 65 %-rock noise
				// seed so the boundaries are organic and the chambers connect
				// through natural rather than ruler-straight passages.
				// ════════════════════════════════════════════════════════════════════
				else if (subtype == 3)
				{
					var solidRng = new Random(worldTile.LocalSeed ^ 0xC4F19A2);
					int W = map.Width, H = map.Height;
					// Two grids for double-buffered CA iteration.
					bool[,] rock = new bool[W, H];
					// Seed: 65 % rock at random, biased toward 80 % near edges so
					// the player never spawns flush against open map edge.
					for (int y = 0; y < H; y++)
					for (int x = 0; x < W; x++)
					{
						int edge = System.Math.Min(System.Math.Min(x, W - 1 - x),
													System.Math.Min(y, H - 1 - y));
						float pRock = edge < 3 ? 0.82f : 0.65f;
						rock[x, y] = solidRng.NextDouble() < pRock;
					}
					// Cellular automata: 4 passes of "if 5+ of 9 neighbours (incl.
					// self) are rock → rock, else floor". Smooths random seed into
					// organic blobs.
					bool[,] next = new bool[W, H];
					for (int it = 0; it < 4; it++)
					{
						for (int y = 0; y < H; y++)
						for (int x = 0; x < W; x++)
						{
							int n = 0;
							for (int dy = -1; dy <= 1; dy++)
							for (int dx = -1; dx <= 1; dx++)
							{
								int nx = x + dx, ny = y + dy;
								if ((uint)nx >= (uint)W || (uint)ny >= (uint)H)
								{
									n++;  // off-map counts as rock so edges stay solid
									continue;
								}
								if (rock[nx, ny]) n++;
							}
							next[x, y] = n >= 5;
						}
						// Swap buffers via simple copy.
						for (int y = 0; y < H; y++)
						for (int x = 0; x < W; x++)
							rock[x, y] = next[x, y];
					}
					// Force a guaranteed open landing pocket at the map centre so
					// the colony always spawns inside, even on dense rolls. 5-tile
					// radius circle of forced floor.
					int ccx = W / 2, ccy = H / 2;
					for (int dy = -5; dy <= 5; dy++)
					for (int dx = -5; dx <= 5; dx++)
					{
						if (dx * dx + dy * dy > 25) continue;
						int nx = ccx + dx, ny = ccy + dy;
						if ((uint)nx < (uint)W && (uint)ny < (uint)H)
							rock[nx, ny] = false;
					}
					// Paint result.
					for (int y = 0; y < H; y++)
					for (int x = 0; x < W; x++)
					{
						var t = map.Get(x, y);
						t.Terrain  = rock[x, y] ? TerrainType.Boulder : TerrainType.Mud;
						t.Passable = !rock[x, y];
						map.Set(x, y, t);
					}
					CleanupIsolatedPassable();
				}

				// ════════════════════════════════════════════════════════════════════
				// Subtype 4 – Canyon (v0.4.45)
				// Two thick rock walls on opposite sides of the map with a wide
				// passable valley in the middle. The floor is ForestFloor (or Grass
				// if rainfall low) with light boulder scatter for cover. 0-2 narrow
				// gaps cut through the side walls so the canyon isn't strictly
				// boxed-in on two sides.
				// ════════════════════════════════════════════════════════════════════
				else if (subtype == 4)
				{
					var canyonRng = new Random(worldTile.LocalSeed ^ 0x9B1E73);
					bool horizontal = canyonRng.NextDouble() < 0.5;
					int W = map.Width, H = map.Height;
					// Floor occupies the middle 40-60 % of the cross-axis.
					float floorFrac = 0.40f + (float)canyonRng.NextDouble() * 0.20f;
					int crossLen = horizontal ? H : W;
					int wallThick = (int)((1f - floorFrac) * 0.5f * crossLen);
					bool isForest = worldTile.Rainfall > 0.5f;
					var edgeNoise = new FastNoiseLite
					{
						NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed        = worldTile.LocalSeed + 67,
						Frequency   = 0.10f,
						FractalType = FastNoiseLite.FractalTypeEnum.None,
					};
					// Pre-roll the side-wall gap positions (0-2 gaps per side).
					int gapsPerSide = canyonRng.Next(3);
					var topGaps    = new System.Collections.Generic.List<int>(gapsPerSide);
					var bottomGaps = new System.Collections.Generic.List<int>(gapsPerSide);
					int alongLen = horizontal ? W : H;
					for (int g = 0; g < gapsPerSide; g++)
					{
						topGaps   .Add(canyonRng.Next(4, alongLen - 4));
						bottomGaps.Add(canyonRng.Next(4, alongLen - 4));
					}
					for (int y = 0; y < H; y++)
					for (int x = 0; x < W; x++)
					{
						int cross = horizontal ? y : x;
						int along = horizontal ? x : y;
						// Wall thickness wobbles ±2 tiles via edge noise.
						float wobble = (Normalize(edgeNoise.GetNoise2D(x, y)) - 0.5f) * 4f;
						bool inTopWall    = cross < wallThick + wobble;
						bool inBottomWall = cross > crossLen - 1 - wallThick - wobble;
						bool gapHere = false;
						foreach (var gp in topGaps)
							if (inTopWall    && System.Math.Abs(along - gp) <= 2) { gapHere = true; break; }
						if (!gapHere) foreach (var gp in bottomGaps)
							if (inBottomWall && System.Math.Abs(along - gp) <= 2) { gapHere = true; break; }
						var t = map.Get(x, y);
						if ((inTopWall || inBottomWall) && !gapHere)
						{
							t.Terrain  = TerrainType.Boulder;
							t.Passable = false;
						}
						else
						{
							t.Terrain  = isForest ? TerrainType.ForestFloor : TerrainType.Grass;
							t.Passable = true;
						}
						map.Set(x, y, t);
					}
					// Light boulder scatter on the canyon floor.
					var scatterNoise3 = new FastNoiseLite
					{
						NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed        = worldTile.LocalSeed + 73,
						Frequency   = 0.09f,
						FractalType = FastNoiseLite.FractalTypeEnum.None,
					};
					for (int y = 0; y < H; y++)
					for (int x = 0; x < W; x++)
					{
						var t = map.Get(x, y);
						if (!t.Passable) continue;
						if (Normalize(scatterNoise3.GetNoise2D(x, y)) > 0.86f
							&& HasPassableNeighbor(map, x, y)
							&& IsSafePlacement(map, x, y))
						{
							t.Terrain  = TerrainType.Boulder;
							t.Passable = false;
							map.Set(x, y, t);
						}
					}
					CleanupIsolatedPassable();
				}

				// ════════════════════════════════════════════════════════════════════
				// Subtype 5 – Crags (v0.4.45)
				// Mostly passable highland with 8-15 tall rock pillars / outcrops
				// scattered across the map. Each pillar is 3-7 tiles wide. Roomy
				// gameplay — players can build freely while still having strategic
				// rock chunks to mine and cover behind. Inspired by RimWorld's
				// "Outdoor" mountain variant.
				// ════════════════════════════════════════════════════════════════════
				else if (subtype == 5)
				{
					var cragRng = new Random(worldTile.LocalSeed ^ 0xD3A2F4);
					bool isForest = worldTile.Rainfall > 0.5f;
					int W = map.Width, H = map.Height;
					// Fill with open highland first.
					for (int y = 0; y < H; y++)
					for (int x = 0; x < W; x++)
					{
						var t = map.Get(x, y);
						t.Terrain  = isForest ? TerrainType.ForestFloor : TerrainType.Grass;
						t.Passable = true;
						map.Set(x, y, t);
					}
					// Scatter 8-15 rock pillars. Each is a noise-shaped blob.
					int pillarCount = 8 + cragRng.Next(8);
					var pillarNoise = new FastNoiseLite
					{
						NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
						Seed        = worldTile.LocalSeed + 79,
						Frequency   = 0.20f,
						FractalType = FastNoiseLite.FractalTypeEnum.None,
					};
					for (int p = 0; p < pillarCount; p++)
					{
						int cx = cragRng.Next(4, W - 4);
						int cy = cragRng.Next(4, H - 4);
						int rMain = 3 + cragRng.Next(5);  // 3-7 tile radius
						// Slight elongation for a more pillar-like silhouette.
						float aspect = 0.7f + (float)cragRng.NextDouble() * 0.6f;
						for (int dy = -rMain - 2; dy <= rMain + 2; dy++)
						for (int dx = -rMain - 2; dx <= rMain + 2; dx++)
						{
							int nx = cx + dx, ny = cy + dy;
							if ((uint)nx >= (uint)W || (uint)ny >= (uint)H) continue;
							float ex = dx / (float)rMain;
							float ey = dy / (float)rMain * aspect;
							float d  = MathF.Sqrt(ex * ex + ey * ey);
							// Noise jitter on the boundary so the pillar is craggy
							// rather than perfectly elliptical.
							float jitter = (Normalize(pillarNoise.GetNoise2D(nx, ny)) - 0.5f) * 0.30f;
							if (d + jitter < 1.0f)
							{
								var t = map.Get(nx, ny);
								t.Terrain  = TerrainType.Boulder;
								t.Passable = false;
								map.Set(nx, ny, t);
							}
						}
					}
					CleanupIsolatedPassable();
				}
			}

			// ── Pass 4e: desert oasis scatter ─────────────────────────────────────
			// Desert biome only. Places 2–5 small oases (1 water tile + grass ring)
			// deterministically from the map seed. Oasis grass tiles attract LargeSandshroom
			// clusters in the vegetation pass and act as natural waypoints for smurfs.

			if (worldTile.Biome == BiomeType.Desert)
			{
				var oasisRng   = new Random(worldTile.LocalSeed ^ 0x8E4F3B);
				int oasisCount = oasisRng.Next(2, 6);

				for (int o = 0; o < oasisCount; o++)
				{
					int px = oasisRng.Next(4, map.Width  - 4);
					int py = oasisRng.Next(4, map.Height - 4);

					// Centre tile → Water (puddle).
					var ctr      = map.Get(px, py);
					ctr.Terrain  = TerrainType.Water;
					ctr.Passable = false;
					ctr.Fertility = 0f;
					map.Set(px, py, ctr);

					// Grass ring up to Manhattan distance 3, excluding far diagonal corners.
					for (int dy = -2; dy <= 2; dy++)
					{
						for (int dx = -2; dx <= 2; dx++)
						{
							if (dx == 0 && dy == 0) continue;
							if (Math.Abs(dx) + Math.Abs(dy) > 3) continue;
							int nx = px + dx, ny = py + dy;
							if (!map.InBounds(nx, ny)) continue;
							var gt = map.Get(nx, ny);
							if (gt.Terrain == TerrainType.Water) continue;
							gt.Terrain   = TerrainType.Grass;
							gt.Passable  = true;
							gt.Fertility = 0.65f;
							map.Set(nx, ny, gt);
						}
					}
				}
			}

			// ── Pass 4f: pond scatter (wetter biomes) ─────────────────────────────
			// Places 2–7 small ponds (1–3 water tiles each) in Forest, Swamp, MagicGrove,
			// Hills, Coast, and Plains. Pond-edge tiles receive a vegetation density boost
			// in the vegetation pass via IsWithinTwoOfWater / effectiveN.

			bool isWetBiome = worldTile.Biome is BiomeType.Forest or BiomeType.Swamp
							  or BiomeType.MagicGrove or BiomeType.Hills
							  or BiomeType.Coast      or BiomeType.Plains;

			if (isWetBiome)
			{
				var pondRng   = new Random(worldTile.LocalSeed ^ 0x2C7A5F);
				// v0.4.37 — pond counts bumped so inland-only maps actually
				// surface small lakes / ponds occasionally. Sam's report:
				// the v0.4.31 0-1 pond on Plains / Hills was so rare players
				// went many worlds without ever seeing one, and "water
				// should be an important feature on any map it's included
				// on". New numbers: Swamp 3-5, Forest/MagicGrove 2-4,
				// Plains/Hills/Coast 1-3 (always at least one).
				int pondCount = worldTile.Biome switch
				{
					BiomeType.Swamp                           => pondRng.Next(3, 6),  // 3–5
					BiomeType.Forest or BiomeType.MagicGrove  => pondRng.Next(2, 5),  // 2–4
					_                                         => pondRng.Next(1, 4),  // 1–3
				};

				int[] cdx = { -1, 1, 0, 0 };
				int[] cdy = {  0, 0,-1, 1 };

				for (int p = 0; p < pondCount; p++)
				{
					int px = pondRng.Next(3, map.Width  - 3);
					int py = pondRng.Next(3, map.Height - 3);
					var ct = map.Get(px, py);
					if (!ct.Passable || ct.Terrain == TerrainType.Sand) continue;

					ct.Terrain  = TerrainType.Water;
					ct.Passable = false;
					map.Set(px, py, ct);

					// Shuffle cardinal directions, then expand to 1–2 random neighbors.
					for (int i = 3; i > 0; i--)
					{
						int j = pondRng.Next(i + 1);
						(cdx[i], cdx[j]) = (cdx[j], cdx[i]);
						(cdy[i], cdy[j]) = (cdy[j], cdy[i]);
					}
					// v0.4.37 — bigger ponds (was expand 1-2, now 2-4) so
					// they read as small lakes instead of single-pixel
					// puddles. Combined with the Shallows ring post-pass
					// the resulting feature is ~4-7 tiles across with a
					// wadeable border.
					int expand = pondRng.Next(2, 5);
					for (int e = 0; e < System.Math.Min(expand, cdx.Length); e++)
					{
						int nx = px + cdx[e], ny = py + cdy[e];
						if (!map.InBounds(nx, ny)) continue;
						var nt = map.Get(nx, ny);
						if (!nt.Passable || nt.Terrain == TerrainType.Sand) continue;
						nt.Terrain  = TerrainType.Water;
						nt.Passable = false;
						map.Set(nx, ny, nt);
					}
				}
			}

			// ── Pass 4g: Coastal fertility boost + organic beach / island edges ──────
			// IsCoastal and Island tiles get +0.15 fertility on all non-water tiles.
			//
			// Coastal (non-island): organic beach on one randomly chosen side.
			//   • Water zone : 2–4 tiles deep, edge varies ±1–2 tiles per column
			//     via low-frequency Simplex noise — produces a natural curved shoreline.
			//   • Sand zone  : 3–5 tiles directly behind water, forced to Sand terrain.
			//   • Rock outcrops: ~15% chance per beach column of a Boulder at or near
			//     the waterline (mimics rocks in the surf and at the tide line).
			//   • Segment width: 1/4–1/2 of the chosen side.
			//
			// Island: concentric rings read inward from the map edge.
			//   • Water ring (tiles 0–3 from edge): forced Water.
			//   • Sand ring  (tiles 4–7 from edge): forced Sand — visible beach band.
			//   • Rock outcrops scattered in the water ring; denser near the sand.
			//   • Interior (tile 8+): normal biome terrain from Pass 1/2.

			bool isCoastalLevel = worldTile.IsCoastal || worldTile.Biome == BiomeType.Island;

			if (isCoastalLevel)
			{
				for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width; x++)
					{
						var t = map.Get(x, y);
						if (t.Terrain == TerrainType.Water) continue;
						t.Fertility = Mathf.Min(t.Fertility + 0.15f, 1.0f);
						map.Set(x, y, t);
					}
			}

			if (worldTile.IsCoastal && worldTile.Biome != BiomeType.Island)
			{
				var coastRng = new Random(worldTile.LocalSeed ^ 0xB4C8D2);
				int  side    = coastRng.Next(0, 4);               // 0=top 1=bottom 2=left 3=right
				bool isHoriz = side < 2;
				int  sideLen = isHoriz ? map.Width : map.Height;

				// Beach spans the full chosen side — no partial segment.
				int segLen   = sideLen;
				int segStart = 0;

				// v0.4.37 — deeper coastline so water reads as a meaningful
				// feature on Coastal maps instead of a 2-4-tile lip on one
				// edge. Was 2-4 water + 3-5 sand (5-9 total depth); now
				// 4-7 water + 3-5 sand (7-12 total depth). Combined with
				// the v0.4.37 inlet-carving pass below, Coastal levels
				// finally feel like coasts.
				int waterBase = coastRng.Next(4, 8);   // 4–7 tiles of water at the edge
				int sandBase  = coastRng.Next(3, 6);   // 3–5 sand tiles behind the water

				// Per-column depth noise → organic curved shoreline.
				var beachNoise = new FastNoiseLite
				{
					NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
					Seed      = worldTile.LocalSeed ^ 0xC3D4E5,
					Frequency = 0.20f,
				};

				for (int pos = segStart; pos < segStart + segLen; pos++)
				{
					float nv   = (beachNoise.GetNoise2D(pos, 0) + 1f) * 0.5f;
					int waterD = Math.Clamp(waterBase + (int)(nv * 2f) - 1, 1, waterBase + 2);
					int sandD  = Math.Clamp(sandBase  + (int)(nv * 1.5f) - 1, 2, sandBase + 2);
					int totalD = waterD + sandD;

					for (int d = 0; d < totalD; d++)
					{
						int bx, by;
						switch (side)
						{
							case 0:  bx = pos;               by = d;                    break; // top
							case 1:  bx = pos;               by = map.Height - 1 - d;   break; // bottom
							case 2:  bx = d;                 by = pos;                  break; // left
							default: bx = map.Width - 1 - d; by = pos;                  break; // right
						}
						if (!map.InBounds(bx, by)) continue;
						var bt = map.Get(bx, by);
						if (!bt.Passable) continue; // never overwrite existing impassable

						if (d < waterD)
						{
							bt.Terrain  = TerrainType.Water;
							bt.Passable = false;
						}
						else
						{
							bt.Terrain  = TerrainType.Sand; // explicit sand band behind waterline
							bt.Passable = true;
						}
						map.Set(bx, by, bt);
					}

					// Rocky outcrop: ~15% chance per column at or just inside the waterline.
					if (coastRng.NextDouble() < 0.15)
					{
						int rd = coastRng.Next(0, waterD + 2);
						int rbx, rby;
						switch (side)
						{
							case 0:  rbx = pos;               rby = rd;                    break;
							case 1:  rbx = pos;               rby = map.Height - 1 - rd;   break;
							case 2:  rbx = rd;                rby = pos;                   break;
							default: rbx = map.Width - 1 - rd; rby = pos;                  break;
						}
						if (map.InBounds(rbx, rby))
						{
							var rt      = map.Get(rbx, rby);
							rt.Terrain  = TerrainType.Boulder;
							rt.Passable = false;
							map.Set(rbx, rby, rt);
						}
					}
				}

				// v0.4.37 — natural-harbour inlets. Drill 2-4 finger-shaped
				// water intrusions perpendicular to the coastline, 6-12
				// tiles deep, 1-2 tiles wide. Spaced along the coast so they
				// don't all bunch up. Each inlet meanders slightly via a
				// per-step ±1 perpendicular jitter. The Shallows ring
				// post-pass wraps every new water tile with wadeable
				// shallows automatically, so the inlets carve a clear deep
				// channel through the beach into the land — the kind of
				// natural harbour a settlement would actually build on.
				int inletCount = coastRng.Next(2, 5);
				int inletDeepEdge = waterBase + sandBase;   // start inlets inland of the sand
				for (int inlet = 0; inlet < inletCount; inlet++)
				{
					int alongPos = coastRng.Next(2, sideLen - 2);
					int inletLen = coastRng.Next(6, 13);     // 6-12 tiles deep
					int inletHalfW = coastRng.Next(0, 2);    // 0 or 1 → 1 or 2 tiles wide
					int jitter = 0;
					for (int step = 0; step < inletLen; step++)
					{
						int depth = inletDeepEdge + step;    // start at edge of sand and push inland
						int ax = alongPos + jitter;
						for (int dw = -inletHalfW; dw <= inletHalfW; dw++)
						{
							int ix, iy;
							int axw = ax + dw;
							switch (side)
							{
								case 0:  ix = axw;             iy = depth;                  break; // top
								case 1:  ix = axw;             iy = map.Height - 1 - depth; break; // bottom
								case 2:  ix = depth;           iy = axw;                    break; // left
								default: ix = map.Width - 1 - depth; iy = axw;              break; // right
							}
							if (!map.InBounds(ix, iy)) continue;
							var it = map.Get(ix, iy);
							// Inlets cut through dry land but yield to existing
							// rock / wood (a Boulder outcrop in the inlet path
							// becomes a small island in the channel).
							if (it.Terrain == TerrainType.Boulder
								|| it.Terrain == TerrainType.DeadLog
								|| it.Terrain == TerrainType.LivingWood) continue;
							it.Terrain  = TerrainType.Water;
							it.Passable = false;
							map.Set(ix, iy, it);
						}
						// Random ±1 jitter so the inlet snakes a little.
						if (coastRng.Next(100) < 35)
							jitter += (coastRng.Next(2) == 0 ? -1 : 1);
					}
				}
			}

			if (worldTile.Biome == BiomeType.Island)
			{
				// Water ring: tiles 0–3 from each edge.
				for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width; x++)
					{
						if (x >= 4 && x < map.Width - 4 && y >= 4 && y < map.Height - 4) continue;
						var t      = map.Get(x, y);
						t.Terrain  = TerrainType.Water;
						t.Passable = false;
						map.Set(x, y, t);
					}

				// Sand ring: tiles 4–7 from each edge (visible beach band).
				// Corner rounding: sand-ring tiles inside a radius-3 arc from each inner
				// water-ring corner are converted to water so corners look circular.
				for (int y = 4; y < map.Height - 4; y++)
					for (int x = 4; x < map.Width - 4; x++)
					{
						if (x >= 8 && x < map.Width - 8 && y >= 8 && y < map.Height - 8) continue;
						int rcx = Math.Min(x, map.Width  - 1 - x) - 4;
						int rcy = Math.Min(y, map.Height - 1 - y) - 4;
						if (rcx >= 0 && rcy >= 0 && rcx * rcx + rcy * rcy < 9)
						{
							var wt      = map.Get(x, y);
							wt.Terrain  = TerrainType.Water;
							wt.Passable = false;
							map.Set(x, y, wt);
							continue;
						}
						var t = map.Get(x, y);
						if (!t.Passable) continue;
						t.Terrain  = TerrainType.Sand;
						t.Passable = true;
						map.Set(x, y, t);
					}

				// Rocky outcrops in the water ring — denser near the sand edge (reef fringe).
				var islandRockRng = new Random(worldTile.LocalSeed ^ 0xF3C8A1);
				for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width; x++)
					{
						bool inWaterRing = !(x >= 4 && x < map.Width - 4 && y >= 4 && y < map.Height - 4);
						if (!inWaterRing) continue;
						// edgeDist: 0 at outermost pixel, 3 adjacent to the sand ring.
						int edgeDist = Math.Min(Math.Min(x, map.Width - 1 - x), Math.Min(y, map.Height - 1 - y));
						float rockChance = 0.22f * (edgeDist / 3f + 0.15f);
						if (islandRockRng.NextDouble() < rockChance)
						{
							var t      = map.Get(x, y);
							t.Terrain  = TerrainType.Boulder;
							t.Passable = false;
							map.Set(x, y, t);
						}
					}

				// v0.4.38 — Island inlets. Mirror of the Coastal inlet pass:
				// drill 3-5 finger-shaped water intrusions inward from the
				// island's beach ring, 5-10 tiles deep, 1-2 tiles wide, with
				// ±1 per-step jitter. Each inlet picks a random edge (N/S/W/E)
				// and a position along it, then pushes inland. Inlets cut
				// through Sand / Grass / Forest interior but yield to Boulder
				// / wood (rock outcrops become small islets in the inlet).
				// Paired with the universal Shallows ring, an Island map
				// now has carved natural-harbour cuts inland from the beach
				// ring instead of being a uniform doughnut of sand around a
				// pristine interior.
				var islandInletRng = new Random(worldTile.LocalSeed ^ 0xE6F2D1);
				int islandInletCount = islandInletRng.Next(3, 6);
				for (int inlet = 0; inlet < islandInletCount; inlet++)
				{
					int edgeChoice = islandInletRng.Next(0, 4);  // 0=top 1=bottom 2=left 3=right
					bool fromHorizSide = edgeChoice < 2;
					int alongLen = fromHorizSide ? map.Width : map.Height;
					int alongPos = islandInletRng.Next(6, alongLen - 6);  // keep inlets away from corners
					int inletLen = islandInletRng.Next(5, 11);            // 5-10 tiles deep
					int inletHalfW = islandInletRng.Next(0, 2);           // 0 or 1 → 1 or 2 tiles wide
					int startDepth = 8;                                    // start inside the sand band
					int jitter = 0;
					for (int step = 0; step < inletLen; step++)
					{
						int depth = startDepth + step;
						int ax = alongPos + jitter;
						for (int dw = -inletHalfW; dw <= inletHalfW; dw++)
						{
							int ix, iy;
							int axw = ax + dw;
							switch (edgeChoice)
							{
								case 0:  ix = axw;                   iy = depth;                  break; // top
								case 1:  ix = axw;                   iy = map.Height - 1 - depth; break; // bottom
								case 2:  ix = depth;                 iy = axw;                    break; // left
								default: ix = map.Width - 1 - depth; iy = axw;                    break; // right
							}
							if (!map.InBounds(ix, iy)) continue;
							var it = map.Get(ix, iy);
							if (it.Terrain == TerrainType.Boulder
								|| it.Terrain == TerrainType.DeadLog
								|| it.Terrain == TerrainType.LivingWood) continue;
							it.Terrain  = TerrainType.Water;
							it.Passable = false;
							map.Set(ix, iy, it);
						}
						if (islandInletRng.Next(100) < 40)
							jitter += (islandInletRng.Next(2) == 0 ? -1 : 1);
					}
				}
			}

			// ── Pass 4h: River carving (Phase 2.6 / v0.4.37 subtypes) ──────────────
			// World tiles flagged HasRiver get a long, wide channel carved across
			// the local map. v0.4.37 expanded the single 1-2 tile-wide channel
			// into four named subtypes drawn from a per-map roll, matching
			// Sam's reference of RimWorld river variations:
			//
			//   ThinSnaking (50 %) — width 2-4, strong meander, no fords.
			//                        The classic "creek through the meadow" look.
			//   WideDeep    (25 %) — width 5-8, gentle curve, thicker mud banks.
			//                        Mississippi / Ohio scale; impassable except
			//                        via Shroombridge (§5.11.d).
			//   Crossing    (15 %) — width 3-5 with 2-3 explicit Shallows fords
			//                        spanning the channel as wadeable crossings.
			//   Delta       (10 %) — main channel forks into 2 sibling channels
			//                        near the downstream half; total wider footprint.
			//
			// v0.4.39 — orphan tiles (HasRiver with no HasRiver neighbour, set
			// by WorldMapGenerator's post-pass A.7) override the random roll
			// and use the new "Creek" subtype: 1-3 thin Shallows-bed
			// streams snaking across the map with scattered Boulder
			// outcrops in the bed. No deep Water; the entire creek is
			// wadeable shallows. Matches the "babbling brook through the
			// woods" reference instead of a full carved river channel.
			//
			// Every Water tile gets a 1-tile Shallows ring as a separate post-
			// pass (CarveShallowsRing) — Sam's "padding any water generation"
			// ask, modelled on RimWorld's automatic shallow water around every
			// deep tile. Mud borders still flank the water (between deep water
			// and dry land) so riverbank vegetation has a substrate.
			//
			// Mountains / Peaks are excluded — rivers on peaks would drain
			// straight down through rock, which isn't sensible.

			if (worldTile.HasRiver
				&& worldTile.Biome != BiomeType.Mountains
				&& worldTile.Biome != BiomeType.Peaks)
			{
				var riverRng = new Random(worldTile.LocalSeed ^ 0xA1B2C3);

				// v0.4.48 — map-size scale factor. Reference is the v0.4.41
				// default level size (240×150 → min dim 150). All river /
				// pond / inlet sizing is multiplied by this so a 480×300
				// map gets 2× channel widths and a 720×450 max gets 3×.
				// Clamped to [0.6, 4.0] so the smallest size still has
				// readable rivers and the largest doesn't generate
				// nonsensical channel widths if Sam ever bumps the dial.
				float mapScale = System.Math.Clamp(
					System.Math.Min(map.Width, map.Height) / 150f, 0.6f, 4.0f);

				// v0.4.37 — pick subtype. v0.4.39: orphan tiles force the
				// Creek subtype (4) so single-cell river seeds become a
				// scatter of thin rocky shallow streams instead of a full
				// river channel.
				//
				// v0.4.48 — subtype-roll bias shifts toward WideDeep and
				// Delta on larger maps. Sam's report: "small rivers
				// predominate with little variation or presence of river
				// delta formations" on big maps where wide / delta
				// channels would have room to actually breathe. The bias
				// keeps ThinSnaking common (it's the canonical "river
				// through a meadow" look) but lets the big-feature
				// subtypes appear more often when the map has the area
				// to render them properly.
				int riverSubtype;
				if (worldTile.IsRiverOrphan)
				{
					riverSubtype = 4;
				}
				else
				{
					int subtypeRoll = riverRng.Next(100);
					// At mapScale 1.0 → 50/25/15/10 split (v0.4.37 baseline).
					// At mapScale 2.0 → 30/30/20/20 split (Large).
					// At mapScale 3.0 → 20/30/22/28 split (Max — Delta + WideDeep dominate).
					int thinThres   = (int)System.Math.Round(50f - (mapScale - 1f) * 15f);
					int wideThres   = thinThres + (int)System.Math.Round(25f + (mapScale - 1f) * 2.5f);
					int crossThres  = wideThres + (int)System.Math.Round(15f + (mapScale - 1f) * 3.5f);
					// remainder → Delta
					if      (subtypeRoll < thinThres)  riverSubtype = 0;
					else if (subtypeRoll < wideThres)  riverSubtype = 1;
					else if (subtypeRoll < crossThres) riverSubtype = 2;
					else                                riverSubtype = 3;
				}

				if (riverSubtype == 4)
				{
					// v0.4.39 — Creek subtype: 1-3 thin Shallows-bed streams
					// snaking across the map with rocky outcrops along the
					// bed. No deep Water — the bed is wadeable shallows
					// end-to-end, and ~12 % of bed tiles get a Boulder
					// outcrop ("babbling brook over rocks"). Each creek
					// uses its own noise seed so they don't run in
					// parallel.
					int creekCount = riverRng.Next(1, 4);  // 1-3
					for (int c = 0; c < creekCount; c++)
					{
						CarveCreekPath(map, riverRng, worldTile.LocalSeed + 71 + c * 13);
					}
				}
				else
				{
					// Per-subtype width / meander / ford params. Half-width is the
					// radius of the carved channel in tiles (so a halfWidth of 3
					// carves a ~6-7 tile-wide water core).
					int   minHalfWidth;
					int   maxHalfWidth;
					float meanderAmplitude;     // ±tiles perpendicular wander
					bool  allowExplicitFords;
					int   fordSpacingTiles;     // distance between Crossing-subtype fords
					int   fordHalfWidth;        // size of the Shallows ford zone (along-river)
					// v0.4.48 — half-width and meander amplitude scale linearly
					// with mapScale so a 720×450 max-size level gets ~3× the
					// channel width of the v0.4.37 baseline (240×150). Sam's
					// report: rivers on big maps look identical to small maps
					// and lose visual weight. Linear scaling keeps the river
					// occupying a similar % of total map area regardless of
					// dimensions, which is the visual goal.
					int ScaleHW(int baseHW) => System.Math.Max(1, (int)System.Math.Round(baseHW * mapScale));
					switch (riverSubtype)
					{
						case 1:  // WideDeep
							minHalfWidth = ScaleHW(3); maxHalfWidth = ScaleHW(5);
							meanderAmplitude = 6f * mapScale;
							allowExplicitFords = false;
							fordSpacingTiles = 0; fordHalfWidth = 0;
							break;
						case 2:  // Crossing
							minHalfWidth = ScaleHW(2); maxHalfWidth = ScaleHW(3);
							meanderAmplitude = 10f * mapScale;
							allowExplicitFords = true;
							fordSpacingTiles = System.Math.Max(map.Width, map.Height) / 4;
							fordHalfWidth = ScaleHW(2);
							break;
						case 3:  // Delta — main channel; branches added below
							minHalfWidth = ScaleHW(2); maxHalfWidth = ScaleHW(4);
							meanderAmplitude = 8f * mapScale;
							allowExplicitFords = false;
							fordSpacingTiles = 0; fordHalfWidth = 0;
							break;
						default: // ThinSnaking
							minHalfWidth = ScaleHW(1); maxHalfWidth = ScaleHW(2);
							meanderAmplitude = 14f * mapScale;
							allowExplicitFords = false;
							fordSpacingTiles = 0; fordHalfWidth = 0;
							break;
					}

					CarveRiverPath(map, riverRng, worldTile.LocalSeed + 41,
						minHalfWidth, maxHalfWidth, meanderAmplitude,
						allowExplicitFords, fordSpacingTiles, fordHalfWidth);

					// Delta: spawn additional smaller sibling channels that fork
					// off the entry side and reconverge near the exit. Cheap reuse
					// of CarveRiverPath with narrower params and entry offsets.
					// v0.4.48 — branch count scales with mapScale (2 baseline,
					// up to 4 on max maps) so a 720×450 Delta actually feels
					// like a delta with multiple distributaries.
					if (riverSubtype == 3)
					{
						int deltaBranchCount = System.Math.Clamp(
							2 + (int)System.Math.Round((mapScale - 1f) * 0.7f), 1, 4);
						for (int b = 0; b < deltaBranchCount; b++)
						{
							CarveRiverPath(map, riverRng, worldTile.LocalSeed + 41 + 7 * (b + 1),
								minHalfWidth: ScaleHW(1), maxHalfWidth: ScaleHW(2),
								meanderAmplitude: 6f * mapScale,
								allowFords: false, fordSpacingTiles: 0, fordHalfWidth: 0);
						}
					}
				}

				// Fertility boost for tiles within 3 of any river-water tile.
				// Captures the riverbank productivity bonus per Roadmap §2.6.3.
				for (int y = 0; y < map.Height; y++)
				for (int x = 0; x < map.Width;  x++)
				{
					var t = map.Get(x, y);
					if (t.Terrain == TerrainType.Water) continue;
					bool nearRiver = false;
					for (int dy = -3; dy <= 3 && !nearRiver; dy++)
					for (int dx = -3; dx <= 3 && !nearRiver; dx++)
					{
						int nx = x + dx, ny = y + dy;
						if (!map.InBounds(nx, ny)) continue;
						if (map.Get(nx, ny).Terrain == TerrainType.Water) nearRiver = true;
					}
					if (nearRiver)
					{
						t.Fertility = MathF.Min(t.Fertility + 0.20f, 1.0f);
						map.Set(x, y, t);
					}
				}
			}

			// v0.4.37 — final Shallows ring post-pass. Every Water tile (river,
			// pond, lake, coast, inlet) gets a 1-tile passable Shallows
			// padding around it. Modelled on RimWorld where every deep water
			// tile is bordered by wadeable shallow water. Shroombridges
			// (§5.11.d) can be placed over Shallows too, so a clear-water
			// crossing is achievable without building over the deep core.
			CarveShallowsRing(map);

			// ════════════════════════════════════════════════════════════════════
			// v0.5.14 (Phase 5C — "Discoveries") — gen-time encounters that
			// turn maps from "blank canvases" into "places with stories".
			// rimport.md §22 + N15-N19. Five passes added below in order:
			//
			//   N17 — Universal cave-carving        (CarveUniversalCaves)
			//   N15 — Pre-existing ruined structures (ScatterRuins)
			//   N16 — Resource clusters              (ScatterResourceVeins)
			//   N18 — Buried treasure quest hooks    (ScatterBuriedTreasure)
			//   N19 — Wildlife spawn-point stubs     (ScatterAnimalSpawnPoints)
			//
			// Rationale: every map should ship with at least one observable,
			// decidable encounter. Sam: "players don't see the same type of
			// level map over and over."
			// ════════════════════════════════════════════════════════════════════

			CarveUniversalCaves(map, worldTile);
			ScatterRuins(map, worldTile);
			ScatterResourceVeins(map, worldTile);
			ScatterBuriedTreasure(map, worldTile);
			ScatterAnimalSpawnPoints(map, worldTile);

			// ── Pass 3: vegetation placement ───────────────────────────────────────

			var vegNoise = new FastNoiseLite
			{
				NoiseType = FastNoiseLite.NoiseTypeEnum.Simplex,
				Seed      = worldTile.LocalSeed + 13,
				Frequency = 0.11f,
			};

			for (int y = 0; y < map.Height; y++)
			{
				for (int x = 0; x < map.Width; x++)
				{
					var tile = map.Get(x, y);
					if (!tile.Passable) continue;

					float n          = Normalize(vegNoise.GetNoise2D(x, y));
					// Tiles within 2 of any water body get a slight vegetation density boost.
					float effectiveN = IsWithinTwoOfWater(map, x, y)
						? Math.Min(n + 0.12f, 1.0f) : n;

					// Sand only supports sparse shrubbery/sandshrooms in clusters of 3+ sand tiles.
					if (tile.Terrain == TerrainType.Sand)
					{
						if (SandNeighborCount(map, x, y) < 2) continue;
						var sandVeg = isCoastalLevel
							? SelectCoastalSand(effectiveN)
							: SelectSandVegetation(effectiveN);
						if (sandVeg != VegetationType.None)
						{
							map.SetVegetation(x, y, VegetationSlot.Create(sandVeg));
							if (sandVeg == VegetationType.LargeSandshroom)
							{
								tile.Passable = false;
								map.Set(x, y, tile);
							}
						}
						continue;
					}

					// Biome-specific routing overrides before the general table.
					VegetationType vegType;
					// Cave passable tiles (Mountains biome + Mud terrain, set by Pass
					// 4h Subtype 0). Only mushrooms and magic vegetation grow in caves
					// — no berries / herbs-only / moss / brushland.
					if (worldTile.Biome == BiomeType.Mountains && tile.Terrain == TerrainType.Mud)
						vegType = SelectCaveVegetation(effectiveN);
					else if (worldTile.Biome == BiomeType.Desert
						&& tile.Terrain == TerrainType.Grass
						&& IsAdjacentToWater(map, x, y))
						vegType = SelectOasisVegetation(effectiveN);
					else if (worldTile.Biome == BiomeType.Forest && IsAdjacentToWood(map, x, y))
						vegType = SelectForestNearWood(effectiveN);
					else
						vegType = SelectVegetation(effectiveN, worldTile.Biome);
					if (vegType == VegetationType.None) continue;

					map.SetVegetation(x, y, VegetationSlot.Create(vegType));

					// Impassable large vegetation fills the tile.
					if (vegType == VegetationType.LargeMushroom ||
						vegType == VegetationType.LargeSandshroom ||
						vegType == VegetationType.PalmShroom)
					{
						tile.Passable = false;
						map.Set(x, y, tile);
					}
				}
			}

			// ── Pass 5: minimum LargeMushroom guarantee ────────────────────────────
			// Every map needs enough Fungal Wood for the colony to build structures.
			// Biome tables skew low on non-forest biomes, so we top up if needed.

			int minMush = MinLargeMushrooms(width, height);
			int current = CountVegetation(map, VegetationType.LargeMushroom);

			if (current < minMush)
			{
				var candidates = new List<(int X, int Y)>(capacity: width * height / 4);
				for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width; x++)
						{
							var t = map.Get(x, y);
							if (t.Passable && t.Terrain != TerrainType.Sand &&
								map.GetVegetation(x, y).Type != VegetationType.LargeMushroom)
								candidates.Add((x, y));
						}

				// Deterministic shuffle using the map seed.
				var rng = new Random(worldTile.LocalSeed ^ 0x4D2F);
				for (int i = candidates.Count - 1; i > 0; i--)
				{
					int j = rng.Next(i + 1);
					(candidates[i], candidates[j]) = (candidates[j], candidates[i]);
				}

				int needed = Math.Min(minMush - current, candidates.Count);
				for (int i = 0; i < needed; i++)
				{
					var (cx, cy) = candidates[i];
					map.SetVegetation(cx, cy, VegetationSlot.Create(VegetationType.LargeMushroom));
					var tile = map.Get(cx, cy);
					tile.Passable = false;
					map.Set(cx, cy, tile);
				}
			}

			// ── Pass 6: minimum magic-essence vegetation guarantee ─────────────────
			// Ensures every map has HerbCluster/MagicFlower targets for the Mage role.
			// Non-MagicGrove biomes may have very few magic sources from the biome table,
			// so we top up with HerbCluster (the universally appropriate fallback).

			int minMagic   = MinMagicVegetation(width, height);
			int magicCount = CountVegetationAny(map, VegetationType.MagicFlower, VegetationType.HerbCluster);

			if (magicCount < minMagic)
			{
				var magicCandidates = new List<(int X, int Y)>(capacity: width * height / 4);
				for (int y = 0; y < map.Height; y++)
					for (int x = 0; x < map.Width; x++)
					{
						if (!map.Get(x, y).Passable) continue;
						var vt = map.GetVegetation(x, y).Type;
						if (vt != VegetationType.MagicFlower && vt != VegetationType.HerbCluster)
							magicCandidates.Add((x, y));
					}

				var rng2 = new Random(worldTile.LocalSeed ^ 0x9C3E);
				for (int i = magicCandidates.Count - 1; i > 0; i--)
				{
					int j = rng2.Next(i + 1);
					(magicCandidates[i], magicCandidates[j]) = (magicCandidates[j], magicCandidates[i]);
				}

				int magicNeeded = Math.Min(minMagic - magicCount, magicCandidates.Count);
				for (int i = 0; i < magicNeeded; i++)
				{
					var (cx, cy) = magicCandidates[i];
					map.SetVegetation(cx, cy, VegetationSlot.Create(VegetationType.HerbCluster));
				}
			}

			// ── Pass 7: stone variation per Boulder tile (v0.4.2) ──────────────────
			// Every Boulder gets a specific stone subtype assigned at generation
			// time. Biome biases pick the dominant stone (mountains favour
			// Granite, swamp/lowlands favour Limestone) with rarer types
			// (Marble / Obsidian / Quartz / Magicstone) scattered, and
			// MagicCrystal as a rare ore-vein cluster.
			AssignStoneVariation(map, worldTile);

			return map;
		}

		// v0.4.2 — biome-biased stone subtype assignment with rare
		// MagicCrystal ore-veins. Walks every Boulder tile, picks a
		// stone family from a biome-weighted base distribution, then
		// over-paints small ore-vein clusters of MagicCrystal at the
		// 0.5–1.5 % rate that DF / RimWorld use for endgame gems.
		private static void AssignStoneVariation(LocalMap map, WorldTile worldTile)
		{
			// Per-biome base weights. Magicstone is rare everywhere except
			// MagicGrove; MagicCrystal is rarer still (overpainted in a
			// separate pass with cluster noise).
			(string SubType, float Weight)[] table = worldTile.Biome switch
			{
				BiomeType.Mountains or BiomeType.Peaks =>
					new[] { ("Granite", 0.50f), ("Marble", 0.20f), ("Obsidian", 0.10f), ("Quartz", 0.15f), ("Magicstone", 0.05f) },
				BiomeType.Hills =>
					new[] { ("Granite", 0.55f), ("Limestone", 0.15f), ("Marble", 0.10f), ("Quartz", 0.15f), ("Magicstone", 0.05f) },
				BiomeType.Swamp =>
					new[] { ("Limestone", 0.55f), ("Granite", 0.20f), ("Marble", 0.10f), ("Obsidian", 0.10f), ("Magicstone", 0.05f) },
				BiomeType.MagicGrove =>
					new[] { ("Granite", 0.30f), ("Magicstone", 0.30f), ("Marble", 0.20f), ("Quartz", 0.15f), ("Obsidian", 0.05f) },
				BiomeType.Coast or BiomeType.Island =>
					new[] { ("Limestone", 0.45f), ("Granite", 0.25f), ("Obsidian", 0.15f), ("Quartz", 0.10f), ("Magicstone", 0.05f) },
				BiomeType.Desert =>
					new[] { ("Granite", 0.40f), ("Quartz", 0.30f), ("Obsidian", 0.15f), ("Marble", 0.10f), ("Magicstone", 0.05f) },
				_ =>
					new[] { ("Granite", 0.50f), ("Limestone", 0.20f), ("Marble", 0.10f), ("Quartz", 0.10f), ("Obsidian", 0.05f), ("Magicstone", 0.05f) },
			};

			var rng = new Random(worldTile.LocalSeed ^ 0x57AB17E);

			// v0.4.28 — Pass A now assigns the stone subtype REGIONALLY,
			// not per-tile. The previous version called RollWeighted per
			// boulder, which yielded a confetti mix where every adjacent
			// tile could be a different subtype. Geologically unrealistic
			// (real strata occur in coherent blocks) and gameplay-hostile
			// (excavating a single ore type was impossible).
			//
			// New scheme: a low-frequency Cellular noise partitions the
			// map into Voronoi-shaped regions ~12-16 tiles across. Every
			// Boulder tile reads its region's per-cell value and uses it
			// as the lookup index into the biome's cumulative weight
			// table. All tiles in the same Voronoi cell therefore resolve
			// to the same subtype — large connected geological blocks,
			// like RimWorld / DF strata.
			var regionNoise = new FastNoiseLite
			{
				NoiseType                = FastNoiseLite.NoiseTypeEnum.Cellular,
				Seed                     = worldTile.LocalSeed ^ 0x57AB17E,
				Frequency                = 0.07f,
				CellularDistanceFunction = FastNoiseLite.CellularDistanceFunctionEnum.Euclidean,
				CellularReturnType       = FastNoiseLite.CellularReturnTypeEnum.CellValue,
			};

			// Pass A: assign base stone subtype per Voronoi region.
			for (int y = 0; y < map.Height; y++)
			for (int x = 0; x < map.Width; x++)
			{
				if (map.Get(x, y).Terrain != TerrainType.Boulder) continue;
				float t = Normalize(regionNoise.GetNoise2D(x, y));
				string pick = PickFromCumulative(table, t);
				map.SetTileStone(x, y, new SmurfulationC.Simulation.Items.MaterialKey("Stone", pick));
			}

			// Pass B: ore-vein clusters — MagicGrove gets 1.2 % crystal
			// chance per Boulder, all other biomes 0.4 %. Veins are 3-5
			// connected tiles via a short random walk so excavators see
			// DF-style stripes of crystal rather than scattered single
			// gems.
			float veinChance = worldTile.Biome == BiomeType.MagicGrove ? 0.012f : 0.004f;
			for (int y = 0; y < map.Height; y++)
			for (int x = 0; x < map.Width; x++)
			{
				if (map.Get(x, y).Terrain != TerrainType.Boulder) continue;
				if (rng.NextDouble() > veinChance) continue;

				int len = 3 + rng.Next(3);   // 3-5 tiles per vein
				int cx = x, cy = y;
				for (int step = 0; step < len; step++)
				{
					if (!map.InBounds(cx, cy)) break;
					if (map.Get(cx, cy).Terrain != TerrainType.Boulder) break;
					map.SetTileStone(cx, cy, new SmurfulationC.Simulation.Items.MaterialKey("Stone","MagicCrystal"));
					// Walk one step in a cardinal direction.
					int dir = rng.Next(4);
					cx += dir switch { 0 => 1, 1 => -1, _ => 0 };
					cy += dir switch { 2 => 1, 3 => -1, _ => 0 };
				}
			}
		}

		private static string RollWeighted((string SubType, float Weight)[] table, Random rng)
		{
			float total = 0;
			foreach (var (_, w) in table) total += w;
			float roll = (float)rng.NextDouble() * total;
			foreach (var (s, w) in table)
			{
				roll -= w;
				if (roll <= 0) return s;
			}
			return table[table.Length - 1].SubType;
		}

		// v0.4.28 — deterministic weight-table lookup keyed by a [0,1] value
		// instead of an RNG draw. Used by AssignStoneVariation Pass A so all
		// tiles in the same Voronoi region resolve to the same subtype: the
		// region-noise cell value is the lookup key, identical for every
		// tile in the cell. No allocations, no per-tile Random.
		private static string PickFromCumulative((string SubType, float Weight)[] table, float t)
		{
			float total = 0;
			foreach (var (_, w) in table) total += w;
			float cumulative = 0;
			foreach (var (s, w) in table)
			{
				cumulative += w / total;
				if (t <= cumulative) return s;
			}
			return table[table.Length - 1].SubType;
		}

		// ── Helpers ────────────────────────────────────────────────────────────────

		private static float Normalize(float v) => Mathf.Clamp((v + 1f) * 0.5f, 0f, 1f);

		// v0.4.37 — extracted river-channel carve. Walks from one edge of the
		// map to the opposite edge along a noise-meandered path, painting:
		//   • Deep Water in the core (dist <= halfWidth - 0.3)
		//   • Mud on the immediate banks (between water and dry ground)
		//   • Explicit Shallows fords at fordSpacingTiles intervals when
		//     allowFords is true (used by the Crossing subtype)
		//
		// Halfwidth oscillates between minHalfWidth and maxHalfWidth via a
		// secondary noise channel so the river isn't a perfectly uniform
		// ribbon. Boulder / DeadLog / LivingWood tiles override Mud (rock
		// outcrops in the riverbank stay) but yield to Water (the river
		// carves through them — same v0.2.6 rule).
		private static void CarveRiverPath(LocalMap map, Random riverRng, int noiseSeed,
			int minHalfWidth, int maxHalfWidth, float meanderAmplitude,
			bool allowFords, int fordSpacingTiles, int fordHalfWidth)
		{
			// v0.5.13 — orientation expanded from 2 modes (horizontal L→R,
			// vertical T→B) to 6 (adds 4 diagonal entry/exit pairs). Pre-
			// v0.5.13 every river ran perpendicular to a map axis; even
			// with strong meander amplitude the overall flow direction was
			// always one of two, making rivers feel repetitive across runs.
			// Now: weighted random selection — 40% axis-aligned (matches
			// the old behaviour), 60% diagonal (4 corner-to-corner-ish
			// patterns). Mountain Face got the same continuous-orientation
			// treatment in v0.5.12; rivers now match.
			//
			//   0: L→R horizontal      (20%)
			//   1: T→B vertical        (20%)
			//   2: L→T (diagonal up-right)   (15%)
			//   3: L→B (diagonal down-right) (15%)
			//   4: R→T (diagonal up-left)    (15%)
			//   5: R→B (diagonal down-left)  (15%)
			int orientationMode;
			double oRoll = riverRng.NextDouble();
			if      (oRoll < 0.20) orientationMode = 0;
			else if (oRoll < 0.40) orientationMode = 1;
			else if (oRoll < 0.55) orientationMode = 2;
			else if (oRoll < 0.70) orientationMode = 3;
			else if (oRoll < 0.85) orientationMode = 4;
			else                   orientationMode = 5;

			int entryX, entryY, exitX, exitY;
			switch (orientationMode)
			{
				case 0:   // L→R horizontal
					entryX = 0;             entryY = riverRng.Next(map.Height / 4, map.Height * 3 / 4);
					exitX  = map.Width - 1; exitY  = riverRng.Next(map.Height / 4, map.Height * 3 / 4);
					break;
				case 1:   // T→B vertical
					entryX = riverRng.Next(map.Width / 4, map.Width * 3 / 4);   entryY = 0;
					exitX  = riverRng.Next(map.Width / 4, map.Width * 3 / 4);   exitY  = map.Height - 1;
					break;
				case 2:   // L→T (enters left, exits top)
					entryX = 0;
					entryY = riverRng.Next(map.Height / 3, (map.Height * 2) / 3);
					exitX  = riverRng.Next(map.Width / 3, (map.Width * 2) / 3);
					exitY  = 0;
					break;
				case 3:   // L→B (enters left, exits bottom)
					entryX = 0;
					entryY = riverRng.Next(map.Height / 3, (map.Height * 2) / 3);
					exitX  = riverRng.Next(map.Width / 3, (map.Width * 2) / 3);
					exitY  = map.Height - 1;
					break;
				case 4:   // R→T (enters right, exits top)
					entryX = map.Width - 1;
					entryY = riverRng.Next(map.Height / 3, (map.Height * 2) / 3);
					exitX  = riverRng.Next(map.Width / 3, (map.Width * 2) / 3);
					exitY  = 0;
					break;
				default:  // case 5: R→B (enters right, exits bottom)
					entryX = map.Width - 1;
					entryY = riverRng.Next(map.Height / 3, (map.Height * 2) / 3);
					exitX  = riverRng.Next(map.Width / 3, (map.Width * 2) / 3);
					exitY  = map.Height - 1;
					break;
			}

			// v0.5.13 — Euclidean step count. Diagonal rivers need more
			// path steps than the longer axis alone (a corner-to-corner
			// path is sqrt(W²+H²) tiles long, not max(W,H)). Pre-v0.5.13
			// only the dominant axis was used (`horizontal ? Width :
			// Height`), which was correct for axis-aligned rivers but
			// would under-sample diagonal paths producing visible step
			// gaps in the channel.
			float pathLen = MathF.Sqrt(
				(exitX - entryX) * (float)(exitX - entryX)
			  + (exitY - entryY) * (float)(exitY - entryY));
			int steps = System.Math.Max(2, (int)System.Math.Ceiling(pathLen));

			// v0.5.13 — multi-octave FBm noise + map-scaled frequency.
			// Pre-v0.5.13 used single-octave Simplex (FractalType.None) at
			// hardcoded 0.06f — produced smooth featureless meanders and
			// the same wavelength regardless of river length. The 3-octave
			// FBm matches the v0.5.12 Mountain Face overhaul pattern
			// (RimWorld's GenStep_ElevationFertility uses 6-octave Perlin
			// for terrain; rivers can use 3 — the small octaves add
			// visible micro-wiggle on top of large bends, the natural
			// multi-scale meandering real rivers exhibit). Frequency is
			// scaled to target ~5 noise oscillations across the full
			// river length, so a 50-tile river and a 200-tile river both
			// have the same number of large bends — the bigger river
			// just has bigger absolute amplitude (controlled by the
			// caller's meanderAmplitude parameter).
			float riverFreq = 5.0f / steps;
			var riverNoise = new FastNoiseLite
			{
				NoiseType         = FastNoiseLite.NoiseTypeEnum.Simplex,
				Seed              = noiseSeed,
				Frequency         = riverFreq,
				FractalType       = FastNoiseLite.FractalTypeEnum.Fbm,
				FractalOctaves    = 3,
				FractalLacunarity = 2.0f,
				FractalGain       = 0.5f,
			};

			// v0.5.13 — perpendicular-to-flow side offset. Pre-v0.5.13 the
			// meander offset was applied to fy (horizontal rivers) or fx
			// (vertical) — axis-aligned only. For diagonal orientations
			// this would put the meander parallel to flow on one axis,
			// flattening the wiggle. Compute the unit perpendicular to
			// the entry→exit direction once; apply offset = noise *
			// amplitude in BOTH x and y via the perpendicular vector.
			float dirX = exitX - entryX;
			float dirY = exitY - entryY;
			float dirLen = MathF.Sqrt(dirX * dirX + dirY * dirY);
			if (dirLen > 0f) { dirX /= dirLen; dirY /= dirLen; }
			float perpX = -dirY;
			float perpY =  dirX;

			float widthRange = maxHalfWidth - minHalfWidth;
			for (int s = 0; s < steps; s++)
			{
				float t = (steps > 1) ? (float)s / (steps - 1) : 0f;
				float fx = entryX + (exitX - entryX) * t;
				float fy = entryY + (exitY - entryY) * t;

				float n = Normalize(riverNoise.GetNoise2D(s, 0));
				float sideOffset = (n - 0.5f) * meanderAmplitude;
				fx += sideOffset * perpX;
				fy += sideOffset * perpY;

				int cx = Mathf.Clamp((int)System.Math.Round(fx), 1, map.Width  - 2);
				int cy = Mathf.Clamp((int)System.Math.Round(fy), 1, map.Height - 2);

				int halfWidth = minHalfWidth + (int)(Normalize(riverNoise.GetNoise2D(s, 100)) * widthRange + 0.5f);
				if (halfWidth < minHalfWidth) halfWidth = minHalfWidth;
				if (halfWidth > maxHalfWidth) halfWidth = maxHalfWidth;

				// Crossing fords: when allowFords, every fordSpacingTiles along
				// the path widen a Shallows zone spanning the whole channel.
				bool inFordZone = allowFords && fordSpacingTiles > 0
					&& ((s + fordSpacingTiles / 2) % fordSpacingTiles) < fordHalfWidth * 2 + 1;

				int paintR = halfWidth + 1;  // include mud border
				for (int dy = -paintR; dy <= paintR; dy++)
				for (int dx = -paintR; dx <= paintR; dx++)
				{
					int nx = cx + dx, ny = cy + dy;
					if (!map.InBounds(nx, ny)) continue;
					float dist = MathF.Sqrt(dx * dx + dy * dy);
					if (dist > halfWidth + 0.4f) continue;
					var ct = map.Get(nx, ny);
					if (ct.Terrain == TerrainType.Water) continue;

					if (inFordZone && dist <= halfWidth - 0.3f)
					{
						// Explicit Shallows ford — full-width wadeable crossing.
						ct.Terrain  = TerrainType.Shallows;
						ct.Passable = true;
					}
					else if (dist <= halfWidth - 0.3f)
					{
						ct.Terrain  = TerrainType.Water;
						ct.Passable = false;
					}
					else
					{
						if (ct.Terrain == TerrainType.Boulder
							|| ct.Terrain == TerrainType.DeadLog
							|| ct.Terrain == TerrainType.LivingWood) continue;
						ct.Terrain  = TerrainType.Mud;
						ct.Passable = true;
					}
					map.Set(nx, ny, ct);
				}
			}
		}

		// v0.4.39 — Creek carve. Walks from one edge of the map to the
		// opposite edge along a strongly-meandered path, painting a thin
		// (1-tile wide) Shallows ribbon directly onto the ground — no
		// deep Water, no mud banks. ~12 % of bed tiles get a Boulder
		// outcrop along the path ("babbling brook over rocks"). Used by
		// orphan world-map river tiles (single-cell seeds with no
		// neighbour chain), called 1-3 times per map for "Creek/Creeks"
		// subtype.
		private static void CarveCreekPath(LocalMap map, Random rng, int noiseSeed)
		{
			var creekNoise = new FastNoiseLite
			{
				NoiseType   = FastNoiseLite.NoiseTypeEnum.Simplex,
				Seed        = noiseSeed,
				Frequency   = 0.10f,   // higher frequency than rivers → tighter wiggle
				FractalType = FastNoiseLite.FractalTypeEnum.None,
			};
			bool horizontal = rng.NextDouble() < 0.5;
			int entryX, entryY, exitX, exitY;
			if (horizontal)
			{
				entryX = 0;             entryY = rng.Next(map.Height / 4, map.Height * 3 / 4);
				exitX  = map.Width - 1; exitY  = rng.Next(map.Height / 4, map.Height * 3 / 4);
			}
			else
			{
				entryX = rng.Next(map.Width / 4, map.Width * 3 / 4);   entryY = 0;
				exitX  = rng.Next(map.Width / 4, map.Width * 3 / 4);   exitY  = map.Height - 1;
			}
			int steps = horizontal ? map.Width : map.Height;
			for (int s = 0; s < steps; s++)
			{
				float t = (steps > 1) ? (float)s / (steps - 1) : 0f;
				float fx = entryX + (exitX - entryX) * t;
				float fy = entryY + (exitY - entryY) * t;

				// Strong meander — ±18 tiles perpendicular wander gives the
				// brook a properly snaking footprint, much more than a
				// ThinSnaking river.
				float n = Normalize(creekNoise.GetNoise2D(s, 0));
				float sideOffset = (n - 0.5f) * 18f;
				if (horizontal) fy += sideOffset;
				else            fx += sideOffset;

				int cx = Mathf.Clamp((int)System.Math.Round(fx), 0, map.Width  - 1);
				int cy = Mathf.Clamp((int)System.Math.Round(fy), 0, map.Height - 1);

				var ct = map.Get(cx, cy);
				// Skip impassable terrain (creek goes AROUND rock / wood, not
				// through). The bed is 1 tile wide; we don't paint a halo.
				if (ct.Terrain == TerrainType.Boulder
					|| ct.Terrain == TerrainType.DeadLog
					|| ct.Terrain == TerrainType.LivingWood
					|| ct.Terrain == TerrainType.Water) continue;
				ct.Terrain  = TerrainType.Shallows;
				ct.Passable = true;
				map.Set(cx, cy, ct);

				// ~12 % chance per bed tile to spawn a Boulder outcrop in or
				// just beside the bed — the rocky-bed look. Placement chooses
				// a random ±1 perpendicular offset so the rocks line the
				// banks instead of always dropping in the centre.
				if (rng.Next(100) < 12)
				{
					int rdx = horizontal ? 0 : (rng.Next(3) - 1);
					int rdy = horizontal ? (rng.Next(3) - 1) : 0;
					int rx = cx + rdx, ry = cy + rdy;
					if (map.InBounds(rx, ry))
					{
						var rt = map.Get(rx, ry);
						if (rt.Terrain != TerrainType.Water
							&& rt.Terrain != TerrainType.Shallows
							&& rt.Terrain != TerrainType.DeadLog
							&& rt.Terrain != TerrainType.LivingWood)
						{
							rt.Terrain  = TerrainType.Boulder;
							rt.Passable = false;
							map.Set(rx, ry, rt);
						}
					}
				}
			}
		}

		// v0.4.37 — wraps every Water tile in a 1-tile Shallows ring (RimWorld
		// "shallow water" pattern). Skips tiles that are already Water,
		// Shallows, Boulder, DeadLog, or LivingWood — those should remain
		// their existing terrain so the channel banks read as either rock
		// or wood instead of an artificial wadeable strip. Run AFTER all
		// water-generating passes (Pondsea spillover, ponds, rivers, coast,
		// inlets) so it catches every aquatic feature in one walk.
		private static void CarveShallowsRing(LocalMap map)
		{
			int W = map.Width, H = map.Height;
			// Snapshot the deep-water tiles up-front so the ring doesn't
			// propagate into a second ring (Shallows-adjacent-to-Shallows
			// would expand indefinitely otherwise).
			var waterTiles = new System.Collections.Generic.List<(int x, int y)>(256);
			for (int y = 0; y < H; y++)
			for (int x = 0; x < W; x++)
			{
				if (map.Get(x, y).Terrain == TerrainType.Water)
					waterTiles.Add((x, y));
			}
			foreach (var (wx, wy) in waterTiles)
			{
				for (int dy = -1; dy <= 1; dy++)
				for (int dx = -1; dx <= 1; dx++)
				{
					if (dx == 0 && dy == 0) continue;
					int nx = wx + dx, ny = wy + dy;
					if (!map.InBounds(nx, ny)) continue;
					var t = map.Get(nx, ny);
					if (t.Terrain == TerrainType.Water
						|| t.Terrain == TerrainType.Shallows
						|| t.Terrain == TerrainType.Boulder
						|| t.Terrain == TerrainType.DeadLog
						|| t.Terrain == TerrainType.LivingWood) continue;
					t.Terrain  = TerrainType.Shallows;
					t.Passable = true;
					map.Set(nx, ny, t);
				}
			}
		}

		private static int CountVegetation(LocalMap map, VegetationType type)
		{
			int count = 0;
			for (int y = 0; y < map.Height; y++)
				for (int x = 0; x < map.Width; x++)
					if (map.GetVegetation(x, y).Type == type)
						count++;
			return count;
		}

		private static int CountVegetationAny(LocalMap map, VegetationType a, VegetationType b)
		{
			int count = 0;
			for (int y = 0; y < map.Height; y++)
				for (int x = 0; x < map.Width; x++)
				{
					var t = map.GetVegetation(x, y).Type;
					if (t == a || t == b) count++;
				}
			return count;
		}

		private static TerrainType SelectTerrain(float elev, float rain, BiomeType biome)
		{
			if (elev < 0.26f) return rain > 0.48f ? TerrainType.Mud : TerrainType.Sand;
			if (elev > 0.78f) return TerrainType.Boulder;

			return biome switch
			{
				BiomeType.Desert                    => rain < 0.32f ? TerrainType.Sand : TerrainType.Grass,
				BiomeType.Swamp                     => TerrainType.Mud,
				BiomeType.Forest                    => TerrainType.ForestFloor,
				BiomeType.MagicGrove                => elev > 0.55f ? TerrainType.MagicGrove : TerrainType.ForestFloor,
				BiomeType.Hills                     => elev > 0.62f ? TerrainType.Boulder : TerrainType.Grass,
				BiomeType.Mountains                 => elev > 0.68f ? TerrainType.Boulder : TerrainType.ForestFloor,
				BiomeType.Peaks                     => TerrainType.Boulder,
				BiomeType.Coast or BiomeType.Island => rain > 0.5f ? TerrainType.Mud : TerrainType.Sand,
				_                                   => TerrainType.Grass,
			};
		}

		// Minimum noise value (after Normalize) for dead log placement.
		// Tiles where noise > threshold become DeadLog. Using the HIGH end of the noise
		// range matches the vegetation convention and avoids the near-zero region that
		// Simplex noise rarely reaches, which caused the previous < density check to
		// produce no dead logs. Wetter biomes get lower thresholds (more dead wood).
		private static float BiomeDeadLogThreshold(BiomeType biome) => biome switch
		{
			BiomeType.Swamp                     => 0.80f,   // wettest — most dead wood
			BiomeType.Forest                    => 0.82f,
			BiomeType.Coast or BiomeType.Island => 0.84f,
			BiomeType.MagicGrove                => 0.85f,
			BiomeType.Hills                     => 0.86f,
			BiomeType.Plains                    => 0.90f,
			BiomeType.Mountains                 => 2.0f,    // above treeline — no dead wood
			BiomeType.Peaks                     => 2.0f,
			BiomeType.Desert                    => 0.96f,
			_                                   => 2.0f,    // never
		};

		// Living wood is rarer than dead logs — higher base thresholds, shallower density roll.
		// Biomes with active living trees (Forest, Swamp) get the lowest thresholds.
		// Desert never spawns living wood.
		private static float BiomeLivingWoodThreshold(BiomeType biome) => biome switch
		{
			BiomeType.Forest                    => 0.88f,   // most living tree stumps
			BiomeType.Swamp                     => 0.90f,
			BiomeType.Hills                     => 0.92f,
			BiomeType.Coast or BiomeType.Island => 0.93f,
			BiomeType.MagicGrove                => 0.94f,
			BiomeType.Plains                    => 0.95f,
			BiomeType.Mountains                 => 2.0f,    // above treeline — no living wood
			BiomeType.Peaks                     => 2.0f,
			BiomeType.Desert                    => 2.0f,    // never
			_                                   => 2.0f,
		};

		// Boulder scatter threshold — ensures stone appears on most maps but sparser than wood.
		// Peaks/Mountains already get heavy boulder coverage from the elevation pass; skip them.
		private static float BiomeBoulderScatterThreshold(BiomeType biome) => biome switch
		{
			BiomeType.Peaks      => 2.0f,    // elevation already saturates with stone
			BiomeType.Mountains  => 2.0f,
			BiomeType.Hills      => 0.93f,   // light scatter on low-elevation hill tiles
			BiomeType.Desert     => 0.94f,   // sparse rocky desert outcrops
			BiomeType.Forest     => 0.90f,   // scattered rocks on the forest floor
			BiomeType.Plains     => 0.91f,
			BiomeType.Coast      => 0.91f,
			BiomeType.MagicGrove => 0.92f,
			BiomeType.Swamp      => 0.93f,   // fewest rocks — wet ground
			_                    => 0.91f,
		};

		// Vegetation threshold: noise > threshold → tile gets vegetation.
		// Higher threshold = sparser biome. Every biome includes a rare LargeMushroom
		// tier at the top of the noise range so no map is structurally starved.
		private static VegetationType SelectVegetation(float n, BiomeType biome) => biome switch
		{
			BiomeType.Forest     => SelectForest(n),
			BiomeType.MagicGrove => SelectMagicGrove(n),
			BiomeType.Plains     => SelectPlains(n),
			BiomeType.Swamp      => SelectSwamp(n),
			BiomeType.Hills      => SelectHills(n),
			BiomeType.Coast      => SelectCoast(n),
			BiomeType.Island     => SelectCoast(n),   // Island grass mirrors Coast vegetation
			BiomeType.Mountains  => SelectMountains(n),
			BiomeType.Peaks      => SelectPeaks(n),
			BiomeType.Desert     => SelectDesert(n),
			_                    => VegetationType.None,
		};

		// Forest: dense — Underbrush, MossPatch (near wood), LargeMushroom, SmallMushroom
		// Underbrush cut ~30% (30% → 21%) to make room for baseline MossPatch.
		private static VegetationType SelectForest(float n)
		{
			if (n <= 0.35f) return VegetationType.None;
			if (n <= 0.56f) return VegetationType.Underbrush;
			if (n <= 0.65f) return VegetationType.MossPatch;
			if (n <= 0.90f) return VegetationType.LargeMushroom;
			return VegetationType.SmallMushroom;
		}

		// Forest tiles adjacent to DeadLog or LivingWood: MossPatch dominant.
		private static VegetationType SelectForestNearWood(float n)
		{
			if (n <= 0.20f) return VegetationType.None;
			if (n <= 0.65f) return VegetationType.MossPatch;
			if (n <= 0.82f) return VegetationType.Underbrush;
			if (n <= 0.94f) return VegetationType.LargeMushroom;
			return VegetationType.SmallMushroom;
		}

		// MagicGrove: dense — MagicFlower, SmallMushroom, LargeMushroom, HerbCluster
		// SmallMushroom band doubled (11% → 22%); LargeMushroom absorbs from above.
		private static VegetationType SelectMagicGrove(float n)
		{
			if (n <= 0.38f) return VegetationType.None;
			if (n <= 0.49f) return VegetationType.MagicFlower;
			if (n <= 0.71f) return VegetationType.SmallMushroom;
			if (n <= 0.87f) return VegetationType.LargeMushroom;
			return VegetationType.HerbCluster;
		}

		// Plains: moderate — SmurfberryBush, Underbrush, HerbCluster, rare LargeMushroom
		// SmurfberryBush band halved (0.28 → 0.14 range); Underbrush absorbs freed space.
		private static VegetationType SelectPlains(float n)
		{
			if (n <= 0.48f) return VegetationType.None;
			if (n <= 0.62f) return VegetationType.SmurfberryBush;
			if (n <= 0.90f) return VegetationType.Underbrush;
			if (n <= 0.95f) return VegetationType.HerbCluster;
			return VegetationType.LargeMushroom;
		}

		// Swamp: moderate — SmallMushroom, MossPatch, Underbrush, rare LargeMushroom
		// SmallMushroom band halved (22.5% → 11%); MossPatch absorbs freed space.
		private static VegetationType SelectSwamp(float n)
		{
			if (n <= 0.42f) return VegetationType.None;
			if (n <= 0.53f) return VegetationType.SmallMushroom;
			if (n <= 0.84f) return VegetationType.MossPatch;
			if (n <= 0.93f) return VegetationType.Underbrush;
			return VegetationType.LargeMushroom;
		}

		// Hills: moderate — SmurfberryBush, MossPatch, rare LargeMushroom
		// SmurfberryBush band halved (0.245 → 0.12 range); MossPatch absorbs freed space.
		private static VegetationType SelectHills(float n)
		{
			if (n <= 0.58f) return VegetationType.None;
			if (n <= 0.70f) return VegetationType.SmurfberryBush;
			if (n <= 0.93f) return VegetationType.MossPatch;
			return VegetationType.LargeMushroom;
		}

		// Coast: moderate — SmurfberryBush, Underbrush, rare LargeMushroom
		// SmurfberryBush band halved (0.235 → 0.12 range); Underbrush absorbs freed space.
		private static VegetationType SelectCoast(float n)
		{
			if (n <= 0.62f) return VegetationType.None;
			if (n <= 0.74f) return VegetationType.SmurfberryBush;
			if (n <= 0.945f) return VegetationType.Underbrush;
			return VegetationType.LargeMushroom;
		}

		// Mountains: sparse — SmurfberryBush, SmallMushroom (~5% each), MossPatch, rare LargeMushroom
		private static VegetationType SelectMountains(float n)
		{
			if (n <= 0.68f) return VegetationType.None;
			if (n <= 0.73f) return VegetationType.SmurfberryBush;
			if (n <= 0.78f) return VegetationType.SmallMushroom;
			if (n <= 0.94f) return VegetationType.MossPatch;
			return VegetationType.LargeMushroom;
		}

		// Peaks: very sparse — SmurfberryBush, SmallMushroom (~5% each), MossPatch, very rare LargeMushroom
		private static VegetationType SelectPeaks(float n)
		{
			if (n <= 0.78f) return VegetationType.None;
			if (n <= 0.83f) return VegetationType.SmurfberryBush;
			if (n <= 0.88f) return VegetationType.SmallMushroom;
			if (n <= 0.97f) return VegetationType.MossPatch;
			return VegetationType.LargeMushroom;
		}

		// Coastal/Island sand: PalmShroom (Fungal Wood) and PineShroom (Food) replace desert sandshrooms.
		private static VegetationType SelectCoastalSand(float n)
		{
			if (n <= 0.78f) return VegetationType.None;
			if (n <= 0.84f) return VegetationType.PineShroom;   // ~6% food (1.5× SmallMushroom)
			if (n <= 0.88f) return VegetationType.Underbrush;
			if (n <= 0.90f) return VegetationType.HerbCluster;
			return VegetationType.PalmShroom;                    // top ~10% Fungal Wood
		}

		// Sand tiles: sparse sandshrooms and scrub, only inside clusters of 3+ sand tiles.
		// SmallSandshroom incidence halved vs. previous (5% of eligible range).
		private static VegetationType SelectSandVegetation(float n)
		{
			if (n <= 0.79f) return VegetationType.None;
			if (n <= 0.84f) return VegetationType.SmallSandshroom;  // ~5% of eligible range
			if (n <= 0.88f) return VegetationType.Underbrush;
			if (n <= 0.90f) return VegetationType.HerbCluster;
			return VegetationType.LargeSandshroom;                   // top ~10%
		}

		// Count how many of the 8 neighbouring tiles are also Sand.
		private static int SandNeighborCount(LocalMap map, int x, int y)
		{
			int count = 0;
			for (int dy = -1; dy <= 1; dy++)
				for (int dx = -1; dx <= 1; dx++)
				{
					if (dx == 0 && dy == 0) continue;
					int nx = x + dx, ny = y + dy;
					if (map.InBounds(nx, ny) && map.Get(nx, ny).Terrain == TerrainType.Sand)
						count++;
				}
			return count;
		}

		// Desert grass: dry scrub only — sandshrooms are sand-tile exclusive.
		// Oasis-adjacent grass uses SelectOasisVegetation instead.
		private static VegetationType SelectDesert(float n)
		{
			if (n <= 0.84f) return VegetationType.None;
			if (n <= 0.93f) return VegetationType.Underbrush;
			if (n <= 0.97f) return VegetationType.HerbCluster;
			return VegetationType.MagicFlower;
		}

		// Oasis-adjacent grass in Desert biome: normal forest vegetation (LargeMushroom, berries, herbs).
		private static VegetationType SelectOasisVegetation(float n)
		{
			if (n <= 0.35f) return VegetationType.None;
			if (n <= 0.60f) return VegetationType.LargeMushroom;
			if (n <= 0.88f) return VegetationType.SmallMushroom;
			if (n <= 0.93f) return VegetationType.HerbCluster;
			if (n <= 0.97f) return VegetationType.SmurfberryBush;
			return VegetationType.MagicFlower;
		}

		// Cave passable tiles (Mountains biome, Mud terrain set by Pass 4h Subtype 0).
		// Only mushroom, magic, and moss vegetation grows underground — no berries,
		// scrub, or brushland. MossPatch is the most common live vegetation (caves
		// are damp and shaded — moss thrives there). Overall density is moderate
		// (~50 % None) so cave halls stay readable; the LargeMushroom + magic-essence
		// guarantee passes (5 / 6) backfill if a particular roll is too thin.
		private static VegetationType SelectCaveVegetation(float n)
		{
			if (n <= 0.50f) return VegetationType.None;
			if (n <= 0.72f) return VegetationType.MossPatch;
			if (n <= 0.86f) return VegetationType.SmallMushroom;
			if (n <= 0.94f) return VegetationType.LargeMushroom;
			if (n <= 0.98f) return VegetationType.MagicFlower;
			return VegetationType.HerbCluster;
		}

		private static bool IsWithinTwoOfWater(LocalMap map, int x, int y)
		{
			for (int dy = -2; dy <= 2; dy++)
				for (int dx = -2; dx <= 2; dx++)
				{
					if (dx == 0 && dy == 0) continue;
					int nx = x + dx, ny = y + dy;
					if (map.InBounds(nx, ny) && map.Get(nx, ny).Terrain == TerrainType.Water)
						return true;
				}
			return false;
		}

		private static bool IsAdjacentToWood(LocalMap map, int x, int y)
		{
			for (int dy = -1; dy <= 1; dy++)
				for (int dx = -1; dx <= 1; dx++)
				{
					if (dx == 0 && dy == 0) continue;
					int nx = x + dx, ny = y + dy;
					if (!map.InBounds(nx, ny)) continue;
					var t = map.Get(nx, ny).Terrain;
					if (t == TerrainType.DeadLog || t == TerrainType.LivingWood) return true;
				}
			return false;
		}

		private static bool IsAdjacentToWater(LocalMap map, int x, int y)
		{
			for (int dy = -1; dy <= 1; dy++)
				for (int dx = -1; dx <= 1; dx++)
				{
					if (dx == 0 && dy == 0) continue;
					int nx = x + dx, ny = y + dy;
					if (map.InBounds(nx, ny) && map.Get(nx, ny).Terrain == TerrainType.Water)
						return true;
				}
			return false;
		}

		// Returns true if at least one of the 8 neighbours is currently passable.
		// Used before any impassable-terrain placement to prevent enclosed pockets
		// where a tile is walled in on all sides by other impassable tiles.
		private static bool HasPassableNeighbor(LocalMap map, int x, int y)
		{
			for (int dy = -1; dy <= 1; dy++)
				for (int dx = -1; dx <= 1; dx++)
				{
					if (dx == 0 && dy == 0) continue;
					int nx = x + dx, ny = y + dy;
					if (map.InBounds(nx, ny) && map.Get(nx, ny).Passable)
						return true;
				}
			return false;
		}

		// Returns true if placing an impassable tile at (x, y) would NOT isolate any
		// adjacent impassable tile. For each impassable 8-neighbor we verify it still
		// has at least one passable neighbor OTHER than (x, y) after the placement.
		// Combined with HasPassableNeighbor this enforces the invariant: no impassable
		// tile is ever fully enclosed by other impassable tiles of any type.
		private static bool IsSafePlacement(LocalMap map, int x, int y)
		{
			for (int dy = -1; dy <= 1; dy++)
			for (int dx = -1; dx <= 1; dx++)
			{
				if (dx == 0 && dy == 0) continue;
				int nx = x + dx, ny = y + dy;
				if (!map.InBounds(nx, ny)) continue;
				if (map.Get(nx, ny).Passable) continue;   // passable neighbors are unaffected

				// (nx, ny) is impassable. After (x, y) becomes impassable, does it
				// still have any passable neighbor besides (x, y)?
				bool hasOther = false;
				for (int iy = -1; iy <= 1 && !hasOther; iy++)
				for (int ix = -1; ix <= 1 && !hasOther; ix++)
				{
					if (ix == 0 && iy == 0) continue;
					int nnx = nx + ix, nny = ny + iy;
					if (nnx == x && nny == y) continue;   // skip the tile being placed
					if (map.InBounds(nnx, nny) && map.Get(nnx, nny).Passable)
						hasOther = true;
				}
				if (!hasOther) return false;   // placement would isolate (nx, ny)
			}
			return true;
		}

		private static TerrainType SelectBiomeFloor(BiomeType biome) => biome switch
		{
			BiomeType.Swamp                     => TerrainType.Mud,
			BiomeType.Desert                    => TerrainType.Sand,
			BiomeType.Coast or BiomeType.Island => TerrainType.Sand,
			BiomeType.Forest or BiomeType.MagicGrove
				or BiomeType.Mountains or BiomeType.Peaks
												=> TerrainType.ForestFloor,
			_                                   => TerrainType.Grass,
		};
	}
}
