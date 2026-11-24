namespace U9Editor.Render
{
    /// <summary>
    /// The shaders, written once in the subset GLSL ES 3.00 and GLSL 3.30 share; the version line is put in
    /// front at run time (ANGLE on Windows gives GLES, other systems desktop GL).
    /// </summary>
    static class Shaders
    {
        public static string Header(bool gles) =>
            gles ? "#version 300 es\nprecision highp float;\nprecision highp int;\nprecision highp usampler2D;\nprecision highp sampler2DArray;\n" : "#version 330 core\n";

        // The terrain: a block of grid vertices, each placed by the raw point it stands on (uPoints, R32UI).
        public const string TerrainVertex = @"
layout(location = 0) in vec2 aLocal;
uniform mat4 uViewProj;
uniform ivec2 uOrigin;
uniform ivec2 uSize;
uniform usampler2D uPoints;
uniform int uNorthRow0;
uniform float uHeightScale;
out vec3 vWorld;
out vec3 vNormal;
out vec2 vPoint;

float heightAt(ivec2 p)
{
    p = clamp(p, ivec2(0), uSize - ivec2(1));
    return float(texelFetch(uPoints, p, 0).r & 0xFFFu) * uHeightScale;
}

void main()
{
    ivec2 p = uOrigin + ivec2(aLocal);
    ivec2 c = clamp(p, ivec2(0), uSize - ivec2(1));
    float h = heightAt(c);
    float z = uNorthRow0 == 1 ? float(uSize.y) - float(c.y) : float(c.y);
    vWorld = vec3(float(c.x), h, z);
    float dx = heightAt(c + ivec2(1, 0)) - heightAt(c - ivec2(1, 0));
    float dr = heightAt(c + ivec2(0, 1)) - heightAt(c - ivec2(0, 1));
    float dz = uNorthRow0 == 1 ? -dr : dr;
    vNormal = normalize(vec3(-dx, 2.0, -dz));
    vPoint = vec2(p);
    gl_Position = uViewProj * vec4(vWorld, 1.0);
}";

        public const string TerrainFragment = @"
in vec3 vWorld;
in vec3 vNormal;
in vec2 vPoint;
uniform usampler2D uPoints;
uniform ivec2 uSize;
uniform int uColourMode;      // 0 height, 1 texture number, 2 flags, 3 textures
uniform sampler2DArray uTerrainTex;
uniform usampler2D uLookup;   // (frame, texture) -> layer + 1
uniform int uHaveTextures;
uniform int uUvRotate;        // debug: quarter turns applied to every quad's texture
uniform int uUvFlipV;         // debug: texture rows run the other way
uniform int uHideUnseen;      // drop quads with texture 29, which the game never shows (dungeon and cave filler)
uniform int uShowTerrain;
uniform float uWaterY;
uniform float uMaxY;
uniform int uHoleMode;        // 0 cut out, 1 show in magenta
uniform int uTileGrid;
uniform ivec2 uHover;         // point under the mouse, -1 for none
uniform ivec2 uSelected;
out vec4 fragColour;

vec3 hashColour(uint n)
{
    uint h = n * 2654435761u;
    return vec3(float((h >> 24) & 255u), float((h >> 16) & 255u), float((h >> 8) & 255u)) / 255.0 * 0.75 + 0.2;
}

vec3 heightColour(float y)
{
    float d = y - uWaterY;
    if (d < 0.0) return mix(vec3(0.55, 0.50, 0.38), vec3(0.18, 0.22, 0.30), clamp(-d / 12.0, 0.0, 1.0));
    float t = clamp(d / max(1.0, uMaxY - uWaterY), 0.0, 1.0);
    vec3 a = vec3(0.30, 0.50, 0.22), b = vec3(0.45, 0.42, 0.28), c = vec3(0.55, 0.52, 0.50), w = vec3(0.95);
    if (t < 0.35) return mix(a, b, t / 0.35);
    if (t < 0.75) return mix(b, c, (t - 0.35) / 0.4);
    return mix(c, w, (t - 0.75) / 0.25);
}

void main()
{
    ivec2 q = ivec2(floor(vPoint));
    if (q.x < 0 || q.y < 0 || q.x >= uSize.x - 1 || q.y >= uSize.y - 1) discard;
    uint raw = texelFetch(uPoints, q, 0).r;
    bool hole = (raw & 0x1000u) != 0u;
    if (hole && uHoleMode == 0) discard;
    if (uShowTerrain == 0) discard;
    if (uHideUnseen == 1 && (raw >> 22) == 29u) discard;

    vec3 base = vec3(0.5);
    if (uColourMode == 3 && uHaveTextures == 1)
    {
        uint layer = texelFetch(uLookup, ivec2(int((raw >> 16) & 63u), int(raw >> 22)), 0).r;
        if (layer == 0u) base = vec3(1.0, 0.0, 1.0) * 0.6;
        else
        {
            // bits 13-14 turn the texture in quarter turns; 0 puts its top row to the north (see TerrainPoint.Rotation)
            vec2 f = fract(vPoint);
            uint rot = (raw >> 13) & 3u;
            vec2 uv = rot == 0u ? vec2(f.x, 1.0 - f.y) : rot == 1u ? vec2(1.0 - f.y, 1.0 - f.x) : rot == 2u ? vec2(1.0 - f.x, f.y) : f.yx;
            if (uUvFlipV == 1) uv.y = 1.0 - uv.y;
            for (int i = 0; i < uUvRotate; i++) uv = vec2(uv.y, 1.0 - uv.x);
            // gradients from the continuous point position, so the quad edges do not pick the smallest mipmap
            base = textureGrad(uTerrainTex, vec3(uv, float(layer - 1u)), dFdx(vPoint), dFdy(vPoint)).rgb;
        }
    }
    else if (uColourMode == 1) base = hashColour(raw >> 22);
    else if (uColourMode == 2)
        base = vec3((raw & 0x2000u) != 0u ? 0.9 : 0.25, (raw & 0x4000u) != 0u ? 0.9 : 0.25, (raw & 0x8000u) != 0u ? 0.9 : 0.25);   // rotation bits, bit 15
    else if (uColourMode == 0 || uColourMode == 3) base = heightColour(vWorld.y);

    vec3 sun = normalize(vec3(-0.45, 0.8, 0.35));
    float light = 0.35 + 0.65 * max(dot(normalize(vNormal), sun), 0.0);
    vec3 col = base * light;

    if (hole) col = vec3(1.0, 0.0, 1.0);
    if (uTileGrid == 1)
    {
        vec2 f = fract(vPoint / 16.0) * 16.0;
        vec2 w = fwidth(vPoint) * 1.2;
        if (f.x < w.x || f.y < w.y) col = mix(col, vec3(0.0), 0.45);
    }
    if (q == uSelected) col = mix(col, vec3(1.0, 0.85, 0.1), 0.7);
    else if (q == uHover) col = mix(col, vec3(1.0), 0.45);
    fragColour = vec4(col, 1.0);
}";

        // Object markers: one point per object, sized by distance, round, coloured by file.
        public const string MarkerVertex = @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in float aKind;
layout(location = 2) in float aId;
uniform mat4 uViewProj;
uniform float uPointScale;
uniform int uHover;
uniform int uSelected;
uniform int uOnlyZones;       // models are shown: mark only the map-change zones
out vec3 vColour;
void main()
{
    gl_Position = uViewProj * vec4(aPos, 1.0);
    if (uOnlyZones == 1 && aKind < 1.5) gl_Position = vec4(2.0, 2.0, 2.0, 1.0);
    int id = int(aId + 0.5);
    float size = clamp(uPointScale / gl_Position.w, 3.0, 14.0);
    vColour = aKind < 0.5 ? vec3(1.0, 0.62, 0.15) : aKind < 1.5 ? vec3(0.25, 0.9, 1.0) : aKind < 2.5 ? vec3(1.0, 0.95, 0.1) : vec3(0.7, 0.45, 1.0);   // fixed, nonfixed, map change zone, arrival
    if (aKind > 1.5) size = max(size, 9.0) + 3.0;
    if (id == uHover) { vColour = vec3(1.0); size += 4.0; }
    if (id == uSelected) { vColour = vec3(1.0, 0.2, 0.9); size += 6.0; }
    gl_PointSize = size;
}";

        public const string MarkerFragment = @"
in vec3 vColour;
out vec4 fragColour;
void main()
{
    vec2 d = gl_PointCoord - vec2(0.5);
    float r = dot(d, d);
    if (r > 0.25) discard;
    fragColour = vec4(r > 0.16 ? vColour * 0.35 : vColour, 1.0);
}";

        // Object models: instanced, one draw per model; the instance's model-to-world matrix comes in as 4 vec4s.
        public const string ModelVertex = @"
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
layout(location = 3) in float aLayer;
layout(location = 4) in vec4 aColour;
layout(location = 5) in vec4 aM0;
layout(location = 6) in vec4 aM1;
layout(location = 7) in vec4 aM2;
layout(location = 8) in vec4 aM3;
layout(location = 9) in float aId;
uniform mat4 uViewProj;
uniform int uSelected;
uniform int uHover;
out vec3 vNormal;
out vec2 vUv;
out float vLayer;
out vec4 vColour;
flat out int vMark;
void main()
{
    mat4 m = mat4(aM0, aM1, aM2, aM3);
    gl_Position = uViewProj * m * vec4(aPos, 1.0);
    vNormal = mat3(m) * aNormal;
    vUv = aUv;
    vLayer = aLayer;
    vColour = aColour;
    int id = int(aId + 0.5);
    vMark = id == uSelected ? 2 : (id == uHover ? 1 : 0);
}";

        public const string ModelFragment = @"
in vec3 vNormal;
in vec2 vUv;
in float vLayer;
in vec4 vColour;
flat in int vMark;
uniform sampler2DArray uTexA;
uniform sampler2DArray uTexB;
uniform int uSplit;
out vec4 fragColour;
void main()
{
    vec4 c = vColour;
    int layer = int(vLayer + 0.5);
    if (vLayer >= -0.5)
    {
        c = layer < uSplit ? texture(uTexA, vec3(vUv, float(layer))) : texture(uTexB, vec3(vUv, float(layer - uSplit)));
        if (c.a < 0.5) discard;
    }
    vec3 n = normalize(vNormal);
    vec3 sun = normalize(vec3(-0.45, 0.8, 0.35));
    float light = 0.4 + 0.6 * abs(dot(n, sun));
    vec3 col = c.rgb * light;
    if (vMark == 2) col = mix(col, vec3(1.0, 0.2, 0.9), 0.55);
    else if (vMark == 1) col = mix(col, vec3(1.0), 0.35);
    fragColour = vec4(col, 1.0);
}";

        public const string WaterVertex = @"
layout(location = 0) in vec2 aXZ;
uniform mat4 uViewProj;
uniform float uWaterY;
void main() { gl_Position = uViewProj * vec4(aXZ.x, uWaterY, aXZ.y, 1.0); }";

        public const string WaterFragment = @"
out vec4 fragColour;
void main() { fragColour = vec4(0.10, 0.30, 0.55, 0.45); }";
    }
}
