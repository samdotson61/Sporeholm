using Godot;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sporeholm.World;

// Node2D that draws the local tile map as a single baked full-resolution texture.
//
// Architecture
// ────────────
// One Rgb8 image at Width*TS × Height*TS pixels — each 16×16 block is one tile.
// Terrain fill is painted first; vegetation shapes are pixel-painted on top, with
// the terrain colour lerped into the shape colour so no alpha channel is needed.
//
// _Draw() emits exactly one DrawTextureRect. No canvas primitives are ever drawn,
// so there are no viewport-edge clipping artefacts on camera move or zoom.
//
// On TerrainChanged or VegetationChanged, only the 16×16 pixel block for that
// tile is repainted (O(256) SetPixel calls), then the full texture is updated.
public partial class LocalMapRenderer : Node2D
{
	public LocalMap? Map { get; private set; }

	private const int TS = LocalMap.TileSize;

	// v0.4.22 — single `Image` + `ImageTexture`. v0.4.21 chunked these into
	// 32×32-tile chunks to reduce per-frame upload bandwidth, but Sam's
	// perf data on Vulkan came back worse — each `_texture.Update()` is a
	// sync point, and 12-150 of them per flush is exactly the pattern
	// Vulkan validation/sync handles poorly. The chunked path also
	// produced visible 1-px seams at chunk boundaries when the camera
	// scrolled with fractional zoom (sub-pixel quad edges on adjacent
	// chunks rendering slightly differently).
	//
	// Single texture wins on both fronts: no seams (it's one quad), and
	// exactly one Update + one sync per flush. The bandwidth concern
	// from before chunking is solved here by *throttling* the Update
	// rate to ~10 Hz regardless of how many tiles dirty since the last
	// flush. Repainting tiles into `_image` is cheap CPU work; the
	// GPU upload is the expensive part, so capping the upload rate
	// caps the bandwidth (max ~110 MB × 10 Hz = ~1.1 GB/sec on the
	// largest map, comfortably under PCIe budget) without losing
	// visual quality — terrain changes are inherently slow-paced and
	// 100 ms of latency between a dig completing and the pixel
	// changing is invisible to the player.
	private Image?        _image;
	private ImageTexture? _texture;

	// v0.4.22 — throttle the GPU upload. Dirty tiles repaint into
	// `_image` every `_Process` (CPU-cheap), but `_texture.Update` only
	// fires when at least `UploadIntervalMs` have elapsed since the
	// last upload. Caps Update-call frequency to a fixed rate, which is
	// the difference between a Vulkan stutter and smooth playback at
	// 250 shroomps.
	//
	// v0.4.56 — bumped 100 → 200 ms (10 Hz → 5 Hz) per Sam's directive
	// that "terrain and vegetation tile updates can be throttled to 200ms
	// as they will not be noticed." The CPU repaint into `_image` still
	// happens on every dirty event, so the in-memory state stays
	// authoritative; only the GPU re-upload waits. Shroomps read tile
	// state from LocalMap directly (not from the rendered texture), so
	// path/task re-evaluation isn't affected by render cadence.
	private const double UploadIntervalMs = 200.0;
	private double       _lastUploadMs;
	private bool         _hasPendingUpload;

	// v0.3.31 — dirty-tile coalescer. Sim thread and main thread both fire
	// `Map.TerrainChanged` / `Map.VegetationChanged` events, which used to
	// each repaint the tile and *immediately* call `_texture.Update(_image)`
	// — a full-map texture re-upload to the GPU costing ~5–50 ms each.
	// During a Gather rush (many Forager harvests landing in the same tick),
	// dozens of those uploads stacked back-to-back and froze the main thread
	// for multiple seconds. Sim ran ahead during the freeze, so by the time
	// the screen unblocked, the player's earlier Excavate designations had
	// all been completed and looked like they'd "disappeared".
	//
	// Now: events only mark the tile dirty. `_Process` (main thread) flushes
	// the dirty set once per frame — one repaint pass + ONE texture upload
	// regardless of how many tiles changed since the last frame. The lock
	// guards the set against concurrent adds from the sim thread.
	private readonly HashSet<(int X, int Y)> _dirty = new();
	private readonly object _dirtyLock = new();

	public override void _Ready()
	{
		TextureFilter = TextureFilterEnum.Nearest;
	}

	// ── Map assignment ─────────────────────────────────────────────────────────

	public void SetMap(LocalMap map)
	{
		if (Map != null)
		{
			Map.TerrainChanged    -= OnTerrainChanged;
			Map.VegetationChanged -= OnVegetationChanged;
		}

		Map = map;
		BakeFullImage();

		Map.TerrainChanged    += OnTerrainChanged;
		Map.VegetationChanged += OnVegetationChanged;

		QueueRedraw();
	}

	// ── Baking ────────────────────────────────────────────────────────────────

	private void BakeFullImage()
	{
		if (Map == null) return;
		_image = Image.CreateEmpty(Map.Width * TS, Map.Height * TS, false, Image.Format.Rgb8);
		// Rows are independent (each PaintTile writes a disjoint 16×16
		// block of the shared image); Parallel.For is race-free as long
		// as Image.SetPixel / Image.FillRect themselves are thread-safe
		// on disjoint pixel ranges, which they are in Godot 4.
		Parallel.For(0, Map.Height, y =>
		{
			for (int x = 0; x < Map.Width; x++)
				PaintTile(x, y);
		});
		_texture = ImageTexture.CreateFromImage(_image);
		_lastUploadMs     = Time.GetTicksMsec();
		_hasPendingUpload = false;
	}

	// ── Event handlers ─────────────────────────────────────────────────────────

	// Events come from the sim thread; we only mutate the dirty set under
	// the lock, no graphics resources touched here. _Process flushes.
	private void OnTerrainChanged(int x, int y, TerrainType _) => MarkDirty(x, y);
	private void OnVegetationChanged(int x, int y)             => MarkDirty(x, y);

	private void MarkDirty(int tx, int ty)
	{
		lock (_dirtyLock) _dirty.Add((tx, ty));
	}

	public override void _Process(double delta)
	{
		// Fast path: nothing dirty AND no pending upload → no work.
		if (_image == null || _texture == null) return;
		bool hasDirty = _dirty.Count > 0;
		if (!hasDirty && !_hasPendingUpload) return;

		// Always repaint accumulated dirty tiles into `_image` — this is
		// CPU-only and cheap. The expensive step is `_texture.Update`
		// (GPU upload), which we throttle below.
		if (hasDirty)
		{
			(int X, int Y)[]? snapshot;
			lock (_dirtyLock)
			{
				if (_dirty.Count == 0) goto AfterRepaint;
				snapshot = new (int, int)[_dirty.Count];
				_dirty.CopyTo(snapshot);
				_dirty.Clear();
			}

			int W = Map?.Width ?? 0, H = Map?.Height ?? 0;
			foreach (var (tx, ty) in snapshot)
			{
				if (tx >= 0 && ty >= 0 && tx < W && ty < H)
					PaintTile(tx, ty);
			}
			_hasPendingUpload = true;
		}
		AfterRepaint:

		// v0.4.22 — throttle GPU upload. The pre-chunking version
		// re-uploaded the full ~110 MB texture every frame whenever
		// anything changed; at 250 shroomps that was 6.6 GB/sec and
		// saturated the GPU pipeline. v0.4.21 chunked it but the
		// per-chunk `Update` calls each became Vulkan sync points,
		// landing Sam at 11 FPS on a 5070 Ti with the GPU at 7 %
		// utilisation. The fix: keep one texture (no chunk seams, one
		// sync per upload) but only call `Update` at ~10 Hz. The CPU
		// can repaint into `_image` as often as it likes; the texture
		// upload paces itself.
		if (_hasPendingUpload)
		{
			double nowMs = Time.GetTicksMsec();
			if (nowMs - _lastUploadMs >= UploadIntervalMs)
			{
				_texture.Update(_image);
				_lastUploadMs     = nowMs;
				_hasPendingUpload = false;
				QueueRedraw();
			}
		}
	}

	// ── Draw ───────────────────────────────────────────────────────────────────

	public override void _Draw()
	{
		if (Map == null || _texture == null)
		{
			int w = (Map?.Width  ?? LocalMap.DefaultWidth)  * TS;
			int h = (Map?.Height ?? LocalMap.DefaultHeight) * TS;
			DrawRect(new Rect2(0, 0, w, h), new Color(0.28f, 0.52f, 0.22f));
			return;
		}
		DrawTextureRect(_texture,
			new Rect2(0, 0, Map.Width * TS, Map.Height * TS), false);
	}

	// ── Cleanup ────────────────────────────────────────────────────────────────

	public override void _ExitTree()
	{
		if (Map == null) return;
		Map.TerrainChanged    -= OnTerrainChanged;
		Map.VegetationChanged -= OnVegetationChanged;
	}

	// ── Tile painting ──────────────────────────────────────────────────────────

	// Repaints the 16×16 pixel block for tile (tx, ty): terrain fill then veg shape.
	private void PaintTile(int tx, int ty)
	{
		if (_image == null) return;
		int ox = tx * TS;
		int oy = ty * TS;

		// Impassable terrain types have their own multi-shape renderers; no vegetation is placed on them.
		var terrainType = Map!.Get(tx, ty).Terrain;
		if (terrainType == TerrainType.DeadLog)   { PaintDeadLog(ox, oy, tx, ty);   return; }
		if (terrainType == TerrainType.LivingWood) { PaintLivingWood(ox, oy, tx, ty); return; }
		if (terrainType == TerrainType.Boulder)    { PaintBoulder(ox, oy, tx, ty);   return; }
		if (terrainType == TerrainType.Skeleton)   { PaintSkeleton(ox, oy, tx, ty);  return; }

		Color terrain = TileColor(terrainType);

		// v0.5.84t — roofed tile tint. RimWorld parity: any tile that was
		// inside a solid mass at worldgen carries IsRoofed=true through
		// mining, producing the "indoor cave" feel after the player digs
		// a tunnel. Multiply RGB by 0.74 and shift slightly toward blue
		// for a subtle "under a ceiling" look. Pre-FillRect so the
		// terrain accents below also pick up the tint.
		// Sam: "establish roof tiles over enclosed areas... naturally spawn
		// (cavern roofs like rimworld) over cave formations and when
		// digging into deadwood or livingwood."
		bool isRoofed = Map.Get(tx, ty).IsRoofed;
		if (isRoofed)
		{
			terrain = new Color(
				terrain.R * 0.74f,
				terrain.G * 0.78f,
				terrain.B * 0.86f,   // less reduction in blue → cool cast
				terrain.A);
		}

		// v0.3.32 — Godot native FillRect replaces a 256-call SetPixel loop.
		// Per-tile terrain fill goes from O(256) C#→C++ interop calls to
		// O(1), which is the bulk of PaintTile's cost. Vegetation overlays
		// below still use the per-pixel helpers (FillCircle for round
		// shapes), but those are bounded by the vegetation footprint
		// inside the tile, not the full 16×16.
		_image!.FillRect(new Rect2I(ox, oy, TS, TS), terrain);

		// v0.5.84e — light terrain texturing. Sam: "Also add light
		// texturing to terrain tiles." 8 accent pixels per tile (4
		// slightly lighter, 4 slightly darker) at positions seeded by
		// (tx, ty) so adjacent tiles use different scatter patterns and
		// the eye doesn't pick up a grid. Cost: 8 SetPixel calls per
		// non-special-painter tile — ~32k interop calls for a full bake
		// on 80×50 maps (~10 ms one-time at load) and 8 calls per dirty-
		// tile repaint (negligible). The base FillRect still carries the
		// terrain identity; accents just break up the flat fill.
		PaintTerrainAccents(ox, oy, tx, ty, terrain);

		var veg = Map.GetVegetation(tx, ty);
		if (!veg.IsPresent) return;

		float a  = veg.IsDepleted ? 0.50f : 1.00f;
		int   cx = ox + TS / 2;
		int   cy = oy + TS / 2;

		// v0.4.15 — once a Large* / Palm shroom has been chopped, the
		// tile reads as a "fungal stump" (RimWorld tree-stump idiom):
		// short stem stub with a slice of the variant's cap colour on
		// top, drawn over a passable terrain background. Distinct
		// silhouette from the live shroom so the player sees at a
		// glance which trees are still standing.
		// v0.4.17 — extended to every harvestable small-vegetation
		// type. Each variant gets a distinct "harvested" silhouette
		// (bare bush, capless stem, twiggy stalk) so the player can
		// distinguish "still has yield" from "harvested / regrowing"
		// across the whole vegetation palette, not just the trees.
		if (veg.IsDepleted)
		{
			switch (veg.Type)
			{
				case VegetationType.LargeMushroom:
					PaintFungalStumpLargeMushroom(ox, oy, cx, terrain);
					return;
				case VegetationType.LargeSandshroom:
					PaintFungalStumpLargeSandshroom(ox, oy, cx, terrain);
					return;
				case VegetationType.PalmShroom:
					PaintFungalStumpPalmShroom(ox, oy, cx, terrain);
					return;
				case VegetationType.CapberryBush:
					PaintDepletedCapberryBush(cx, cy, terrain);
					return;
				case VegetationType.SmallMushroom:
					PaintDepletedSmallMushroom(cx, cy, terrain);
					return;
				case VegetationType.HerbCluster:
					PaintDepletedHerbCluster(cx, cy, terrain);
					return;
				case VegetationType.MagicFlower:
					PaintDepletedMagicFlower(cx, cy, terrain);
					return;
				case VegetationType.SmallSandshroom:
					PaintDepletedSmallSandshroom(cx, cy, terrain);
					return;
				case VegetationType.PineShroom:
					PaintDepletedPineShroom(cx, cy, terrain);
					return;
			}
		}

		switch (veg.Type)
		{
			case VegetationType.Underbrush:
			{
				// Multi-blob leafy bush silhouette
				Color ubD = terrain.Lerp(new Color(0.08f, 0.35f, 0.08f), a);
				Color ubL = terrain.Lerp(new Color(0.18f, 0.52f, 0.12f), a);
				FillCircle(cx,     cy + 1, 4.0f, ubD);
				FillCircle(cx - 3, cy,     2.5f, ubD);
				FillCircle(cx + 3, cy,     2.5f, ubD);
				FillCircle(cx,     cy - 2, 2.5f, ubL);
				FillCircle(cx - 2, cy - 3, 1.5f, ubL);
				FillCircle(cx + 2, cy - 3, 1.5f, ubL);
				break;
			}
			case VegetationType.CapberryBush:
			{
				// Spreading bush — blobs reach tile edges so adjacent tiles merge into one connected bush.
				Color sbD = terrain.Lerp(new Color(0.12f, 0.48f, 0.08f), a);
				Color sbM = terrain.Lerp(new Color(0.20f, 0.64f, 0.14f), a);
				Color sbL = terrain.Lerp(new Color(0.28f, 0.78f, 0.20f), a);
				Color sbR = terrain.Lerp(new Color(0.88f, 0.08f, 0.08f), a);
				FillCircle(cx,     cy + 1, 5.0f, sbD);
				FillCircle(cx - 4, cy,     3.5f, sbD);
				FillCircle(cx + 4, cy - 1, 3.5f, sbD);
				FillCircle(cx,     cy - 4, 3.0f, sbM);
				FillCircle(cx + 1, cy + 4, 3.0f, sbM);
				FillCircle(cx - 1, cy - 2, 2.0f, sbL);
				FillCircle(cx - 2, cy + 1, 1.2f, sbR);
				FillCircle(cx + 2, cy + 2, 1.0f, sbR);
				FillCircle(cx + 1, cy - 2, 0.9f, sbR);
				FillCircle(cx - 3, cy - 1, 0.9f, sbR);
				FillCircle(cx + 3, cy + 1, 0.8f, sbR);
				break;
			}
			case VegetationType.SmallMushroom:
			{
				// 3/4-view mushroom: cream stem, dark gill line, tan rounded cap
				Color smC = terrain.Lerp(new Color(0.82f, 0.70f, 0.45f), a);
				Color smS = terrain.Lerp(new Color(0.90f, 0.86f, 0.72f), a);
				Color smG = terrain.Lerp(new Color(0.52f, 0.38f, 0.22f), a);
				FillRect(cx - 1, cy + 1, 2, 4, smS);
				FillRect(cx - 3, cy,     6, 2, smG);
				FillCircle(cx, cy - 2, 3.5f, smC);
				break;
			}
			case VegetationType.LargeMushroom:
			{
				// Fly Amanita: stem, red cap, white spots painted over existing terrain fill.
				Color mStem = terrain.Lerp(new Color(0.92f, 0.88f, 0.76f), a);
				Color mGill = terrain.Lerp(new Color(0.80f, 0.72f, 0.62f), a);
				Color mCap  = terrain.Lerp(new Color(0.85f, 0.12f, 0.08f), a);
				Color mSpot = terrain.Lerp(Colors.White, a);
				FillRect(cx - 2,   oy + 10, 4,  6,  mStem);
				FillRect(cx - 5,   oy + 11, 10, 1,  mGill);
				FillCircle(cx,     oy + 6,  6.0f,   mCap);
				FillCircle(cx - 2, oy + 3,  1.5f,   mSpot);
				FillCircle(cx + 2, oy + 4,  1.3f,   mSpot);
				FillCircle(cx,     oy + 7,  1.0f,   mSpot);
				break;
			}
			case VegetationType.HerbCluster:
			{
				// Paired herb stems, leaf sprays, and magenta flower cluster
				Color hcS = terrain.Lerp(new Color(0.18f, 0.60f, 0.12f), a);
				Color hcL = terrain.Lerp(new Color(0.24f, 0.72f, 0.18f), a);
				Color hcF = terrain.Lerp(new Color(0.85f, 0.15f, 0.85f), a);
				FillRect(cx - 1, cy,     1, 5, hcS);
				FillRect(cx + 1, cy,     1, 5, hcS);
				FillRect(cx - 4, cy + 1, 4, 2, hcL);
				FillRect(cx + 1, cy + 1, 4, 2, hcL);
				FillRect(cx - 3, cy - 1, 3, 2, hcL);
				FillRect(cx + 1, cy - 1, 3, 2, hcL);
				FillCircle(cx,     cy - 3, 2.0f, hcF);
				FillCircle(cx - 2, cy - 2, 1.2f, hcF);
				FillCircle(cx + 2, cy - 2, 1.2f, hcF);
				break;
			}
			case VegetationType.MagicFlower:
			{
				// Eight-petal radial flower with golden center
				Color mfP1 = terrain.Lerp(new Color(0.78f, 0.15f, 0.88f), a);
				Color mfP2 = terrain.Lerp(new Color(0.52f, 0.10f, 0.82f), a);
				Color mfCn = terrain.Lerp(new Color(1.00f, 0.92f, 0.30f), a);
				FillCircle(cx,     cy - 4, 2.5f, mfP1);
				FillCircle(cx,     cy + 4, 2.5f, mfP1);
				FillCircle(cx - 4, cy,     2.5f, mfP2);
				FillCircle(cx + 4, cy,     2.5f, mfP2);
				FillCircle(cx - 3, cy - 3, 1.8f, mfP1);
				FillCircle(cx + 3, cy - 3, 1.8f, mfP1);
				FillCircle(cx - 3, cy + 3, 1.8f, mfP2);
				FillCircle(cx + 3, cy + 3, 1.8f, mfP2);
				FillCircle(cx,     cy,     2.5f, mfCn);
				break;
			}
			case VegetationType.SmallSandshroom:
			{
				// Flat-domed arid mushroom: amber stem, sandy granular cap, deep under-brim shadow.
				Color ssS  = terrain.Lerp(new Color(0.68f, 0.50f, 0.24f), a);  // amber stem
				Color ssC  = terrain.Lerp(new Color(0.80f, 0.68f, 0.40f), a);  // sandy cap body
				Color ssL  = terrain.Lerp(new Color(0.91f, 0.81f, 0.56f), a);  // bright sandy highlight
				Color ssD  = terrain.Lerp(new Color(0.58f, 0.44f, 0.22f), a);  // cap edge / darker sand
				Color ssSh = terrain.Lerp(new Color(0.32f, 0.22f, 0.08f), a);  // under-brim shadow
				FillRect(cx - 1, cy + 2,  2, 4, ssS);
				FillRect(cx - 4, cy + 1,  8, 1, ssSh);
				FillRect(cx - 4, cy,      8, 1, ssD);
				FillCircle(cx,     cy - 2, 3.5f, ssC);
				FillCircle(cx,     cy - 3, 2.0f, ssL);
				break;
			}
			case VegetationType.LargeSandshroom:
			{
				// Large flat-domed arid mushroom filling the tile; impassable.
				// Broad sandy cap with grain speckling, thick amber stem, deep shadow brim.
				Color lsS  = terrain.Lerp(new Color(0.66f, 0.48f, 0.22f), a);  // amber stem
				Color lsC  = terrain.Lerp(new Color(0.80f, 0.67f, 0.40f), a);  // sandy cap body
				Color lsL  = terrain.Lerp(new Color(0.93f, 0.83f, 0.58f), a);  // bright cap highlight
				Color lsD  = terrain.Lerp(new Color(0.57f, 0.43f, 0.20f), a);  // cap outer edge
				Color lsGr = terrain.Lerp(new Color(0.68f, 0.54f, 0.30f), a);  // mid-tone grain speckle
				Color lsSh = terrain.Lerp(new Color(0.26f, 0.16f, 0.04f), a);  // deep under-brim shadow
				FillRect(cx - 2,   oy + 10, 4,  6,  lsS);
				FillRect(cx - 6,   oy + 10, 12, 1,  lsSh);
				FillCircle(cx,     oy + 6,  6.5f,   lsD);
				FillCircle(cx,     oy + 5,  5.5f,   lsC);
				FillCircle(cx,     oy + 4,  3.0f,   lsL);
				FillCircle(cx - 3, oy + 5,  0.9f,   lsGr);
				FillCircle(cx + 3, oy + 6,  0.9f,   lsGr);
				FillCircle(cx,     oy + 7,  0.9f,   lsGr);
				FillCircle(cx + 2, oy + 4,  0.9f,   lsGr);
				FillCircle(cx - 2, oy + 3,  0.9f,   lsGr);
				break;
			}
			case VegetationType.PalmShroom:
			{
				// Tropical palm-mushroom: golden stem, wide drooping green cap; impassable.
				Color psS  = terrain.Lerp(new Color(0.72f, 0.56f, 0.22f), a);  // amber-gold stem
				Color psC  = terrain.Lerp(new Color(0.28f, 0.58f, 0.20f), a);  // tropical green cap
				Color psL  = terrain.Lerp(new Color(0.42f, 0.72f, 0.28f), a);  // bright cap highlight
				Color psSh = terrain.Lerp(new Color(0.10f, 0.28f, 0.08f), a);  // shadow under brim
				Color psFr = terrain.Lerp(new Color(0.36f, 0.66f, 0.24f), a);  // drooping frond accent
				FillRect(cx - 1, oy + 5, 2, 7, psS);
				FillRect(cx - 6, cy - 1, 12, 2, psSh);
				FillCircle(cx, cy - 3, 5.5f, psC);
				FillCircle(cx, cy - 4, 3.0f, psL);
				FillCircle(cx - 5, cy - 1, 1.5f, psFr);
				FillCircle(cx + 5, cy - 1, 1.5f, psFr);
				FillCircle(cx - 4, cy - 5, 1.2f, psFr);
				FillCircle(cx + 4, cy - 5, 1.2f, psFr);
				break;
			}
			case VegetationType.PineShroom:
			{
				// Pine/conifer mushroom pair: two stacked cones, dark coastal forest green.
				Color piSt = terrain.Lerp(new Color(0.36f, 0.24f, 0.10f), a);  // brown stem
				Color piD  = terrain.Lerp(new Color(0.18f, 0.42f, 0.14f), a);  // dark pine green
				Color piM  = terrain.Lerp(new Color(0.26f, 0.54f, 0.20f), a);  // mid green
				Color piL  = terrain.Lerp(new Color(0.36f, 0.66f, 0.28f), a);  // bright needle tip
				FillRect(cx - 4, cy + 1, 2, 4, piSt);
				FillCircle(cx - 3, cy + 1, 2.5f, piD);
				FillCircle(cx - 3, cy - 1, 2.0f, piM);
				FillCircle(cx - 3, cy - 3, 1.2f, piL);
				FillRect(cx + 2, cy + 2, 2, 3, piSt);
				FillCircle(cx + 3, cy + 1, 2.0f, piD);
				FillCircle(cx + 3, cy - 1, 1.8f, piM);
				FillCircle(cx + 3, cy - 3, 1.2f, piL);
				FillCircle(cx + 3, cy - 5, 0.9f, piL);
				break;
			}
			case VegetationType.MossPatch:
			{
				// Irregular overlapping organic blobs in teal-green
				Color mp1 = terrain.Lerp(new Color(0.22f, 0.60f, 0.48f), 0.45f * a);
				Color mp2 = terrain.Lerp(new Color(0.18f, 0.50f, 0.38f), 0.55f * a);
				Color mp3 = terrain.Lerp(new Color(0.28f, 0.70f, 0.55f), 0.35f * a);
				FillCircle(cx - 2, cy + 1, 3.5f, mp1);
				FillCircle(cx + 2, cy - 1, 3.0f, mp2);
				FillCircle(cx - 1, cy - 2, 2.5f, mp3);
				FillCircle(cx + 1, cy + 2, 2.0f, mp1);
				break;
			}
		}
	}

	// v0.4.15 — Fungal stump variants for chopped Large* / Palm shrooms.
	// Each variant keeps its cap colour as a thin rim on top of a short
	// stem stub, so the player can identify which species was felled.
	// Drawn over the existing passable terrain fill (the depleted tile
	// is already walkable, so the stump sits on grass / mud / sand /
	// forest-floor instead of replacing the tile background).
	private void PaintFungalStumpLargeMushroom(int ox, int oy, int cx, Color terrain)
	{
		Color stemD = terrain.Lerp(new Color(0.62f, 0.58f, 0.46f), 0.85f);   // weathered stem base
		Color stemL = terrain.Lerp(new Color(0.82f, 0.78f, 0.62f), 0.85f);   // bleached stem highlight
		Color rim   = terrain.Lerp(new Color(0.62f, 0.16f, 0.12f), 0.85f);   // remnant Fly Amanita rim
		Color core  = terrain.Lerp(new Color(0.36f, 0.30f, 0.22f), 0.85f);   // chopped core dark
		// Short trunk stub centred in the tile.
		FillRect(cx - 2, oy + 11, 4, 4, stemD);
		FillRect(cx - 2, oy + 11, 1, 4, stemL);
		// Chopped-flat top with a remnant red rim — the cap is gone but
		// the outer ring of the cap base still sits on the stump.
		FillRect(cx - 3, oy + 10, 6, 1, core);
		FillRect(cx - 3, oy + 10, 1, 1, rim);
		FillRect(cx + 2, oy + 10, 1, 1, rim);
		FillCircle(cx, oy + 11, 0.6f, rim);
	}

	private void PaintFungalStumpLargeSandshroom(int ox, int oy, int cx, Color terrain)
	{
		Color stemD = terrain.Lerp(new Color(0.46f, 0.34f, 0.16f), 0.85f);   // dark amber stem
		Color stemL = terrain.Lerp(new Color(0.74f, 0.58f, 0.30f), 0.85f);   // amber highlight
		Color rim   = terrain.Lerp(new Color(0.78f, 0.66f, 0.40f), 0.85f);   // sandy cap remnant
		Color core  = terrain.Lerp(new Color(0.32f, 0.22f, 0.10f), 0.85f);
		FillRect(cx - 2, oy + 11, 4, 4, stemD);
		FillRect(cx - 2, oy + 11, 1, 4, stemL);
		FillRect(cx - 3, oy + 10, 6, 1, core);
		FillRect(cx - 3, oy + 10, 1, 1, rim);
		FillRect(cx + 2, oy + 10, 1, 1, rim);
		// Wider rim line — sandshroom caps were broader so the remnant
		// ring extends a pixel further on each side than the LargeMushroom.
		_image!.SetPixel(cx - 3, oy + 11, rim);
		_image!.SetPixel(cx + 3, oy + 11, rim);
	}

	private void PaintFungalStumpPalmShroom(int ox, int oy, int cx, Color terrain)
	{
		Color stemD = terrain.Lerp(new Color(0.52f, 0.40f, 0.16f), 0.85f);   // amber-gold dark
		Color stemL = terrain.Lerp(new Color(0.78f, 0.62f, 0.28f), 0.85f);   // gold highlight
		Color rim   = terrain.Lerp(new Color(0.26f, 0.52f, 0.18f), 0.85f);   // green cap remnant
		Color core  = terrain.Lerp(new Color(0.18f, 0.32f, 0.10f), 0.85f);
		FillRect(cx - 2, oy + 11, 4, 4, stemD);
		FillRect(cx - 2, oy + 11, 1, 4, stemL);
		FillRect(cx - 3, oy + 10, 6, 1, core);
		FillRect(cx - 3, oy + 10, 1, 1, rim);
		FillRect(cx + 2, oy + 10, 1, 1, rim);
		// Palm caps drape downward, so leave a small green tuft on each
		// shoulder of the stump rather than a wider rim.
		_image!.SetPixel(cx - 3, oy + 11, rim);
		_image!.SetPixel(cx + 3, oy + 11, rim);
	}

	// v0.4.17 — depleted-state paint routines for small / harvestable
	// vegetation. Each variant draws a "harvested" silhouette over the
	// underlying passable terrain so the player reads at a glance which
	// tiles have been picked-over and which still hold yield.

	private void PaintDepletedCapberryBush(int cx, int cy, Color terrain)
	{
		// Bare bush: the green leafy blobs without the red berry dots.
		// Dim the foliage roughly 50 % toward terrain so a freshly-
		// picked bush reads as muted rather than dead.
		Color leafD = terrain.Lerp(new Color(0.12f, 0.48f, 0.08f), 0.50f);
		Color leafL = terrain.Lerp(new Color(0.20f, 0.64f, 0.14f), 0.50f);
		FillCircle(cx,     cy + 1, 3.5f, leafD);
		FillCircle(cx - 3, cy,     2.5f, leafD);
		FillCircle(cx + 3, cy - 1, 2.5f, leafD);
		FillCircle(cx,     cy - 3, 2.0f, leafL);
	}

	private void PaintDepletedSmallMushroom(int cx, int cy, Color terrain)
	{
		// Capless stem: a cream stalk with a darker stub where the cap
		// was cut off. No tan cap dome.
		Color stem = terrain.Lerp(new Color(0.90f, 0.86f, 0.72f), 0.85f);
		Color cut  = terrain.Lerp(new Color(0.55f, 0.45f, 0.28f), 0.85f);
		FillRect(cx - 1, cy,     2, 4, stem);
		FillRect(cx - 2, cy - 1, 4, 1, cut);
	}

	private void PaintDepletedHerbCluster(int cx, int cy, Color terrain)
	{
		// Cut herb base: two short stalk stubs without the leafy spray
		// or the magenta flower cluster.
		Color stem = terrain.Lerp(new Color(0.18f, 0.50f, 0.10f), 0.75f);
		Color base_ = terrain.Lerp(new Color(0.32f, 0.22f, 0.10f), 0.65f);
		FillRect(cx - 2, cy + 2, 1, 3, stem);
		FillRect(cx + 1, cy + 2, 1, 3, stem);
		FillRect(cx - 3, cy + 4, 6, 1, base_);
	}

	private void PaintDepletedMagicFlower(int cx, int cy, Color terrain)
	{
		// Cut flower: just the stalk and a small dark seed-pod where
		// the radial petals used to bloom.
		Color stem = terrain.Lerp(new Color(0.18f, 0.45f, 0.10f), 0.75f);
		Color pod  = terrain.Lerp(new Color(0.42f, 0.10f, 0.55f), 0.75f);
		FillRect(cx, cy, 1, 5, stem);
		FillCircle(cx, cy - 1, 1.2f, pod);
	}

	private void PaintDepletedSmallSandshroom(int cx, int cy, Color terrain)
	{
		// Short amber stub with no sandy cap.
		Color stem = terrain.Lerp(new Color(0.66f, 0.48f, 0.22f), 0.80f);
		Color cut  = terrain.Lerp(new Color(0.42f, 0.30f, 0.12f), 0.80f);
		FillRect(cx - 1, cy + 2, 2, 3, stem);
		FillRect(cx - 2, cy + 1, 4, 1, cut);
	}

	private void PaintDepletedPineShroom(int cx, int cy, Color terrain)
	{
		// Two short brown stem stubs (the conifer-cone caps gone).
		Color stem = terrain.Lerp(new Color(0.36f, 0.24f, 0.10f), 0.85f);
		Color cut  = terrain.Lerp(new Color(0.20f, 0.14f, 0.06f), 0.85f);
		FillRect(cx - 4, cy + 2, 2, 3, stem);
		FillRect(cx + 2, cy + 2, 2, 3, stem);
		FillRect(cx - 4, cy + 1, 2, 1, cut);
		FillRect(cx + 2, cy + 1, 2, 1, cut);
	}

	// Dead log tile: weathered silver-grey dead wood — full-tile fill so large formations
	// read as solid blocks at distance. Palette references aged fallen logs and stumps
	// (silver surface, dark longitudinal cracks, warm heartwood exposed at splits).
	private void PaintDeadLog(int ox, int oy, int tx, int ty)
	{
		Color surf  = new Color(0.56f, 0.54f, 0.48f);   // weathered silver-grey (dominant base)
		Color light = new Color(0.72f, 0.70f, 0.63f);   // bleached grain highlight
		Color crack = new Color(0.18f, 0.15f, 0.12f);   // dark longitudinal crack
		Color warm  = new Color(0.46f, 0.38f, 0.24f);   // exposed inner heartwood

		// v0.4.16 — single FillRect base fill (was a 256-call SetPixel
		// nested loop; one C++ call instead of 256 interop crossings).
		_image!.FillRect(new Rect2I(ox, oy, TS, TS), surf);

		int cx = ox + TS / 2;
		int cy = oy + TS / 2;
		int variant = (tx * 1723 + ty * 3691) % 3;

		if (variant == 0)
		{
			// End-grain (stump top): concentric rect rings + radial cracks.
			FillRect(ox + 1, oy + 1, 14, 14, light);
			FillRect(ox + 3, oy + 3, 10, 10, warm);
			FillRect(ox + 5, oy + 5,  6,  6, light);
			FillRect(ox + 7, oy + 7,  2,  2, warm);
			FillRect(cx,     oy + 1,  1,  6, crack);
			FillRect(cx,     oy + 9,  1,  6, crack);
			FillRect(ox + 1, cy,      6,  1, crack);
			FillRect(ox + 9, cy,      6,  1, crack);
		}
		else if (variant == 1)
		{
			// Side grain — bleached light bands + full-height longitudinal cracks.
			FillRect(ox, oy + 2,  16, 2, light);
			FillRect(ox, oy + 6,  16, 1, warm);
			FillRect(ox, oy + 9,  16, 2, light);
			FillRect(ox, oy + 12, 16, 1, warm);
			FillRect(ox + 3,  oy, 1, 16, crack);
			FillRect(ox + 9,  oy, 1, 16, crack);
			FillRect(ox + 13, oy, 1, 16, crack);
		}
		else
		{
			// Rough split bark — offset grain bands + asymmetric cracks.
			FillRect(ox,     oy + 1,  16, 2, light);
			FillRect(ox,     oy + 6,  12, 1, warm);
			FillRect(ox + 4, oy + 6,  12, 1, warm);
			FillRect(ox,     oy + 10, 16, 2, light);
			FillRect(ox,     oy + 14,  8, 1, warm);
			FillRect(ox + 8, oy + 13,  8, 1, warm);
			FillRect(ox + 5,  oy,     1, 6, crack);
			FillRect(ox + 5,  oy + 6, 1, 5, crack);
			FillRect(ox + 11, oy + 4, 1, 8, crack);
			FillRect(ox + 2,  oy + 9, 1, 7, crack);
		}
	}

	// Boulder tile: angular faceted rock — geometric planes, sharp cracks, and deep shadows
	// so boulders read as hard stone rather than smooth terrain. Three deterministic variants.
	// v0.4.2 — palette now varies per stone subtype (Granite / Limestone / Marble /
	// Obsidian / Quartz / Magicstone / MagicCrystal) using the tile's
	// assigned material from `LocalMap.GetTileStone(tx, ty)`.
	// MagicCrystal gets a special ore-vein treatment (a separate
	// PaintMagicCrystal path) because the visual language is veins
	// running through host rock rather than a uniform stone fill.
	// v0.5.84e — light terrain texturing helper. Sprinkles 8 single-pixel
	// accents per tile (mix of slightly-lighter and slightly-darker than
	// the base terrain colour). Positions are seeded per-(tx, ty) using
	// an LCG so adjacent tiles get distinct scatter patterns — no visible
	// repeating grid. Channel adjustments are clamped to [0, 1] for safety
	// near pure-black or pure-white base colours.
	private void PaintTerrainAccents(int ox, int oy, int tx, int ty, Color baseCol)
	{
		var lighter = new Color(
			System.Math.Min(1f, baseCol.R + 0.06f),
			System.Math.Min(1f, baseCol.G + 0.06f),
			System.Math.Min(1f, baseCol.B + 0.06f), 1f);
		var darker = new Color(
			System.Math.Max(0f, baseCol.R - 0.06f),
			System.Math.Max(0f, baseCol.G - 0.06f),
			System.Math.Max(0f, baseCol.B - 0.06f), 1f);
		uint s = (uint)((tx * 73856093) ^ (ty * 19349663));
		for (int i = 0; i < 8; i++)
		{
			s = s * 1664525u + 1013904223u;
			int dx = (int)((s >> 8)  & 15);
			int dy = (int)((s >> 16) & 15);
			_image!.SetPixel(ox + dx, oy + dy, (s & 1u) == 0 ? lighter : darker);
		}
	}

	private void PaintBoulder(int ox, int oy, int tx, int ty)
	{
		var stone = Map?.GetTileStone(tx, ty);
		string sub = stone?.SubType ?? "Granite";

		if (sub == "MagicCrystal")
		{
			PaintMagicCrystalOre(ox, oy, tx, ty);
			return;
		}

		var (base_, light, shad, crack) = StonePalette(sub);

		// v0.4.16 — single FillRect base fill (was a 256-call SetPixel
		// nested loop).
		_image!.FillRect(new Rect2I(ox, oy, TS, TS), base_);

		int variant = (tx * 1723 + ty * 3691) % 3;

		if (variant == 0)
		{
			// Flat slab: one large lit face, shadow band below, angular cracks.
			FillRect(ox + 1, oy + 1,  10, 6, light);
			FillRect(ox + 1, oy + 9,  14, 6, shad);
			FillRect(ox + 6, oy,       1, 8, crack);
			FillRect(ox,     oy + 8,  16, 1, crack);
			FillRect(ox + 11, oy + 1,  1, 7, crack);
			FillRect(ox + 3,  oy + 9,  1, 7, crack);
		}
		else if (variant == 1)
		{
			// Fractured surface: four rock fragments divided by crossing joints.
			FillRect(ox + 1, oy + 1,  6, 5, light);
			FillRect(ox + 9, oy + 1,  5, 5, shad);
			FillRect(ox + 1, oy + 8,  5, 6, shad);
			FillRect(ox + 8, oy + 8,  6, 6, light);
			FillRect(ox + 8, oy,       1, 16, crack);
			FillRect(ox,     oy + 7,  16,  1, crack);
			FillRect(ox + 4, oy + 1,   1,  6, crack);
			FillRect(ox + 12, oy + 8,  1,  7, crack);
		}
		else
		{
			// Rough boulder: large asymmetric highlight, deep shadow corner, diagonal crack.
			FillRect(ox + 1, oy + 1,  9, 7, light);
			FillRect(ox + 1, oy + 9,  6, 6, shad);
			FillRect(ox + 9, oy + 6,  6, 9, shad);
			FillRect(ox + 3, oy + 7,  1, 8, crack);
			FillRect(ox + 10, oy + 1, 1, 5, crack);
			FillRect(ox + 1, oy + 13, 8, 1, crack);
			FillRect(ox + 4, oy + 7,  6, 1, crack);
		}
	}

	// v0.4.2 — palette per stone subtype for PaintBoulder. Tuned by hand
	// so each variety reads distinctly at 1× zoom: Granite is the v0.3.x
	// blue-grey baseline; Limestone leans warm cream; Marble adds a near-
	// white highlight + grey shadow; Obsidian is near-black with a deep
	// blue highlight; Quartz is bright with a white core; Magicstone has
	// a violet tint. Returns (base, light, shadow, crack) in that order.
	private static (Color Base, Color Light, Color Shad, Color Crack) StonePalette(string subtype) => subtype switch
	{
		"Granite"    => (new(0.52f, 0.54f, 0.60f), new(0.70f, 0.72f, 0.78f), new(0.38f, 0.40f, 0.46f), new(0.22f, 0.22f, 0.26f)),
		"Limestone"  => (new(0.78f, 0.74f, 0.62f), new(0.92f, 0.88f, 0.76f), new(0.58f, 0.54f, 0.42f), new(0.36f, 0.32f, 0.22f)),
		"Marble"     => (new(0.88f, 0.86f, 0.82f), new(0.98f, 0.96f, 0.94f), new(0.62f, 0.60f, 0.58f), new(0.32f, 0.30f, 0.30f)),
		"Obsidian"   => (new(0.16f, 0.14f, 0.18f), new(0.30f, 0.28f, 0.40f), new(0.06f, 0.05f, 0.08f), new(0.02f, 0.02f, 0.04f)),
		"Quartz"     => (new(0.78f, 0.82f, 0.88f), new(0.96f, 0.98f, 1.00f), new(0.58f, 0.62f, 0.70f), new(0.34f, 0.40f, 0.50f)),
		"Magicstone" => (new(0.46f, 0.38f, 0.62f), new(0.66f, 0.58f, 0.86f), new(0.28f, 0.22f, 0.42f), new(0.18f, 0.10f, 0.30f)),
		_            => (new(0.52f, 0.54f, 0.60f), new(0.70f, 0.72f, 0.78f), new(0.38f, 0.40f, 0.46f), new(0.22f, 0.22f, 0.26f)),
	};

	// v0.5.84t — partial buried skeleton. Bone fragments poking out of an
	// earthy ground patch — visually reads as "old bones half-buried in dirt"
	// at Shroomp scale. Three variants seeded by tile position so adjacent
	// fragments in a cluster don't all look identical:
	//   0 — rib bone: long horizontal off-white bar with two notch shadows
	//   1 — skull   : rounded near-circular bone fragment with two dark socket pixels
	//   2 — pelvis  : asymmetric chunk with a central hole
	// Excavate-drops-Bone hook lives in BehaviorSystem.cs:3769 (TerrainType.Skeleton
	// → ItemKind.Material/BoneFragment, yield 3).
	private void PaintSkeleton(int ox, int oy, int tx, int ty)
	{
		Color ground   = new Color(0.36f, 0.28f, 0.18f);   // damp earth
		Color groundLt = new Color(0.46f, 0.36f, 0.22f);   // disturbed earth highlight
		Color bone     = new Color(0.92f, 0.88f, 0.76f);   // cream bone
		Color boneLt   = new Color(1.00f, 0.96f, 0.84f);   // sunlit edge
		Color boneShad = new Color(0.62f, 0.56f, 0.44f);   // bone shadow / socket
		Color dirt     = new Color(0.24f, 0.18f, 0.12f);   // deep shadow under bone

		// Base disturbed-earth patch.
		_image!.FillRect(new Rect2I(ox, oy, TS, TS), ground);
		// Scattered lighter clods around the edges.
		FillRect(ox + 1,  oy + 1,  2, 1, groundLt);
		FillRect(ox + 12, oy + 2,  2, 1, groundLt);
		FillRect(ox + 2,  oy + 13, 3, 1, groundLt);
		FillRect(ox + 11, oy + 14, 2, 1, groundLt);

		int variant = (tx * 1499 + ty * 2347) % 3;
		if (variant == 0)
		{
			// Rib bone: 10×3 bone bar with shadow line + two notch cracks.
			FillRect(ox + 3, oy + 8,  10, 3, bone);
			FillRect(ox + 3, oy + 11, 10, 1, boneShad);
			FillRect(ox + 6, oy + 8,   1, 3, dirt);
			FillRect(ox + 10, oy + 8,  1, 3, dirt);
			FillRect(ox + 3, oy + 8,  10, 1, boneLt);
		}
		else if (variant == 1)
		{
			// Skull fragment: rounded bone blob with two eye sockets.
			FillRect(ox + 4, oy + 4,  8, 7, bone);
			FillRect(ox + 4, oy + 11, 8, 1, boneShad);
			FillRect(ox + 5, oy + 3,  6, 1, bone);
			FillRect(ox + 3, oy + 5,  1, 5, bone);
			FillRect(ox + 12, oy + 5, 1, 5, bone);
			// Eye sockets.
			FillRect(ox + 5,  oy + 6, 2, 2, dirt);
			FillRect(ox + 9,  oy + 6, 2, 2, dirt);
			// Sunlit top edge.
			FillRect(ox + 5, oy + 4,  6, 1, boneLt);
		}
		else
		{
			// Pelvis chunk: asymmetric bone fragment with central hole.
			FillRect(ox + 2,  oy + 5,  12, 6, bone);
			FillRect(ox + 2,  oy + 11, 12, 1, boneShad);
			FillRect(ox + 6,  oy + 7,   4, 2, dirt);     // central hole
			FillRect(ox + 1,  oy + 7,   1, 3, bone);     // left wing
			FillRect(ox + 14, oy + 7,   1, 3, bone);     // right wing
			FillRect(ox + 2,  oy + 5,  12, 1, boneLt);   // sunlit top
		}
	}

	// v0.4.2 — MagicCrystal ore vein. Host rock (granite-grey) with
	// bright violet-cyan crystal facets running through. Dwarf-Fortress
	// "gem cluster" pattern: a few large facets oriented along the vein
	// axis, with sparkling pixels at the points.
	private void PaintMagicCrystalOre(int ox, int oy, int tx, int ty)
	{
		Color hostBase  = new Color(0.40f, 0.42f, 0.48f);   // dark granite host
		Color hostLight = new Color(0.55f, 0.58f, 0.65f);
		Color crystalA  = new Color(0.50f, 0.85f, 1.00f);   // bright cyan facet
		Color crystalB  = new Color(0.80f, 0.55f, 1.00f);   // bright violet facet
		Color crystalH  = new Color(1.00f, 0.95f, 1.00f);   // near-white facet edge
		Color crack     = new Color(0.18f, 0.20f, 0.28f);

		// v0.4.16 — single FillRect base fill.
		_image!.FillRect(new Rect2I(ox, oy, TS, TS), hostBase);

		int variant = (tx * 1723 + ty * 3691) % 3;
		if (variant == 0)
		{
			// Diagonal vein TL→BR with a large crystal cluster in middle.
			FillRect(ox + 1, oy + 2, 6, 2, hostLight);
			FillRect(ox + 9, oy + 11, 6, 2, hostLight);
			FillRect(ox + 4, oy + 5, 2, 2, crystalA);
			FillRect(ox + 6, oy + 6, 3, 3, crystalB);
			FillRect(ox + 9, oy + 8, 2, 2, crystalA);
			FillRect(ox + 7, oy + 7, 1, 1, crystalH);
			FillRect(ox + 5, oy + 6, 1, 1, crystalH);
			FillRect(ox + 10, oy + 8, 1, 1, crystalH);
			FillRect(ox + 0, oy + 0, 1, 16, crack);
		}
		else if (variant == 1)
		{
			// Vertical vein with a pocket cluster lower-right.
			FillRect(ox + 6, oy + 0, 2, 16, crystalA);
			FillRect(ox + 7, oy + 0, 1, 16, crystalH);
			FillRect(ox + 9, oy + 9, 4, 4, crystalB);
			FillRect(ox + 10, oy + 10, 2, 2, crystalH);
			FillRect(ox + 13, oy + 12, 1, 1, crystalA);
			FillRect(ox + 1, oy + 12, 4, 2, hostLight);
		}
		else
		{
			// Scattered facet pattern — geode-like, multiple small crystals.
			FillRect(ox + 2, oy + 3, 2, 2, crystalA);
			FillRect(ox + 5, oy + 2, 1, 1, crystalH);
			FillRect(ox + 8, oy + 4, 3, 2, crystalB);
			FillRect(ox + 9, oy + 5, 1, 1, crystalH);
			FillRect(ox + 11, oy + 9, 2, 3, crystalA);
			FillRect(ox + 4, oy + 10, 3, 3, crystalB);
			FillRect(ox + 5, oy + 11, 1, 1, crystalH);
			FillRect(ox + 12, oy + 10, 1, 1, crystalH);
			FillRect(ox + 0, oy + 7, 16, 1, crack);
		}
	}

	// Living wood tile: warm brown fresh stumps/logs — same variant patterns as DeadLog
	// so large formations of either type read as coherent blocks at distance.
	// Palette: deep outer bark (base fill), mid grain, lighter highlight, shadow crack.
	private void PaintLivingWood(int ox, int oy, int tx, int ty)
	{
		Color bark  = new Color(0.28f, 0.17f, 0.08f);   // deep outer bark (dominant base)
		Color grain = new Color(0.38f, 0.24f, 0.12f);   // mid grain
		Color light = new Color(0.50f, 0.34f, 0.16f);   // lighter highlight
		Color dark  = new Color(0.19f, 0.11f, 0.05f);   // shadow/crack

		// v0.4.16 — single FillRect base fill.
		_image!.FillRect(new Rect2I(ox, oy, TS, TS), bark);

		int cx = ox + TS / 2;
		int cy = oy + TS / 2;
		int variant = (tx * 1723 + ty * 3691) % 3;

		if (variant == 0)
		{
			// End-grain (stump top): concentric rect rings + radial cracks.
			FillRect(ox + 1, oy + 1, 14, 14, grain);
			FillRect(ox + 3, oy + 3, 10, 10, light);
			FillRect(ox + 5, oy + 5,  6,  6, grain);
			FillRect(ox + 7, oy + 7,  2,  2, light);
			FillRect(cx,     oy + 1,  1,  6, dark);
			FillRect(cx,     oy + 9,  1,  6, dark);
			FillRect(ox + 1, cy,      6,  1, dark);
			FillRect(ox + 9, cy,      6,  1, dark);
		}
		else if (variant == 1)
		{
			// Side grain — lighter bands + full-height longitudinal cracks.
			FillRect(ox, oy + 2,  16, 2, grain);
			FillRect(ox, oy + 6,  16, 1, light);
			FillRect(ox, oy + 9,  16, 2, grain);
			FillRect(ox, oy + 12, 16, 1, light);
			FillRect(ox + 3,  oy, 1, 16, dark);
			FillRect(ox + 9,  oy, 1, 16, dark);
			FillRect(ox + 13, oy, 1, 16, dark);
		}
		else
		{
			// Rough split bark — offset grain bands + asymmetric cracks.
			FillRect(ox,     oy + 1,  16, 2, grain);
			FillRect(ox,     oy + 6,  12, 1, light);
			FillRect(ox + 4, oy + 6,  12, 1, light);
			FillRect(ox,     oy + 10, 16, 2, grain);
			FillRect(ox,     oy + 14,  8, 1, light);
			FillRect(ox + 8, oy + 13,  8, 1, light);
			FillRect(ox + 5,  oy,     1, 6, dark);
			FillRect(ox + 5,  oy + 6, 1, 5, dark);
			FillRect(ox + 11, oy + 4, 1, 8, dark);
			FillRect(ox + 2,  oy + 9, 1, 7, dark);
		}
	}

	private void FillRect(int x, int y, int w, int h, Color col)
	{
		// v0.3.32 — Godot native FillRect; clamps to image bounds inside the
		// C++ implementation, so we just pass the clipped rect.
		int xLo = Math.Max(0, x);
		int yLo = Math.Max(0, y);
		int xHi = Math.Min(x + w, _image!.GetWidth());
		int yHi = Math.Min(y + h, _image!.GetHeight());
		if (xHi <= xLo || yHi <= yLo) return;
		_image!.FillRect(new Rect2I(xLo, yLo, xHi - xLo, yHi - yLo), col);
	}

	// v0.4.16 — scanline-based fill replaces the per-pixel SetPixel loop.
	// The previous version made up to π·r² SetPixel calls per circle,
	// and each call crosses the C#↔C++ managed boundary. With ~5
	// FillCircles per LargeMushroom repaint × 17 shroomps chopping per
	// second the interop transitions dominated PaintTile's runtime and
	// produced the microstutter on vegetation state changes. One row
	// of the disk = one FillRect call instead of `2r` SetPixels, so the
	// interop count drops from O(r²) to O(r). For an r=6 cap that's
	// 13 FillRects in place of ~113 SetPixels — roughly an order of
	// magnitude fewer crossings, well below the per-frame jank floor.
	private void FillCircle(int cx, int cy, float r, Color col)
	{
		int ir = (int)Math.Ceiling(r);
		int w  = _image!.GetWidth();
		int h  = _image!.GetHeight();
		float r2 = r * r;
		for (int dy = -ir; dy <= ir; dy++)
		{
			int py = cy + dy;
			if (py < 0 || py >= h) continue;
			// Half-width of the disk row at this y: sqrt(r² − dy²).
			float dy2 = dy * dy;
			if (dy2 > r2) continue;
			int halfW = (int)Math.Floor(Math.Sqrt(r2 - dy2));
			int xLo = cx - halfW;
			int xHi = cx + halfW;
			if (xLo < 0) xLo = 0;
			if (xHi >= w) xHi = w - 1;
			int rowW = xHi - xLo + 1;
			if (rowW <= 0) continue;
			_image!.FillRect(new Rect2I(xLo, py, rowW, 1), col);
		}
	}

	// ── Colour table ───────────────────────────────────────────────────────────

	public static Color TileColor(TerrainType t) => t switch
	{
		TerrainType.Water       => new Color(0.18f, 0.42f, 0.72f),
		TerrainType.Mud         => new Color(0.38f, 0.30f, 0.16f),
		TerrainType.Sand        => new Color(0.82f, 0.75f, 0.48f),
		TerrainType.Grass       => new Color(0.28f, 0.52f, 0.22f),
		TerrainType.ForestFloor => new Color(0.14f, 0.38f, 0.11f),
		TerrainType.Boulder     => new Color(0.54f, 0.56f, 0.62f),
		TerrainType.MagicGrove  => new Color(0.44f, 0.22f, 0.72f),
		TerrainType.DeadLog     => new Color(0.56f, 0.54f, 0.48f),
		TerrainType.LivingWood  => new Color(0.28f, 0.17f, 0.08f),
		// v0.4.37 — Shallows: lighter cyan-blue than deep Water so the
		// player can read "this is wadeable" at a glance. Sits between
		// Water (0.18, 0.42, 0.72) and Mud, tinted toward cyan.
		TerrainType.Shallows    => new Color(0.45f, 0.68f, 0.85f),
		// v0.5.84t — Skeleton: bone-cream over earthy brown ground. The
		// PaintSkeleton specialty paint draws actual bone fragments on top;
		// this colour is the fallback for any minimap / preview path that
		// reads TileColor without going through PaintTile.
		TerrainType.Skeleton    => new Color(0.86f, 0.82f, 0.70f),
		_                       => Colors.Black,
	};
}
