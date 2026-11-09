#define NONE 0
#define HUED 1
#define PARTIAL_HUED 2
#define HUE_TEXT_NO_BLACK 3
#define HUE_TEXT 4
#define LAND 5
#define LAND_COLOR 6
#define SPECTRAL 7
#define SHADOW 8
#define LIGHTS 9
#define EFFECT_HUED 10
#define KR_HUED 11
#define KR_PARTIAL_HUED 12
#define KR_NONE 13
#define WATER 14
#define TINT 15
#define KR_LAND 16
#define SWAMP 17
#define SWAMP_WATER 18
#define LAVA 19
#define STARS 20
#define KR_TERRAIN 21
#define GUMP 32

// The KR modes are contiguous so the sampler choice below is one compare.
#define KR_FIRST 11
#define KR_LAST 13

const static float3 LIGHT_DIRECTION = float3(0.0f, 1.0f, 1.0f);

// Kingdom Reborn art is full 32-bit colour, not the near-greyscale ramps the
// 2D art uses, so HUED/PARTIAL_HUED's red-channel index does not work on it:
// a saturated pixel's red says nothing about how light that pixel is, and
// r == g == b is almost never true in anti-aliased art. The KR_* modes index
// the ramp by luminance and gate the partial hue on saturation instead.
const static float3 KR_LUMA = float3(0.299f, 0.587f, 0.114f);

// Saturation below KR_PARTIAL_SAT_LO is fully hued, above KR_PARTIAL_SAT_HI is
// left alone. Measured on KR body 400: 72% of the opaque pixels sit below 0.10
// (the skin the 2D art draws greyscale) while the dyed shorts sit above 0.30.
const static float KR_PARTIAL_SAT_LO = 0.10f;
const static float KR_PARTIAL_SAT_HI = 0.30f;

const static float HUE_ROWS = 1024;
const static float HUE_COLUMNS = 16;
const static float HUE_WIDTH = 32;
const static float HUES_PER_TEXTURE = HUE_ROWS * HUE_COLUMNS;

float4x4 MatrixTransform;
float4x4 WorldMatrix;
float2 Viewport;
float Brightlight;
float CircleOfTransparencyRadius;

// KR-style water (mode WATER). KR draws water with a bump-mapped specular
// shader (UOWaterShader). This approximates its look in place,
// without moving texture lookups - the art sits in an atlas, so an offset
// lookup could read a neighbouring sprite: moving wave shading, a deeper
// tint, and sun glints that ride the wave crests. WaterOrigin is the camera
// offset in world pixels, so the pattern stays put on the world as you move.
float WaterTime;
float2 WaterOrigin;
float WaterTextured;

// KR's own water textures (terrain definition 5), wrapped and filtered:
// bump 01000013_water_alpha, reflected sky 01000017_cube3. (Its colour,
// 02000051, is unused by KR's pass, so s4 holds the grunge instead.)
//
// KR's grunge: world art 1000045, a soft
// grey noise KR's static sprite shader multiplies every static by,
// sampled at the ground point under the pixel, one repeat per 16 x 16
// tiles. KrGrunge is 1 while the world's objects draw.
sampler GrungeSampler : register(s4);
float KrGrunge;
sampler WaterNormalSampler : register(s5);
sampler WaterReflectSampler : register(s6);
sampler SwampSampler : register(s7);
sampler SwampBumpSampler : register(s8);
sampler SwampReflectSampler : register(s9);
sampler LavaNormalSampler : register(s10);
sampler LavaSampler : register(s11);
sampler StarsBumpSampler : register(s12);
sampler StarsReflectSampler : register(s13);
sampler StarsFieldSampler : register(s14);

// GPU terrain (KrTerrainAtlas): the layer textures of the terrains in view,
// 8 x 8 cells of 256 px, and per entry (top, ground, mask cells, repeats
// packed top + ground x 64 + mask x 4096).
sampler KrTerrainSampler : register(s15);
float4 KrTerrainTable[64];
// A hue (index + 1) laid over all GPU terrain, 0 for none: the dead grey.
// The quads' own hue slot holds their table entry, so it comes from here.
float KrTerrainHue;

sampler DrawSampler : register(s0);
sampler HueSampler0 : register(s1);
sampler HueSampler1 : register(s2);

// The same texture as DrawSampler, bound again with linear filtering. The
// world batch is point-sampled so the 2D art is reproduced texel for texel,
// but KR frames sit in the atlas at their native resolution and are minified
// at draw time, so point sampling throws away two of every three pixels. Only
// the KR_* modes read this slot. The sprites behind it are atlased with a 1px
// transparent gutter so the filter cannot reach a neighbouring frame.
sampler KrSampler : register(s3);

struct VS_INPUT
{
	float4 Position : POSITION0;
	float3 Normal	: NORMAL0;
	float3 TexCoord : TEXCOORD0;
	float3 Hue		: TEXCOORD1;
};

struct PS_INPUT
{
	float4 Position : POSITION0;
	float3 TexCoord : TEXCOORD0;
	float3 Normal	: TEXCOORD1;
	float3 Hue		: TEXCOORD2;
	float3 PixelPos : TEXCOORD3;
	float2 WorldPx  : TEXCOORD4;
};

float3 get_rgb(float gray, float hue)
{
    float halfPixelX = (1.0f / (HUE_COLUMNS * HUE_WIDTH)) * 0.5f;
    float hueColumnWidth = 1.0f / HUE_COLUMNS;
    float hueStart = frac(hue / HUE_COLUMNS);
    
    float xPos = hueStart + gray / HUE_COLUMNS;
    xPos = clamp(xPos, hueStart + halfPixelX, hueStart + hueColumnWidth - halfPixelX);
    float yPos = (hue % HUES_PER_TEXTURE) / (HUES_PER_TEXTURE - 1);
    return tex2D(HueSampler0, float2(xPos, yPos)).rgb;
}

float get_light(float3 norm)
{
	float3 light = normalize(LIGHT_DIRECTION);
	float3 normal = normalize(norm);
	float base = (max(dot(normal, light), 0.0f) / 2.0f) + 0.5f;

	// At 45 degrees (the angle the flat tiles are lit at) it must come out
	// to (cos(45) / 2) + 0.5 or 0.85355339...
	return base + ((Brightlight * (base - 0.85355339f)) - (base - 0.85355339f));
}

// KR's terrain light, fitted to KR's vertex colours, against ClassicUO's own vertex normals: grey = 0.555 + 0.384 * dot(n, L). Flat ground comes out 0.852,
// as in ClassicUO, but the light comes from the side, so ground falling away
// toward the water (south-east) is in shade rather than lit.
float get_kr_light(float3 norm)
{
	float3 light = normalize(float3(-0.563f, 0.291f, 0.773f));
	float base = saturate(0.555f + 0.384f * dot(normalize(norm), light));

	return base + ((Brightlight * (base - 0.852f)) - (base - 0.852f));
}

float3 get_colored_light(float shader, float gray)
{
	float2 texcoord = float2(gray, (shader - 0.5) / 63);

	return tex2D(HueSampler1, texcoord).rgb;
}

PS_INPUT VertexShaderFunction(VS_INPUT IN)
{
	PS_INPUT OUT;
	
	OUT.Position = mul(mul(IN.Position, WorldMatrix), MatrixTransform);
	
	OUT.Position.x -= 0.5 / Viewport.x;
	OUT.Position.y += 0.5 / Viewport.y;

	OUT.TexCoord = IN.TexCoord;
	OUT.Normal = IN.Normal;
	OUT.Hue = IN.Hue;
	OUT.PixelPos = OUT.Position.xyz;

	// World pixel for the water: the vertex's position on screen (before the
	// projection, so zoom and render scale do not enter) plus the camera
	// offset, since ClassicUO places objects at world - offset.
	OUT.WorldPx = mul(IN.Position, WorldMatrix).xy + WaterOrigin;

	return OUT;
}

float water_hash(float2 p)
{
	return frac(sin(dot(p, float2(127.1f, 311.7f))) * 43758.5453f);
}

float3 kr_water(float2 q, float2 screen)
{
	// KR's land water, as the client draws it
	//. Fixed-function environment bump
	// mapping: stage 0 BUMPENVMAP with 01000013_water_alpha, one repeat per
	// 16 tiles and drifting slowly along x; stage 1 the reflected sky
	// 01000017_cube3, looked up by the camera-space reflection vector and
	// perturbed by the bump (matrix 1.0), MODULATEd with the vertex colour.
	// There is no colour texture: the water is the sky, lit.
	//
	// q is world px with y stretched 2:1, so one tile is 44 px on each axis.
	float2 tiles = q / 44.0f;

	// Fixed to the world: standing still at the shore shows the
	// bump offset constant (section 32); the drift seen at the bridge came
	// from the player running, not from time.
	float2 uv = tiles / 16.0f;
	float2 duv = tex2D(WaterNormalSampler, uv).rg - 0.5f;

	// The reflection vector over flat water changes little across the view,
	// so the sky is looked up by screen position (as before) plus the bump.
	float3 sky = tex2D(WaterReflectSampler, screen / 900.0f + duv).rgb;

	// Vertex colour: 0xdadada over open water.
	return sky * 0.855f;
}

// One layer of the terrain atlas at (u, v) in repeats, wrapped in its cell.
// tex2Dlod: no derivatives, so it is safe in the mode branches and the wrap
// does not pick a tiny mip; inset half a texel so the filter stays inside.
float4 kr_terrain_cell(float cell, float2 uv)
{
	float row = floor(cell / 8.0f);
	float col = cell - (row * 8.0f);
	float2 local = frac(uv) * (255.0f / 256.0f) + (0.5f / 256.0f);

	return tex2Dlod(KrTerrainSampler, float4((float2(col, row) + local) / 8.0f, 0.0f, 0.0f));
}

// One terrain's ground at world (x, -y) in tiles, as KrTerrainArt.ComposeTile
// does on the CPU: lerp(layer 1, layer 0,
// layer 2's alpha), each layer repeating every N tiles.
float3 kr_terrain_ground(float entry, float2 w)
{
	float4 e = KrTerrainTable[(int)(entry + 0.5f)];
	float rm = floor(e.w / 4096.0f);
	float rg = floor((e.w - (rm * 4096.0f)) / 64.0f);
	float rt = e.w - (rm * 4096.0f) - (rg * 64.0f);

	float3 top = kr_terrain_cell(e.x, w / rt).rgb;
	float3 ground = kr_terrain_cell(e.y, w / rg).rgb;
	float a = kr_terrain_cell(e.z, w / rm).a;

	return lerp(ground, top, a);
}

// Texel (x, y) of a border tile's record in the terrain atlas (KrTerrainAtlas:
// 8 x 5 texels a slot, 256 slots a row, from y 1536).
float4 kr_record(float slot, float2 texel)
{
	float row = floor(slot / 256.0f);
	float2 origin = float2((slot - (row * 256.0f)) * 8.0f, 1536.0f + (row * 5.0f));

	return tex2Dlod(KrTerrainSampler, float4((origin + texel) / 2048.0f, 0.0f, 0.0f));
}

// A KR land tile composed on the GPU. Hue.x is -1 - entry for a tile of one
// terrain, else the slot of its record: row 4 holds the base terrain's entry
// and up to seven layers' (bytes, 63 for none), rows 0-3 two 4 x 4 blocks of
// their weights at texels -1..2, so a bilinear fetch at (1 + 2u, 1 + 2v)
// gives KrTerrainBlend.Weight for four layers at once. TexCoord is (u, v) on
// a sloped tile (y + 2 marks it), else 0..1 across the 44 px quad with the
// diamond cut here; TexCoord.z is the tile's origin.
float4 kr_terrain(PS_INPUT IN)
{
	float u, v;

	if (IN.TexCoord.y >= 1.5f)
	{
		u = saturate(IN.TexCoord.x);
		v = saturate(IN.TexCoord.y - 2.0f);
	}
	else
	{
		float2 local = IN.TexCoord.xy * 44.0f;
		u = (local.x - 22.0f + local.y) / 44.0f;
		v = (local.y - local.x + 22.0f) / 44.0f;

		// Overlap the neighbours by about half a pixel: with an exact cut,
		// rounding in the interpolated coordinates let both tiles reject a
		// pixel on their shared edge, leaving thin black lines. The ground
		// is continuous in world space, so the overlap draws the same colour.
		const float edge = 0.6f / 44.0f;

		if (u < -edge || u > 1.0f + edge || v < -edge || v > 1.0f + edge)
			return float4(0.0f, 0.0f, 0.0f, 0.0f);

		u = saturate(u);
		v = saturate(v);
	}

	float packed = IN.TexCoord.z;
	float ty = floor(packed / 4096.0f);
	float tx = packed - (ty * 4096.0f);
	float2 w = float2(tx + u, -(ty + v));

	float h = IN.Hue.x;

	if (h < -0.5f)
		return float4(kr_terrain_ground(-1.0f - h, w), 1.0f);

	float4 ea = floor(kr_record(h, float2(0.5f, 4.5f)) * 255.0f + 0.5f);
	float4 eb = floor(kr_record(h, float2(1.5f, 4.5f)) * 255.0f + 0.5f);
	float2 at = float2(1.0f + (2.0f * u), 1.0f + (2.0f * v));

	float3 rgb = kr_terrain_ground(ea.x, w);
	float4 wa = kr_record(h, at);

	if (ea.y < 62.5f) rgb = lerp(rgb, kr_terrain_ground(ea.y, w), wa.r);
	if (ea.z < 62.5f) rgb = lerp(rgb, kr_terrain_ground(ea.z, w), wa.g);
	if (ea.w < 62.5f) rgb = lerp(rgb, kr_terrain_ground(ea.w, w), wa.b);

	if (eb.x < 62.5f)
	{
		rgb = lerp(rgb, kr_terrain_ground(eb.x, w), wa.a);

		if (eb.y < 62.5f)
		{
			float4 wb = kr_record(h, at + float2(4.0f, 0.0f));

			rgb = lerp(rgb, kr_terrain_ground(eb.y, w), wb.r);

			if (eb.z < 62.5f) rgb = lerp(rgb, kr_terrain_ground(eb.z, w), wb.g);
			if (eb.w < 62.5f) rgb = lerp(rgb, kr_terrain_ground(eb.w, w), wb.b);
		}
	}

	return float4(rgb, 1.0f);
}

float3 swamp_water(float2 worldPx)
{
	// KR's swamp water (terrain 70, the sewer channels), from its client: drawn like the sea -
	// stage 0 BUMPENVMAP with 02000703_Swamp_Water_D one repeat per 16
	// tiles, stage 1 the reflection 02000702_Swamp_Water_C - but the bump
	// drifts: its texture offset moved -0.000178, -0.0000888 per frame,
	// about -0.0107, -0.0053 per second at 60 fps, so the water flows. The
	// client ran slower than that, so the per-second rate is lower;
	// 75% of it matched KR by eye (direction measured, speed tuned).
	float2 tiles = worldPx * float2(1.0f, 2.0f) / 44.0f;
	float2 uv = tiles / 16.0f + WaterTime * float2(-0.0080f, -0.0040f);
	float2 duv = tex2D(SwampBumpSampler, uv).rg - 0.5f;
	return tex2D(SwampReflectSampler, (worldPx - WaterOrigin) / 900.0f + duv).rgb * 0.855f;
}

float3 water(float3 rgb, float3 pixelPos, float2 worldPx)
{
	// Screen pixel, then world pixel. Ground is seen at 2:1, so y is
	// stretched back to make the pattern lie flat on the water.
	float2 q = worldPx * float2(1.0f, 2.0f);
	float t = WaterTime;

	if (WaterTextured > 0.5f)
		return kr_water(q, worldPx - WaterOrigin);

	float w1 = sin(q.x * 0.045f + t * 1.1f);
	float w2 = sin(q.y * 0.060f - t * 0.8f);
	float w3 = sin((q.x + q.y) * 0.030f + t * 0.6f);

	rgb *= 1.0f + 0.25f * (w1 + w2 + w3) / 3.0f;
	rgb = lerp(rgb, rgb * float3(0.75f, 0.85f, 1.0f), 0.35f);

	// Glints: a few cells in each patch of water flash briefly, and only
	// where two waves crest together.
	float2 cell = floor(q / 10.0f);
	float r = water_hash(cell);
	float phase = water_hash(cell + 17.0f);
	float crest = saturate(w1 * w2 * 2.0f);
	float twinkle = pow(saturate(sin(t * 3.0f + phase * 6.2831f)), 6.0f);
	float2 f = frac(q / 10.0f) - 0.5f;
	float glint = (r > 0.80f ? 1.0f : 0.0f) * twinkle * crest * saturate(1.0f - length(f) * 3.0f);

	return rgb + glint * float3(1.0f, 0.97f, 0.85f) * 2.0f;
}

float4 PixelShader_Hue(PS_INPUT IN) : COLOR0
{
	// Screen-pixel step in texture space, for the shadow blur below; taken
	// outside any branch, where gradients are defined.
	float2 step = fwidth(IN.TexCoord.xy);
	
	int mode = int(IN.Hue.y);
	bool isKr = (mode >= KR_FIRST && mode <= KR_LAST);

	float4 color = isKr
		? tex2D(KrSampler, IN.TexCoord.xy)
		: tex2D(DrawSampler, IN.TexCoord.xy);

	// Shadows: KR softens its shadow mask with a filtered pass before laying
	// it on the ground; a small box of linear taps does the same per sprite,
	// about one screen pixel each way. Point-sampled, the squashed sprite
	// came out jagged.
	if (mode == SHADOW)
	{
		float a = 0.0f;

		for (int sy = -1; sy <= 1; sy++)
			for (int sx = -1; sx <= 1; sx++)
				a += tex2Dlod(KrSampler, float4(IN.TexCoord.xy + float2(sx, sy) * step, 0.0f, 0.0f)).a;

		color.a = a / 9.0f;
	}

	// GPU terrain: composed here, then lit and finished as KR land.
	if (mode == KR_TERRAIN)
	{
		color = kr_terrain(IN);
		mode = KR_LAND;

		if (KrTerrainHue > 0.0f)
			color.rgb = get_rgb(dot(color.rgb, KR_LUMA), KrTerrainHue - 1.0f);
	}

	if (color.a == 0.0f)
		discard;

	// KR frames are stored premultiplied, which is what the blend state and
	// the filter both want, but the hue maths below reads colour as the
	// artist painted it. Divide the alpha back out here and put it back on
	// just before the return, so everything between the two sees straight
	// colour exactly as it did when KR art was point-sampled and unscaled.
	float krAlpha = 1.0f;

	if (isKr)
	{
		krAlpha = color.a;
		color.rgb /= max(color.a, 0.0001f);
	}

	float alpha = IN.Hue.z;

	bool useTrans = false;
	if (alpha > 1.0f)
	{
		useTrans = true;
		alpha -= 1.0f;
	}

	if (alpha == 0.0f)
		discard;

	float hue = IN.Hue.x;

	if (mode >= GUMP)
	{
		mode -= GUMP;

		if (color.r < 0.02f)
		{
			hue = 0;
		}
	}

	if (mode == HUED || (mode == PARTIAL_HUED && color.r == color.g && color.r == color.b))
	{
		color.rgb = get_rgb(color.r, hue);
	}
	else if (mode == KR_HUED)
	{
		color.rgb = get_rgb(dot(color.rgb, KR_LUMA), hue);
	}
	else if (mode == KR_PARTIAL_HUED)
	{
		float mx = max(color.r, max(color.g, color.b));
		float mn = min(color.r, min(color.g, color.b));
		float sat = (mx - mn) / max(mx, 0.0001f);
		float weight = 1.0f - smoothstep(KR_PARTIAL_SAT_LO, KR_PARTIAL_SAT_HI, sat);

		color.rgb = lerp(color.rgb, get_rgb(dot(color.rgb, KR_LUMA), hue), weight);
	}
	else if (mode == HUE_TEXT_NO_BLACK)
	{
		if (color.r > 0.04f || color.g > 0.04f || color.b > 0.04f)
		{
			color.rgb = get_rgb(1.0f, hue);
		}
	}
	else if (mode == HUE_TEXT)
	{
		// 31 is max red, so this is just selecting the color of the darkest pixel in the hue
		color.rgb = get_rgb(1.0f, hue);
	}
	else if (mode == TINT)
	{
		// RGB packed into the hue as r * 65536 + g * 256 + b (bytes); a
		// float holds 24 bits exactly. KR particle colours.
		float packed = IN.Hue.x;
		float r = floor(packed / 65536.0f);
		float g = floor((packed - r * 65536.0f) / 256.0f);
		float b = packed - r * 65536.0f - g * 256.0f;
		color.rgb *= float3(r, g, b) / 255.0f;
	}
	else if (mode == WATER)
	{
		// KR's shoreline pulse: the shore
		// water pieces are alpha-tested against a threshold that follows a
		// sine, 128-207 (of 255) every ~2.05 s (timed on a KR recording; the
		// trace's 62.6 frames ran at ~30 fps), so their soft alpha edge
		// creeps in and out against the shore. Opaque water always passes.
		float edge = (167.5f + 39.5f * sin(WaterTime * 6.2831853f / 2.05f)) / 255.0f;

		float maskA = isKr ? krAlpha : color.a;

		if (maskA < edge)
			discard;

		// A shore piece's sprite carries its edge mask: alpha for the test
		// and, in rgb, the foam KR adds as stage 2 (KrTerrain.GetWater). Open
		// water is opaque and its rgb is colour, so it gets no foam.
		float3 foam = maskA < 0.999f ? color.rgb : float3(0.0f, 0.0f, 0.0f);

		// What passes the test is drawn solid, as KR's alpha test leaves it.
		color.a = 1.0f;

		if (dot(IN.Normal, IN.Normal) > 0.0f)
			color.rgb *= get_light(IN.Normal);

		color.rgb = water(color.rgb, IN.PixelPos, IN.WorldPx) + foam;
	}
	else if (mode == LAND)
	{
		color.rgb *= get_light(IN.Normal);
	}
	else if (mode == STARS)
	{
		// KR's star field (terrain 53), at Trammel 5150,1768
		//: stage 0 BUMPENVMAP with
		// 02000532_Stars_C x24, stage 1 02000530_Stars_A looked up by
		// D3DTSS_TCI_CAMERASPACEREFLECTIONVECTOR through the matrix
		// (2, -2) + (0, 1), LOCALVIEWER on. KR's camera is orthographic but
		// sits only ~10 units (tiles) above the ground, straight over the
		// screen centre, so the eye vector to each point swings strongly
		// within a few tiles: the reflected stars streak out from the centre
		// and shift as the player walks. View rotation measured.
		float2 px = IN.PixelPos.xy * Viewport * 0.5f;
		px.y = -px.y;
		float2 dt = float2(px.x + px.y, px.y - px.x) / 44.0f;
		float3 w = float3(dt.x, 10.0f, -dt.y);
		float3x3 view = float3x3(
			0.7063993f, -0.7069289f, -0.03537598f,
			0.0f, -0.0499792f, 0.9987503f,
			0.7078135f, 0.7055165f, 0.03530525f);
		float3 e = normalize(mul(w, view));
		float3 n = -view[1];
		float3 r = e - 2.0f * dot(n, e) * n;
		float2 tiles = IN.WorldPx * float2(1.0f, 2.0f) / 44.0f;
		float2 duv = tex2D(StarsBumpSampler, tiles / 24.0f + WaterTime * float2(-0.02f, -0.02f)).rg - 0.5f;
		color.rgb = tex2D(StarsReflectSampler, float2(2.0f * r.x, -2.0f * r.y + 1.0f) + duv).rgb;
	}
	else if (mode == LAVA)
	{
		// KR lava (0x12F4-0x1318, and terrain 10), at Trammel
		// 4601,3616: stage 0 BUMPENVMAP with
		// 01000012_lava_normal x12, identity bump matrix; stage 1
		// 02000100_Lava_A x6. The bump's offset drifts -0.000255 u per frame,
		// about -0.0078 per second at the ~30 fps the client runs at.
		float2 tiles = IN.WorldPx * float2(1.0f, 2.0f) / 44.0f;
		// The normal map is DXT1 (unsigned, channels 99-158 around 127),
		// but BUMPENVMAP takes signed du/dv. *Hypothesis:* KR's driver reads
		// the bytes as two's complement, so values just below 128 give about
		// +0.9 and just above about -0.9: strong offsets that flip from patch
		// to patch, which churns the lava in every direction as KR's does
		// (read as unsigned, it only drifted one way).
		float2 b = tex2D(LavaNormalSampler, tiles / 12.0f + WaterTime * float2(-0.0078f, 0.0f)).rg * 255.0f;
		float2 duv = (b < 127.5f ? b : b - 256.0f) / 127.0f;
		color.rgb = tex2D(LavaSampler, tiles / 6.0f + duv).rgb;
	}
	else if (mode == SWAMP_WATER)
	{
		color.rgb = swamp_water(IN.WorldPx);
	}
	else if (mode == SWAMP)
	{
		// KR's swamp floor statics (0x326D, the teal sewer channels): their
		// KR shader draws 02000700_Swamp_Water_A, one repeat per 5 tiles,
		// laid in world space like terrain. Still in KR - the moving sewer
		// water is land (terrain 70), drawn in WATER mode.
		float2 tiles = IN.WorldPx * float2(1.0f, 2.0f) / 44.0f;
		color.rgb = tex2D(SwampSampler, tiles / 5.0f).rgb;
	}
	else if (mode == KR_LAND)
	{
		color.rgb *= get_kr_light(IN.Normal);

		// Land beside swamp water carries the water's blend weight in its
		// alpha (KrTerrainProvider.ApplyWaterEdge): mix the flowing water in.
		if (color.a < 0.999f)
		{
			color.rgb = lerp(swamp_water(IN.WorldPx), color.rgb, color.a);
			color.a = 1.0f;
		}
	}
	else if (mode == LAND_COLOR)
	{
		color.rgb = get_rgb(color.r, hue) * get_light(IN.Normal);
	}
	else if (mode == SPECTRAL)
	{
		alpha = 1.0f - (color.r * 1.5f);
		color.r = 0;
		color.g = 0;
		color.b = 0;
	}
	else if (mode == SHADOW)
	{
		// Coverage from the softened mask above, so the edges fade.
		alpha = 0.4f * color.a;
		color.r = 0;
		color.g = 0;
		color.b = 0;
	}
	else if (mode == LIGHTS)
	{
		color.rgb = get_colored_light(IN.Hue.x - 1, color.r);
	}
	else if (mode == EFFECT_HUED)
	{
		color.rgb = get_rgb(color.g, hue);
	}

	// Statics and items (the plain modes; KR's mobiles have a shader of
	// their own, our KR_* modes). The tile under the pixel is taken at
	// height 0: a raised static's pattern is shifted, which noise hides.
	if (KrGrunge > 0.5f && int(IN.Hue.y) <= PARTIAL_HUED)
	{
		float2 tiles = float2(IN.WorldPx.x + IN.WorldPx.y, IN.WorldPx.y - IN.WorldPx.x) / 44.0f;
		color.rgb *= tex2D(GrungeSampler, tiles / 16.0f).rgb;
	}

	if (isKr)
	{
		color.rgb *= krAlpha;
	}

	if (useTrans && CircleOfTransparencyRadius > 0)
	{
		float2 pixelDist = IN.PixelPos.xy * Viewport * 0.5;
		float ratio = length(pixelDist) / CircleOfTransparencyRadius;

		if (ratio < 0.85f)
			discard;

		if (ratio < 1.0f)
		{
			float t = (ratio - 0.85f) / 0.15f;
			alpha *= t * t * t;

			if (alpha < 0.02f)
				discard;
		}
	}

	return color * alpha;
}

technique HueTechnique
{
	pass p0
	{
		VertexShader = compile vs_3_0 VertexShaderFunction();
		PixelShader = compile ps_3_0 PixelShader_Hue();
	}
}

