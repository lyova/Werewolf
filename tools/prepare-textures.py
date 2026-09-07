r"""Converts the purchased 4096 TGA maps into what the Unity project imports, in art\textures.

    python mods\Werewolf\tools\prepare-textures.py [--size 2048] [--emission-size 2048]

The bundle material is Unity's Standard shader in its SPECULAR setup, configured exactly like the
vanilla wolf's material (wolf.mat: keywords _NORMALMAP _SPECGLOSSMAP _EMISSION) - that variant is
known to be compiled into the game's player, because a vanilla animal ships with it. So the maps
are packed for that shader:

    werewolf_albedo_<variant>.png    RGB albedo, sRGB
    werewolf_emission_<variant>.png  RGB emission (the eyes and glow pattern), sRGB
    werewolf_normal.png              tangent-space normal, Unity (OpenGL, +Y) convention as shipped
    werewolf_specgloss.png           RGB specular colour (flat dielectric 0.2), A smoothness

Smoothness comes straight out of the seller's T_Werewolf_MetallicSmoothness alpha, un-inverted,
because Standard reads smoothness in the alpha the same way. The metallic channels of that map are
all zero, so specular is a constant.

For the OTHER route - moving the textures onto the game's own animal fur material at runtime
(Game/Animal/Fur, DireWolf.mat) - the shader wants _RMOL: roughness R, metallic G, occlusion B.
That is written too, as werewolf_rmo.png, with roughness = 255 - smoothness and occlusion flat 255
(there is no AO map in the package; 0 would kill all ambient light). Measured against the vanilla
dire_wolf_RMO.tga: R mean 123, G mean 61, B mean 238. Ours: R mean ~237 (a very rough creature),
G 0, B 255. Check with --stats before shipping that route.
"""
import argparse
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.abspath(os.path.join(HERE, ".."))
SRC = os.path.join(MOD, "art", "source", "unitypackage", "Assets", "Lil Pupinduy", "Character",
                   "Werewolf Character", "Textures")
OUT = os.path.join(MOD, "art", "textures")

VARIANTS = {"brown": "Brown", "dark": "Dark", "gray": "Gray", "ice": "Ice"}
# The seller's _Albedo_Brightness, one per variant, read straight out of their .mat files. THIS IS
# NOT A TASTE ADJUSTMENT - it is part of the artwork, and dropping it is why the ice werewolf shipped
# as a grey one.
#
# Their material drives a custom URP shadergraph whose BaseColor is "Texture Color x Albedo
# Brightness". Three of the four sit at 1, so the texture is the colour. Ice sits at 2.5, and its
# texture is a dark blue-grey (mean 55/65/75) that only becomes snow once multiplied: 139/162/186.
# Everyone who measured that file without the multiplier - including this toolchain until now -
# concluded there was no white variant in the asset. There is; it lives in the material.
#
# The game runs the built-in pipeline and cannot load that shadergraph, so the multiply is baked
# into the texture here instead. Highlights clip a little where the seller's shader would have kept
# them in HDR until tonemapping; on fur that is not visible.
#
# NOT carried over, and worth knowing: the same shader adds a Fresnel rim to EMISSION, coloured per
# variant (_FresnelColor - ice is cyan 0/0.83/1, dark a deep red 0.42/0/0, at _Fresnel_Power 6).
# That rim is the glow around the head in the store renders and it is a big part of how distinct the
# four look there. It cannot be baked into a texture because it depends on the view angle; it needs
# a shader with a fresnel term, which is a separate job.
ALBEDO_BRIGHTNESS = {
    "brown": 1.0,
    "dark": 1.0,
    "gray": 1.0,
    "ice": 2.5,
}


def brightened(img, gain):
    """Multiply an sRGB image by gain, clipping at white. gain 1.0 returns it untouched."""
    if abs(gain - 1.0) < 1e-6:
        return img
    lut = [min(255, int(v * gain + 0.5)) for v in range(256)]
    return img.point(lut * 3)


SPEC_DIELECTRIC = 51  # 0.2 in sRGB, Unity's Standard default specular colour


def stats(img, label):
    img = img.convert("RGBA")
    parts = []
    for name, ch in zip("RGBA", img.split()):
        h = ch.histogram()
        n = sum(h)
        mean = sum(i * c for i, c in enumerate(h)) / n
        lo, hi = ch.getextrema()
        parts.append(f"{name} {lo:3d}/{mean:5.1f}/{hi:3d}")
    print(f"  {label:34s} {img.size[0]}x{img.size[1]}  " + "  ".join(parts))


def resized(img, size):
    return img if img.size == (size, size) else img.resize((size, size), Image.LANCZOS)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--size", type=int, default=2048)
    ap.add_argument("--emission-size", type=int, default=2048)
    ap.add_argument("--stats", action="store_true", help="only print channel statistics of the outputs")
    a = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)

    if a.stats:
        for f in sorted(os.listdir(OUT)):
            if f.endswith(".png"):
                stats(Image.open(os.path.join(OUT, f)), f)
        return

    print("min/mean/max per channel")
    for key, name in VARIANTS.items():
        alb = Image.open(os.path.join(SRC, f"T_Werewolf_{name}_AlbedoTransparency.tga")).convert("RGB")
        alb = resized(alb, a.size)
        alb = brightened(alb, ALBEDO_BRIGHTNESS.get(key, 1.0))
        p = os.path.join(OUT, f"werewolf_albedo_{key}.png")
        alb.save(p, optimize=True)
        stats(alb, os.path.basename(p))

        emi = Image.open(os.path.join(SRC, f"T_Werewolf_{name}_Emission.tga")).convert("RGB")
        emi = resized(emi, a.emission_size)
        p = os.path.join(OUT, f"werewolf_emission_{key}.png")
        emi.save(p, optimize=True)
        stats(emi, os.path.basename(p))

    normal = Image.open(os.path.join(SRC, "T_Werewolf_Normal.tga")).convert("RGB")
    normal = resized(normal, a.size)
    p = os.path.join(OUT, "werewolf_normal.png")
    normal.save(p, optimize=True)
    stats(normal, os.path.basename(p))

    ms = Image.open(os.path.join(SRC, "T_Werewolf_MetallicSmoothness.tga")).convert("RGBA")
    ms = resized(ms, a.size)
    smooth = ms.split()[3]
    spec = Image.new("L", (a.size, a.size), SPEC_DIELECTRIC)
    specgloss = Image.merge("RGBA", (spec, spec, spec, smooth))
    p = os.path.join(OUT, "werewolf_specgloss.png")
    specgloss.save(p, optimize=True)
    stats(specgloss, os.path.basename(p))

    rough = smooth.point(lambda v: 255 - v)
    zero = Image.new("L", (a.size, a.size), 0)
    full = Image.new("L", (a.size, a.size), 255)
    rmo = Image.merge("RGBA", (rough, zero, full, full))
    p = os.path.join(OUT, "werewolf_rmo.png")
    rmo.save(p, optimize=True)
    stats(rmo, os.path.basename(p))

    print("done ->", OUT)


main()
