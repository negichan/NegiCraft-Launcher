# Vendored: MinecraftSkinRender

This directory is a **vendored copy** of [Coloryr/MinecraftSkinRender](https://github.com/Coloryr/MinecraftSkinRender)
(the core library plus its OpenGL backend, which shipped as the `MinecraftSkinRender` and
`MinecraftSkinRender.OpenGL` NuGet packages) so we can pose individual body parts — something
the packaged renderer cannot express, see below.

- **Upstream:** https://github.com/Coloryr/MinecraftSkinRender
- **Commit:** `8da2c913da91b84c6ae7d3f5b817bab6bcbe9505` (the tree NuGet `1.2.0` was built from)
- **License:** MIT. `LICENSE` is retained unmodified, as MIT requires.
- **Scope:** only the two projects we use. `MinecraftSkinRender.Image`, `.Vulkan*`, `.Silk`,
  `.MojangApi`, `.SkinDownload` and `.Test` are **not** vendored and are not referenced.

Assembly name and namespaces stay `MinecraftSkinRender` / `MinecraftSkinRender.OpenGL`, so
`Controls/SkinRenderControl.cs` and `Controls/OpenGL/AvaloniaApi.cs` needed no import changes.

## Local changes from upstream

`SkinRender.cs` — `GetMatrix4` restructured, and the single mirrored pair of pose inputs replaced:

- `ArmRotate` / `LegRotate` (one vector driving both sides, negated for the right limb — so only
  an antiphase walk swing was reachable) became `LeftArmRotate` / `RightArmRotate` /
  `LeftLegRotate` / `RightLegRotate`, plus `BodyRotate`. The mirrored `_skina` walk-animation path
  is unchanged.
- New per-part offsets `BodyPos` / `HeadPos` / `LeftArmPos` / `RightArmPos` / `LeftLegPos` /
  `RightLegPos`, applied in root space before each part's pivot. A crouch folds the rig (head
  sinks toward the torso, feet slide back) instead of tipping the whole model over, and a rigid
  global transform cannot produce that.
- Limb pivots moved to where Minecraft puts them — head about the neck joint (was 2px below it),
  arms about the shoulder top (upstream pivoted them at the hand, so any arm rotation swung the
  shoulder), legs about the top-centre of the hip. Rest placements are unchanged, so a model with
  no pose set renders exactly as upstream.

`OpenGL/SkinRenderOpenGL.cs` — `DrawSkin()` uploaded a hardcoded `Matrix4x4.Identity` for the base
torso while `DrawSkinTop()` used `GetMatrix4(ModelPartType.Body)`; with a posed torso the jacket
layer would have torn away from the body, so both now use the same matrix.

## Re-syncing with upstream

This is a fork; upstream updates are a **manual** merge — diff the new upstream
`MinecraftSkinRender` and `MinecraftSkinRender.OpenGL` against this directory and re-apply the
edits above.
